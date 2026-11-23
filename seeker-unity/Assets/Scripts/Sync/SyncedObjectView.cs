using Seeker.Ar;
using UnityEngine;

namespace Seeker.Sync
{
    /// <summary>1オブジェクト分の GameObject。補間は共有マップ座標で行い、描画時にローカルへ変換する。</summary>
    public sealed class SyncedObjectView : MonoBehaviour
    {
        private readonly TransformSmoother smoother = new TransformSmoother();
        private static readonly Color HighlightColor = new Color(1f, 0.85f, 0.2f, 1f);
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");

        private GameObject model;
        private Renderer[] renderers = System.Array.Empty<Renderer>();
        private MaterialPropertyBlock highlightBlock;
        private bool isHighlighted;

        /// <summary>サーバー上のオブジェクト ID。GameObject 名の文字列には頼らない。</summary>
        public string ObjectId { get; set; }

        /// <summary>最後に要求したアセット。ロード完了時に食い違っていたら（途中で差し替わった）その結果は捨てる。</summary>
        public string AssetKey { get; set; }

        public void SetTarget(MapTransform target) => smoother.SetTarget(target);

        /// <summary>新しいモデルのロード完了後に古いものを消すので、差し替え中も見た目が途切れない。</summary>
        public void AttachModel(GameObject newModel)
        {
            if (model != null) Destroy(model);
            model = newModel;
            renderers = model.GetComponentsInChildren<Renderer>();
            RebuildCollider();
            ApplyHighlight();
        }

        /// <summary>
        /// 選択中の見た目。マテリアルは共有されるので、Renderer ごとの MaterialPropertyBlock で色味だけ変える。
        /// </summary>
        public void SetHighlighted(bool value)
        {
            isHighlighted = value;
            ApplyHighlight();
        }

        private void ApplyHighlight()
        {
            if (highlightBlock == null) highlightBlock = new MaterialPropertyBlock();
            highlightBlock.Clear();
            if (isHighlighted)
            {
                highlightBlock.SetColor(BaseColorId, HighlightColor);
                highlightBlock.SetColor(ColorId, HighlightColor);
            }
            foreach (var renderer in renderers)
            {
                if (renderer != null) renderer.SetPropertyBlock(isHighlighted ? highlightBlock : null);
            }
        }

        /// <summary>
        /// 全 Renderer の localBounds を view ルートのローカル座標に直して包む。
        /// Renderer.bounds は非アクティブ（未較正で隠れている）間に古い値になりうるので使わない。
        /// </summary>
        private void RebuildCollider()
        {
            var collider = GetComponent<BoxCollider>();
            var hasBounds = false;
            var combined = new Bounds();
            var toViewLocal = transform.worldToLocalMatrix;
            foreach (var renderer in renderers)
            {
                if (renderer == null) continue;
                var local = renderer.localBounds;
                var toView = toViewLocal * renderer.transform.localToWorldMatrix;
                for (var i = 0; i < 8; i++)
                {
                    var corner = local.center + Vector3.Scale(local.extents, new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f));
                    var point = toView.MultiplyPoint3x4(corner);
                    if (!hasBounds) { combined = new Bounds(point, Vector3.zero); hasBounds = true; }
                    else combined.Encapsulate(point);
                }
            }
            if (!hasBounds)
            {
                if (collider != null) Destroy(collider);
                return;
            }
            if (collider == null) collider = gameObject.AddComponent<BoxCollider>();
            collider.center = combined.center;
            collider.size = combined.size;
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
