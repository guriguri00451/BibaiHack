using Seeker.Ar;
using UnityEngine;

namespace Seeker.Sync
{
    /// <summary>
    /// 10〜20Hz で届く目標値を、端末FPSの描画へなめらかに追従させる。
    /// 固定の Lerp 係数だとFPSで追従速度が変わるため、経過時間から係数を出す（指数平滑）。
    /// </summary>
    public sealed class TransformSmoother
    {
        // 配信間隔(50〜100ms)程度で追いつく値。小さいほど機敏だがカクつきが目立つ
        private const float DefaultTimeConstantSeconds = 0.08f;

        private readonly float timeConstantSeconds;
        private MapTransform current;
        private MapTransform target;
        private bool hasValue;

        public TransformSmoother(float timeConstantSeconds = DefaultTimeConstantSeconds)
        {
            this.timeConstantSeconds = timeConstantSeconds;
        }

        /// <summary>最初の目標値は補間せず即座に採用する（原点から飛んでくる演出を避ける）。</summary>
        public void SetTarget(MapTransform newTarget)
        {
            target = newTarget;
            if (hasValue) return;
            current = newTarget;
            hasValue = true;
        }

        public MapTransform Step(float deltaSeconds)
        {
            if (!hasValue) return MapTransform.Identity;
            var alpha = 1f - Mathf.Exp(-Mathf.Max(0f, deltaSeconds) / timeConstantSeconds);
            current = new MapTransform(
                Vector3.Lerp(current.position, target.position, alpha),
                Quaternion.Slerp(current.rotation, target.rotation, alpha),
                Vector3.Lerp(current.scale, target.scale, alpha));
            return current;
        }
    }
}
