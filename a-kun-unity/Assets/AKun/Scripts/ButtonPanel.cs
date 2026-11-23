using System;
using System.Collections.Generic;
using UnityEngine;

namespace AKun
{
    // XR 空間に浮かぶボタンとスライダー (Web 版 a-kun/src/panel.js の移植)。
    // 指先で押す・なぞるか、手から伸びるレイで指してつまむ (OS のカーソルと同じ)。
    // 板の表は -Z (ローカル) を向く。x 右、y 上 (m)
    public class ButtonDef
    {
        public string id;
        public Func<string> label = () => "";
        public float x, y, w, h;
        public Action onPress;
        public Func<bool> isActive, isEnabled;
        public string color;      // アクセント色 (#rrggbb)
        public bool swatch;       // 色見本 (常に color で塗り、選ばれていると白い太枠)
        public bool slider;
        public float min, max;
        public Func<float> get;
        public Action<float> set;
        public Func<float, string> format;
    }

    public class ButtonPanel
    {
        public readonly Transform group;
        public readonly List<Btn> buttons = new List<Btn>();
        public readonly HashSet<string> hover = new HashSet<string>(); // レイが指しているボタン (毎フレーム入れ直す)
        public float minX, maxX, minY, maxY; // 板の範囲

        static readonly Color C_BTN = Gfx.Hex("#2c313b"), C_BTN_HOVER = Gfx.Hex("#3b4250"), C_EDGE = Gfx.Hex("#4a5160");
        static readonly Color C_TEXT = Gfx.Hex("#e8eaee"), C_TEXT_DARK = Gfx.Hex("#111111"), C_SUB = Gfx.Hex("#c9ced8");

        public class Btn
        {
            public ButtonDef def;
            public MeshRenderer bg, track, fill, knob;
            public TextMesh text, valueText;
            public string key = "";
            public float pressedAt = -1e9f;
            public bool hovered;
            public int sliderStep = -1;
        }

        // 音を鳴らすための知らせ (ボタンの位置、ワールド)。押した / 指し始めた / スライダーの目盛りを越えた
        public static Action<Vector3> Pressed, Hovered, Slid;
        Vector3 WorldPos(Btn b) => group.TransformPoint(new Vector3(b.def.x, b.def.y, 0));

        public ButtonPanel(Transform parent, string name, List<ButtonDef> defs, string[] title = null)
        {
            group = new GameObject(name).transform;
            group.SetParent(parent, false);
            title ??= new string[0];

            // 背景の板
            minX = float.PositiveInfinity; maxX = float.NegativeInfinity; minY = float.PositiveInfinity; maxY = float.NegativeInfinity;
            foreach (var d in defs)
            {
                minX = Mathf.Min(minX, d.x - d.w / 2); maxX = Mathf.Max(maxX, d.x + d.w / 2);
                minY = Mathf.Min(minY, d.y - d.h / 2); maxY = Mathf.Max(maxY, d.y + d.h / 2);
            }
            const float pad = 0.012f;
            float titleH = title.Length > 0 ? 0.016f * title.Length + 0.01f : 0;
            float plateW = maxX - minX + pad * 2, plateH = maxY - minY + pad * 2 + titleH;
            var plate = Rect(group, (minX + maxX) / 2, (minY + maxY) / 2 + titleH / 2, 0.003f, plateW, plateH, 2978);
            SetRect(plate, Gfx.Hex("#14171c"), Gfx.Hex("#14171c"), plateW, plateH, 0.012f, 0, 0.82f);
            float tx = minX, ty = maxY + pad / 2 + titleH - 0.003f;
            minX -= pad; maxX += pad; minY -= pad; maxY += pad + titleH;

            for (int i = 0; i < title.Length; i++)
            {
                var t = Gfx.Text(group, title[i], 0.0105f, C_SUB, TextAnchor.UpperLeft);
                t.transform.localPosition = new Vector3(tx, ty - i * 0.016f, 0);
            }

            foreach (var d in defs)
            {
                var b = new Btn { def = d };
                b.bg = Rect(group, d.x, d.y, 0, d.w, d.h, 2980);
                if (d.slider)
                {
                    b.track = Rect(group, d.x, d.y - d.h * 0.2f, -0.0005f, d.w - 0.012f, d.h * 0.12f, 2981);
                    b.fill = Rect(group, d.x, d.y - d.h * 0.2f, -0.001f, d.w - 0.012f, d.h * 0.12f, 2982);
                    b.knob = Rect(group, d.x, d.y - d.h * 0.2f, -0.0015f, d.h * 0.26f, d.h * 0.26f, 2983);
                    b.text = Gfx.Text(group, "", d.h * 0.3f, C_TEXT, TextAnchor.MiddleLeft);
                    b.text.transform.localPosition = new Vector3(d.x - d.w / 2 + 0.007f, d.y + d.h * 0.2f, -0.002f);
                    b.valueText = Gfx.Text(group, "", d.h * 0.3f, C_SUB, TextAnchor.MiddleRight);
                    b.valueText.transform.localPosition = new Vector3(d.x + d.w / 2 - 0.007f, d.y + d.h * 0.2f, -0.002f);
                }
                else if (!d.swatch)
                {
                    b.text = Gfx.Text(group, "", d.h * 0.42f, C_TEXT);
                    b.text.transform.localPosition = new Vector3(d.x, d.y, -0.002f);
                }
                buttons.Add(b);
            }
            Refresh();
        }

        static readonly MaterialPropertyBlock mpb = new MaterialPropertyBlock();

        public static MeshRenderer Rect(Transform parent, float x, float y, float z, float w, float h, int queue)
        {
            var go = new GameObject("rect");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(x, y, z);
            go.transform.localScale = new Vector3(w, h, 1);
            go.AddComponent<MeshFilter>().sharedMesh = Gfx.Quad;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = Gfx.Shared("RoundRect", queue);
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return r;
        }

        public static void SetRect(MeshRenderer r, Color fill, Color border, float w, float h, float radius, float borderW, float alpha = 1)
        {
            r.GetPropertyBlock(mpb);
            mpb.SetColor("_Fill", fill);
            mpb.SetColor("_Border", border);
            mpb.SetVector("_Size", new Vector4(w, h, 0, 0));
            mpb.SetFloat("_Radius", radius);
            mpb.SetFloat("_BorderWidth", borderW);
            mpb.SetFloat("_Alpha", alpha);
            r.SetPropertyBlock(mpb);
        }

        // 文字を幅に収める (はみ出すなら小さくする)
        static void Fit(TextMesh t, string s, float lineH, float maxW)
        {
            float cs = lineH / 6.4f;
            t.characterSize = cs;
            t.text = s;
            float w = Measure(t, s);
            if (w > maxW && w > 0) t.characterSize = cs * maxW / w;
        }

        public static float Measure(TextMesh t, string s)
        {
            var f = t.font;
            f.RequestCharactersInTexture(s, t.fontSize, t.fontStyle);
            float adv = 0;
            foreach (char ch in s)
                if (f.GetCharacterInfo(ch, out var ci, t.fontSize, t.fontStyle)) adv += ci.advance;
            return adv * t.characterSize * 0.1f;
        }

        public void Refresh()
        {
            float now = Time.unscaledTime;
            foreach (var b in buttons)
            {
                var d = b.def;
                string label = d.label();
                bool hov = hover.Contains(d.id);
                if (hov && !b.hovered && (d.isEnabled == null || d.isEnabled())) Hovered?.Invoke(WorldPos(b));
                b.hovered = hov;
                if (d.slider)
                {
                    float v = d.get();
                    string val = d.format != null ? d.format(v) : v.ToString("0.00");
                    string key = $"{label}|{val}|{hov}";
                    if (key == b.key) continue;
                    b.key = key;
                    SetRect(b.bg, hov ? C_BTN_HOVER : C_BTN, hov ? Color.white : C_BTN, d.w, d.h, 0.006f, hov ? 0.0015f : 0);
                    b.text.text = label;
                    b.valueText.text = val;
                    float t = Mathf.InverseLerp(d.min, d.max, v);
                    float tw = d.w - 0.012f, th = d.h * 0.12f;
                    SetRect(b.track, Gfx.Hex("#4a5160"), Color.clear, tw, th, th / 2, 0);
                    float fw = Mathf.Max(th, tw * t);
                    b.fill.transform.localScale = new Vector3(fw, th, 1);
                    b.fill.transform.localPosition = new Vector3(d.x - tw / 2 + fw / 2, d.y - d.h * 0.2f, -0.001f);
                    SetRect(b.fill, Gfx.Hex(d.color ?? "#c98060"), Color.clear, fw, th, th / 2, 0);
                    float ks = d.h * 0.26f;
                    b.knob.transform.localPosition = new Vector3(d.x - tw / 2 + tw * t, d.y - d.h * 0.2f, -0.0015f);
                    SetRect(b.knob, Color.white, Color.clear, ks, ks, ks / 2, 0);
                    continue;
                }
                bool active = d.isActive != null && d.isActive();
                bool enabled = d.isEnabled == null || d.isEnabled();
                bool flash = now - b.pressedAt < 0.18f;
                string k2 = $"{active}|{flash}|{label}|{enabled}|{hov}";
                if (k2 == b.key) continue;
                b.key = k2;
                float alpha = enabled ? 1 : 0.35f;
                float rad = Mathf.Min(0.008f, d.h * 0.3f);
                if (d.swatch)
                {
                    bool ring = active || hov;
                    SetRect(b.bg, Gfx.Hex(d.color), ring ? Color.white : C_EDGE, d.w, d.h, rad, ring ? 0.0028f : 0.0008f, alpha);
                }
                else
                {
                    Color fill = flash ? Color.white : active ? Gfx.Hex(d.color ?? "#4fc3f7") : hov && enabled ? C_BTN_HOVER : C_BTN;
                    Color edge = active || (hov && enabled) ? Color.white : C_EDGE;
                    SetRect(b.bg, fill, edge, d.w, d.h, rad, hov && enabled ? 0.0016f : 0.0008f, alpha);
                    var tc = active || flash ? C_TEXT_DARK : C_TEXT;
                    tc.a = alpha;
                    b.text.color = tc;
                    Fit(b.text, label, d.h * 0.42f, d.w - 0.008f);
                }
            }
        }

        // ---------- レイ (指して、つまんでクリック) ----------

        public class Hit
        {
            public ButtonPanel panel;
            public Btn button;   // なければ null (板のすきま)
            public Vector3 point;
            public float t;
        }

        // レイを板の座標に直して、板の面との交点 (ローカル x, y) と距離を返す。板の表側から当たらなければ false
        bool RayLocal(Vector3 origin, Vector3 dir, out Vector2 p, out float t)
        {
            p = default; t = 0;
            var o = group.InverseTransformPoint(origin);
            var d = group.InverseTransformDirection(dir);
            if (o.z >= 0 || d.z <= 1e-4f) return false;
            t = -o.z / d.z;
            if (t < 0.03f || t > 4) return false;
            p = new Vector2(o.x + d.x * t, o.y + d.y * t);
            return true;
        }

        bool Clickable(Btn b) => (b.def.onPress != null || b.def.slider) && (b.def.isEnabled == null || b.def.isEnabled());

        public Hit RayHit(Vector3 origin, Vector3 dir)
        {
            if (!group.gameObject.activeInHierarchy) return null;
            if (!RayLocal(origin, dir, out var l, out float t)) return null;
            if (l.x < minX || l.x > maxX || l.y < minY || l.y > maxY) return null;
            Btn button = null;
            float best = float.PositiveInfinity;
            foreach (var b in buttons)
            {
                if (!Clickable(b)) continue;
                // ボタンの間のすきまでも、いちばん近いボタンを指す (ボタンの外 1cm まで)
                float dx = Mathf.Max(0, Mathf.Abs(l.x - b.def.x) - b.def.w / 2), dy = Mathf.Max(0, Mathf.Abs(l.y - b.def.y) - b.def.h / 2);
                float dist = Mathf.Sqrt(dx * dx + dy * dy);
                if (dist < 0.01f && dist < best) { best = dist; button = b; }
            }
            return new Hit { panel = this, button = button, point = group.TransformPoint(new Vector3(l.x, l.y, 0)), t = t };
        }

        // つまみ始め (クリック)。スライダーは値を決め、ボタンは押す
        public void RayDown(Hit hit)
        {
            var b = hit.button;
            if (b == null) return;
            if (b.def.slider) { SliderTo(b, group.InverseTransformPoint(hit.point).x); return; }
            b.pressedAt = Time.unscaledTime;
            b.def.onPress();
            Pressed?.Invoke(WorldPos(b));
        }

        // つまんだまま動かす: スライダーは、レイが板からはずれても左右の位置で値を決め続ける
        public void RayDrag(Btn b, Vector3 origin, Vector3 dir)
        {
            if (b == null || !b.def.slider) return;
            if (RayLocal(origin, dir, out var l, out _)) SliderTo(b, l.x);
        }

        void SliderTo(Btn b, float x)
        {
            var d = b.def;
            float t = Mathf.Clamp01((x - (d.x - d.w / 2)) / d.w * 1.1f - 0.05f);
            d.set(d.min + t * (d.max - d.min));
            // 20 目盛りごとに小さく鳴らす (なぞっている手ごたえ)
            int step = Mathf.RoundToInt(t * 20);
            if (step != b.sliderStep) { if (b.sliderStep >= 0) Slid?.Invoke(WorldPos(b)); b.sliderStep = step; }
        }

        // 指先が触れているボタン (なければ null)。localX: 板の上の左右の位置
        public Btn FingerHit(Vector3 tipWorld, out float localX)
        {
            localX = 0;
            if (!group.gameObject.activeInHierarchy) return null;
            var p = group.InverseTransformPoint(tipWorld);
            foreach (var b in buttons)
            {
                if (!Clickable(b)) continue;
                // 表 (-Z) から 1.5cm 手前〜裏へ 2.5cm
                if (Mathf.Abs(p.x - b.def.x) < b.def.w / 2 && Mathf.Abs(p.y - b.def.y) < b.def.h / 2 && p.z > -0.015f && p.z < 0.025f)
                {
                    localX = p.x;
                    return b;
                }
            }
            return null;
        }

        // 指先で押す。pressed: 手ごとの「押している id」(押し続けても 1 回だけ押す)
        public void FingerPress(Btn hit, float localX, ref string pressed)
        {
            if (hit.def.slider)
            {
                SliderTo(hit, localX);
                pressed = hit.def.id;
                return;
            }
            if (pressed != hit.def.id)
            {
                pressed = hit.def.id;
                hit.pressedAt = Time.unscaledTime;
                hit.def.onPress();
                Pressed?.Invoke(WorldPos(hit));
            }
        }

        public bool Visible
        {
            get => group.gameObject.activeSelf;
            set => group.gameObject.SetActive(value);
        }
    }
}
