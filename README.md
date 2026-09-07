# CU-MCP-Bridge

BepInEx mod + Python MCP server that lets AI assistants control [Casualties Unknown](https://store.steampowered.com/app/Casualties_Unknown) gameplay in real time over a local HTTP link.

## Requirements

- Windows, Linux, or macOS — the game may run natively or under Proton/Wine; a
  `127.0.0.1` HTTP socket bridges the two either way
- [Casualties Unknown](https://store.steampowered.com/app/Casualties_Unknown) (Steam)
- Python 3.10+ (standard library only — no `pywin32`)
- .NET SDK (for building the mod)
- [BepInEx](https://docs.bepinex.dev/articles/user_guide/installation/index.html) installed in the game

## Quick start

### 1. Install the mod

Copy the two DLLs from `mod/` into your game's `BepInEx/plugins/` folder:

```
<game directory>/BepInEx/plugins/CU-MCP-Mod.dll
<game directory>/BepInEx/plugins/Newtonsoft.Json.dll
```

### 2. Install Python dependencies

With [uv](https://docs.astral.sh/uv/) (recommended — `pyproject.toml` + `uv.lock`
are checked in):

```bash
uv sync
```

or with pip:

```bash
pip install -r requirements.txt
```

Standard library only otherwise — no `pywin32`, no native transport dependency.

### 3. Configure your AI client

Add to your MCP configuration (e.g. `opencode.json`):

```json
{
  "mcp": {
    "servers": {
      "cu-mcp-bridge": {
        "type": "stdio",
        "command": "python",
        "args": ["src/bridge_server/server.py"],
        "cwd": "/path/to/CU-MCP-Bridge"
      }
    }
  }
}
```

### 4. Play

1. Start the game, enter a level
2. Start the Python server: `python src/bridge_server/server.py`
   (usually launched for you by the MCP client via the config above)
3. Ask your AI assistant to control the player

> Start order no longer matters: the mod keeps polling until the server is up,
> and the server queues anything it wants to send until the mod connects.

### Configuration

The mod and server rendezvous on `http://127.0.0.1:8765` by default.

- **Server**: set `CU_MCP_HTTP_HOST` / `CU_MCP_HTTP_PORT` before launching
  `server.py`.
- **Mod**: set `"http_url"` in `cu_mcp_config.json` (next to the game executable),
  e.g. `{ "http_url": "http://127.0.0.1:8765" }`.

Both must point at the same host/port.

## Building from source

```bash
# Build the C# mod
dotnet build BepInEx/CU-MCP-Mod.csproj -c Release

# Deploy to game (kills the game process, rebuilds, copies DLL)
.\deploy.ps1 -Action deploy
```

Output: `BepInEx/bin/Release/net472/CU-MCP-Mod.dll`

## Architecture

```
AI Client (opencode)
        |
    stdio (JSON-RPC)
        |
Python FastMCP Server (src/bridge_server/)   <-- HTTP server on 127.0.0.1:8765
        |
    Local HTTP (newline-delimited JSON Message bodies)
        |
C# BepInEx Mod (BepInEx/, inside Unity game) <-- HTTP client (poll + post)
        |
    Unity Game (Casualties Unknown)
```

### Communication

The mod drives both directions against the server:

- `GET /poll?timeout=<sec>` — long-poll for the next **AI -> Game** message
  (order / query / contingency); `204` means "nothing yet, poll again".
- `POST /message` — **Game -> AI** player state, query/search results, order
  results, acks, interrupts (newline-delimited JSON; batches allowed).
- `GET /health` — liveness / `connected` flag.

Message payloads are byte-for-byte the same newline-delimited JSON `Message`
objects the previous named-pipe transport used. Each order still blocks on the
MCP side until the mod reports completion (success/failure/timeout).

## Project structure

```
CU-MCP-Bridge/
├── BepInEx/                # C# mod source (BepInEx plugin)
│   ├── Executor/           # Order execution, pathfinding, movement
│   ├── Collector/          # Game state collection (player, environment)
│   ├── Contingency/        # Local condition-action rules
│   ├── Transport/          # HTTP bridge client + wire protocol
│   ├── AIPlayerManager.cs  # AI companion creation/destruction
│   ├── BridgePlugin.cs     # Entry point, tick loop
│   └── DebugGUI.cs         # F6 debug panel
├── src/bridge_server/      # Python MCP server
│   ├── server.py           # MCP tool definitions
│   ├── state_manager.py    # inbound reader, state cache
│   ├── order_manager.py    # Order queue
│   ├── transport.py        # HTTP bridge server (stdlib http.server)
│   ├── pipe_server.py      # back-compat shim -> transport.py
│   └── contingency.py      # Contingency rule manager
├── tests/                  # Unit & integration tests
├── deploy.ps1              # Build + deploy script
└── requirements.txt        # Python dependencies
```

## MCP Tools

| Tool | Description |
|---|---|
| `get_game_state` | Player state (position, health, inventory) + environment snapshot |
| `get_map_info` | Terrain and entity info around the player |
| `get_nearby_items` | List dropped items near the player |
| `query_position` | Query entities/terrain at a specific world coordinate |
| `search_blocks` | Search terrain by material name (e.g. "sand", "rock") |
| `move_to` | Move the active player to a world position |
| `move_to_player` | Move the AI companion to the human player |
| `follow` | Make the AI companion follow the human |
| `jump` | Jump (supports horizontal direction) |
| `use_item` | Use an inventory item |
| `pick_up_item` | Pick up a nearby item (by name or nearest) |
| `drop_item` | Drop an inventory item |
| `sleep` | Rest to recover energy |
| `heal_ai` | Heal a specific limb on the AI companion |
| `create_ai_player` | Clone an AI companion next to the human |
| `destroy_ai_player` | Remove the AI companion |
| `set_contingency` | Set condition-action rules (e.g. "heal if HP < 30%") |
| `update_contingency` | Update contingency rules at runtime |
| `user_interact` | Highest-priority human intervention |

## Known issues

- **Skin mod compatibility**: Installing third-party skin mods (e.g. "Skin Sync") may cause the AI companion to display a duplicate tail that mirrors the human player's tail. This is a skin mod compatibility issue and does not occur in the vanilla game.

## License

MIT
