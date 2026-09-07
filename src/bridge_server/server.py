import sys
import threading
import time

from mcp.server.fastmcp import FastMCP

from transport import HttpBridgeServer as PipeServer, PIPE_NAME, SOCKET_PATH, DEFAULT_HOST, DEFAULT_PORT
from state_manager import StateManager
from order_manager import OrderManager, Order
from contingency import ContingencyManager, ContingencyRule

pipe: PipeServer = None
state_mgr: StateManager = None
order_mgr: OrderManager = None
contingency_mgr: ContingencyManager = None

mcp = FastMCP("CU-MCP-Bridge")

def _dispatch_order(action: str, parameters: dict) -> dict:
    """Queue an order for the mod to pull on its next GET /poll."""
    order = Order(action=action, parameters=parameters)
    pipe.send({"type": "order", "data": order.to_dict()})
    return {"order_id": order.id, "status": "dispatched"}


# ═══════════════════════════════════════
# MCP Tools — Orders
# ═══════════════════════════════════════

@mcp.tool(
    annotations={
        "title": "Move To",
    }
)
def move_to(x: float, y: float, arrival_distance: float = 1.5, pathfind: bool = True) -> dict:
    """Move the active player to a target world position (x, y)."""
    return _dispatch_order("move_to", {
        "x": x, "y": y,
        "arrival_distance": arrival_distance,
        "pathfind": pathfind,
    })


@mcp.tool(
    annotations={
        "title": "Follow",
    }
)
def follow(distance: float = 2.0, max_ydiff: float = 3.0) -> dict:
    """Follow the human player at a set distance. Runs continuously until cancelled."""
    return _dispatch_order("follow", {
        "distance": distance,
        "max_ydiff": max_ydiff,
    })


@mcp.tool(
    annotations={
        "title": "Move To Player",
    }
)
def move_to_player(arrival_distance: float = 1.5, pathfind: bool = True) -> dict:
    """Move the AI player to the human player's current position."""
    import time as _time
    pipe.send({"type": "query", "data": {"player": "human", "id": f"mtp_{int(_time.time()*1000)}"}})
    _time.sleep(0.5)
    state = state_mgr.get_latest_state()
    player = state.get("player", {})
    pos = player.get("position", {})
    hx = pos.get("x", 0)
    hy = pos.get("y", 0)
    if hx == 0 and hy == 0:
        return {"error": "could not get human player position"}
    return _dispatch_order("move_to", {
        "x": hx, "y": hy,
        "arrival_distance": arrival_distance,
        "pathfind": pathfind,
    })


@mcp.tool(
    annotations={
        "title": "Use Item",
    }
)
def use_item(item: str) -> dict:
    """Use an item from the active player's inventory by item ID."""
    return _dispatch_order("use_item", {"item": item})


@mcp.tool(
    annotations={
        "title": "Wait",
    }
)
def wait(seconds: float = 1.0) -> dict:
    """Wait for a specified duration in seconds."""
    return _dispatch_order("wait", {"seconds": seconds})


@mcp.tool(
    annotations={
        "title": "Jump",
    }
)
def jump(x: float = 0.0, hold_seconds: float = 0.0) -> dict:
    """Make the active player jump.
    x — horizontal direction (-1 left, 0 up, 1 right).
    hold_seconds — keep horizontal movement held after jump (e.g. for gaps).
    """
    return _dispatch_order("jump", {"x": x, "hold_seconds": hold_seconds})


@mcp.tool(
    annotations={
        "title": "Create AI Player",
    }
)
def create_ai_player(offset_x: float = 2.0) -> dict:
    """Create an AI player clone next to the human player."""
    return _dispatch_order("create_ai_player", {"offset_x": offset_x})


@mcp.tool(
    annotations={
        "title": "Destroy AI Player",
    }
)
def destroy_ai_player() -> dict:
    """Destroy the AI player."""
    return _dispatch_order("destroy_ai_player", {})


@mcp.tool(
    annotations={
        "title": "Heal AI",
    }
)
def heal_ai(limb: str, item_id: str) -> dict:
    """Heal a specific limb on the AI player using a wound-healing item from the human's inventory.
    limb — name of the limb to heal (e.g. "Head", "Body", "LeftArm").
    item_id — ID of the wound item to use (e.g. "bandage", "suture_kit").
    """
    return _dispatch_order("heal_ai", {"limb": limb, "item_id": item_id})


@mcp.tool(
    annotations={
        "title": "Pick Up Item",
    }
)
def pick_up_item(item: str = None, search_range: float = 3.0, slot: int = -1, force: bool = False) -> dict:
    """Pick up an item from the ground near the active player.
    item — item ID to pick up (omit to pick nearest any item).
    search_range — max distance to search for items.
    slot — target inventory slot (default: auto).
    force — force pick up even if slot is occupied.
    """
    params = {"search_range": search_range, "slot": slot, "force": force}
    if item is not None:
        params["item"] = item
    return _dispatch_order("pick_up_item", params)


@mcp.tool(
    annotations={
        "title": "Drop Item",
    }
)
def drop_item(slot: int = None, item_id: str = None) -> dict:
    """Drop an item from the active player's inventory.
    Provide slot (inventory index) or item_id (item identifier).
    When neither is given, drops the first non-empty slot found.
    """
    params = {}
    if slot is not None:
        params["slot"] = slot
    if item_id is not None:
        params["item_id"] = item_id
    return _dispatch_order("drop_item", params)


@mcp.tool(
    annotations={
        "title": "Sleep",
    }
)
def sleep() -> dict:
    """Make the active player sleep/rest to recover energy."""
    return _dispatch_order("sleep", {})


# ═══════════════════════════════════════
# MCP Tools — Legacy generic fallback
# ═══════════════════════════════════════

@mcp.tool(
    annotations={
        "title": "Send Order (legacy)",
    }
)
def send_order(
    action: str,
    parameters: dict,
    order_id: str = None,
    pipeline_input: str = None,
    pipeline_output: str = None,
) -> dict:
    """[Legacy] Send a raw order to the game. Prefer the typed tools (move_to, use_item, etc.)
    for better discoverability.  Use this only for actions that don't yet have a dedicated tool.
    """
    order = Order(
        action=action,
        parameters=parameters,
        order_id=order_id,
        pipeline_input=pipeline_input,
        pipeline_output=pipeline_output,
    )
    pipe.send({"type": "order", "data": order.to_dict()})
    return {"order_id": order.id, "status": "dispatched"}


# ═══════════════════════════════════════
# MCP Tools — Query / State
# ═══════════════════════════════════════

@mcp.tool(
    annotations={
        "readOnlyHint": True,
        "title": "Get Game State",
    }
)
def get_game_state(player: str = None) -> dict:
    """Return current player state + surrounding environment snapshot.

    Args:
        player: Optional - "human" for human player state, "ai" for AI player state.
                When omitted, returns the currently active player's state.
    """
    if player is not None:
        pipe.send({"type": "query", "data": {"player": player, "id": f"gs_{int(time.time()*1000)}"}})
        time.sleep(0.5)
        state = state_mgr.get_latest_state()
        return state
    result = state_mgr.get_latest_state()
    interrupts = state_mgr.pop_interrupts()
    if interrupts:
        result["pending_interrupts"] = interrupts
    return result


@mcp.tool(
    annotations={
        "readOnlyHint": True,
        "title": "Get Map Info",
    }
)
def get_map_info(range: int = 20) -> dict:
    """Get terrain and entity info within a range of the player."""
    state = state_mgr.get_latest_state()
    env = state.get("environment", {})
    return {
        "entities": env.get("entities", []),
        "terrain": env.get("nearby_terrain", []),
        "range": range,
    }


@mcp.tool(
    annotations={
        "readOnlyHint": True,
        "title": "Query Position",
    }
)
def query_position(x: float, y: float, range: float = 20.0) -> dict:
    """Query entities and terrain blocks at a specific position (x, y).

    Returns all limbs/creatures and terrain blocks within the given range
    of the specified world coordinate. Useful for scouting ahead, checking
    for enemies, or examining terrain composition at arbitrary positions.
    """
    pipe.send({"type": "query", "data": {"x": x, "y": y, "range": range, "id": f"q_{int(time.time()*1000)}"}})
    time.sleep(1.0)
    state = state_mgr.get_latest_state()
    qr = state.get("query_result")
    if qr:
        return qr
    return {"entities": [], "terrain": [], "note": "query_sent_awaiting_response"}


@mcp.tool(
    annotations={
        "readOnlyHint": True,
        "title": "Search Blocks",
    }
)
def search_blocks(material: str, x: float = None, y: float = None, range: int = 100, limit: int = 20) -> dict:
    """Search for terrain blocks matching a material name pattern (substring match).

    Scans a large area and returns only blocks whose material name contains the
    given string. Returns both tile coordinates and world coordinates for easy
    navigation with move_to.
    """
    data = {"material": material, "range": range, "limit": limit, "id": f"s_{int(time.time()*1000)}"}
    if x is not None and y is not None:
        data["x"] = x
        data["y"] = y
    pipe.send({"type": "search", "data": data})
    time.sleep(1.5)
    state = state_mgr.get_latest_state()
    sr = state.get("search_result")
    if sr:
        return sr
    return {"matches": [], "total_scanned": 0, "total_matched": 0, "note": "search_sent_awaiting_response"}


# ═══════════════════════════════════════
# MCP Tools — Contingency / Interrupt
# ═══════════════════════════════════════

@mcp.tool(
    annotations={
        "title": "Set Contingency",
    }
)
def set_contingency(rules: list[dict]) -> dict:
    """Set local contingency rules executed by the game mod without AI wake."""
    parsed = [ContingencyRule(**r) for r in rules]
    contingency_mgr.update_rules(parsed)
    return {"status": "updated", "rule_count": len(rules)}


@mcp.tool(
    annotations={
        "title": "Update Contingency",
    }
)
def update_contingency(rules: list[dict]) -> dict:
    """Update contingency rules at runtime. Replaces ALL existing rules."""
    contingency_mgr.clear_rules()
    parsed = [ContingencyRule(**r) for r in rules]
    contingency_mgr.update_rules(parsed)
    return {"status": "updated", "rule_count": len(rules)}


@mcp.tool(
    annotations={
        "title": "User Interact",
        "dangerousHint": True,
    }
)
def user_interact(message: str) -> dict:
    """Channel for player intervention. Highest priority interrupt."""
    contingency_mgr.clear_rules()
    return {
        "status": "player_intervention",
        "message": message,
        "priority": "highest",
    }


# ═══════════════════════════════════════
# Entry point
# ═══════════════════════════════════════

def main():
    global pipe, state_mgr, order_mgr, contingency_mgr

    print(f"[CU-MCP] Starting bridge server (platform={sys.platform})...")
    print(f"[CU-MCP] Game link: HTTP on http://{DEFAULT_HOST}:{DEFAULT_PORT} "
          f"(override with CU_MCP_HTTP_HOST / CU_MCP_HTTP_PORT)")
    pipe = PipeServer(PIPE_NAME)
    state_mgr = StateManager(pipe)
    order_mgr = OrderManager(pipe)
    contingency_mgr = ContingencyManager(pipe)

    # Connect to game in background (non-blocking)
    def _connect():
        try:
            pipe.wait_for_connect()
            print("[CU-MCP] Game connected")
            pipe.send({"type": "ping", "data": {"hello": "from_python"}})
            print("[CU-MCP] Initial ping sent")
            state_mgr.start()
        except Exception as e:
            print(f"[CU-MCP] Game transport error: {e}")

    t = threading.Thread(target=_connect, daemon=True)
    t.start()

    print("[CU-MCP] Ready. Waiting for MCP client (OpenCode)...")
    mcp.run(transport="stdio")


if __name__ == "__main__":
    main()
