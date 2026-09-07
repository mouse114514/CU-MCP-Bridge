using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CUMCP.Protocol
{
    public enum MessageType
    {
        StateUpdate,
        Interrupt,
        Order,
        ContingencyUpdate,
        Ack,
        Error
    }

    public class Message
    {
        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("seq")]
        public long Seq { get; set; }

        [JsonProperty("timestamp")]
        public double Timestamp { get; set; }

        [JsonProperty("data")]
        public JObject Data { get; set; }

        public Message() { Timestamp = DateTime.UtcNow.Ticks / 10000000.0; }

        public static Message FromJson(string json) =>
            JsonConvert.DeserializeObject<Message>(json);

        public string ToJson() => JsonConvert.SerializeObject(this);

        public T GetData<T>() where T : class =>
            Data?.ToObject<T>();
    }

    public static class MessageBuilder
    {
        private static long _seq;

        public static Message StateUpdate(object data) => new Message
        {
            Type = "state_update",
            Seq = System.Threading.Interlocked.Increment(ref _seq),
            Data = JObject.FromObject(data)
        };

        public static Message Interrupt(string reason, int priority, object data) => new Message
        {
            Type = "interrupt",
            Seq = System.Threading.Interlocked.Increment(ref _seq),
            Data = JObject.FromObject(new { reason, priority, data })
        };

        public static Message Order(string id, string action, object parameters, object pipeline = null) => new Message
        {
            Type = "order",
            Seq = System.Threading.Interlocked.Increment(ref _seq),
            Data = JObject.FromObject(new { id, action, parameters, pipeline })
        };

        public static Message Ack(long ackSeq, bool success, string error = null) => new Message
        {
            Type = "ack",
            Seq = System.Threading.Interlocked.Increment(ref _seq),
            Data = JObject.FromObject(new { ack_seq = ackSeq, success, error })
        };

        public static Message OrderResult(string id, bool success, string error = null) => new Message
        {
            Type = "order_result",
            Seq = System.Threading.Interlocked.Increment(ref _seq),
            Data = JObject.FromObject(new { id, success, error })
        };
    }

    public class PlayerState
    {
        public PlayerPosition Position;
        public float Health;
        public float MaxHealth;
        public float Temperature;
        public float Oxygen;
        public List<string> Items;
        public string CurrentAction;
    }

    public class PlayerPosition
    {
        public float X, Y;
    }

    public class EnvironmentSnapshot
    {
        public List<EntityInfo> Entities;
        public List<TerrainBlock> NearbyTerrain;
    }

    public class EntityInfo
    {
        public string Id;
        public string Type;
        public float X, Y;
        public float Distance;
        public float Health;
    }

    public class TerrainBlock
    {
        public int TileX, TileY;
        public string Material;
    }

    public class OrderParams
    {
        [JsonProperty("id")]
        public string Id;
        [JsonProperty("action")]
        public string Action;
        [JsonProperty("parameters")]
        public JObject Parameters;
        [JsonProperty("pipeline")]
        public PipelineInfo Pipeline;
    }

    public class PipelineInfo
    {
        public string InputFrom;
        public string OutputTo;
        public Dictionary<string, object> Variables;
    }

    public class SearchResults
    {
        public string MaterialFilter;
        public List<SearchMatch> Matches;
        public int TotalScanned;
        public int TotalMatched;
    }

    public class SearchMatch
    {
        public int TileX, TileY;
        public string Material;
        public float WorldX, WorldY;
    }

    public class ContingencyRule
    {
        [JsonProperty("condition")]
        public string Condition;
        [JsonProperty("action")]
        public string Action;
        [JsonProperty("parameters")]
        public JObject Parameters;
    }
}
