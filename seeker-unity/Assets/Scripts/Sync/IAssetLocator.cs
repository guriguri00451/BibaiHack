using System;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine.Networking;

namespace Seeker.Sync
{
    /// <summary>
    /// assetKey から実際にダウンロードできる URL を得る。DB には署名なしの永続キーを保存する方針（仕様 §3）のため、
    /// 取得のたびに署名付きURLへ引き換える必要がある。
    /// </summary>
    public interface IAssetLocator
    {
        Task<string> ResolveDownloadUrl(string assetKey);
    }

    /// <summary>
    /// REST で署名付きダウンロードURLを引く。
    /// TODO(soma): 署名URL取得エンドポイントは仕様に未定義。サーバー側と合意できたらパスと応答形を合わせる
    ///   （現状の想定: GET {restBaseUrl}/assets/download-url?key=... → {"url":"..."}）。
    /// </summary>
    public sealed class RestAssetLocator : IAssetLocator
    {
        private readonly string restBaseUrl;
        private readonly string accessToken;

        public RestAssetLocator(string restBaseUrl, string accessToken)
        {
            this.restBaseUrl = restBaseUrl.TrimEnd('/');
            this.accessToken = accessToken;
        }

        public async Task<string> ResolveDownloadUrl(string assetKey)
        {
            // すでに URL で来ているケース（モックや将来の仕様変更）はそのまま使う
            if (assetKey.StartsWith("http://") || assetKey.StartsWith("https://")) return assetKey;

            using var request = UnityWebRequest.Get($"{restBaseUrl}/assets/download-url?key={UnityWebRequest.EscapeURL(assetKey)}");
            request.SetRequestHeader("Authorization", $"Bearer {accessToken}");
            var operation = request.SendWebRequest();
            while (!operation.isDone) await Task.Yield();
            if (request.result != UnityWebRequest.Result.Success)
                throw new InvalidOperationException($"署名URLの取得に失敗 key={assetKey}: {request.error}");
            var url = (string)JObject.Parse(request.downloadHandler.text)["url"];
            if (string.IsNullOrEmpty(url)) throw new InvalidOperationException($"署名URLの応答に url が無い key={assetKey}");
            return url;
        }
    }
}
