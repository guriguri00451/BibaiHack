using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Seeker.Sync.Lobby
{
    /// <summary>
    /// GET /v1/admin/sessions の1件から、参加前に見せる情報だけを抜いたもの。
    /// 管理 API の形は server/src/game/service.ts の adminSessions が正（OpenAPI に載っていない）。
    /// </summary>
    public sealed class SessionSummary
    {
        public string Id { get; }
        public string Status { get; }
        public int Round { get; }
        public string MapId { get; }
        public string HostName { get; }
        public int PlayerCount { get; }
        public int ConnectedCount { get; }
        public DateTimeOffset CreatedAt { get; }

        public SessionSummary(string id, string status, int round, string mapId, string hostName, int playerCount, int connectedCount, DateTimeOffset createdAt)
        {
            Id = id;
            Status = status;
            Round = round;
            MapId = mapId;
            HostName = hostName;
            PlayerCount = playerCount;
            ConnectedCount = connectedCount;
            CreatedAt = createdAt;
        }

        public string StatusLabel => SessionStatusLabel.Of(Status);
    }

    public static class SessionStatusLabel
    {
        public static string Of(string status)
        {
            switch (status)
            {
                case "waiting": return "待機中";
                case "preparing": return "準備中";
                case "playing": return "本番中";
                case "finished": return "結果";
                default: return "不明";
            }
        }
    }

    public static class SessionList
    {
        /// <summary>
        /// 応答本文を読み、新しい順に並べて返す。サーバーも新しい順で返すが、表示順をここで保証する。
        /// id の無い要素は選べないので落とす。
        /// </summary>
        /// <exception cref="Newtonsoft.Json.JsonException">本文が JSON でないとき</exception>
        public static IReadOnlyList<SessionSummary> Parse(string body)
        {
            var sessions = JObject.Parse(body)["sessions"] as JArray;
            if (sessions == null) return Array.Empty<SessionSummary>();
            return sessions
                .OfType<JObject>()
                .Select(ReadOne)
                .Where(s => !string.IsNullOrEmpty(s.Id))
                .OrderByDescending(s => s.CreatedAt)
                .ToList();
        }

        // 管理 API はホスト名・人数を直接返さないので players[] から数える
        private static SessionSummary ReadOne(JObject json)
        {
            var players = (json["players"] as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
            var hostUserId = (string)json["hostUserId"];
            var host = players.FirstOrDefault(p => hostUserId != null && (string)p["userId"] == hostUserId);
            return new SessionSummary(
                (string)json["id"],
                (string)json["status"],
                (int?)json["round"] ?? 1,
                (string)json["mapId"] ?? "",
                (string)host?["name"] ?? "",
                players.Count,
                players.Count(p => (bool?)p["connected"] ?? false),
                ReadTime(json["createdAt"]));
        }

        // Newtonsoft は ISO 文字列を勝手に DateTime にすることがあるので、どちらで来ても読む
        private static DateTimeOffset ReadTime(JToken token)
        {
            if (token == null) return DateTimeOffset.MinValue;
            if (token.Type == JTokenType.Date) return new DateTimeOffset(token.Value<DateTime>().ToUniversalTime(), TimeSpan.Zero);
            return DateTimeOffset.TryParse((string)token, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : DateTimeOffset.MinValue;
        }
    }
}
