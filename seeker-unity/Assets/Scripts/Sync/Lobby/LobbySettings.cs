using System;

namespace Seeker.Sync.Lobby
{
    /// <summary>端末に保存する文字列の置き場所。実機では PlayerPrefs、テストではメモリ</summary>
    public interface IKeyValueStore
    {
        string Get(string key);
        void Set(string key, string value);
        void Delete(string key);
    }

    /// <summary>
    /// サーバーブラウザが端末に覚えておく値。接続先 URL はビルドに埋め込まず、ここに保存する。
    /// トークンは発行したサーバーでしか通らないので、接続先ごとに分けて持つ。
    /// </summary>
    public sealed class LobbySettings
    {
        private const string BaseUrlKey = "lobby.baseUrl";
        private const string PlayerNameKey = "lobby.playerName";
        private const string TokenKeyPrefix = "lobby.token:";
        public const int MaxPlayerNameLength = 32;

        private readonly IKeyValueStore store;

        public LobbySettings(IKeyValueStore store)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
        }

        public string BaseUrl
        {
            get => store.Get(BaseUrlKey) ?? "";
            set => store.Set(BaseUrlKey, value);
        }

        public string PlayerName
        {
            get => store.Get(PlayerNameKey) ?? "";
            set => store.Set(PlayerNameKey, value);
        }

        public string GetToken(string baseUrl) => store.Get(TokenKeyPrefix + baseUrl);

        public void SaveToken(string baseUrl, string token) => store.Set(TokenKeyPrefix + baseUrl, token);

        public void ClearToken(string baseUrl) => store.Delete(TokenKeyPrefix + baseUrl);

        /// <summary>
        /// 入力された接続先を揃える（前後の空白・末尾の / を落とす）。http(s) の絶対 URL でなければ false。
        /// 末尾を揃えるのは、トークンの保存キーが表記ゆれで分かれないようにするため
        /// </summary>
        public static bool TryNormalizeBaseUrl(string input, out string normalized)
        {
            normalized = (input ?? "").Trim().TrimEnd('/');
            return Uri.TryCreate(normalized, UriKind.Absolute, out var uri) && (uri.Scheme == "https" || uri.Scheme == "http");
        }

        /// <summary>前後の空白を落とし、サーバーの上限（32 文字）に収まる名前か</summary>
        public static bool TryNormalizePlayerName(string input, out string normalized)
        {
            normalized = (input ?? "").Trim();
            return normalized.Length >= 1 && normalized.Length <= MaxPlayerNameLength;
        }
    }
}
