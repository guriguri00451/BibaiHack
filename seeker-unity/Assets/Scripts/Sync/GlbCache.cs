using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using GLTFast;
using UnityEngine;

namespace Seeker.Sync
{
    /// <summary>
    /// assetKey ごとに GLB を1回だけ読み込み、同じキーのオブジェクトは読み込み済みデータからインスタンス化する。
    /// アセットは上書きされず更新時は新キーが発行される（仕様 §5）ため、キーをそのままキャッシュキーにできる。
    /// 進行中のロードも共有するので、同じキーが同時に来ても通信は1回。
    /// </summary>
    public sealed class GlbCache : IDisposable
    {
        private readonly IAssetLocator locator;
        private readonly Dictionary<string, Task<GltfImport>> imports = new Dictionary<string, Task<GltfImport>>();

        public GlbCache(IAssetLocator locator)
        {
            this.locator = locator;
        }

        /// <returns>インスタンス化に成功したか</returns>
        public async Task<bool> InstantiateInto(string assetKey, Transform parent)
        {
            if (!imports.TryGetValue(assetKey, out var importTask))
            {
                importTask = Import(assetKey);
                imports[assetKey] = importTask;
            }

            GltfImport import;
            try
            {
                import = await importTask;
            }
            catch (Exception exception)
            {
                // 失敗を残すと再試行できなくなるので捨てる（次の updated / snapshot で再挑戦）
                imports.Remove(assetKey);
                Debug.LogWarning($"[GlbCache] load failed key={assetKey}: {exception.Message}");
                return false;
            }
            return await import.InstantiateMainSceneAsync(parent);
        }

        private async Task<GltfImport> Import(string assetKey)
        {
            var url = await locator.ResolveDownloadUrl(assetKey);
            var import = new GltfImport();
            if (!await import.Load(url)) throw new InvalidOperationException($"glTF の読み込みに失敗 url={url}");
            return import;
        }

        public async void Dispose()
        {
            foreach (var task in imports.Values)
            {
                try { (await task).Dispose(); }
                catch (Exception) { /* ロード失敗済みのものは破棄するものが無い */ }
            }
            imports.Clear();
        }
    }
}
