using System.Text;

namespace Seeker.Sync
{
    /// <summary>スコア・捕獲結果の文言。純関数なので EditMode でテストする。</summary>
    public static class ScoreText
    {
        public const string OtherSeekerCaught = "他のケイがドロを見つけた";

        /// <summary>演出に出す文言。エラーは演出しないので null。miss は scoreDelta=0 なので数字を出さない。</summary>
        public static string CaptureFeedback(CaptureResult result)
        {
            switch (result.Status)
            {
                case CaptureStatus.Caught:
                    return result.ScoreDelta > 0 ? $"見つけた！ +{result.ScoreDelta}" : "見つけた！";
                case CaptureStatus.Miss:
                    return "ハズレ";
                default:
                    return null;
            }
        }

        public static string FinishReason(string reason)
        {
            switch (reason)
            {
                case "time_up": return "時間切れ";
                case "all_caught": return "ドロを全員見つけた";
                case "host": return "ホストが終了した";
                default: return null;
            }
        }

        /// <summary>finished 画面の本文。PhasePanelsView は App 側でテストから見えないためここに置く。</summary>
        public static string BuildResult(GameResult result)
        {
            var builder = new StringBuilder("ゲーム終了\n\n");
            if (result.isCaptured.HasValue) builder.Append(result.isCaptured.Value ? "ドロを捕まえた！" : "ドロは逃げ切った");
            if (result.score.HasValue) builder.Append($"\nスコア: {result.score.Value}");
            var reasonText = FinishReason(result.reason);
            if (reasonText != null) builder.Append($"\n{reasonText}");
            return builder.ToString();
        }

        public static string Hud(int? score) => score.HasValue ? $"スコア {score.Value}" : "スコア -";
    }
}
