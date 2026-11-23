using System;
using System.Threading.Tasks;
using GLTFast;
using Seeker.Ar;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Seeker.App
{
    /// <summary>
    /// マップ定義の GLB を読み込み、共有マップ原点を基準に配置する。
    /// 原点が合わせ直されるたびに <see cref="Place"/> で置き直す。マップが替わったら <see cref="LoadAsync"/> で差し替える。
    /// </summary>
    public sealed class MapPresenter
    {
        private readonly Transform root;
        private MapTransform mapTransform = MapTransform.Identity;
        private GameObject currentModel;
        private GltfImport currentImport;

        public bool IsLoaded => currentModel != null;

        public MapPresenter(Transform parent)
        {
            root = new GameObject("Map").transform;
            root.SetParent(parent, false);
            root.gameObject.SetActive(false); // 原点が決まるまで見せない（原点(0,0,0)に出てしまうため）
        }

        /// <summary>
        /// GLB を別コンテナに読み込み、成功したら表示中のマップと差し替える。失敗しても前のマップは残る。
        /// 途中で <paramref name="isStillWanted"/> が false になったら（次の変更が来たら）読み込んだ分を捨てて false を返す。
        /// </summary>
        /// <exception cref="InvalidOperationException">GLB の読み込み・配置に失敗したとき</exception>
        public async Task<bool> LoadAsync(MapDefinition definition, Func<bool> isStillWanted)
        {
            var url = MapService.ResolveStreamingAssets(definition.modelUrl);
            var import = new GltfImport();
            var model = new GameObject($"Model_{definition.mapId}");
            model.transform.SetParent(root, false);
            model.SetActive(false);
            try
            {
                if (!await import.Load(url)) throw new InvalidOperationException($"GLB の読み込みに失敗: {url}");
                if (!IsWanted(isStillWanted)) return Discard(model, import);
                if (!await import.InstantiateMainSceneAsync(model.transform)) throw new InvalidOperationException($"GLB の配置に失敗: {url}");
                if (!IsWanted(isStillWanted)) return Discard(model, import);
            }
            catch
            {
                Discard(model, import);
                throw;
            }

            Discard(currentModel, currentImport);
            currentModel = model;
            currentImport = import;
            model.SetActive(true);
            SetTransform(definition);
            return true;
        }

        /// <summary>
        /// 置き方だけ更新する（GLB は同じで transform だけ変わったとき）。
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
            root.gameObject.SetActive(true);
        }

        // await 中にシーンごと破棄された場合も、続きを触らない
        private bool IsWanted(Func<bool> isStillWanted) => root != null && isStillWanted();

        /// <summary>GameObject を消しただけでは glTFast が作ったメッシュ・テクスチャが残るので、import も破棄する。</summary>
        private static bool Discard(GameObject model, GltfImport import)
        {
            if (model != null) Object.Destroy(model);
            import?.Dispose();
            return false;
        }
    }
}
