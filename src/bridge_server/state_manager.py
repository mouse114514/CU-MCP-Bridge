import json
import threading
from typing import Optional

from transport import HttpBridgeServer as PipeServer


class StateManager:
    """Caches latest game state from C# mod.  Runs pipe I/O on a background thread."""

    def __init__(self, pipe: PipeServer):
        self.pipe = pipe
        self._latest_player: Optional[dict] = None
        self._latest_environment: Optional[dict] = None
        self._latest_query_result: Optional[dict] = None
        self._latest_search_result: Optional[dict] = None
        self._pending_interrupts: list[dict] = []
        self._lock = threading.Lock()
        self._thread: Optional[threading.Thread] = None

    def start(self):
        """Start the background read-loop thread."""
        self._thread = threading.Thread(target=self._read_loop, daemon=True, name="pipe-reader")
        self._thread.start()

    def _read_loop(self):
        """Blocking loop reading messages from the C# mod."""
        while True:
            line = self.pipe.read_line()
            if line is None:
                # HTTP transport: read_line only returns None once the server is
                # closed. Client reconnects are transparent (the mod just resumes
                # polling), so there is nothing to re-establish here.
                print("[StateManager] Transport closed, stopping reader")
                break

            try:
                msg = json.loads(line)
            except json.JSONDecodeError:
                continue

            with self._lock:
                if msg.get("type") == "state_update":
                    data = msg.get("data", {})
                    if "player" in data:
                        self._latest_player = data["player"]
                    if "environment" in data:
                        self._latest_environment = data["environment"]
                    if "query_result" in data:
                        self._latest_query_result = data["query_result"]
                    if "search_result" in data:
                        self._latest_search_result = data["search_result"]

    def get_latest_state(self) -> dict:
        with self._lock:
            result = {
                "player": self._latest_player or {},
                "environment": self._latest_environment or {},
                "pending_interrupts": list(self._pending_interrupts),
            }
            if self._latest_query_result:
                result["query_result"] = self._latest_query_result
                self._latest_query_result = None
            if self._latest_search_result:
                result["search_result"] = self._latest_search_result
                self._latest_search_result = None
            return result

    def pop_interrupts(self) -> list[dict]:
        with self._lock:
            result = list(self._pending_interrupts)
            self._pending_interrupts.clear()
            return result
