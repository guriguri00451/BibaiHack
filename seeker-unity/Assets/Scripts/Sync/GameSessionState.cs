using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Seeker.Sync
{
    public enum GamePhase
    {
        Waiting,
        Preparing,
        Playing,
        Finished,
    }

    /// <summary>finished 画面に出す結果。サーバーが載せなかった項目は null のまま。</summary>
    public readonly struct GameResult
    {
        public readonly bool? isCaptured;
        public readonly int? score;

        public GameResult(bool? isCaptured, int? score)
        {
            this.isCaptured = isCaptured;
            this.score = score;
        }
    }

    /// <summary>
    /// セッションのフェーズ・ラウンド・参加者・結果の最新状態。UI から切り離した純ロジック。
    /// 形は server/src/contracts/ws.ts が正:
    ///   session.snapshot:      payload.session = { status, round, phaseEndsAt }
    ///   session.phase_changed: payload = { status, round, phaseEndsAt, result? }
    /// participants は仕様に明記が無いため、payload.session → payload 直下の順に探す想定。
    /// round はメッセージ直下を優先し、無ければ status と同じ階層を見る。
    /// </summary>
    public sealed class GameSessionState
    {
        private readonly List<string> participants = new List<string>();

        public GamePhase Phase { get; private set; } = GamePhase.Waiting;
        public long Round { get; private set; } = long.MinValue;
        public GameResult Result { get; private set; }
        /// <summary>現フェーズの終了時刻（Unix ms、サーバー時刻）。waiting では null。finished はサイクル中だけ次のラウンドの開始時刻。</summary>
        public long? PhaseEndsAtMs { get; private set; }
        public IReadOnlyList<string> Participants => participants;

        /// <summary>
        /// サイクル中（rounds/start の resultSeconds）で、サーバーが次のラウンドを自分で始める予定があるか。
        /// cycle は REST の SessionDetail にしか載らないので、finished の phaseEndsAt（= 次のラウンドの開始時刻）の有無で見分ける。
        /// 停止すると finished のまま phaseEndsAt が null の snapshot が届き、false に戻る。
        /// </summary>
        public bool IsNextRoundScheduled => Phase == GamePhase.Finished && PhaseEndsAtMs.HasValue;

        /// <summary>表示に影響する変化があったら true。</summary>
        public bool Apply(ServerMessage message)
        {
            var isSnapshot = message.Type == "session.snapshot";
            if (!isSnapshot && message.Type != "session.phase_changed") return false;

            // snapshot は payload.session 配下。phase_changed は payload 直下
            var source = isSnapshot && message.Payload["session"] is JObject session ? session : message.Payload;
            var round = message.Round ?? (long?)source["round"];
            // 古いラウンドの遅延メッセージで、進んだ画面を巻き戻さない（snapshot は再接続時の正なので常に採用）
            if (!isSnapshot && round.HasValue && round.Value < Round) return false;

            var isChanged = false;
            if (round.HasValue && round.Value != Round)
            {
                // ラウンドが変わったら前ラウンドの結果・参加者表示を持ち越さない（§4 の round++）
                Round = round.Value;
                participants.Clear();
                Result = default;
                PhaseEndsAtMs = null;
                isChanged = true;
            }

            if (TryParsePhase((string)source["status"], out var phase) && phase != Phase)
            {
                Phase = phase;
                isChanged = true;
            }

            var phaseEndsAt = ReadEpochMs(source["phaseEndsAt"]);
            if (phaseEndsAt != PhaseEndsAtMs)
            {
                PhaseEndsAtMs = phaseEndsAt;
                isChanged = true;
            }

            isChanged |= ReadParticipants(source["participants"] ?? message.Payload["participants"]);
            isChanged |= ReadResult(source["result"] ?? message.Payload["result"]);
            return isChanged;
        }

        // Newtonsoft は ISO 文字列を勝手に Date 型へ変えることがあるので、型に頼らず ToObject で読む
        private static long? ReadEpochMs(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            return token.ToObject<DateTimeOffset>().ToUnixTimeMilliseconds();
        }

        public static bool TryParsePhase(string status, out GamePhase phase)
        {
            switch (status)
            {
                case "waiting": phase = GamePhase.Waiting; return true;
                case "preparing": phase = GamePhase.Preparing; return true;
                case "playing": phase = GamePhase.Playing; return true;
                case "finished": phase = GamePhase.Finished; return true;
                default: phase = default; return false;
            }
        }

        private bool ReadParticipants(JToken token)
        {
            if (!(token is JArray array)) return false;
            var names = new List<string>();
            foreach (var item in array)
            {
                var name = item.Type == JTokenType.String ? (string)item : (string)(item["name"] ?? item["displayName"] ?? item["userId"]);
                if (!string.IsNullOrEmpty(name)) names.Add(name);
            }
            if (names.Count == participants.Count && names.TrueForAll(participants.Contains)) return false;
            participants.Clear();
            participants.AddRange(names);
            return true;
        }

        private bool ReadResult(JToken token)
        {
            if (!(token is JObject result)) return false;
            var next = new GameResult((bool?)result["captured"], (int?)result["score"]);
            if (next.isCaptured == Result.isCaptured && next.score == Result.score) return false;
            Result = next;
            return true;
        }
    }
}
