namespace Seeker.Ar
{
    /// <summary>
    /// 表示中のマップと、進行中の読み込み要求の世代を持つ。Unity 非依存の純ロジック。
    /// 読み込みは非同期で順不同に終わるため、要求ごとに世代番号を払い出し、最後の要求の結果だけ反映する。
    /// </summary>
    public sealed class MapLoadTracker
    {
        private int latestRequest;
        private string loadedMapId;
        private string loadedModelUrl;
        private string loadedSplatUrl;

        /// <summary>新しい要求を始める。以前の要求はこの時点で古くなる。</summary>
        public int BeginRequest() => ++latestRequest;

        public bool IsLatest(int request) => request == latestRequest;

        /// <summary>
        /// GLB / SPZ を読み込み直す必要があるか。登録マップの URL は中身を差し替えると <c>?v=</c> が変わるので、
        /// mapId が同じでも modelUrl / splatUrl が違えば読み込み直す。
        /// </summary>
        public bool NeedsReload(MapDefinition definition) =>
            definition.mapId != loadedMapId || definition.modelUrl != loadedModelUrl || NormalizeEmpty(definition.splatUrl) != loadedSplatUrl;

        public void MarkLoaded(MapDefinition definition)
        {
            loadedMapId = definition.mapId;
            loadedModelUrl = definition.modelUrl;
            loadedSplatUrl = NormalizeEmpty(definition.splatUrl);
        }

        // JsonUtility は null の文字列を "" で読むことがあるので、null と "" を同じ「SPZ 無し」として比べる
        private static string NormalizeEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;
    }
}
