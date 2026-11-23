using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine.Networking;

namespace Seeker.Sync.Lobby
{
    /// <summary>
    /// サーバーブラウザが使う REST（POST /v1/guests・GET /v1/admin/sessions・POST /v1/sessions/{id}/join）。
    /// 一覧だけは管理 API で、管理トークンを送る（参加はゲストのトークン）。
    /// 失敗は <see cref="LobbyException"/> にする。トークンはログに出さない
    /// </summary>
    public sealed class LobbyClient
    {
        private const int ListLimit = 20;
        private readonly string apiBaseUrl;

        /// <param name="baseUrl">環境の接続先（<see cref="SyncConnection.FromBaseUrl"/> に渡すのと同じもの）</param>
        public LobbyClient(string baseUrl)
        {
            apiBaseUrl = baseUrl.TrimEnd('/') + "/v1";
        }

        public async Task<string> CreateGuestAsync(string playerName)
        {
            var body = new JObject { ["name"] = playerName }.ToString(Newtonsoft.Json.Formatting.None);
            var response = await SendAsync(UnityWebRequest.kHttpVerbPOST, "/guests", body, null);
            return (string)JObject.Parse(response)["accessToken"];
        }

        public async Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(string adminToken)
        {
            var response = await SendAsync(UnityWebRequest.kHttpVerbGET, $"/admin/sessions?limit={ListLimit}", null, adminToken);
            return SessionList.Parse(response);
        }

        /// <summary>再実行しても同じ playerId が返る（サーバーが既存の参加を返す）</summary>
        public async Task<string> JoinAsync(string token, string sessionId)
        {
            var response = await SendAsync(UnityWebRequest.kHttpVerbPOST, $"/sessions/{Uri.EscapeDataString(sessionId)}/join", "{}", token);
            return (string)JObject.Parse(response)["playerId"];
        }

        private async Task<string> SendAsync(string method, string path, string jsonBody, string token)
        {
            var url = apiBaseUrl + path;
            using (var request = new UnityWebRequest(url, method))
            {
                request.downloadHandler = new DownloadHandlerBuffer();
                if (jsonBody != null)
                {
                    request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(jsonBody));
                    request.SetRequestHeader("Content-Type", "application/json");
                }
                if (!string.IsNullOrEmpty(token)) request.SetRequestHeader("Authorization", "Bearer " + token);

                var completion = new TaskCompletionSource<bool>();
                request.SendWebRequest().completed += _ => completion.TrySetResult(true);
                await completion.Task;

                if (request.result == UnityWebRequest.Result.Success) return request.downloadHandler.text;
                // 届かなかったときは responseCode が 0 のまま
                var statusCode = request.result == UnityWebRequest.Result.ConnectionError ? 0 : request.responseCode;
                var kind = LobbyErrors.Classify(statusCode, request.downloadHandler?.text);
                throw new LobbyException(kind, $"{method} {url} failed: {request.responseCode} {request.error}");
            }
        }
    }
}
