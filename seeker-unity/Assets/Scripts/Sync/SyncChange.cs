using Seeker.Ar;

namespace Seeker.Sync
{
    public enum SyncChangeKind
    {
        Added,
        AssetChanged,
        TransformChanged,
        Removed,
    }

    /// <summary>ストアの状態変化。描画側はこれだけを見て GameObject を増減・更新する。</summary>
    public readonly struct SyncChange
    {
        public readonly SyncChangeKind kind;
        public readonly string objectId;
        public readonly string assetKey;
        /// <summary>ダウンロード用の署名付きURL。キーは変わらず URL だけ変わった更新では AssetChanged にならない。</summary>
        public readonly string assetUrl;
        public readonly MapTransform transform;

        public SyncChange(SyncChangeKind kind, string objectId, string assetKey, string assetUrl, MapTransform transform)
        {
            this.kind = kind;
            this.objectId = objectId;
            this.assetKey = assetKey;
            this.assetUrl = assetUrl;
            this.transform = transform;
        }
    }
}
