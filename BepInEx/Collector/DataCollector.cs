using UnityEngine;
using CUMCP.Transport;
using CUMCP.Protocol;

namespace CUMCP.Collector
{
    public class DataCollector
    {
        public bool Enabled { get; set; } = true;

        private readonly HttpBridgeClient _pipe;
        private readonly PlayerSnapshot _playerSnap = new PlayerSnapshot();
        private readonly EnvironmentScan _envScan = new EnvironmentScan();
        private PlayerState _lastState;
        private float _lastScanTime;
        private float _lastHeartbeat;
        private const float SCAN_INTERVAL = 2f;
        private const float HEARTBEAT_INTERVAL = 5f;

        public DataCollector(HttpBridgeClient pipe)
        {
            _pipe = pipe;
        }

        public void Tick()
        {
            if (!Enabled) return;

            // Heartbeat to verify pipe alive (even if player not spawned yet)
            if (Time.time - _lastHeartbeat > HEARTBEAT_INTERVAL)
            {
                _lastHeartbeat = Time.time;
                _pipe.Send(MessageBuilder.Ack(0, true));
            }

            var body = AIPlayerManager.GetActiveBody();
            if (body == null) return;

            var state = _playerSnap.Collect(body);
            if (state == null) return;

            bool changed = _lastState == null ||
                           Mathf.Abs(_lastState.Health - state.Health) > 5f ||
                           Mathf.Abs(_lastState.Temperature - state.Temperature) > 2f ||
                           _lastState.CurrentAction != state.CurrentAction;

            if (!changed && Time.time - _lastScanTime < SCAN_INTERVAL)
                return;

            var env = _envScan.Scan(body);

            bool healthCritical = state.Health < 30f;
            bool tempCritical = state.Temperature > 40f || state.Temperature < 30f;

            object updateData;
            if (AIPlayerManager.HumanBody != null && AIPlayerManager.AIBody != null)
            {
                var humanPos = AIPlayerManager.HumanBody.transform.position;
                updateData = new {
                    player = state,
                    human = new { x = humanPos.x, y = humanPos.y },
                    environment = env
                };
            }
            else
            {
                updateData = new { player = state, environment = env };
            }
            _pipe.Send(MessageBuilder.StateUpdate(updateData));

            if (healthCritical)
                _pipe.Send(MessageBuilder.Interrupt("health_critical", 9, new { health = state.Health }));
            if (tempCritical)
                _pipe.Send(MessageBuilder.Interrupt("temperature_critical", 8, new { temp = state.Temperature }));

            _lastState = state;
            _lastScanTime = Time.time;
        }
    }
}
