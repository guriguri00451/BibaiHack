using Seeker.Sync;
using UnityEngine;
using UnityEngine.UI;

namespace Seeker.App
{
    /// <summary>残り時間を MM:SS で出す uGUI。表示だけで、0 になってもフェーズは動かさない。</summary>
    public sealed class TimerHudView
    {
        private static readonly Color NormalColor = Color.white;
        private static readonly Color WarningColor = new Color(1f, 0.3f, 0.25f);

        private readonly Text label;

        public TimerHudView(Text label)
        {
            this.label = label;
        }

        /// <summary>phaseEndsAt が無い（waiting / finished）ときは空にする。</summary>
        public void Render(GameSessionState session, ServerClock clock)
        {
            var isTimed = session.PhaseEndsAtMs.HasValue
                && (session.Phase == GamePhase.Preparing || session.Phase == GamePhase.Playing);
            if (!isTimed)
            {
                label.text = "";
                return;
            }
            var remaining = clock.RemainingMs(session.PhaseEndsAtMs.Value);
            label.text = TimerFormat.Format(remaining);
            label.color = TimerFormat.IsWarning(remaining) ? WarningColor : NormalColor;
        }
    }
}
