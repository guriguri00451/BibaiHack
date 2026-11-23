using System;
using Newtonsoft.Json.Linq;

namespace Seeker.Sync.Lobby
{
    public enum LobbyErrorKind
    {
        /// <summary>接続先に届かない（DNS・オフライン・URL 違い）。HTTP ステータス 0</summary>
        Unreachable,
        /// <summary>401。トークンが無効（DB 作り直し後など）。ゲストを作り直せば直る</summary>
        Unauthorized,
        /// <summary>404 SESSION_NOT_FOUND</summary>
        SessionNotFound,
        Other,
    }

    public sealed class LobbyException : Exception
    {
        public LobbyErrorKind Kind { get; }

        public LobbyException(LobbyErrorKind kind, string detail) : base(detail)
        {
            Kind = kind;
        }

        /// <summary>画面に出す文言。サーバーの message は出さない（英語・内部向けのため）</summary>
        public string UserMessage => LobbyErrors.MessageFor(Kind);
    }

    public static class LobbyErrors
    {
        public static LobbyErrorKind Classify(long statusCode, string body)
        {
            if (statusCode == 0) return LobbyErrorKind.Unreachable;
            if (statusCode == 401) return LobbyErrorKind.Unauthorized;
            if (statusCode == 404 && ReadErrorCode(body) == "SESSION_NOT_FOUND") return LobbyErrorKind.SessionNotFound;
            return LobbyErrorKind.Other;
        }

        public static string MessageFor(LobbyErrorKind kind)
        {
            switch (kind)
            {
                case LobbyErrorKind.Unreachable: return "接続先に届きません（URL と通信を確認）";
                case LobbyErrorKind.Unauthorized: return "認証に失敗しました。もう一度試してください";
                case LobbyErrorKind.SessionNotFound: return "セッションが見つかりません。一覧を更新してください";
                default: return "サーバーでエラーが起きました";
            }
        }

        private static string ReadErrorCode(string body)
        {
            if (string.IsNullOrEmpty(body)) return null;
            try
            {
                return (string)(JObject.Parse(body)["error"] as JObject)?["code"];
            }
            catch (Newtonsoft.Json.JsonException)
            {
                // プロキシの HTML など JSON でない本文は「コード不明」
                return null;
            }
        }
    }
}
