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
    /// payload の形は仕様に明記が無いため、ここに閉じ込めている。想定:
    ///   session.snapshot / session.phase_changed: { status, round?, participants?: [name | { name|displayName }], result?: { captured, score } }
    /// round はメッセージ直下を優先し、無ければ payload を見る。
    /// </summary>
    public sealed class GameSessionState
    {
        private readonly List<string> participants = new List<string>();

        public GamePhase Phase { get; private set; } = GamePhase.Waiting;
        public long Round { get; private set; } = long.MinValue;
        public GameResult Result { get; private set; }
        /// <summary>セッションのマップ。a-kun-unity のホストが待機中・ラウンド後に替えると snapshot（session.mapId）で届く。未着なら null。</summary>
        public string MapId { get; private set; }
        public IReadOnlyList<string> Participants => participants;

        /// <summary>表示に影響する変化があったら true。</summary>
        public bool Apply(ServerMessage message)
        {
            var isSnapshot = message.Type == "session.snapshot";
            if (!isSnapshot && message.Type != "session.phase_changed") return false;

            var round = message.Round ?? (long?)message.Payload["round"];
            // 古いラウンドの遅延メッセージで、進んだ画面を巻き戻さない（snapshot は再接続時の正なので常に採用）
            if (!isSnapshot && round.HasValue && round.Value < Round) return false;

            var isChanged = false;
            if (round.HasValue && round.Value != Round)
            {
                // ラウンドが変わったら前ラウンドの結果・参加者表示を持ち越さない（§4 の round++）
                Round = round.Value;
                participants.Clear();
                Result = default;
                isChanged = true;
            }

            if (TryParsePhase((string)message.Payload["status"], out var phase) && phase != Phase)
            {
                Phase = phase;
                isChanged = true;
            }

            if (isSnapshot)
            {
                var mapId = (string)(message.Payload["session"]?["mapId"] ?? message.Payload["mapId"]);
                if (!string.IsNullOrEmpty(mapId) && mapId != MapId)
                {
                    MapId = mapId;
                    isChanged = true;
                }
            }

            isChanged |= ReadParticipants(message.Payload["participants"]);
            isChanged |= ReadResult(message.Payload["result"]);
            return isChanged;
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
