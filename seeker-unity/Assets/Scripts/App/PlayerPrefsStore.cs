using Seeker.Sync.Lobby;
using UnityEngine;

namespace Seeker.App
{
    /// <summary>
    /// <see cref="LobbySettings"/> の保存先。ゲストトークンもここに入る（端末内の平文）。
    /// ゲストのトークンは名前だけで誰でも発行できる値なので、Keystore までは使わない
    /// </summary>
    public sealed class PlayerPrefsStore : IKeyValueStore
    {
        public string Get(string key) => PlayerPrefs.HasKey(key) ? PlayerPrefs.GetString(key) : null;

        public void Set(string key, string value)
        {
            PlayerPrefs.SetString(key, value);
            PlayerPrefs.Save();
        }

        public void Delete(string key)
        {
            PlayerPrefs.DeleteKey(key);
            PlayerPrefs.Save();
        }
    }
}
