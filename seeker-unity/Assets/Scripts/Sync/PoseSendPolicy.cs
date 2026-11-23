using System;
using Newtonsoft.Json;
using Seeker.Ar;
using UnityEngine;

namespace Seeker.Sync
{
    /// <summary>
    /// ケイの Pose を player.transform として送るかどうかの判断とメッセージ生成。時刻は注入できる。
    /// </summary>
    public sealed class PoseSendPolicy
    {
        public const float SendHz = 15f;
        private static readonly long MinIntervalMs = (long)Math.Ceiling(1000.0 / SendHz);

        private readonly Func<long> nowMs;
        private long nextClientSeq;
        private long? lastSentAtMs;

        /// <summary>
        /// clientSeq の起点は生成時の Unix ms。サーバーは前回以下の clientSeq を捨て、再接続・ラウンドをまたいでも覚えているため、
        /// 再起動で 0 に戻せない。サーバーの z.int() は zod 4 の安全整数（±2^53-1）で、Unix ms（約 1.7e12）は収まる
        /// （Int32 には収まらないので long を使う）。送信は最大 15 回/秒 &lt; 1000 回/秒なので、
        /// 再起動後の起点（その時の Unix ms）は前回の「起点 + 送信回数」を必ず上回る。
        /// </summary>
        public PoseSendPolicy(Func<long> nowMs)
        {
            this.nowMs = nowMs;
            nextClientSeq = nowMs();
        }

        /// <summary>
        /// 送ってよい状態で、前回送信から十分間隔が空いているときだけ JSON を返す。
        /// readPose は送ると決まってから呼ぶ（未較正で CoordinateAdapter を呼ぶと例外になるため）。
        /// </summary>
        public bool TryCreateMessage(bool isCalibrated, bool isTracking, bool isPlaying, Func<MapTransform> readPose, out string json)
        {
            json = null;
            if (!isCalibrated || !isTracking || !isPlaying) return false;
            var now = nowMs();
            if (lastSentAtMs.HasValue && now - lastSentAtMs.Value < MinIntervalMs) return false;

            var pose = readPose();
            var rotation = Normalize(pose.rotation);
            lastSentAtMs = now;
            json = JsonConvert.SerializeObject(new
            {
                type = "player.transform",
                payload = new
                {
                    clientSeq = nextClientSeq++,
                    transform = new
                    {
                        position = new[] { pose.position.x, pose.position.y, pose.position.z },
                        rotation = new[] { rotation.x, rotation.y, rotation.z, rotation.w },
                        scale = new[] { 1f, 1f, 1f },
                    },
                },
            });
            return true;
        }

        // サーバーは長さのずれが 0.02 を超えると拒否するので、送る直前に必ず揃える
        private static Quaternion Normalize(Quaternion q)
        {
            var magnitude = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (magnitude < 1e-6f) return Quaternion.identity;
            return new Quaternion(q.x / magnitude, q.y / magnitude, q.z / magnitude, q.w / magnitude);
        }
    }
}
