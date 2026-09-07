using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using CUMCP.Protocol;
using CUMCP.Transport;

namespace CUMCP.Executor
{
    public class OrderExecutor
    {
        public event Action<string> OnOrderCompleted;
        public event Action<string, string> OnOrderFailed;

        private readonly HttpBridgeClient _pipe;
        private readonly Pathfinder _pathfinder = new Pathfinder();
        private MonoBehaviour _coroutineHost;
        private Coroutine _activeCoroutine;
        private OrderParams _currentOrder;
        private float _obstacleCooldown;
        private const float ARRIVAL_EPS = 0.25f;    // horizontal "at target" epsilon (world units)
        private const float ARRIVAL_Y_SLACK = 2.0f; // vertical slack: body center sits ~1.5 above ground

        public bool IsBusy => _currentOrder != null;

        public OrderExecutor(HttpBridgeClient pipe, MonoBehaviour coroutineHost)
        {
            _pipe = pipe;
            _coroutineHost = coroutineHost;
        }

        public void ExecuteOrder(OrderParams order)
        {
            if (IsBusy)
            {
                OnOrderFailed?.Invoke(order.Id, "Busy with another order");
                return;
            }

            _currentOrder = order;
            BridgePlugin.Log.LogInfo($"[CU-MCP] Executing order: {order.Action} id={order.Id}");
            _pipe.Send(MessageBuilder.Ack(0, true));

            switch (order.Action)
            {
                case "move_to":
                    _activeCoroutine = _coroutineHost.StartCoroutine(MoveToRoutine(order));
                    break;
                case "follow":
                    _activeCoroutine = _coroutineHost.StartCoroutine(FollowRoutine(order));
                    break;
                case "use_item":
                    _activeCoroutine = _coroutineHost.StartCoroutine(UseItemRoutine(order));
                    break;
                case "wait":
                    _activeCoroutine = _coroutineHost.StartCoroutine(WaitRoutine(order));
                    break;
                case "jump":
                    _activeCoroutine = _coroutineHost.StartCoroutine(JumpRoutine(order));
                    break;
                case "create_ai_player":
                    HandleCreateAI(order);
                    break;
                case "destroy_ai_player":
                    HandleDestroyAI(order);
                    break;

                case "heal_ai":
                    HandleHealAI(order);
                    break;
                case "console":
                    HandleConsole(order);
                    break;
                default:
                    OnOrderFailed?.Invoke(order.Id, $"Unknown action: {order.Action}");
                    _currentOrder = null;
                    break;
            }
        }

        public void CancelCurrent()
        {
            if (_activeCoroutine != null)
                _coroutineHost.StopCoroutine(_activeCoroutine);
            _currentOrder = null;
            _activeCoroutine = null;
        }

        // "console": run a dev-console command line as if typed in-game.
        //   parameters: { "command": "inflate 0.4" }
        // Runs on the Unity main thread (orders are dispatched from
        // BridgePlugin.ProcessMessageOnMainThread). ConsoleScript.TryExecuteCommand
        // is private, so it is invoked via reflection.
        private void HandleConsole(OrderParams order)
        {
            var line = order.Parameters?["command"]?.ToString();
            if (string.IsNullOrEmpty(line) || line.Trim().Length == 0)
            {
                OnOrderFailed?.Invoke(order.Id, "console: missing 'command'");
                _currentOrder = null;
                return;
            }

            try
            {
                var cs = ConsoleScript.instance;
                if (cs == null)
                {
                    OnOrderFailed?.Invoke(order.Id, "console: ConsoleScript.instance is null (not in a run?)");
                }
                else
                {
                    var args = line.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    HarmonyLib.Traverse.Create(cs).Method("TryExecuteCommand", new object[] { args, true }).GetValue();
                    BridgePlugin.Log.LogInfo($"[CU-MCP] console: {line}");
                    OnOrderCompleted?.Invoke(order.Id);
                }
            }
            catch (Exception e)
            {
                OnOrderFailed?.Invoke(order.Id, $"console error: {e.Message}");
            }
            _currentOrder = null;
        }

        private IEnumerator MoveToRoutine(OrderParams order)
        {
            var body = AIPlayerManager.GetActiveBody();
            if (body == null) { BridgePlugin.Log.LogWarning("[CU-MCP] MoveTo: Body not found"); Fail("Body not found"); yield break; }

            float targetX = order.Parameters["x"]?.ToObject<float>() ?? 0;
            float targetY = order.Parameters["y"]?.ToObject<float>() ?? 0;
            float arrivalDist = order.Parameters["arrival_distance"]?.ToObject<float>() ?? 1.5f;
            bool usePathfinding = order.Parameters["pathfind"]?.ToObject<bool>() ?? true;

            Vector2 targetPos = new Vector2(targetX, targetY);
            BridgePlugin.Log.LogInfo($"[CU-MCP] MoveTo: ({targetX:F1},{targetY:F1}) pathfind={usePathfinding} arrival={arrivalDist:F1}");

            const int MAX_RETRIES = 3;
            bool reached = false;

            for (int attempt = 0; attempt <= MAX_RETRIES && _currentOrder != null; attempt++)
            {
                Vector2 startPos = body.transform.position;
                BridgePlugin.Log.LogInfo($"[CU-MCP] MoveTo: attempt {attempt}/{MAX_RETRIES} from ({startPos.x:F1},{startPos.y:F1})");

                List<Vector2> path = null;
                List<Pathfinder.PathWaypoint> wps = null;
                bool needsCrouch = false;
                if (usePathfinding)
                {
                PathVisualizer.Clear();
                yield return _coroutineHost.StartCoroutine(_pathfinder.FindPathCoroutine(startPos, targetPos));
                path = _pathfinder.LastResult;
                wps = _pathfinder.LastWaypoints;
                if (path == null || path.Count == 0)
                {
                    path = _pathfinder.FindPath(startPos, targetPos);
                    needsCrouch = _pathfinder.LastPathUsedCrouch;
                    if (path != null && path.Count > 0)
                    {
                        wps = BuildSimpleWaypoints(path);
                        BridgePlugin.Log.LogInfo($"[CU-MCP] MoveTo: legacy path found with {path.Count} waypoints crouch={needsCrouch}");
                    }
                }
                else
                {
                    BridgePlugin.Log.LogInfo($"[CU-MCP] MoveTo: path found with {path.Count} waypoints jumps={CountJumps(wps)}");
                }
            }

                if (path == null || path.Count == 0)
                {
                    BridgePlugin.Log.LogWarning($"[CU-MCP] MoveTo: pathfinding FAILED attempt={attempt} start=({startPos.x:F1},{startPos.y:F1}) target=({targetPos.x:F1},{targetPos.y:F1})");
                    path = new List<Vector2> { targetPos };
                    wps = BuildSimpleWaypoints(path);
                }

            int waypointIndex = 0;
            int stuckFrames = 0;
            int stuckTotalFrames = 0;
            int descentDir = 0;
            Vector2 lastPos = body.transform.position;
            float jumpCooldown = 0f;
            int frameCount = 0;
            int lastWp = -1;
            float wpEnterTime = 0f;
            int wallDbg = 0;
            float wallJumpLiftUntil = 0f;

            for (int i = 0; i < wps.Count; i++)
            {
                var w = wps[i];
                BridgePlugin.Log.LogInfo($"[CU-MCP] MoveTo: wp{i} pos=({w.World.x:F1},{w.World.y:F1}) air={w.Airborne} jt={w.JumpType} jd={(w.JumpDir.HasValue ? $"({w.JumpDir.Value.x:F0},{w.JumpDir.Value.y:F0})" : "null")}");
            }

            while (_currentOrder != null && waypointIndex < path.Count)
            {
                Vector2 waypoint = path[waypointIndex];
                Vector2 pos = body.transform.position;
                float dx = waypoint.x - pos.x;
                float dy = waypoint.y - pos.y;
                float distX = Mathf.Abs(dx);
                float distY = Mathf.Abs(dy);

                // Runtime crouch:
                //  - crouch decision looks AHEAD (the passage you're about to enter): <=4 high -> crouch
                //  - stand decision looks ABOVE your head: >=5 clearance -> stand up
                BodyMoveDirOverride.IsCrouching = needsCrouch || ShouldCrouch(body);

                if (waypointIndex != lastWp) { lastWp = waypointIndex; wpEnterTime = Time.time; }

                bool isLastWaypoint = waypointIndex == path.Count - 1;
                bool isDescent = dy < -1.5f;
                float wpArrival = isLastWaypoint ? ARRIVAL_EPS : 1.5f;

                bool isAirborne = wps != null && waypointIndex < wps.Count && wps[waypointIndex].Airborne;
                bool isTakeoff = wps != null && waypointIndex < wps.Count && wps[waypointIndex].JumpDir.HasValue;
                bool isWallJumpTakeoff = isAirborne && isTakeoff && wps[waypointIndex].JumpType == Pathfinder.JumpType.WallJump;

                if (isWallJumpTakeoff)
                {
                    // Mid-air wall contact: steer into the wall, then launch away when sliding.
                    // Do NOT give up on body.grounded here - right after the takeoff jump the
                    // game's grounded flag is still set (down boxcast tolerance), so it would
                    // fire spuriously. Only the 2.5s timeout advances us.
                    if (Time.time - wpEnterTime > 2.5f)
                    {
                        BridgePlugin.Log.LogWarning($"[CU-MCP] MoveTo: wall-jump timeout wp{waypointIndex} pos=({pos.x:F1},{pos.y:F1})");
                        WallJumpTrigger.CancelRequest();
                        waypointIndex++;
                        stuckFrames = 0;
                        continue;
                    }
                }
                else if (isAirborne)
                {
                    if (distX < 1f || dy < -1f || Time.time - wpEnterTime > 5f)
                    { waypointIndex++; stuckFrames = 0; continue; }
                }
                else if (!isTakeoff)
                {
                    bool arrived;
                    if (isDescent)
                    {
                        // Downward waypoint: advance once the body has dropped to near the
                        // waypoint's level (landed on the lower ground). Do not require exact X
                        // while falling - the body drifts during the drop.
                        arrived = distY < ARRIVAL_Y_SLACK + 0.5f;
                    }
                    else if (isLastWaypoint)
                    {
                        // Final waypoint: only advance when essentially AT the target (no tolerance).
                        arrived = distX < ARRIVAL_EPS && distY < ARRIVAL_Y_SLACK;
                    }
                    else
                    {
                        arrived = distX < wpArrival && distY < wpArrival + 1.5f;
                    }
                    if (arrived || Time.time - wpEnterTime > 8f)
                    { waypointIndex++; stuckFrames = 0; continue; }
                }
                else if (Time.time - wpEnterTime > 4f)
                {
                    BridgePlugin.Log.LogWarning($"[CU-MCP] MoveTo: takeoff timeout wp{waypointIndex} pos=({pos.x:F1},{pos.y:F1})");
                    waypointIndex++; stuckFrames = 0; continue;
                }

                if (!isAirborne && !isTakeoff)
                {
                    float moved = Vector2.Distance(pos, lastPos);
                    if (moved < 0.05f)
                    {
                        stuckFrames++;
                        stuckTotalFrames++;
                        if (stuckFrames > 5 * 60 || stuckTotalFrames > 10 * 60)
                        {
                            BridgePlugin.Log.LogWarning($"[CU-MCP] MoveTo: STUCK wp{waypointIndex} pos=({pos.x:F1},{pos.y:F1}) target=({waypoint.x:F1},{waypoint.y:F1}) cont={stuckFrames / 60f:F1}s total={stuckTotalFrames / 60f:F1}s - re-pathing");
                            break;
                        }
                    }
                    else
                    {
                        stuckFrames = 0;
                    }
                }
                lastPos = pos;

                float dirX = 0f;
                // On a takeoff point, walk right onto the block instead of stopping early.
                // On a descent, keep walking toward the waypoint even when horizontally close so
                // the body walks off the ledge and falls instead of halting at the edge.
                float moveThreshold = (isTakeoff || isDescent) ? 0f : wpArrival;
                if (distX > moveThreshold)
                    dirX = dx > 0f ? 1f : -1f;
                if (isDescent)
                {
                    if (body.grounded)
                    {
                        // While walking off the ledge, latch ONE horizontal direction so the body
                        // does not oscillate back and forth at the edge as dx flips sign.
                        if (descentDir == 0)
                            descentDir = dx != 0f ? (dx > 0f ? 1 : -1) : 1;
                        dirX = descentDir;
                    }
                    else
                    {
                        // Airborne: falling, no steering here - the OverrideMoveDir block below
                        // releases control so the body does not flip direction every frame.
                        dirX = 0f;
                    }
                }
                else
                {
                    descentDir = 0;
                }

                bool wantJump = false;
                if (isWallJumpTakeoff)
                {
                    // Steering handled below; jump fires on slide contact.
                }
                else if (isTakeoff)
                {
                    // Jump when standing on the takeoff block. The character transform sits
                    // ~2 units above the block's world position, so allow distY up to 3.
                    wantJump = body.grounded && body.standing && jumpCooldown <= 0f && distX < 1f && distY < 3f;
                }
                else if (wps == null)
                {
                    if (body.grounded && jumpCooldown <= 0f)
                    {
                        float heightAbove = waypoint.y - pos.y;
                        if (heightAbove > 0.5f && heightAbove < 4.5f && distX < 5f)
                            wantJump = true;
                    }
                }

                if (wantJump && isTakeoff)
                {
                    var jd = wps[waypointIndex].JumpDir.Value;
                    float vxRatio;
                    // If the next waypoint is a wall-jump contact, aim this jump at the wall,
                    // not at the (unreachable-by-single-jump) landing. Also guarantee the jump
                    // drifts INTO the wall so the body actually contacts it (the contact cell may
                    // sit directly above the takeoff, in which case the pure aiming ratio is ~0).
                    bool nextIsWallContact = waypointIndex + 1 < wps.Count
                        && wps[waypointIndex + 1].Airborne
                        && wps[waypointIndex + 1].JumpType == Pathfinder.JumpType.WallJump;
                    if (nextIsWallContact)
                    {
                        vxRatio = ComputeJumpVxRatio(body, path, wps, waypointIndex, waypointIndex + 1);
                        var njd = wps[waypointIndex + 1].JumpDir.Value;
                        float towardWall = -Mathf.Sign(njd.x != 0f ? njd.x : 1f);
                        if (Mathf.Abs(vxRatio) < 0.3f)
                            vxRatio = towardWall * 0.3f;
                    }
                    else
                        vxRatio = ComputeJumpVxRatio(body, path, wps, waypointIndex, -1);
                    BodyMoveDirOverride.OverrideMoveDir = new Vector2(vxRatio, 0f);
                    body.Jump();
                    jumpCooldown = 0.3f;
                    BridgePlugin.Log.LogInfo($"[CU-MCP] MoveTo: JUMP at takeoff wp{waypointIndex} pos=({pos.x:F1},{pos.y:F1}) dir=({jd.x:F0},{jd.y:F0}) vxRatio={vxRatio:F2} wallChain={nextIsWallContact}");
                    waypointIndex++;
                    continue;
                }

                if (isWallJumpTakeoff)
                {
                    // Steer into the wall so the body slides, then launch once contact is made.
                    // We launch directly (no reliance on the game's slide flag - the body may
                    // barely clip the wall face and never register a slide).
                    var jd = wps[waypointIndex].JumpDir.Value;
                    float towardWall = -Mathf.Sign(jd.x != 0f ? jd.x : 1f);
                    BodyMoveDirOverride.OverrideMoveDir = new Vector2(towardWall, 0f);

                    if (wallDbg++ % 5 == 0)
                        BridgePlugin.Log.LogInfo($"[CU-MCP] MoveTo: WALL wp{waypointIndex} pos=({pos.x:F1},{pos.y:F1}) wp=({waypoint.x:F1},{waypoint.y:F1}) dx={distX:F1} dy={dy:F1} gr={body.grounded} slide={WallJumpTrigger.IsSliding} steer={towardWall:F0} vel=({body.rb?.velocity.x:F1},{body.rb?.velocity.y:F1})");

                    // Waypoint world Y is the block center, but the body transform sits ~2 units
                    // above the block it occupies - so "the body is at the contact cell" means
                    // pos.y >= waypoint.y + 2, i.e. dy <= -1.5f.
                    if (!body.grounded && dy <= -1.5f && distX < 1.5f)
                    {
                        WallJumpTrigger.LaunchWallJump(jd);
                        BodyMoveDirOverride.OverrideMoveDir = null;
                        jumpCooldown = 0.3f;
                        wallJumpLiftUntil = Time.time + 0.6f;
                        BridgePlugin.Log.LogInfo($"[CU-MCP] MoveTo: WALL-JUMP at wp{waypointIndex} pos=({pos.x:F1},{pos.y:F1}) dir=({jd.x:F0},{jd.y:F0})");
                        waypointIndex++;
                        stuckFrames = 0;
                        continue;
                    }

                    yield return new WaitForFixedUpdate();
                    continue;
                }

                if (Time.time < wallJumpLiftUntil && !body.grounded)
                {
                    // Let the wall-jump launch carry the body up and over the wall before steering.
                    BodyMoveDirOverride.OverrideMoveDir = null;
                }
                else if (isDescent && !body.grounded)
                {
                    // Falling during a descent: release steering so the body does not flip
                    // direction every frame (twitching). Preserve the existing horizontal
                    // velocity and let gravity drop it onto the lower ground.
                    BodyMoveDirOverride.OverrideMoveDir = null;
                }
                else
                {
                    BodyMoveDirOverride.OverrideMoveDir = new Vector2(dirX, 0f);
                }

                jumpCooldown -= Time.fixedDeltaTime;
                _obstacleCooldown -= Time.fixedDeltaTime;

                if (frameCount++ % 30 == 0)
                {
                    BridgePlugin.Log.LogInfo($"[CU-MCP] MoveTo: f={frameCount} pos=({pos.x:F1},{pos.y:F1}) wp{waypointIndex}=({waypoint.x:F1},{waypoint.y:F1}) dist=({distX:F1},{distY:F1}) gr={body.grounded} vel=({body.rb?.velocity.x:F1},{body.rb?.velocity.y:F1})");
                }

                yield return new WaitForFixedUpdate();
            }

                BodyMoveDirOverride.OverrideMoveDir = new Vector2(0f, 0f);
                BodyMoveDirOverride.IsCrouching = false;
                yield return new WaitForFixedUpdate();
                BodyMoveDirOverride.OverrideMoveDir = null;

                if (_currentOrder == null) break;

                if (Mathf.Abs(body.transform.position.x - targetPos.x) < ARRIVAL_EPS
                    && Mathf.Abs(body.transform.position.y - targetPos.y) < ARRIVAL_Y_SLACK)
                {
                    BridgePlugin.Log.LogInfo($"[CU-MCP] MoveTo: reached target on attempt {attempt}");
                    reached = true;
                    break;
                }

                BridgePlugin.Log.LogWarning($"[CU-MCP] MoveTo: attempt {attempt} did not reach target pos=({body.transform.position.x:F1},{body.transform.position.y:F1}) target=({targetPos.x:F1},{targetPos.y:F1})");
            }

            if (_currentOrder == null) yield break;

            if (reached)
            {
                BridgePlugin.Log.LogInfo("[CU-MCP] MoveTo: completed");
                Complete(order.Id);
            }
            else
            {
                BridgePlugin.Log.LogWarning($"[CU-MCP] MoveTo: failed after {MAX_RETRIES} retries, did not reach target ({targetPos.x:F1},{targetPos.y:F1})");
                Fail($"Failed to reach target ({targetX:F1},{targetY:F1}) after {MAX_RETRIES} retries");
            }
        }

        private List<Pathfinder.PathWaypoint> BuildSimpleWaypoints(List<Vector2> path)
        {
            var wps = new List<Pathfinder.PathWaypoint>(path.Count);
            foreach (var p in path) wps.Add(new Pathfinder.PathWaypoint { World = p, Airborne = false, JumpDir = null });
            return wps;
        }

        // Crouch/stand runtime decision:
        //  - Crouch is driven by the PASSAGE AHEAD OR BEHIND (either side of the body): if the
        //    space on either side is 4 blocks high or less, crouch now so you fit through it.
        //  - Stand up is driven by the space ABOVE your head: once there is >=5 blocks of
        //    clearance overhead, you can stand back up.
        //  - Otherwise keep the current stance.
        private bool ShouldCrouch(Body body)
        {
            var wg = UnityEngine.Object.FindObjectOfType<WorldGeneration>();
            if (wg == null || !wg.worldExists) return false;

            Vector2 pos = body.transform.position;
            float dir = body.rb != null ? Mathf.Sign(body.rb.velocity.x) : 0f;
            if (dir == 0f) dir = body.moveDir.x;

            // Crouch if the passage in front OR behind is <=4 high, so the body fits through
            // tight spaces regardless of which way it is travelling/backing.
            if (HeadroomAt(wg, pos + new Vector2(dir * 1f, 0f)) <= 4
                || HeadroomAt(wg, pos + new Vector2(-dir * 1f, 0f)) <= 4) return true;
            if (HeadroomAt(wg, pos) >= 5) return false;
            return !body.standing;
        }

        private static int HeadroomAt(WorldGeneration wg, Vector2 pos)
        {
            var b = wg.WorldToBlockPos(pos);
            int surfaceY = int.MinValue;
            for (int yy = b.y; yy >= b.y - 30; yy--)
            {
                if (IsSolidBlock(wg, b.x, yy)) { surfaceY = yy + 1; break; }
            }
            if (surfaceY == int.MinValue) return 12;

            int hr = 0;
            for (int yy = surfaceY; yy < surfaceY + 12; yy++)
            {
                if (IsSolidBlock(wg, b.x, yy)) break;
                hr++;
            }
            return hr;
        }

        private static bool IsSolidBlock(WorldGeneration wg, int x, int y)
        {
            try
            {
                ushort id = wg.GetBlock(new UnityEngine.Vector2Int(x, y));
                if (id == 0) return false;
                var info = wg.GetBlockInfo(id);
                return info != null && !string.IsNullOrEmpty(info.name) && info.name != "air";
            }
            catch { return true; }
        }

        private int CountJumps(List<Pathfinder.PathWaypoint> wps)
        {
            int n = 0;
            foreach (var w in wps) if (w.JumpDir.HasValue) n++;
            return n;
        }

        private float ComputeJumpVxRatio(Body body, List<Vector2> path, List<Pathfinder.PathWaypoint> wps, int takeoffIndex, int targetIndex)
        {
            if (body.rb == null || body.actualMaxSpeed <= 0f) return Mathf.Sign(wps[takeoffIndex].JumpDir.Value.x);

            Vector2 takeoff = path[takeoffIndex];
            Vector2 landing;
            if (targetIndex >= 0 && targetIndex < path.Count)
            {
                landing = path[targetIndex];
            }
            else
            {
                landing = takeoff;
                for (int k = takeoffIndex + 1; k < path.Count; k++)
                {
                    if (!wps[k].Airborne) { landing = path[k]; break; }
                }
            }

            float a = Mathf.Abs(Physics2D.gravity.y * body.rb.gravityScale);
            if (a < 0.1f) a = 9.81f;
            float vy = body.actualJumpSpeed;
            float dxLand = landing.x - takeoff.x;
            float dyLand = landing.y - takeoff.y;
            float disc = vy * vy - 2f * a * dyLand;
            float t = (vy + Mathf.Sqrt(Mathf.Max(0f, disc))) / a;
            if (t < 0.01f) return Mathf.Sign(dxLand != 0f ? dxLand : wps[takeoffIndex].JumpDir.Value.x);

            float vx = dxLand / t;
            float ratio = vx / body.actualMaxSpeed;
            return Mathf.Clamp(ratio, -1.2f, 1.2f);
        }

        private IEnumerator UseItemRoutine(OrderParams order)
        {
            string itemId = order.Parameters["item"]?.ToString();
            var body = AIPlayerManager.GetActiveBody();
            if (body == null) { Fail("Body not found"); yield break; }

            for (int i = 0; i < body.slots.Length; i++)
            {
                var slot = body.slots[i];
                if (slot != null)
                {
                    var item = slot.GetComponent<Item>();
                    if (item != null && item.id == itemId)
                    {
                        body.slots[i].GetComponent<Item>();
                        body.UseItemInHand();
                        yield return new WaitForSeconds(1f);
                        Complete(order.Id);
                        yield break;
                    }
                }
            }
            Fail($"Item '{itemId}' not found");
        }

        private IEnumerator WaitRoutine(OrderParams order)
        {
            float duration = order.Parameters["seconds"]?.ToObject<float>() ?? 1f;
            yield return new WaitForSeconds(duration);
            Complete(order.Id);
        }

        private IEnumerator JumpRoutine(OrderParams order)
        {
            var body = AIPlayerManager.GetActiveBody();
            if (body == null) { Fail("Body not found"); yield break; }

            float holdTime = order.Parameters["hold_seconds"]?.ToObject<float>() ?? 0f;
            float dirX = order.Parameters["x"]?.ToObject<float>() ?? 0f;

            if (dirX != 0f)
            {
                BodyMoveDirOverride.OverrideMoveDir = new Vector2(Mathf.Sign(dirX), 0f);
            }

            body.Jump();

            if (holdTime > 0f)
            {
                yield return new WaitForSeconds(holdTime);
            }
            else
            {
                yield return new WaitForFixedUpdate();
            }

            BodyMoveDirOverride.OverrideMoveDir = null;
            BodyMoveDirOverride.IsCrouching = false;
            Complete(order.Id);
        }

        private void Complete(string id)
        {
            _currentOrder = null;
            _activeCoroutine = null;
            OnOrderCompleted?.Invoke(id);
        }

        private IEnumerator FollowRoutine(OrderParams order)
        {
            var body = AIPlayerManager.GetActiveBody();
            if (body == null) { Fail("Body not found"); yield break; }

            float followDistance = order.Parameters["distance"]?.ToObject<float>() ?? 2.0f;
            float maxYDiff = order.Parameters["max_ydiff"]?.ToObject<float>() ?? 3f;

            while (_currentOrder != null && _activeCoroutine != null)
            {
                var humanBody = AIPlayerManager.HumanBody;
                if (humanBody == null) { yield return new WaitForSeconds(0.5f); continue; }

                // Periodically move toward player using normal MoveTo logic
                Vector2 targetPos = humanBody.transform.position;
                targetPos.x -= followDistance;

                float dx = targetPos.x - body.transform.position.x;
                float dy = targetPos.y - body.transform.position.y;
                float dist = Mathf.Sqrt(dx * dx + dy * dy);

                if (dist > 2.0f)
                {
                    // Use rb.velocity directly like MoveTo does
                    float dirX = dx > 0 ? 1f : -1f;
                    if (body.rb != null && body.grounded && Mathf.Abs(dy) < maxYDiff)
                    {
                        body.rb.velocity = new Vector2(dirX * body.actualMaxSpeed, body.rb.velocity.y);
                        BodyMoveDirOverride.OverrideMoveDir = new Vector2(dirX, 0f);
                    }
                    else
                    {
                        BodyMoveDirOverride.OverrideMoveDir = null;
                    }

                    if (body.grounded && dist > 5f && Mathf.Abs(dy) > 1f)
                    {
                        body.Jump();
                    }
                }

                yield return new WaitForSeconds(0.3f);
            }

            BodyMoveDirOverride.OverrideMoveDir = null;
            Complete(order.Id);
        }

        private void HandleCreateAI(OrderParams order)
        {
            var humanBody = AIPlayerManager.HumanBody;
            if (humanBody == null) { Fail("Body not found"); return; }

            Vector2 pos = humanBody.transform.position;
            pos.x += order.Parameters["offset_x"]?.ToObject<float>() ?? 2.0f;
            BridgePlugin.Log?.LogInfo($"[CU-MCP] CreateAI: human=({humanBody.transform.position.x:F1},{humanBody.transform.position.y:F1}) target=({pos.x:F1},{pos.y:F1})");

            var ai = AIPlayerManager.CreateAI(pos);
            if (ai != null)
                Complete(order.Id);
            else
                Fail("Failed to create AI player");
        }

        private void HandleDestroyAI(OrderParams order)
        {
            AIPlayerManager.DestroyAI();
            Complete(order.Id);
        }

        private void HandleHealAI(OrderParams order)
        {
            var aiBody = AIPlayerManager.AIBody;
            if (aiBody == null) { Fail("AI body not found"); return; }

            var humanBody = AIPlayerManager.HumanBody;
            if (humanBody == null) { Fail("Human body not found"); return; }

            string limbName = order.Parameters["limb"]?.ToString();
            string itemId = order.Parameters["item_id"]?.ToString();

            if (string.IsNullOrEmpty(limbName) || string.IsNullOrEmpty(itemId))
            {
                Fail("Parameters 'limb' and 'item_id' are required");
                return;
            }

            try
            {
                // Find the limb on AI body
                Limb targetLimb = null;
                foreach (var limb in aiBody.limbs)
                {
                    if (limb != null && limb.name == limbName)
                    {
                        targetLimb = limb;
                        break;
                    }
                }
                if (targetLimb == null)
                {
                    Fail($"Limb '{limbName}' not found on AI body");
                    return;
                }

                // Find the item in human's inventory
                Item targetItem = null;
                foreach (var slot in humanBody.slots)
                {
                    if (slot != null)
                    {
                        var item = slot.GetComponent<Item>();
                        if (item != null && item.id == itemId)
                        {
                            targetItem = item;
                            break;
                        }
                    }
                }
                if (targetItem == null)
                {
                    Fail($"Item '{itemId}' not found in human inventory");
                    return;
                }

                // Check item is usable on limbs
                if (!targetItem.Stats.usableOnLimb)
                {
                    Fail($"Item '{itemId}' is not usable on limbs");
                    return;
                }

                // Check limb is not dismembered
                if (targetLimb.dismembered)
                {
                    Fail($"Limb '{limbName}' is dismembered");
                    return;
                }

                // Apply wound item directly (bypass multiplayer network path)
                targetItem.Stats.useLimbAction.Invoke(targetLimb, targetItem);

                BridgePlugin.Log?.LogInfo($"[CU-MCP] Healed AI: limb={limbName} item={itemId}");
                Complete(order.Id);
            }
            catch (System.Exception e)
            {
                BridgePlugin.Log?.LogError($"[CU-MCP] HealAI failed: {e.Message}");
                Fail($"HealAI failed: {e.Message}");
            }
        }

        private void Fail(string reason)
        {
            if (_currentOrder != null)
                OnOrderFailed?.Invoke(_currentOrder.Id, reason);
            _currentOrder = null;
            _activeCoroutine = null;
        }
    }
}
