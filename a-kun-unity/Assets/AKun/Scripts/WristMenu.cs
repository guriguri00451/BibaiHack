using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;

namespace AKun
{
    // 手首メニュー: 左手のひらを上 (自分の方) に向けると、手のひらの上に小さなパネルが出る。
    // 右手の指先で押すか、レイで指してつまむ (ほかのパネルと同じ ButtonPanel)。
    // 粘土のそばや、つまんでいる間は出さない (こねている手のひらが上を向いても開かないように)
    public class WristMenu
    {
        public readonly ButtonPanel panel;
        bool open, forced;
        float wantSince = -1;
        bool placed;

        public bool Open => open;

        public WristMenu(List<ButtonDef> defs)
        {
            panel = new ButtonPanel(null, "wrist-menu", defs, new[] { "手首メニュー (右手で押す)" });
            panel.Visible = false;
        }

        // 画面で見るとき (XR が無い): M キーで出し入れする
        public void Toggle(Transform head)
        {
            forced = !forced;
            open = forced;
            panel.Visible = open;
            if (!open) return;
            var fwd = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            var right = Vector3.Cross(Vector3.up, fwd);
            Place(head.position + fwd * 0.35f - right * 0.12f + Vector3.down * 0.12f, head.position, 1);
        }

        public void Update(HandState left, Transform head, bool allowed, float dt)
        {
            if (forced) return;
            bool tracked = left.connected
                && left.valid.Contains(XRHandJointID.Wrist) && left.valid.Contains(XRHandJointID.IndexProximal)
                && left.valid.Contains(XRHandJointID.LittleProximal) && left.valid.Contains(XRHandJointID.MiddleProximal);
            bool want = false;
            Vector3 palm = default, n = default;
            if (tracked)
            {
                var w = left.jreal[XRHandJointID.Wrist];
                var ip = left.jreal[XRHandJointID.IndexProximal];
                var lp = left.jreal[XRHandJointID.LittleProximal];
                var mp = left.jreal[XRHandJointID.MiddleProximal];
                // 左手: (人差し指の付け根 − 手首) × (小指の付け根 − 手首) が手のひらの向き
                n = Vector3.Cross(ip - w, lp - w).normalized;
                palm = (w + ip + lp + mp) * 0.25f;
                var toHead = (head.position - palm).normalized;
                float up = Vector3.Dot(n, Vector3.up), face = Vector3.Dot(n, toHead);
                want = allowed && (open ? up > 0.25f && face > -0.1f : up > 0.55f && face > 0.2f);
            }
            if (want && !open)
            {
                if (wantSince < 0) wantSince = Time.time;
                if (Time.time - wantSince > 0.25f)
                {
                    open = true;
                    placed = false;
                    Sfx.Play("pop", palm, 0.35f, 1.7f);
                }
            }
            else if (!want)
            {
                wantSince = -1;
                if (open) { open = false; Sfx.Play("hover", palm, 0.6f, 0.7f); }
            }
            if (panel.Visible != open) panel.Visible = open;
            if (!open) return;
            Place(palm + n * 0.045f, head.position, placed ? 1 - Mathf.Exp(-20 * dt) : 1);
            placed = true;
        }

        // anchor: パネルの下の辺の中ほどを置く所。板の表を頭に向ける
        void Place(Vector3 anchor, Vector3 headPos, float k)
        {
            var look = anchor - headPos;
            if (look.sqrMagnitude < 1e-6f) look = Vector3.forward;
            var rot = Quaternion.LookRotation(look.normalized, Vector3.up);
            var centerLocal = new Vector3((panel.minX + panel.maxX) / 2, (panel.minY + panel.maxY) / 2, 0);
            float h = panel.maxY - panel.minY;
            var pos = anchor + rot * (Vector3.up * (h / 2 + 0.01f)) - rot * centerLocal;
            var g = panel.group;
            g.position = Vector3.Lerp(g.position, pos, k);
            g.rotation = Quaternion.Slerp(g.rotation, rot, k);
        }
    }
}
