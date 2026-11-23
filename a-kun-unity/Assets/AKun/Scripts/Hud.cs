using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace AKun
{
    // 体験者に見せる案内:
    //   「今やること」の帯 (作業台の上): 工程 (つくる → 隠す → 化ける → 待つ) と、いまやることを 1 行で大きく
    //   残り時間の輪 (帯の右): フェーズの残り時間。少なくなると黄色→赤、最後の 10 秒は点滅
    //   通知 (頭の前、少し下): 「隠しました」などを数秒だけ出して消す。頭の向きにゆっくり付いてくる
    public class Hud
    {
        public readonly Transform banner;
        readonly Camera cam;
        readonly MeshRenderer plate;
        readonly TextMesh mainText, subText;
        const float W = 0.36f, H = 0.1f;

        // 工程
        readonly List<(MeshRenderer bg, TextMesh text)> chips = new List<(MeshRenderer, TextMesh)>();
        readonly List<TextMesh> arrows = new List<TextMesh>();
        string[] stepNames = new string[0];
        int step = -1;
        string chipKey = "";

        // 残り時間の輪
        readonly Transform timer;
        readonly LineRenderer track, arc;
        readonly TextMesh timeText, timeLabel;
        string timerKey;
        double timerTotal = 1;
        const float TIMER_R = 0.034f;
        const int ARC_SEG = 64;

        // 通知
        class ToastItem { public Transform t; public MeshRenderer bg; public TextMesh text; public float t0, life; public Color accent; public float w; }
        readonly Transform toastRoot;
        readonly List<ToastItem> toasts = new List<ToastItem>();
        bool toastPlaced;

        public static readonly Color OK = Gfx.Hex("#81c784"), WARN = Gfx.Hex("#ffb74d"), ERROR = Gfx.Hex("#e57373"), INFO = Gfx.Hex("#4fc3f7");

        public Hud(Transform workspace, Vector3 bannerPos, Camera cam)
        {
            this.cam = cam;
            banner = new GameObject("guide-banner").transform;
            banner.SetParent(workspace, false);
            banner.localPosition = bannerPos;
            plate = ButtonPanel.Rect(banner, 0, 0, 0.002f, W, H, 2978);
            ButtonPanel.SetRect(plate, Gfx.Hex("#14171c"), Gfx.Hex("#2c313b"), W, H, 0.012f, 0.0012f, 0.88f);
            mainText = Gfx.Text(banner, "", 0.02f, Gfx.Hex("#ffffff"));
            mainText.transform.localPosition = new Vector3(0, -0.006f, 0);
            subText = Gfx.Text(banner, "", 0.012f, Gfx.Hex("#c9ced8"));
            subText.transform.localPosition = new Vector3(0, -0.033f, 0);

            // 残り時間の輪
            timer = new GameObject("timer").transform;
            timer.SetParent(banner, false);
            timer.localPosition = new Vector3(W / 2 + TIMER_R + 0.016f, 0, 0);
            var disc = ButtonPanel.Rect(timer, 0, 0, 0.002f, TIMER_R * 2 + 0.014f, TIMER_R * 2 + 0.014f, 2978);
            ButtonPanel.SetRect(disc, Gfx.Hex("#14171c"), Gfx.Hex("#14171c"), TIMER_R * 2 + 0.014f, TIMER_R * 2 + 0.014f, TIMER_R + 0.007f, 0, 0.88f);
            track = Circle(timer, "track", Gfx.Hex("#3b4250"), 2984);
            arc = Circle(timer, "arc", Color.white, 2985);
            timeText = Gfx.Text(timer, "", 0.017f, Color.white);
            timeText.transform.localPosition = new Vector3(0, 0.002f, -0.001f);
            timeLabel = Gfx.Text(timer, "", 0.008f, Gfx.Hex("#c9ced8"));
            timeLabel.transform.localPosition = new Vector3(0, -0.016f, -0.001f);
            timer.gameObject.SetActive(false);

            toastRoot = new GameObject("toasts").transform;
        }

        static LineRenderer Circle(Transform parent, string name, Color c, int queue)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var l = go.AddComponent<LineRenderer>();
            l.useWorldSpace = false;
            l.alignment = LineAlignment.TransformZ;
            l.widthMultiplier = 0.006f;
            l.numCapVertices = 4;
            l.sharedMaterial = Gfx.Unlit(Color.white, queue);
            l.startColor = l.endColor = c;
            l.shadowCastingMode = ShadowCastingMode.Off;
            l.receiveShadows = false;
            SetArc(l, 1);
            return l;
        }

        // 12 時から時計回りに frac (0〜1) だけ描く
        static void SetArc(LineRenderer l, float frac)
        {
            int n = Mathf.Max(2, Mathf.CeilToInt(ARC_SEG * Mathf.Clamp01(frac)) + 1);
            l.positionCount = n;
            for (int i = 0; i < n; i++)
            {
                float a = Mathf.PI / 2 - 2 * Mathf.PI * frac * i / (n - 1);
                l.SetPosition(i, new Vector3(Mathf.Cos(a) * TIMER_R, Mathf.Sin(a) * TIMER_R, -0.0015f));
            }
        }

        static void Fit(TextMesh t, string s, float lineH, float maxW)
        {
            float cs = lineH / 6.4f;
            if (t.text == s && t.characterSize <= cs) return;
            t.characterSize = cs;
            t.text = s;
            float w = ButtonPanel.Measure(t, s);
            if (w > maxW && w > 0) t.characterSize = cs * maxW / w;
        }

        public bool Visible { get => banner.gameObject.activeSelf; set => banner.gameObject.SetActive(value); }

        // ---------- 今やること ----------
        // steps: 工程の名前、current: 今の工程 (-1 なら工程を出さない)
        public void SetGuide(string main, string sub, string[] steps, int current)
        {
            Fit(mainText, main ?? "", 0.02f, W - 0.02f);
            Fit(subText, sub ?? "", 0.012f, W - 0.02f);
            string key = string.Join("|", steps ?? new string[0]) + "#" + current;
            if (key == chipKey) return;
            chipKey = key;
            stepNames = steps ?? new string[0];
            step = current;
            // 工程の札を作り直す (数は少ないので毎回でよい)
            foreach (var c in chips) { Object.Destroy(c.bg.gameObject); Object.Destroy(c.text.gameObject); }
            foreach (var a in arrows) Object.Destroy(a.gameObject);
            chips.Clear(); arrows.Clear();
            int n = stepNames.Length;
            if (n == 0) return;
            const float cw = 0.062f, ch = 0.02f, gap = 0.018f;
            float total = n * cw + (n - 1) * gap, y = H / 2 - 0.006f - ch / 2;
            for (int i = 0; i < n; i++)
            {
                float x = -total / 2 + cw / 2 + i * (cw + gap);
                bool now = i == current, done = current >= 0 && i < current;
                var bg = ButtonPanel.Rect(banner, x, y, 0, cw, ch, 2980);
                var fill = now ? INFO : done ? Gfx.Hex("#3e5b47") : Gfx.Hex("#2c313b");
                ButtonPanel.SetRect(bg, fill, now ? Color.white : fill, cw, ch, ch / 2, now ? 0.0012f : 0, 1);
                var t = Gfx.Text(banner, stepNames[i], ch * 0.55f, now ? Gfx.Hex("#111111") : done ? Gfx.Hex("#c8e6c9") : Gfx.Hex("#8a93a3"));
                t.transform.localPosition = new Vector3(x, y, -0.002f);
                chips.Add((bg, t));
                if (i < n - 1)
                {
                    var a = Gfx.Text(banner, "→", ch * 0.6f, Gfx.Hex("#6b7383"));
                    a.transform.localPosition = new Vector3(x + cw / 2 + gap / 2, y, -0.002f);
                    arrows.Add(a);
                }
            }
        }

        // ---------- 残り時間 ----------
        // key: フェーズが変わったと分かる文字列 (ラウンド・フェーズ・期限)。remaining: 残り秒 (null なら輪を出さない)
        public void SetTimer(string key, double? remaining, string label)
        {
            if (remaining == null) { if (timer.gameObject.activeSelf) timer.gameObject.SetActive(false); timerKey = null; return; }
            if (!timer.gameObject.activeSelf) timer.gameObject.SetActive(true);
            double rem = remaining.Value;
            if (key != timerKey) { timerKey = key; timerTotal = System.Math.Max(1, rem); }
            float frac = (float)System.Math.Max(0, System.Math.Min(1, rem / timerTotal));
            SetArc(arc, frac);
            Color c = rem > 30 ? OK : rem > 10 ? WARN : ERROR;
            if (rem <= 10 && rem > 0) c = Color.Lerp(c, Color.white, 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 12));
            arc.startColor = arc.endColor = c;
            int s = Mathf.CeilToInt((float)rem);
            timeText.text = $"{s / 60}:{s % 60:00}";
            timeText.color = rem <= 10 ? ERROR : Color.white;
            timeLabel.text = label ?? "";
        }

        // ---------- 通知 ----------
        public void Toast(string text, Color accent, float life = 3.5f)
        {
            if (string.IsNullOrEmpty(text)) return;
            while (toasts.Count >= 3) { Kill(toasts[0]); toasts.RemoveAt(0); }
            var t = new GameObject("toast").transform;
            t.SetParent(toastRoot, false);
            const float th = 0.03f;
            var bg = ButtonPanel.Rect(t, 0, 0, 0.001f, 0.2f, th, 3200);
            bg.sharedMaterial = Gfx.Shared("RoundRect", 3200, CompareFunction.Always);
            var tx = Gfx.Text(t, "", 0.014f, Color.white, TextAnchor.MiddleCenter, 3201);
            tx.GetComponent<MeshRenderer>().sharedMaterial = Gfx.TextMaterial(CompareFunction.Always, 3201);
            Fit(tx, text, 0.014f, 0.32f);
            float w = Mathf.Clamp(ButtonPanel.Measure(tx, text) + 0.03f, 0.12f, 0.34f);
            bg.transform.localScale = new Vector3(w, th, 1);
            toasts.Add(new ToastItem { t = t, bg = bg, text = tx, t0 = Time.unscaledTime, life = life, accent = accent, w = w });
        }

        static void Kill(ToastItem t) => Object.Destroy(t.t.gameObject);

        public void Update(float dt)
        {
            // 通知: 頭の前 (少し下) にゆっくり付いてくる。粘土の手前の面 (約 45cm) より手前に置く
            if (cam != null)
            {
                var head = cam.transform;
                var fwd = Vector3.ProjectOnPlane(head.forward, Vector3.up);
                if (fwd.sqrMagnitude < 1e-4f) fwd = head.up;
                fwd.Normalize();
                var target = head.position + fwd * 0.38f + Vector3.down * 0.13f;
                var rot = Quaternion.LookRotation(target - head.position, Vector3.up);
                if (!toastPlaced) { toastRoot.SetPositionAndRotation(target, rot); toastPlaced = true; }
                float k = 1 - Mathf.Exp(-4 * dt);
                toastRoot.position = Vector3.Lerp(toastRoot.position, target, k);
                toastRoot.rotation = Quaternion.Slerp(toastRoot.rotation, rot, k);
            }
            float now = Time.unscaledTime;
            for (int i = toasts.Count - 1; i >= 0; i--)
            {
                var t = toasts[i];
                float age = now - t.t0;
                if (age > t.life) { Kill(t); toasts.RemoveAt(i); continue; }
                float a = Mathf.Clamp01(age / 0.15f) * Mathf.Clamp01((t.life - age) / 0.4f);
                // 新しいものが下。古いものは上へ押し上げる
                int fromBottom = toasts.Count - 1 - i;
                var lp = t.t.localPosition;
                float y = fromBottom * 0.036f;
                t.t.localPosition = new Vector3(0, Mathf.Lerp(lp.y, y, 1 - Mathf.Exp(-12 * dt)), 0);
                float pop = age < 0.2f ? 1 + 0.15f * Fx.Wobble(age / 0.2f) : 1;
                t.t.localScale = Vector3.one * pop;
                ButtonPanel.SetRect(t.bg, Gfx.Hex("#14171c"), t.accent, t.w, 0.03f, 0.015f, 0.002f, 0.92f * a);
                var c = Color.white; c.a = a;
                t.text.color = c;
            }
        }
    }
}
