using UnityEngine;
using CUMCP.Transport;
using CUMCP.Protocol;

namespace CUMCP.Executor
{
    public class InterruptDetector
    {
        private readonly HttpBridgeClient _pipe;

        public InterruptDetector(HttpBridgeClient pipe)
        {
            _pipe = pipe;
        }

        public void Tick()
        {
            var body = AIPlayerManager.GetActiveBody();
            if (body == null) return;

            float totalHealth = 0;
            foreach (var limb in body.limbs)
                totalHealth += limb.skinHealth;

            if (totalHealth < 30f)
            {
                _pipe.Send(MessageBuilder.Interrupt("health_critical", 9,
                    new { health = totalHealth, source = "low_hp" }));
            }

            if (body.temperature > 40f)
            {
                _pipe.Send(MessageBuilder.Interrupt("temperature_high", 8,
                    new { temp = body.temperature }));
            }

            if (body.temperature < 30f)
            {
                _pipe.Send(MessageBuilder.Interrupt("temperature_low", 8,
                    new { temp = body.temperature }));
            }

            if (body.bloodOxygen < 10f)
            {
                _pipe.Send(MessageBuilder.Interrupt("oxygen_low", 9,
                    new { oxygen = body.bloodOxygen }));
            }

            if (body.brainHealth <= 0f || !body.alive)
            {
                _pipe.Send(MessageBuilder.Interrupt("player_dead", 10,
                    new { reason = "Player died" }));
            }
        }
    }
}
