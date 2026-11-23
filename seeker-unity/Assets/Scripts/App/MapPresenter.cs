using System;
using System.Threading.Tasks;
using GaussianSplatting.Runtime;
using GLTFast;
using Seeker.Ar;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Seeker.App
{
    /// <summary>
    /// マップ定義の GLB と Gaussian Splat（SPZ）を読み込み、共有マップ原点を基準に配置する。
    /// 原点が合わせ直されるたびに <see cref="Place"/> で置き直す。マップが替わったら <see cref="LoadAsync"/> で差し替える。
    /// </summary>
    /// <remarks>
    /// GaussianSplatRenderer は無効化（OnDisable）されると GPU のデータを捨て、有効化で読み込み直す（SPZ は数十MB）。
    /// そのため root は常に有効にしておき、原点が決まるまでは中身（GLB・SPZ）を有効にしないことで隠す。
    /// SPZ は一度有効にしたら、マップを差し替えて破棄するまで無効にしない。
    /// </remarks>
    public sealed class MapPresenter
    {
        private readonly Transform root;
        private MapTransform mapTransform = MapTransform.Identity;
        private LoadedMap current;
        private bool isPlaced;

        public bool IsLoaded => current != null;

        public MapPresenter(Transform parent)
        {
            root = new GameObject("Map").transform;
            root.SetParent(parent, false);
        }

        /// <summary>
        /// GLB・SPZ を新しいコンテナに読み込み、成功したら表示中のマップと差し替える。失敗しても前のマップは残る。
        /// 途中で <paramref name="isStillWanted"/> が false になったら（次の変更が来たら）読み込んだ分を捨てて false を返す。
        /// 原点が決まる前は SPZ の読み込みを始めず、最初の <see cref="Place"/> で始める（隠したまま読み込めないため）。
        /// </summary>
        /// <exception cref="InvalidOperationException">GLB / SPZ の読み込み・配置に失敗したとき</exception>
        public async Task<bool> LoadAsync(MapDefinition definition, Func<bool> isStillWanted)
        {
            var loading = new LoadedMap(root, definition.mapId);
            try
            {
                if (definition.HasModel && !await loading.LoadModelAsync(definition.modelUrl, () => IsWanted(isStillWanted)))
                    return Discard(loading);
                if (definition.HasSplat)
                {
                    loading.PrepareSplat(definition.splatUrl);
                    // 原点が決まっていれば、差し替える前に読み込み終えておく（読み込み中に前のマップが消えないように）
                    if (isPlaced && !await loading.LoadSplatAsync(() => IsWanted(isStillWanted)))
                        return Discard(loading);
                }
            }
            catch
            {
                Discard(loading);
                throw;
            }

            current?.Dispose();
            current = loading;
            SetTransform(definition);
            if (isPlaced) current.Show();
            return true;
        }

        /// <summary>
        /// 置き方だけ更新する（GLB・SPZ は同じで transform だけ変わったとき）。
        /// glTF は右手系で、glTFast が子の頂点・階層を Unity の左手系へ変換済み。
        /// 残る root の姿勢だけを MapToLocal(Z 反転) で変換するので、二重反転にならない
        /// </summary>
        public void SetTransform(MapDefinition definition) => mapTransform = definition.ToMapTransform();

        public void Place(CoordinateAdapter adapter)
        {
            if (!IsLoaded || !adapter.IsCalibrated) return;
            var local = adapter.MapToLocal(mapTransform);
            root.SetPositionAndRotation(local.position, local.rotation);
            root.localScale = local.scale;
            isPlaced = true;
            current.Show();
        }

        // await 中にシーンごと破棄された場合も、続きを触らない
        private bool IsWanted(Func<bool> isStillWanted) => root != null && isStillWanted();

        private static bool Discard(LoadedMap map)
        {
            map.Dispose();
            return false;
        }

        /// <summary>1つのマップの GLB と SPZ。GameObject を消しただけでは glTFast のメッシュ・テクスチャが残るので import も持って破棄する。</summary>
        private sealed class LoadedMap : IDisposable
        {
            private readonly GameObject container;
            private readonly GameObject model;
            private GltfImport import;
            private GaussianSplatRenderer splat;

            public LoadedMap(Transform root, string mapId)
            {
                container = new GameObject($"Map_{mapId}");
                container.transform.SetParent(root, false);
                model = new GameObject("Model");
                model.transform.SetParent(container.transform, false);
                model.SetActive(false);
            }

            public async Task<bool> LoadModelAsync(string modelUrl, Func<bool> isStillWanted)
            {
                var url = MapService.ResolveStreamingAssets(modelUrl);
                import = new GltfImport();
                if (!await import.Load(url)) throw new InvalidOperationException($"GLB の読み込みに失敗: {url}");
                if (!isStillWanted()) return false;
                if (!await import.InstantiateMainSceneAsync(model.transform)) throw new InvalidOperationException($"GLB の配置に失敗: {url}");
                return isStillWanted();
            }

            /// <summary>読み込みはまだ始めない（GameObject を有効にしたとき始まる）。</summary>
            public void PrepareSplat(string splatUrl)
            {
                var splatObject = new GameObject("Splat");
                splatObject.SetActive(false);
                splatObject.transform.SetParent(container.transform, false);
                splat = splatObject.AddComponent<GaussianSplatRenderer>();
                splat.m_SourcePath = splatUrl;
            }

            public async Task<bool> LoadSplatAsync(Func<bool> isStillWanted)
            {
                // 有効化した時点の OnEnable で読み込みが始まり isLoading が立つ。以降は Update で進む
                splat.gameObject.SetActive(true);
                while (splat != null && splat.isLoading)
                {
                    if (!isStillWanted()) return false;
                    await Task.Yield();
                }
                if (splat == null) return false;
                if (splat.loadError != null) throw new InvalidOperationException($"SPZ の読み込みに失敗: {splat.loadError}");
                LogSplatLoaded();
                return isStillWanted();
            }

            // マップ座標のどこに点があるかは、管理画面で transform を合わせるときの手がかりになる
            private void LogSplatLoaded() =>
                Debug.Log($"[MapPresenter] SPZ を読み込んだ {splat.m_SourcePath} 点={splat.splatCount} 範囲 中心={splat.localBounds.center} 大きさ={splat.localBounds.size}");

            public void Show()
            {
                model.SetActive(true);
                // まだ読み込んでいない SPZ はここで読み込みが始まり、終わったら表示される
                if (splat != null && !splat.gameObject.activeSelf) splat.gameObject.SetActive(true);
            }

            public void Dispose()
            {
                if (container != null) Object.Destroy(container);
                import?.Dispose();
                import = null;
            }
        }
    }
}
