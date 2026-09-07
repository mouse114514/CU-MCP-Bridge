using System;
using System.Collections.Concurrent;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using CUMCP.Transport;
using CUMCP.Collector;
using CUMCP.Executor;
using CUMCP.Contingency;
using CUMCP.Protocol;

namespace CUMCP
{
    [BepInPlugin("com.cuMCPBridge", "CU-MCP-Bridge Mod", "0.1.0")]
    public class BridgePlugin : BaseUnityPlugin
    {
        internal static BridgePlugin Instance;
        internal new static BepInEx.Logging.ManualLogSource Log => Instance?.Logger;

        private HttpBridgeClient _pipe;
        private DataCollector _collector;
        private OrderExecutor _executor;
        private InterruptDetector _interruptDetector;
        private ContingencyRunner _contingency;

        // Transport callbacks fire on background threads; Unity API calls must run
        // on the main thread, so inbound messages are parked here and drained in Update().
        private readonly ConcurrentQueue<Message> _mainThreadQueue = new ConcurrentQueue<Message>();

        private float _tickTimer;
        private float _watchdogTimer;
        private const float TICK_INTERVAL = 0.5f;
        private const float WATCHDOG_INTERVAL = 10f;

        private DebugGUI _debugGui;

        void Awake()
        {
            Instance = this;
            Transport.HttpBridgeClient.Log = (msg) => Logger.LogInfo("[CU-MCP] " + msg);

            _debugGui = gameObject.AddComponent<DebugGUI>();

            Harmony.CreateAndPatchAll(typeof(Executor.BodyMovementPatch));
            Harmony.CreateAndPatchAll(typeof(Executor.BodyMoveDirOverride));
            Harmony.CreateAndPatchAll(typeof(AIPlayerManager.BodyStartPatch));
            Harmony.CreateAndPatchAll(typeof(Executor.WoundViewAIPatch));
            Harmony.CreateAndPatchAll(typeof(Executor.WallJumpTrigger.Patch));

            _pipe = new HttpBridgeClient(MCPConfig.Instance.http_url);
            _collector = new DataCollector(_pipe);
            _executor = new OrderExecutor(_pipe, this);
            _interruptDetector = new InterruptDetector(_pipe);
            _contingency = new ContingencyRunner();

            _executor.OnOrderCompleted += (id) => _pipe.Send(MessageBuilder.OrderResult(id, true));
            _executor.OnOrderFailed += (id, reason) => _pipe.Send(MessageBuilder.OrderResult(id, false, reason));

            _pipe.OnError += (err) => Logger.LogWarning($"[CU-MCP] Transport: {err}");
            _pipe.OnDisconnected += () => Logger.LogInfo("[CU-MCP] Transport disconnected");
            _pipe.OnMessage += HandleMessage;

            Logger.LogInfo("[CU-MCP] Plugin loaded, version 0.1.0");
        }

        void Start()
        {
            Logger.LogInfo("[CU-MCP] Start()");
            _pipe.ConnectAsync();
        }

        void LateUpdate()
        {
            if (Executor.BodyMoveDirOverride.OverrideMoveDir.HasValue)
            {
                var body = AIPlayerManager.GetActiveBody();
                if (body != null)
                {
                    body.moveDir = Executor.BodyMoveDirOverride.OverrideMoveDir.Value;
                }
            }
        }

        void Update()
        {
            while (_mainThreadQueue.TryDequeue(out var queued))
                ProcessMessageOnMainThread(queued);

            _tickTimer += Time.deltaTime;
            _watchdogTimer += Time.deltaTime;

            if (_watchdogTimer >= WATCHDOG_INTERVAL)
            {
                _watchdogTimer = 0;
                if (_pipe.Connected)
                {
                    long elapsed = DateTime.UtcNow.Ticks - _pipe.LastReadTicks;
                    if (elapsed > TimeSpan.TicksPerSecond * 30)
                    {
                        Logger.LogWarning("[CU-MCP] Health: no data for 30s, reconnecting...");
                        _pipe.Disconnect();
                        _pipe.ConnectAsync();
                    }
                }
                else
                {
                    _pipe.ConnectAsync();
                }
            }

            AIPlayerManager.Tick();

            if (_tickTimer < TICK_INTERVAL) return;
            _tickTimer = 0;

            if (!_pipe.Connected) return;
            _collector.Tick();
            _interruptDetector.Tick();
            _contingency.Tick();
        }

        void OnDestroy()
        {
            _pipe?.Disconnect();
        }

        // Background thread: just hand off to the main thread.
        private void HandleMessage(Message msg)
        {
            _mainThreadQueue.Enqueue(msg);
        }

        private void ProcessMessageOnMainThread(Message msg)
        {
            switch (msg.Type)
            {
                case "order":
                    var order = msg.GetData<OrderParams>();
                    if (order != null) _executor.ExecuteOrder(order);
                    break;

                case "query":
                    HandleQuery(msg);
                    break;

                case "search":
                    HandleSearch(msg);
                    break;

                case "contingency_update":
                    var rulesData = msg.Data?["rules"];
                    if (rulesData != null)
                    {
                        var rules = rulesData.ToObject<System.Collections.Generic.List<ContingencyRule>>();
                        _contingency.UpdateRules(rules);
                    }
                    break;

                case "contingency_clear":
                    _contingency.ClearRules();
                    break;

                case "ping":
                    _pipe.Send(MessageBuilder.Ack(msg.Seq, true));
                    break;
            }
        }

        private void HandleQuery(Message msg)
        {
            var data = msg.Data;
            if (data == null) { BridgePlugin.Log.LogInfo("[CU-MCP] Query: data null"); return; }
            float qx = data["x"]?.ToObject<float>() ?? 0;
            float qy = data["y"]?.ToObject<float>() ?? 0;
            float qrange = data["range"]?.ToObject<float>() ?? 20f;
            string playerTarget = data["player"]?.ToString();
            BridgePlugin.Log.LogInfo($"[CU-MCP] Query at ({qx},{qy}) range={qrange} player={playerTarget}");

            // Select which body to get state for
            Body targetBody = null;
            if (playerTarget == "human")
                targetBody = AIPlayerManager.HumanBody;
            else if (playerTarget == "ai")
                targetBody = AIPlayerManager.AIBody;
            else
                targetBody = AIPlayerManager.GetActiveBody();

            // Collect player state
            Protocol.PlayerState playerState = null;
            if (targetBody != null)
            {
                var snap = new Collector.PlayerSnapshot();
                playerState = snap.Collect(targetBody);
            }

            // If no explicit position, use target body's position
            bool hasExplicitPos = data["x"] != null && data["y"] != null;
            if (!hasExplicitPos && targetBody != null)
            {
                var pos = targetBody.transform.position;
                qx = pos.x;
                qy = pos.y;
            }

            var scan = new Collector.EnvironmentScan();
            var result = new Protocol.EnvironmentSnapshot
            {
                Entities = new System.Collections.Generic.List<Protocol.EntityInfo>(),
                NearbyTerrain = new System.Collections.Generic.List<Protocol.TerrainBlock>()
            };
            scan.ScanAtPosition(new Vector2(qx, qy), qrange, result);
            BridgePlugin.Log.LogInfo($"[CU-MCP] Query result: {result.Entities.Count} ents, {result.NearbyTerrain.Count} blocks");
            _pipe.Send(MessageBuilder.StateUpdate(new { player = playerState, environment = result, query_x = qx, query_y = qy }));
        }

        private void HandleSearch(Message msg)
        {
            var data = msg.Data;
            if (data == null) return;

            string material = data["material"]?.ToString() ?? "";
            float sx = data["x"]?.ToObject<float>() ?? 0f;
            float sy = data["y"]?.ToObject<float>() ?? 0f;
            int range = data["range"]?.ToObject<int>() ?? 100;
            int limit = data["limit"]?.ToObject<int>() ?? 20;

            var body = AIPlayerManager.GetActiveBody();
            Vector2 center = body != null ? (Vector2)body.transform.position : new Vector2(sx, sy);
            if (data["x"] != null && data["y"] != null)
                center = new Vector2(sx, sy);

            var searchResult = new Collector.EnvironmentScan().SearchBlocks(material, center, range, limit);
            BridgePlugin.Log.LogInfo($"[CU-MCP] Search '{material}' -> {searchResult?.TotalMatched ?? 0} match(es)");
            _pipe.Send(MessageBuilder.StateUpdate(new { search_result = searchResult }));
        }

        public HttpBridgeClient GetPipe() => _pipe;
        public OrderExecutor GetExecutor() => _executor;
    }
}
