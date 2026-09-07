# Known Issues / 已知问题

## 1. 路径查找器跳跃高度过低导致无法寻路 — ✅ 已修复
- **文件**: `Executor/Pathfinder.cs`
- **现象**: AI 执行 `move_to` 时路径查找返回 `no path found`，回退为直接直线移动，导致被墙挡住卡住
- **根因**: `MAX_JUMP_HEIGHT = 8`，实际角色跳跃高度远大于 8 格；`_fallHeight` 初始值仅 5
- **修复**: 
  - v1: `MAX_JUMP_HEIGHT → 30`，`_fallHeight` 初始值 → 50（临时硬编码）
  - v2: 改为运行时动态计算 — 通过反射读取 Body prefab 的 `jumpSpeed` 字段，用公式 `h = jumpSpeed² / (2 × |gravity|) + 2` 计算出精确最大跳跃高度（tiles），自适应任何 prefab 配置

## 2. AI 角色无法睡觉会困死 — ✅ 已修复
- **现象**: AI 角色被召唤后无法休息/睡觉，随时间推移体力耗尽濒临死亡
- **根因**: OrderExecutor 中没有 `sleep`/`rest` 等动作
- **修复**: 添加 `HandleSleep()` → 调用 `body.TakeANap()`；新增 `sleep`/`rest` 两个 action 别名

## 3. Python→C# 命令通道断开 — ✅ 已修复（传输层重写）
- **文件**: `server/server.py` → `BridgePlugin.cs`
- **原现象**: C# 侧收到初始 ping 后，任何后续 Python 向 pipe 写入都会导致
  C# 端断开连接（ReadLoop 退出）
- **原根因**: `PipeClient.OnMessage` 回调在**后台读线程**上触发，处理器直接调用
  Unity API（`ExecuteOrder` 等），在非主线程访问 Unity 对象使读循环崩溃
- **原绕过**: `cu_mcp_commands.json` 文件队列
- **2026-09 修复**:
  - 命名管道整体替换为本地 HTTP 传输（`transport.py` / `HttpBridgeClient.cs`）。
    Python→C# 方向改为 C# 侧长轮询 `GET /poll`。
  - `BridgePlugin` 现将入站消息压入 `ConcurrentQueue<Message> _mainThreadQueue`，
    在 `Update()` 里于**主线程**排空（`ProcessMessageOnMainThread`），不再在
    网络线程上碰 Unity 对象。
  - `cu_mcp_commands.json` 文件队列**已移除**：`server.py` 的 order/query/search
    分发改为 `pipe.send(...)`，`FileCommandQueue` 一并删除。
  - 仍需在游戏内实测确认。

## 4. `pick_up_item` 参数名不匹配 — ✅ 已修复
- **文件**: `Executor/OrderExecutor.cs:470`
- **现象**: 传 `{"item":"地生果"}` 不生效，总是捡最近的物品
- **根因**: 代码读 `order.Parameters["item_id"]`，但 MCP bridge 发送的参数名是 `item`
- **修复**: 加 `?? order.Parameters["item"]` fallback

## 5. `EnvironmentScan` 不扫描地面物品 (Item) — ✅ 已修复
- **文件**: `Collector/EnvironmentScan.cs`
- **现象**: `get_game_state` / `get_map_info` / `query_position` 均返回空 entities 列表，即使地上有物品
- **根因**: `Scan()` 只查找 `Limb`，未扫描 `Item`；`ScanAtPosition()` 甚至不扫描任何实体
- **修复**: 
  - `Scan()` 中增加 `GameObject.FindObjectsOfType<Item>()` 扫描，过滤身上装备的
  - `ScanAtPosition()` 同样增加 Item 扫描

## 6. `PlayerSnapshot.Collect()` 未填充 Entities 字段 — ✅ 已修复
- **文件**: `Collector/PlayerSnapshot.cs`
- **现象**: `PlayerState.Entities` 字段存在但始终为 null
- **根因**: `Collect()` 未设置 `Entities`
- **修复**: 初始化 `state.Entities = new List<EntityInfo>()`
