using System;
using UnityEngine;

namespace Seeker.Ar
{
    /// <summary>
    /// <c>GET /v1/maps/{mapId}</c> のレスポンス。共有マップ座標系（右手系・Y-up・メートル）で書かれている。
    /// API 仕様にレスポンス形が未定義のため、オブジェクト作成の transform（§5.1）に揃えた暫定スキーマ。
    /// </summary>
    [Serializable]
    public sealed class MapDefinition
    {
        public string mapId;
        public string name;
        /// <summary>GLB の URL。相対パスなら呼び出し側が StreamingAssets 配下として解決する。</summary>
        public string modelUrl;
        public TransformJson transform;

        public static MapDefinition Parse(string json)
        {
            var definition = JsonUtility.FromJson<MapDefinition>(json);
            if (definition == null || string.IsNullOrEmpty(definition.modelUrl))
                throw new FormatException("map definition has no modelUrl");
            return definition;
        }

        /// <summary>transform が省略されたマップは、マップ原点にそのまま置く。</summary>
        public MapTransform ToMapTransform() => transform?.ToMapTransform() ?? MapTransform.Identity;

        [Serializable]
        public sealed class TransformJson
        {
            public float[] position;
            public float[] rotation;
            public float[] scale;

            public MapTransform ToMapTransform() => new MapTransform(
                ReadVector(position, nameof(position), Vector3.zero),
                ReadQuaternion(rotation),
                ReadVector(scale, nameof(scale), Vector3.one));

            private static Vector3 ReadVector(float[] values, string field, Vector3 fallback)
            {
                if (values == null || values.Length == 0) return fallback;
                if (values.Length != 3) throw new FormatException($"transform.{field} must have 3 elements but had {values.Length}");
                return new Vector3(values[0], values[1], values[2]);
            }

            private static Quaternion ReadQuaternion(float[] values)
            {
                if (values == null || values.Length == 0) return Quaternion.identity;
                if (values.Length != 4) throw new FormatException($"transform.rotation must have 4 elements but had {values.Length}");
                return new Quaternion(values[0], values[1], values[2], values[3]).Normalized();
            }
        }
    }
}
