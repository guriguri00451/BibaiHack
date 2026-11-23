using Newtonsoft.Json.Linq;

namespace Seeker.Sync
{
    public enum CaptureStatus
    {
        Caught,
        Miss,
        Error,
    }

    /// <summary>
    /// POST /sessions/{id}/captures の応答を解釈した結果。形は server/src/contracts/rest.ts が正:
    ///   200: { result: "caught" | "miss", scoreDelta, score, caughtPlayerId? }
    ///   エラー: { error: { code, message } }。429 は Retry-After（秒）ヘッダ付き。
    /// エラー本文（message）は画面に出さず、コードごとの固定文言に変換する。
    /// </summary>
    public readonly struct CaptureResult
    {
        public const string GenericErrorMessage = "捕獲に失敗しました";

        public CaptureStatus Status { get; }
        public int ScoreDelta { get; }
        public int Score { get; }
        public string CaughtPlayerId { get; }
        /// <summary>画面に出す短い日本語。</summary>
        public string Message { get; }
        /// <summary>429 のとき、次に送れるまでの秒数。それ以外は null。</summary>
        public double? RetryAfterSeconds { get; }

        private CaptureResult(CaptureStatus status, int scoreDelta, int score, string caughtPlayerId, string message, double? retryAfterSeconds)
        {
            Status = status;
            ScoreDelta = scoreDelta;
            Score = score;
            CaughtPlayerId = caughtPlayerId;
            Message = message;
            RetryAfterSeconds = retryAfterSeconds;
        }

        public bool IsError => Status == CaptureStatus.Error;

        public static CaptureResult Failure(string message, double? retryAfterSeconds = null) =>
            new CaptureResult(CaptureStatus.Error, 0, 0, null, message, retryAfterSeconds);

        /// <param name="statusCode">HTTP ステータス。通信失敗は 0。</param>
        /// <param name="retryAfter">Retry-After ヘッダの秒数（無ければ null）。</param>
        public static CaptureResult Parse(long statusCode, string body, double? retryAfter)
        {
            if (statusCode == 200) return ParseSuccess(body);
            if (statusCode == 429)
            {
                var wait = retryAfter.HasValue && retryAfter.Value > 0 ? retryAfter.Value : (double?)null;
                var text = wait.HasValue ? $"連打しすぎです。{System.Math.Ceiling(wait.Value):0}秒待ってください" : "連打しすぎです。少し待ってください";
                return Failure(text, wait);
            }
            return Failure(MessageForCode(ReadErrorCode(body)));
        }

        private static CaptureResult ParseSuccess(string body)
        {
            try
            {
                var json = JObject.Parse(body);
                var resultText = (string)json["result"];
                var scoreDelta = (int?)json["scoreDelta"];
                var score = (int?)json["score"];
                if (resultText != "caught" && resultText != "miss") return Failure(GenericErrorMessage);
                if (!scoreDelta.HasValue || !score.HasValue) return Failure(GenericErrorMessage);
                var isCaught = resultText == "caught";
                return new CaptureResult(
                    isCaught ? CaptureStatus.Caught : CaptureStatus.Miss,
                    scoreDelta.Value,
                    score.Value,
                    (string)json["caughtPlayerId"],
                    isCaught ? $"見つけた！ +{scoreDelta.Value}点" : "ハズレ",
                    null);
            }
            catch (System.Exception exception) when (exception is Newtonsoft.Json.JsonException || exception is System.InvalidCastException || exception is System.FormatException)
            {
                return Failure(GenericErrorMessage);
            }
        }

        private static string ReadErrorCode(string body)
        {
            if (string.IsNullOrEmpty(body)) return null;
            try
            {
                return (string)JObject.Parse(body)["error"]?["code"];
            }
            catch (System.Exception exception) when (exception is Newtonsoft.Json.JsonException || exception is System.InvalidCastException)
            {
                return null;
            }
        }

        private static string MessageForCode(string code)
        {
            switch (code)
            {
                case "INVALID_GAME_PHASE": return "今は捕獲できません";
                case "CONFLICT": return "ほかの人が先に捕まえました";
                case "OBJECT_NOT_FOUND": return "そのオブジェクトはもうありません";
                case "FORBIDDEN_ROLE": return "ケイだけが捕獲できます";
                default: return GenericErrorMessage;
            }
        }
    }
}
