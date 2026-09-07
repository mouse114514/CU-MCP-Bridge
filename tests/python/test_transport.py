import json
import os
import sys
import threading
import time
import urllib.request

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "..", "src", "bridge_server"))

from transport import HttpBridgeServer, PipeServer  # noqa: E402


# --------------------------------------------------------------------------- shapes

def test_order_message_format():
    msg = {
        "type": "order",
        "data": {"id": "ord_001", "action": "move_to", "parameters": {"x": 50.0, "y": -100.0}},
    }
    line = json.dumps(msg, ensure_ascii=False) + "\n"
    parsed = json.loads(line.strip())
    assert parsed["type"] == "order"
    assert parsed["data"]["action"] == "move_to"


def test_interrupt_message_shape():
    msg = {"type": "interrupt", "data": {"reason": "health_critical", "priority": 9, "data": {"health": 15.0}}}
    assert msg["data"]["priority"] >= 0
    assert "reason" in msg["data"]


def test_pipeserver_is_http_alias():
    assert PipeServer is HttpBridgeServer


# ---------------------------------------------------------------- live round-trip

def _url(server, path):
    return f"http://{server.host}:{server.port}{path}"


def test_http_round_trip():
    server = HttpBridgeServer(host="127.0.0.1", port=0)
    server.wait_for_connect(timeout=0.1)  # starts the HTTP server; no client yet
    try:
        # mod -> Python : POST /message is drained by read_line()
        inbound = {"type": "state_update", "data": {"player": {"health": 85.0}}}
        urllib.request.urlopen(
            urllib.request.Request(
                _url(server, "/message"),
                data=(json.dumps(inbound) + "\n").encode("utf-8"),
                headers={"Content-Type": "application/json"},
                method="POST",
            ),
            timeout=2,
        ).read()

        line = server.read_line()
        assert json.loads(line)["data"]["player"]["health"] == 85.0

        # Python -> mod : send() is delivered by GET /poll
        server.send({"type": "order", "data": {"id": "ord_9", "action": "jump", "parameters": {}}})
        with urllib.request.urlopen(_url(server, "/poll?timeout=5"), timeout=8) as resp:
            assert resp.status == 200
            out = json.loads(resp.read())
        assert out["type"] == "order"
        assert out["data"]["action"] == "jump"

        # a client request flips `connected`
        assert server.connected is True

        # empty queue -> 204 within the timeout window
        with urllib.request.urlopen(_url(server, "/poll?timeout=0"), timeout=3) as resp:
            assert resp.status == 204
    finally:
        server.close()


def test_read_line_returns_none_after_close():
    server = HttpBridgeServer(host="127.0.0.1", port=0)
    server.wait_for_connect(timeout=0.1)

    result = {}

    def reader():
        result["line"] = server.read_line()

    t = threading.Thread(target=reader, daemon=True)
    t.start()
    time.sleep(0.1)
    server.close()
    t.join(timeout=2)
    assert result.get("line", "sentinel") is None
