namespace Seeker.Sync
{
    /// <summary>
    /// (round, seq) の大小で古いイベントを捨てる。seq は「ラウンド内」の順序なので round を先に比べる（仕様 §6.1）。
    /// </summary>
    public sealed class EventSequencer
    {
        private long round = long.MinValue;
        private long seq = long.MinValue;

        /// <summary>snapshot を適用した時点を基準にする。null は「基準なし」で、次の有効なイベントを必ず通す。</summary>
        public void Reset(long? baselineRound, long? baselineSeq)
        {
            round = baselineRound ?? long.MinValue;
            seq = baselineSeq ?? long.MinValue;
        }

        public bool TryAccept(long eventRound, long eventSeq)
        {
            var isNewer = eventRound > round || (eventRound == round && eventSeq > seq);
            if (!isNewer) return false;
            round = eventRound;
            seq = eventSeq;
            return true;
        }
    }
}
