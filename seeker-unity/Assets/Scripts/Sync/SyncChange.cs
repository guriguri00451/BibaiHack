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
        public readonly MapTransform transform;

        public SyncChange(SyncChangeKind kind, string objectId, string assetKey, MapTransform transform)
        {
            this.kind = kind;
            this.objectId = objectId;
            this.assetKey = assetKey;
            this.transform = transform;
        }
    }
}
