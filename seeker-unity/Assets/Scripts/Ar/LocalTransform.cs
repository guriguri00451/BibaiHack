using UnityEngine;

namespace Seeker.Ar
{
    /// <summary>端末ローカル（Unity ワールド）側の Transform。左手系・Y-up・メートル。</summary>
    public readonly struct LocalTransform
    {
        public readonly Vector3 position;
        public readonly Quaternion rotation;
        public readonly Vector3 scale;

        public LocalTransform(Vector3 position, Quaternion rotation, Vector3 scale)
        {
            this.position = position;
            this.rotation = rotation;
            this.scale = scale;
        }

        public static implicit operator LocalTransform(Pose pose) => new LocalTransform(pose.position, pose.rotation, Vector3.one);
    }
}
