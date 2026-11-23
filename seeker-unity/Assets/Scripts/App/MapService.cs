using System;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Seeker.Ar;
using UnityEngine;
using UnityEngine.Networking;

namespace Seeker.App
{
    /// <summary>
    /// マップ定義(JSON)の取得。接続情報があれば <c>GET {RestBaseUrl}/maps/{mapId}</c> を Bearer 付きで叩き、
    /// 無ければ StreamingAssets のモックを読む。
    /// </summary>
    public sealed class MapService
    {
        private const string MapNotFoundCode = "MAP_NOT_FOUND";

        private readonly string apiBaseUrl;
        private readonly string accessToken;

        /// <param name="apiBaseUrl"><c>/v1</c> まで含めたベース URL（<see cref="Seeker.Sync.SyncConnection.RestBaseUrl"/>）。null ならモックを使う</param>
        public MapService(string apiBaseUrl = null, string accessToken = null)
        {
            this.apiBaseUrl = apiBaseUrl;
            this.accessToken = accessToken;
        }

        /// <exception cref="MapFetchException">取得できなかったとき</exception>
        public async Task<MapDefinition> FetchAsync(string mapId)
        {
            var url = string.IsNullOrEmpty(apiBaseUrl)
                ? ResolveStreamingAssets($"mock/{mapId}.json")
                : $"{apiBaseUrl.TrimEnd('/')}/maps/{UnityWebRequest.EscapeURL(mapId)}";
            using var request = UnityWebRequest.Get(url);
            if (!string.IsNullOrEmpty(accessToken)) request.SetRequestHeader("Authorization", $"Bearer {accessToken}");
            var operation = request.SendWebRequest();
            while (!operation.isDone) await Task.Yield();

            if (request.result != UnityWebRequest.Result.Success)
            {
                var isNotFound = request.responseCode == 404 && ReadErrorCode(request.downloadHandler?.text) == MapNotFoundCode;
                throw new MapFetchException(mapId, isNotFound, $"GET {url} failed: {request.responseCode} {request.error}");
            }
            var definition = MapDefinition.Parse(request.downloadHandler.text);
            // モック JSON などで mapId が抜けていても、要求した mapId として扱う（変更判定に使うため）
            if (string.IsNullOrEmpty(definition.mapId)) definition.mapId = mapId;
            return definition;
        }

        /// <summary>相対パスは StreamingAssets 配下、絶対 URL はそのまま返す。</summary>
        public static string ResolveStreamingAssets(string pathOrUrl)
        {
            if (Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var uri) && uri.Scheme != "file") return pathOrUrl;
            // Android の StreamingAssets は jar 内なので、File ではなく UnityWebRequest 経由でしか読めない
            var root = Application.streamingAssetsPath;
            return root.Contains("://") ? $"{root}/{pathOrUrl}" : $"file://{root}/{pathOrUrl}";
        }

        /// <summary>サーバーのエラー本文 <c>{"error":{"code"}}</c> から code を読む。読めなければ null。</summary>
        private static string ReadErrorCode(string body)
        {
            if (string.IsNullOrEmpty(body)) return null;
            try
            {
                return (string)(JObject.Parse(body)["error"] as JObject)?["code"];
            }
            catch (Newtonsoft.Json.JsonException)
            {
                // プロキシの HTML など JSON でない本文は「コード不明」として扱う
                return null;
            }
        }
    }

    public sealed class MapFetchException : Exception
    {
        public string MapId { get; }

        /// <summary>サーバーに存在しないマップ（404 MAP_NOT_FOUND）。false なら通信失敗など。</summary>
        public bool IsNotFound { get; }

        public MapFetchException(string mapId, bool isNotFound, string message) : base(message)
        {
            MapId = mapId;
            IsNotFound = isNotFound;
        }
    }
}
