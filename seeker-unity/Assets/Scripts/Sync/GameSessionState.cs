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
        /// <summary>終了理由: time_up / all_caught / host。サーバーが載せなければ null。</summary>
        public readonly string reason;

        public GameResult(bool? isCaptured, int? score, string reason = null)
        {
            this.isCaptured = isCaptured;
            this.score = score;
            this.reason = reason;
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
        /// <summary>現フェーズの終了時刻（Unix ms、サーバー時刻）。waiting / finished では null。</summary>
        public long? PhaseEndsAtMs { get; private set; }
        public IReadOnlyList<string> Participants => participants;
        /// <summary>自分の playerId。snapshot の payload.you.playerId。</summary>
        public string MyPlayerId { get; private set; }
        /// <summary>自分の累計スコア。まだ分からなければ null。</summary>
        public int? MyScore { get; private set; }
        /// <summary>他のケイがドロを見つけた回数。増えたら通知を出す合図。</summary>
        public int OtherSeekerCaughtCount { get; private set; }

        /// <summary>
        /// 捕獲応答の累計 score を反映する。加算ではなく上書きなので、同じ値の player.caught が後から来ても二重にならない。
        /// </summary>
        public bool ApplyScore(int score)
        {
            if (MyScore == score) return false;
            MyScore = score;
            return true;
        }

        /// <summary>表示に影響する変化があったら true。</summary>
        public bool Apply(ServerMessage message)
        {
            if (message.Type == "player.caught") return ApplyPlayerCaught(message);
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

            if (isSnapshot) isChanged |= ReadMyScore(message.Payload);
            isChanged |= ReadParticipants(source["participants"] ?? message.Payload["participants"]);
            isChanged |= ReadResult(source["result"] ?? message.Payload["result"]);
            return isChanged;
        }

        // snapshot 直下に score は無い。players[] から you.playerId の行を探す
        private bool ReadMyScore(JObject payload)
        {
            var myId = (string)payload["you"]?["playerId"];
            var score = FindScore(payload["players"] as JArray, myId);
            if (myId == MyPlayerId && score == MyScore) return false;
            MyPlayerId = myId;
            MyScore = score;
            return true;
        }

        private static int? FindScore(JArray players, string playerId)
        {
            if (players == null || string.IsNullOrEmpty(playerId)) return null;
            foreach (var player in players)
            {
                if ((string)player["playerId"] == playerId) return (int?)player["score"];
            }
            return null;
        }

        // 自分が見つけたなら seekerScore で上書き。他のケイなら通知だけでスコアは変えない
        private bool ApplyPlayerCaught(ServerMessage message)
        {
            if (message.Round.HasValue && message.Round.Value < Round) return false;
            var by = (string)message.Payload["byPlayerId"];
            if (string.IsNullOrEmpty(by)) return false;
            if (by == MyPlayerId)
            {
                var seekerScore = (int?)message.Payload["seekerScore"];
                return seekerScore.HasValue && ApplyScore(seekerScore.Value);
            }
            OtherSeekerCaughtCount++;
            return true;
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
            var next = new GameResult((bool?)result["captured"], (int?)result["score"], (string)result["reason"]);
            if (next.isCaptured == Result.isCaptured && next.score == Result.score && next.reason == Result.reason) return false;
            Result = next;
            return true;
        }
    }
}
