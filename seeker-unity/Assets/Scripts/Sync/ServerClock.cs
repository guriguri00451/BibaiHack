using System;

namespace Seeker.Sync
{
    /// <summary>
    /// 端末時計とサーバー時計のズレを、受信メッセージの timestamp から推定する。
    /// 受信時刻は通信遅延のぶん必ず遅れる（端末時刻 − timestamp = ズレ + 遅延）ので、
    /// 観測した中で最小の値が遅延の最も小さい = 真のズレに最も近い、として採用する。
    /// </summary>
    public sealed class ServerClock
    {
        private readonly Func<long> localNowMs;
        private long? offsetMs;

        public ServerClock(Func<long> localNowMs)
        {
            this.localNowMs = localNowMs;
        }

        public static long SystemNowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        public bool IsSynced => offsetMs.HasValue;

        /// <summary>メッセージ受信の瞬間に呼ぶ。</summary>
        public void Observe(long serverTimestampMs)
        {
            var sample = localNowMs() - serverTimestampMs;
            if (!offsetMs.HasValue || sample < offsetMs.Value) offsetMs = sample;
        }

        /// <summary>推定したサーバー時刻（Unix ms）。まだ観測が無ければ端末時計のまま。</summary>
        public long ServerNowMs => localNowMs() - (offsetMs ?? 0);

        /// <summary>終了時刻までの残り ms。0 未満にはしない（0 で止め、遷移はサーバーの phase_changed を待つ）。</summary>
        public long RemainingMs(long phaseEndsAtMs) => Math.Max(0L, phaseEndsAtMs - ServerNowMs);
    }

    /// <summary>タイマー HUD の表示規則。App は asmdef が無くテストから参照できないので、純ロジックはここに置く。</summary>
    public static class TimerFormat
    {
        public const int WarningSeconds = 10;

        public static string Format(long remainingMs)
        {
            // 切り上げ: 0.5 秒残りなら 00:01 と出し、00:00 は本当に尽きたときだけ
            var totalSeconds = (int)((Math.Max(0L, remainingMs) + 999) / 1000);
            return $"{totalSeconds / 60:00}:{totalSeconds % 60:00}";
        }

        public static bool IsWarning(long remainingMs) => remainingMs <= WarningSeconds * 1000L;

        /// <summary>finished 画面の次ラウンド案内。remainingMs が null ならサイクル外（ホストが rounds/start するまで待つ）。</summary>
        public static string FormatNextRound(long? remainingMs)
        {
            if (!remainingMs.HasValue) return "ホストの開始を待っています";
            var seconds = (Math.Max(0L, remainingMs.Value) + 999) / 1000;
            return seconds > 0 ? $"次のラウンドまで {seconds} 秒" : "まもなく次のラウンドが始まります";
        }
    }
}
