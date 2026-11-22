using System;
using GLTFast;
using Seeker.Ar;
using UnityEngine;

namespace Seeker.App
{
    /// <summary>
    /// マップ定義の GLB を読み込み、共有マップ原点を基準に配置する。
    /// 原点が合わせ直されるたびに <see cref="Place"/> で置き直す（マップ自体は読み込み直さない）。
    /// </summary>
    public sealed class MapPresenter
    {
        private readonly Transform root;
        private MapTransform mapTransform = MapTransform.Identity;

        public bool IsLoaded { get; private set; }

        public MapPresenter(Transform parent)
        {
            root = new GameObject("Map").transform;
            root.SetParent(parent, false);
            root.gameObject.SetActive(false); // 原点が決まるまで見せない（原点(0,0,0)に出てしまうため）
        }

        public async System.Threading.Tasks.Task LoadAsync(MapDefinition definition)
        {
            var gltf = new GltfImport();
            var url = MapService.ResolveStreamingAssets(definition.modelUrl);
            if (!await gltf.Load(url)) throw new InvalidOperationException($"GLB の読み込みに失敗: {url}");
            if (!await gltf.InstantiateMainSceneAsync(root)) throw new InvalidOperationException($"GLB の配置に失敗: {url}");
            // glTF は右手系で、glTFast が子の頂点・階層を Unity の左手系へ変換済み。
            // 残る root の姿勢だけを MapToLocal(Z 反転) で変換するので、二重反転にならない
            mapTransform = definition.ToMapTransform();
            IsLoaded = true;
        }

        public void Place(CoordinateAdapter adapter)
        {
            if (!IsLoaded || !adapter.IsCalibrated) return;
            var local = adapter.MapToLocal(mapTransform);
            root.SetPositionAndRotation(local.position, local.rotation);
            root.localScale = local.scale;
            root.gameObject.SetActive(true);
        }
    }
}
