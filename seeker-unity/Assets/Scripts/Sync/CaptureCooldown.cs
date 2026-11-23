using System;

namespace Seeker.Sync
{
    /// <summary>
    /// 捕獲の連打防止。サーバーは1人 1.5 秒に 1 回までなので、クライアントでも同じ間隔は送らない。
    /// 429 の Retry-After が来たらその分だけ待つ。時刻は秒（単調増加）を注入するのでテストで固定できる。
    /// </summary>
    public sealed class CaptureCooldown
    {
        public const double DefaultIntervalSeconds = 1.5;

        private readonly Func<double> nowSeconds;
        private readonly double intervalSeconds;
        private double readyAt = double.NegativeInfinity;

        public CaptureCooldown(Func<double> nowSeconds, double intervalSeconds = DefaultIntervalSeconds)
        {
            this.nowSeconds = nowSeconds;
            this.intervalSeconds = intervalSeconds;
        }

        public bool CanSend => nowSeconds() >= readyAt;

        public void MarkSent() => readyAt = nowSeconds() + intervalSeconds;

        /// <summary>サーバーの Retry-After 秒。クライアント側の間隔より短くはしない。</summary>
        public void ApplyRetryAfter(double seconds) => readyAt = Math.Max(readyAt, nowSeconds() + seconds);
    }
}
