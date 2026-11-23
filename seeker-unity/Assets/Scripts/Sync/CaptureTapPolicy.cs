namespace Seeker.Sync
{
    /// <summary>タップを捕獲として扱うかの判定（UI 非依存の純ロジック）。</summary>
    public static class CaptureTapPolicy
    {
        public static bool ShouldHandle(GamePhase phase, bool isPointerOverUi) => phase == GamePhase.Playing && !isPointerOverUi;
    }
}
