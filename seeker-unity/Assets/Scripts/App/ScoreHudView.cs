using Seeker.Sync;
using UnityEngine;
using UnityEngine.UI;

namespace Seeker.App
{
    /// <summary>累計スコアの常時表示と、捕獲結果のフェード演出。値は上書きするだけで加算しない。</summary>
    public sealed class ScoreHudView
    {
        private const float PopupSeconds = 1.6f;
        private const float PopupStartScale = 1.5f;

        private readonly Text scoreLabel;
        private readonly Text popupLabel;
        private float popupStartedAt = float.NegativeInfinity;

        public ScoreHudView(Transform playingHud)
        {
            // playingHud はステータス帯（上端から 120〜240）。下端基準で、直下のタイマー（〜約 385）より下に置く
            scoreLabel = CreateText(playingHud, "Score", new Vector2(0.5f, 0f), new Vector2(0f, -450f), 64, new Vector2(700f, 100f));
            popupLabel = CreateText(playingHud, "CapturePopup", new Vector2(0.5f, 0f), new Vector2(0f, -800f), 96, new Vector2(900f, 160f));
            popupLabel.text = "";
            Render(null);
        }

        public void Render(int? score) => scoreLabel.text = ScoreText.Hud(score);

        public void ShowPopup(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            popupLabel.text = message;
            popupStartedAt = Time.unscaledTime;
        }

        public void Tick()
        {
            var progress = (Time.unscaledTime - popupStartedAt) / PopupSeconds;
            if (progress >= 1f || progress < 0f)
            {
                popupLabel.text = "";
                return;
            }
            popupLabel.transform.localScale = Vector3.one * Mathf.Lerp(PopupStartScale, 1f, Mathf.Min(1f, progress * 4f));
            var color = popupLabel.color;
            color.a = 1f - progress * progress;
            popupLabel.color = color;
        }

        private static Text CreateText(Transform parent, string name, Vector2 anchor, Vector2 position, int fontSize, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = anchor;
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            var text = go.GetComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = fontSize;
            text.fontStyle = FontStyle.Bold;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;
            text.raycastTarget = false;
            return text;
        }
    }
}
