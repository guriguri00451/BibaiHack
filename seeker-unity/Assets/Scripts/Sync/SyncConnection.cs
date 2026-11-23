using System;

namespace Seeker.Sync
{
    /// <summary>
    /// 同期の接続先と認証情報。ゲーム進行側が guest / session 作成のあとに組み立てて渡す。
    /// トークンはセッションごとに払い出される値なので、シーンやコードに埋め込まない。
    /// </summary>
    public sealed class SyncConnection
    {
        private const string ApiPrefix = "/v1";

        public string WsUrl { get; }
        public string RestBaseUrl { get; }
        public string SessionId { get; }
        public string AccessToken { get; }

        public SyncConnection(string wsUrl, string restBaseUrl, string sessionId, string accessToken)
        {
            if (string.IsNullOrEmpty(wsUrl)) throw new ArgumentException("wsUrl が空", nameof(wsUrl));
            if (string.IsNullOrEmpty(restBaseUrl)) throw new ArgumentException("restBaseUrl が空", nameof(restBaseUrl));
            if (string.IsNullOrEmpty(sessionId)) throw new ArgumentException("sessionId が空", nameof(sessionId));
            if (string.IsNullOrEmpty(accessToken)) throw new ArgumentException("accessToken が空", nameof(accessToken));
            WsUrl = wsUrl;
            RestBaseUrl = restBaseUrl.TrimEnd('/');
            SessionId = sessionId;
            AccessToken = accessToken;
        }

        /// <summary>
        /// 環境の接続先（例 https://bibai.t0waxx.com/so-ma-01）から REST と WS の URL を組み立てる。
        /// https → wss、http → ws。http はローカルのモック用で、リリースビルドでは通信自体が拒否される。
        /// </summary>
        public static SyncConnection FromBaseUrl(string baseUrl, string sessionId, string accessToken)
        {
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
                throw new ArgumentException($"接続先は http(s):// で始まる絶対URLが必要: {baseUrl}", nameof(baseUrl));

            var root = baseUrl.TrimEnd('/');
            var wsRoot = (uri.Scheme == "https" ? "wss" : "ws") + root.Substring(uri.Scheme.Length);
            return new SyncConnection($"{wsRoot}{ApiPrefix}/ws", $"{root}{ApiPrefix}", sessionId, accessToken);
        }
    }
}
