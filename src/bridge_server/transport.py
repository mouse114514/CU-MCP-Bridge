"""Local HTTP transport between the Python MCP server and the C# BepInEx mod.

Replaces the previous Windows named-pipe link (`win32pipe`). The Python side is
the HTTP *server*; the C# mod is the *client* and drives both directions:

    GET  /poll?timeout=<sec>  -> next queued Python->mod message, or 204 on timeout
    POST /message             <- one mod->Python message (newline-delimited JSON
                                 body; batches allowed)
    GET  /health              -> {"status": "ok", "connected": <bool>}

Message bodies are exactly the same newline-delimited JSON `Message` objects the
pipe used, so nothing else in the server or mod had to change.

Why HTTP: named pipes need `pywin32` (no Linux wheels) and a pipe created inside
the Wine/Proton prefix is unreachable from a native Linux Python process, so the
bridge could only ever run on Windows. A localhost HTTP socket works everywhere,
including the game running under Proton talking to a native Linux server.

`HttpBridgeServer` keeps the old `PipeServer` method surface
(`wait_for_connect` / `read_line` / `send` / `connected` / `close`) and is also
exported as `PipeServer` for backwards compatibility.
"""

import json
import os
import queue
import sys
import tempfile
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlsplit

# --- Backwards-compat constants (named-pipe era; unused by the HTTP transport) ---
PIPE_NAME = r"\\.\pipe\CU-MCP-Bridge"
SOCKET_PATH = os.path.join(tempfile.gettempdir(), "cu_mcp_bridge.sock")

# --- HTTP transport configuration -------------------------------------------------
DEFAULT_HOST = os.environ.get("CU_MCP_HTTP_HOST", "127.0.0.1")
DEFAULT_PORT = int(os.environ.get("CU_MCP_HTTP_PORT", "8765"))

# How long a client is considered "connected" after its last request.
_CONNECTED_GRACE_SEC = 40.0
# Upper bound we allow a /poll to block for, regardless of the query string.
_MAX_POLL_SEC = 60.0

_SHUTDOWN = object()


class HttpBridgeServer:
    """Localhost HTTP bridge to the C# mod. Drop-in replacement for PipeServer.

    Runs a small ThreadingHTTPServer on a background thread. Thread-safe.
    """

    def __init__(self, pipe_name: str = PIPE_NAME, host: str = None, port: int = None):
        # `pipe_name` is accepted (and ignored) so existing `PipeServer(PIPE_NAME)`
        # call sites keep working.
        self.host = host or DEFAULT_HOST
        self.port = DEFAULT_PORT if port is None else port
        self._outbound: "queue.Queue" = queue.Queue()   # dict messages -> mod (via GET /poll)
        self._inbound: "queue.Queue" = queue.Queue()     # json strings <- mod (via POST /message)
        self._httpd: "ThreadingHTTPServer | None" = None
        self._thread: "threading.Thread | None" = None
        self._connect_event = threading.Event()
        self._last_seen = 0.0
        self._seen = False
        self._closed = False

    # ------------------------------------------------------------------ lifecycle
    @property
    def connected(self) -> bool:
        return (
            not self._closed
            and self._seen
            and (time.time() - self._last_seen) < _CONNECTED_GRACE_SEC
        )

    def wait_for_connect(self, timeout: float = None):
        """Start the HTTP server (idempotent) and block until the mod first calls in."""
        if self._closed:
            raise RuntimeError("HttpBridgeServer is closed")
        if self._httpd is None:
            self._start_server()
        self._connect_event.wait(timeout)

    def _start_server(self):
        server = self

        class Handler(BaseHTTPRequestHandler):
            protocol_version = "HTTP/1.1"

            def log_message(self, *args):  # silence per-request stderr spam
                pass

            def _write_json(self, code: int, obj):
                body = json.dumps(obj).encode("utf-8")
                self.send_response(code)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

            def _write_empty(self, code: int):
                self.send_response(code)
                self.send_header("Content-Length", "0")
                self.end_headers()

            def do_GET(self):
                path = urlsplit(self.path).path
                if path == "/health":
                    server._mark_seen()
                    self._write_json(200, {"status": "ok", "connected": server.connected})
                    return
                if path == "/poll":
                    server._mark_seen()
                    qs = parse_qs(urlsplit(self.path).query)
                    try:
                        wait = float(qs.get("timeout", ["25"])[0])
                    except (TypeError, ValueError):
                        wait = 25.0
                    wait = max(0.0, min(wait, _MAX_POLL_SEC))
                    try:
                        msg = server._outbound.get(timeout=wait)
                    except queue.Empty:
                        self._write_empty(204)
                        return
                    if msg is _SHUTDOWN:
                        self._write_empty(204)
                        return
                    self._write_json(200, msg)
                    return
                self._write_json(404, {"error": "not found"})

            def do_POST(self):
                path = urlsplit(self.path).path
                if path != "/message":
                    self._write_json(404, {"error": "not found"})
                    return
                server._mark_seen()
                length = int(self.headers.get("Content-Length", 0) or 0)
                raw = self.rfile.read(length) if length else b""
                text = raw.decode("utf-8", errors="replace")
                for line in text.splitlines():
                    line = line.strip()
                    if line:
                        server._inbound.put(line)
                self._write_json(200, {"ok": True})

        self._httpd = ThreadingHTTPServer((self.host, self.port), Handler)
        self._httpd.daemon_threads = True
        self.port = self._httpd.server_address[1]
        self._thread = threading.Thread(
            target=self._httpd.serve_forever,
            name="cu-mcp-http",
            daemon=True,
        )
        self._thread.start()
        print(f"[HttpBridgeServer] Listening on http://{self.host}:{self.port} ...", file=sys.stderr)

    def _mark_seen(self):
        self._last_seen = time.time()
        if not self._seen:
            self._seen = True
            print("[HttpBridgeServer] Mod connected", file=sys.stderr)
        self._connect_event.set()

    # ------------------------------------------------------------------------- io
    def read_line(self) -> "str | None":
        """Block for the next mod->Python JSON line. Returns None once closed."""
        if self._closed:
            return None
        item = self._inbound.get()
        if item is _SHUTDOWN:
            return None
        return item

    def send(self, message: dict) -> bool:
        """Queue one Python->mod message for the next GET /poll. Never blocks."""
        if self._closed:
            return False
        self._outbound.put(message)
        return True

    def close(self):
        if self._closed:
            return
        self._closed = True
        self._seen = False
        self._connect_event.set()
        self._inbound.put(_SHUTDOWN)
        self._outbound.put(_SHUTDOWN)
        if self._httpd is not None:
            try:
                self._httpd.shutdown()
            except Exception:
                pass
            try:
                self._httpd.server_close()
            except Exception:
                pass
            self._httpd = None


# Backwards-compat alias: the rest of the codebase still imports `PipeServer`.
PipeServer = HttpBridgeServer
