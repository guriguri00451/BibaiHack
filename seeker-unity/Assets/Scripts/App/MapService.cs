using System;
using System.Threading.Tasks;
using Seeker.Ar;
using UnityEngine;
using UnityEngine.Networking;

namespace Seeker.App
{
    /// <summary>
    /// マップ定義(JSON)の取得。API 未実装の間は StreamingAssets のモックを読む。
    /// 本番は <see cref="apiBaseUrl"/> を設定し、Authorization を付けて <c>/maps/{mapId}</c> を叩く。
    /// </summary>
    public sealed class MapService
    {
        private readonly string apiBaseUrl;
        private readonly string accessToken;

        /// <param name="apiBaseUrl">例: https://host/v1。null ならモックを使う</param>
        public MapService(string apiBaseUrl = null, string accessToken = null)
        {
            this.apiBaseUrl = apiBaseUrl;
            this.accessToken = accessToken;
        }

        public async Task<MapDefinition> FetchAsync(string mapId)
        {
            var url = string.IsNullOrEmpty(apiBaseUrl)
                ? ResolveStreamingAssets($"mock/{mapId}.json")
                : $"{apiBaseUrl.TrimEnd('/')}/maps/{UnityWebRequest.EscapeURL(mapId)}";
            using var request = UnityWebRequest.Get(url);
            if (!string.IsNullOrEmpty(accessToken)) request.SetRequestHeader("Authorization", $"Bearer {accessToken}");
            await Send(request);
            return MapDefinition.Parse(request.downloadHandler.text);
        }

        /// <summary>相対パスは StreamingAssets 配下、絶対 URL はそのまま返す。</summary>
        public static string ResolveStreamingAssets(string pathOrUrl)
        {
            if (Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var uri) && uri.Scheme != "file") return pathOrUrl;
            // Android の StreamingAssets は jar 内なので、File ではなく UnityWebRequest 経由でしか読めない
            var root = Application.streamingAssetsPath;
            return root.Contains("://") ? $"{root}/{pathOrUrl}" : $"file://{root}/{pathOrUrl}";
        }

        private static async Task Send(UnityWebRequest request)
        {
            var operation = request.SendWebRequest();
            while (!operation.isDone) await Task.Yield();
            if (request.result != UnityWebRequest.Result.Success)
                throw new InvalidOperationException($"GET {request.url} failed: {request.error}");
        }
    }
}
