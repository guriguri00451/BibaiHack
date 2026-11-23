using System.Collections.Generic;
using System.Text;
using Seeker.Sync;
using UnityEngine;
using UnityEngine.UI;

namespace Seeker.App
{
    /// <summary>
    /// フェーズごとの uGUI パネルを出し分ける。状態は持たず、GameSessionState を描くだけ。
    /// playing はカメラ映像（AR ビュー）を見せるので全画面の背景を持たない。
    /// </summary>
    public sealed class PhasePanelsView
    {
        private static readonly Color FullScreenTint = new Color(0.05f, 0.08f, 0.14f, 0.92f);

        private readonly Dictionary<GamePhase, GameObject> panels = new Dictionary<GamePhase, GameObject>();
        private readonly Text waitingBody;
        private readonly Text finishedBody;
        private readonly TimerHudView playingTimer;
        private readonly TimerHudView preparingTimer;
        private readonly Text nextRoundLabel;

        public PhasePanelsView(Transform canvas, GameObject playingHud)
        {
            panels[GamePhase.Playing] = playingHud;
            waitingBody = CreateFullScreenPanel(canvas, GamePhase.Waiting, "参加者を待っています");
            var preparingBody = CreateFullScreenPanel(canvas, GamePhase.Preparing, "ドロが隠れています");
            preparingBody.text = "ドロが隠れています\nしばらくお待ちください";
            preparingTimer = new TimerHudView(CreateTimerText(preparingBody.transform.parent, new Vector2(0.5f, 0.3f), 120));
            // playing HUD（ステータス帯）の直下に出す。帯は上端から 120 + 高さ 120 の位置
            playingTimer = new TimerHudView(CreateTimerText(playingHud.transform, new Vector2(0.5f, 0f), 96));
            finishedBody = CreateFullScreenPanel(canvas, GamePhase.Finished, "結果");
            nextRoundLabel = CreateTimerText(finishedBody.transform.parent, new Vector2(0.5f, 0.25f), 56);
        }

        public void Render(GameSessionState session)
        {
            foreach (var pair in panels) pair.Value.SetActive(pair.Key == session.Phase);
            switch (session.Phase)
            {
                case GamePhase.Waiting:
                    waitingBody.text = BuildWaitingText(session.Participants);
                    break;
                case GamePhase.Finished:
                    finishedBody.text = BuildResultText(session.Result);
                    break;
            }
        }

        /// <summary>毎フレーム呼ぶ。残り時間の表示だけを更新する（フェーズ遷移はしない）。</summary>
        public void RenderTimer(GameSessionState session, ServerClock clock)
        {
            if (session.Phase == GamePhase.Playing) playingTimer.Render(session, clock);
            else if (session.Phase == GamePhase.Preparing) preparingTimer.Render(session, clock);
            else if (session.Phase == GamePhase.Finished) nextRoundLabel.text = BuildNextRoundText(session, clock);
        }

        private static Text CreateTimerText(Transform parent, Vector2 anchor, int fontSize)
        {
            var go = new GameObject("Timer", typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform;
            rect.anchorMin = rect.anchorMax = anchor;
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, -10f);
            rect.sizeDelta = new Vector2(600f, fontSize * 1.4f);
            var text = go.GetComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = fontSize;
            text.fontStyle = FontStyle.Bold;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;
            // 親（半透明の黒帯）の外へ出てもタップを奪わない
            text.raycastTarget = false;
            return text;
        }

        private static string BuildNextRoundText(GameSessionState session, ServerClock clock)
        {
            long? remaining = session.IsNextRoundScheduled ? clock.RemainingMs(session.PhaseEndsAtMs.Value) : (long?)null;
            return TimerFormat.FormatNextRound(remaining);
        }

        public static string BuildWaitingText(IReadOnlyList<string> participants)
        {
            var builder = new StringBuilder("参加者を待っています");
            if (participants.Count > 0) builder.Append($"\n\n参加者 ({participants.Count})");
            foreach (var name in participants) builder.Append('\n').Append(name);
            return builder.ToString();
        }

        public static string BuildResultText(GameResult result)
        {
            var builder = new StringBuilder("ゲーム終了\n\n");
            if (result.isCaptured.HasValue) builder.Append(result.isCaptured.Value ? "ドロを捕まえた！" : "ドロは逃げ切った");
            if (result.score.HasValue) builder.Append($"\nスコア: {result.score.Value}");
            return builder.ToString();
        }

        private Text CreateFullScreenPanel(Transform canvas, GamePhase phase, string initialText)
        {
            var panel = new GameObject($"Panel_{phase}", typeof(RectTransform), typeof(Image));
            panel.transform.SetParent(canvas, false);
            panel.GetComponent<Image>().color = FullScreenTint;
            var rect = (RectTransform)panel.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;

            var labelObject = new GameObject("Label", typeof(RectTransform), typeof(Text));
            labelObject.transform.SetParent(panel.transform, false);
            var labelRect = (RectTransform)labelObject.transform;
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = new Vector2(60f, 60f);
            labelRect.offsetMax = new Vector2(-60f, -60f);
            var label = labelObject.GetComponent<Text>();
            label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            label.fontSize = 56;
            label.alignment = TextAnchor.MiddleCenter;
            label.color = Color.white;
            label.text = initialText;

            panel.SetActive(false);
            panels[phase] = panel;
            return label;
        }
    }
}
