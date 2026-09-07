using NUnit.Framework;
using CUMCP.Protocol;

namespace CUMCP.Tests.Transport
{
    [TestFixture]
    public class ProtocolTests
    {
        [Test]
        public void Message_SerializeDeserialize_RoundTrip()
        {
            var original = MessageBuilder.StateUpdate(new
            {
                player = new { health = 85.5f, position = new { x = 10f, y = -20f } }
            });

            string json = original.ToJson();
            var deserialized = Message.FromJson(json);

            Assert.AreEqual("state_update", deserialized.Type);
            Assert.AreEqual(original.Seq, deserialized.Seq);
            Assert.IsNotNull(deserialized.Data);
        }

        [Test]
        public void MessageBuilder_Interrupt_SetsCorrectType()
        {
            var msg = MessageBuilder.Interrupt("health_critical", 9, new { health = 15f });
            Assert.AreEqual("interrupt", msg.Type);
        }

        [Test]
        public void MessageBuilder_Order_SetsCorrectType()
        {
            var msg = MessageBuilder.Order("ord_001", "move_to", new { x = 50f, y = -100f });
            Assert.AreEqual("order", msg.Type);
            Assert.IsNotNull(msg.Data);
        }

        [Test]
        public void MessageBuilder_Ack_SetsSuccess()
        {
            var msg = MessageBuilder.Ack(42, true);
            Assert.AreEqual("ack", msg.Type);
            var data = msg.GetData<AckData>();
            Assert.IsNotNull(data);
            Assert.AreEqual(42, data.ack_seq);
            Assert.IsTrue(data.success);
        }

        [Test]
        public void InterruptMessage_HasPriorityField()
        {
            var msg = MessageBuilder.Interrupt("test", 7, new { source = "test" });
            Assert.AreEqual("interrupt", msg.Type);
            Assert.AreEqual(7, msg.Data["priority"]?.ToObject<int>());
        }
    }

    public class AckData
    {
        public long ack_seq;
        public bool success;
        public string error;
    }
}
