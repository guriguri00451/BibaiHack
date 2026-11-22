using UnityEngine;

namespace Seeker.Ar
{
    /// <summary>
    /// 仕様 §3.2 の Transform。共有マップ座標系 = 右手系・Y-up・メートル。
    /// Unity は左手系なので、この型の値をそのままシーンに入れてはいけない（CoordinateAdapter を通す）。
    /// </summary>
    public readonly struct MapTransform
    {
        public readonly Vector3 position;
        public readonly Quaternion rotation;
        public readonly Vector3 scale;

        public MapTransform(Vector3 position, Quaternion rotation, Vector3 scale)
        {
            this.position = position;
            this.rotation = rotation;
            this.scale = scale;
        }

        public static MapTransform Identity => new MapTransform(Vector3.zero, Quaternion.identity, Vector3.one);
    }
}
