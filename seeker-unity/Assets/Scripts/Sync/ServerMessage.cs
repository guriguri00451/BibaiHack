using System;
using Newtonsoft.Json.Linq;
using Seeker.Ar;
using UnityEngine;

namespace Seeker.Sync
{
    /// <summary>仕様 §6.1 の WSMessage。round / seq / timestamp はサーバー配信メッセージにだけ付く。</summary>
    public sealed class ServerMessage
    {
        public string Type { get; }
        public long? Round { get; }
        public long? Seq { get; }
        public JObject Payload { get; }

        private ServerMessage(string type, long? round, long? seq, JObject payload)
        {
            Type = type;
            Round = round;
            Seq = seq;
            Payload = payload;
        }

        public static ServerMessage Parse(string json)
        {
            var root = JObject.Parse(json);
            var type = (string)root["type"];
            if (string.IsNullOrEmpty(type)) throw new FormatException("WSMessage に type が無い");
            return new ServerMessage(type, (long?)root["round"], (long?)root["seq"], root["payload"] as JObject ?? new JObject());
        }

        /// <summary>{"position":[x,y,z],"rotation":[x,y,z,w],"scale":[x,y,z]} を共有マップ座標のまま読む。</summary>
        public static MapTransform ParseTransform(JToken token)
        {
            var position = ReadFloats(token["position"], 3, "position");
            var rotation = ReadFloats(token["rotation"], 4, "rotation");
            var scale = ReadFloats(token["scale"], 3, "scale");
            var quaternion = new Quaternion(rotation[0], rotation[1], rotation[2], rotation[3]);
            // サーバーの値域検証を信用しすぎない。零クォータニオンは NaN を連鎖させるので弾く
            if (quaternion.x * quaternion.x + quaternion.y * quaternion.y + quaternion.z * quaternion.z + quaternion.w * quaternion.w < 1e-8f)
                throw new FormatException("rotation がゼロクォータニオン");
            return new MapTransform(
                new Vector3(position[0], position[1], position[2]),
                quaternion,
                new Vector3(scale[0], scale[1], scale[2]));
        }

        private static float[] ReadFloats(JToken token, int length, string name)
        {
            if (!(token is JArray array) || array.Count != length)
                throw new FormatException($"transform.{name} は長さ {length} の配列である必要がある");
            var values = new float[length];
            for (var i = 0; i < length; i++) values[i] = (float)array[i];
            return values;
        }
    }
}
