using Seeker.Ar;
using UnityEngine;

namespace Seeker.Sync
{
    /// <summary>1オブジェクト分の GameObject。補間は共有マップ座標で行い、描画時にローカルへ変換する。</summary>
    public sealed class SyncedObjectView : MonoBehaviour
    {
        private readonly TransformSmoother smoother = new TransformSmoother();
        private GameObject model;

        /// <summary>最後に要求したアセット。ロード完了時に食い違っていたら（途中で差し替わった）その結果は捨てる。</summary>
        public string AssetKey { get; set; }

        public void SetTarget(MapTransform target) => smoother.SetTarget(target);

        /// <summary>新しいモデルのロード完了後に古いものを消すので、差し替え中も見た目が途切れない。</summary>
        public void AttachModel(GameObject newModel)
        {
            if (model != null) Destroy(model);
            model = newModel;
        }

        /// <summary>マーカー未検出の間は原点が決まらないため位置を出せない。</summary>
        public void Render(CoordinateAdapter adapter, float deltaSeconds)
        {
            var isVisible = adapter.IsCalibrated;
            if (gameObject.activeSelf != isVisible) gameObject.SetActive(isVisible);
            if (!isVisible) return;
            var local = adapter.MapToLocal(smoother.Step(deltaSeconds));
            transform.SetPositionAndRotation(local.position, local.rotation);
            transform.localScale = local.scale;
        }
    }
}
