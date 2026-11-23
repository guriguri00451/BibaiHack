using System;
using System.Threading.Tasks;

namespace Seeker.Sync.Lobby
{
    /// <summary>
    /// 保存したゲストトークンを使い回し、401（DB 作り直し後など）なら一度だけゲストを作り直して再実行する。
    /// HTTP は渡された関数に任せるので、UI や UnityWebRequest に依存しない
    /// </summary>
    public sealed class GuestTokenProvider
    {
        private readonly LobbySettings settings;
        private readonly string baseUrl;
        private readonly Func<Task<string>> createGuest;

        /// <param name="createGuest">POST /v1/guests を叩いて accessToken を返す</param>
        public GuestTokenProvider(LobbySettings settings, string baseUrl, Func<Task<string>> createGuest)
        {
            this.settings = settings;
            this.baseUrl = baseUrl;
            this.createGuest = createGuest;
        }

        /// <summary>直近に使ったトークン。<see cref="RunAsync{T}"/> の後に SyncConnection を組むのに使う</summary>
        public string CurrentToken => settings.GetToken(baseUrl);

        public async Task<T> RunAsync<T>(Func<string, Task<T>> call)
        {
            var token = settings.GetToken(baseUrl);
            var isFresh = false;
            if (string.IsNullOrEmpty(token))
            {
                token = await RenewAsync();
                isFresh = true;
            }
            try
            {
                return await call(token);
            }
            catch (LobbyException e) when (e.Kind == LobbyErrorKind.Unauthorized && !isFresh)
            {
                return await call(await RenewAsync());
            }
        }

        private async Task<string> RenewAsync()
        {
            settings.ClearToken(baseUrl);
            var token = await createGuest();
            settings.SaveToken(baseUrl, token);
            return token;
        }
    }
}
