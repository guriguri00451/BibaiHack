using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;

namespace AKun
{
    // 最初の説明: 白い半透明の手 (お手本の手) が、粘土の上で「なでる」「こねる」「つまむ」を 1 回ずつやって見せる。
    // お手本の手は粘土を変えない。起動して粘土が目の前に来たときと、道具のパネルの「使い方」で始まる。
    // 今の説明 (Caption) は「今やること」の帯に出す
    public class Tutorial
    {
        readonly HandLook look;
        float t0 = -1;
        int lastPhase = -1;
        const float PHASE = 3.6f;
        static readonly string[] STEPS =
        {
            "1/3 なでる: 手のひらで軽く触れて、表面をなでる",
            "2/3 こねる: 深く押し込むと、こねて形が変わる",
            "3/3 つまむ: 指でつまんで、引っ張って伸ばす",
        };

        public bool Active => t0 >= 0;
        public string Caption => Active && lastPhase >= 0 && lastPhase < STEPS.Length ? STEPS[lastPhase] : null;

        public Tutorial()
        {
            look = new HandLook("tutorial-hand");
        }

        public void Begin()
        {
            t0 = Time.time;
            lastPhase = -1;
        }

        public void Stop()
        {
            t0 = -1;
            look.Begin(); look.Draw();
        }

        // clayCenter / clayRadius: 粘土の中心と半径 (m)。head: 頭
        public void Update(Vector3 clayCenter, float clayRadius, Transform head)
        {
            if (t0 < 0) return;
            float t = Time.time - t0;
            int phase = (int)(t / PHASE);
            if (phase >= STEPS.Length) { Stop(); return; }
            float u = t / PHASE - phase;
            if (phase != lastPhase)
            {
                lastPhase = phase;
                Sfx.Play("hover", clayCenter, 0.8f, 1.2f);
            }

            var d = Vector3.ProjectOnPlane(head.position - clayCenter, Vector3.up);
            if (d.sqrMagnitude < 1e-4f) d = Vector3.back;
            d.Normalize();
            var right = Vector3.Cross(Vector3.up, -d); // 見ている人から見て右

            // お手本の手の姿勢
            float pinch = 0;
            Vector3 pos; Quaternion rot;
            if (phase < 2)
            {
                // 手前の面 (右寄り、やや下) に手のひらを当てる。指は上へ。見ている人には手の甲が見える。
                // 指先が粘土のてっぺんより上 (投稿のパネル) にかからない高さにする
                var n = (d * 0.9f + right * 0.35f - Vector3.up * 0.15f).normalized;
                var s = clayCenter + n * clayRadius;
                var tangent = Vector3.ProjectOnPlane(Vector3.up, n).normalized;
                var side = Vector3.Cross(n, tangent);
                rot = Quaternion.LookRotation(tangent, n);
                float off;
                var slide = Vector3.zero;
                if (phase == 0) { off = 0.006f; slide = side * (0.05f * Mathf.Sin(2 * Mathf.PI * u * 2)); }
                else off = 0.012f - 0.032f * Mathf.Max(0, Mathf.Sin(2 * Mathf.PI * u * 2));
                var palmTarget = s + n * off + slide;
                pos = palmTarget - rot * new Vector3(0, -0.012f, 0.06f);
            }
            else
            {
                // 手前の面 (右寄り) を、人差し指と親指でつまんで引っ張る
                var n = (d * 0.9f + right * 0.35f).normalized;
                var s = clayCenter + n * clayRadius;
                rot = Quaternion.LookRotation(-n, Vector3.ProjectOnPlane(Vector3.up, n).normalized);
                float off;
                if (u < 0.3f) { off = Mathf.Lerp(0.09f, 0.0f, u / 0.3f); pinch = 0; }
                else if (u < 0.45f) { off = 0; pinch = (u - 0.3f) / 0.15f; }
                else if (u < 0.85f) { off = Mathf.Lerp(0, 0.07f, (u - 0.45f) / 0.4f); pinch = 1; }
                else { off = 0.07f; pinch = 1 - (u - 0.85f) / 0.15f; }
                pos = s + n * off - rot * PINCH_POINT;
            }

            float fade = Mathf.Clamp01(t / 0.4f) * Mathf.Clamp01((STEPS.Length * PHASE - t) / 0.4f);
            var joints = Pose(pinch);
            look.Begin();
            foreach (var b in HandBones.BONES)
            {
                if (!joints.TryGetValue(b.a, out var a) || !joints.TryGetValue(b.b, out var bb)) continue;
                look.AddBone(pos + rot * a, pos + rot * bb, b.r > 0 ? b.r : HandBones.FINGER_R * 0.9f);
            }
            look.SetColor(new Color(1, 1, 1, 0.55f * fade));
            look.Draw();
        }

        // ---------- お手本の手の形 (右手) ----------
        // 手首が原点、+Z が指の向き、+Y が手の甲、-X が親指の側 (手のひらを下に向けた右手)
        static readonly Vector3 PINCH_POINT = new Vector3(-0.045f, -0.045f, 0.12f);
        static readonly Dictionary<XRHandJointID, Vector3> OPEN = new Dictionary<XRHandJointID, Vector3>
        {
            [XRHandJointID.Wrist] = new Vector3(0, 0, 0),
            [XRHandJointID.IndexMetacarpal] = new Vector3(-0.02f, 0, 0.01f),
            [XRHandJointID.IndexProximal] = new Vector3(-0.024f, 0, 0.09f),
            [XRHandJointID.IndexIntermediate] = new Vector3(-0.026f, -0.004f, 0.13f),
            [XRHandJointID.IndexDistal] = new Vector3(-0.027f, -0.006f, 0.155f),
            [XRHandJointID.IndexTip] = new Vector3(-0.028f, -0.008f, 0.175f),
            [XRHandJointID.MiddleMetacarpal] = new Vector3(-0.004f, 0, 0.01f),
            [XRHandJointID.MiddleProximal] = new Vector3(-0.004f, 0, 0.092f),
            [XRHandJointID.MiddleIntermediate] = new Vector3(-0.004f, -0.004f, 0.137f),
            [XRHandJointID.MiddleDistal] = new Vector3(-0.004f, -0.006f, 0.165f),
            [XRHandJointID.MiddleTip] = new Vector3(-0.004f, -0.008f, 0.187f),
            [XRHandJointID.RingMetacarpal] = new Vector3(0.012f, 0, 0.01f),
            [XRHandJointID.RingProximal] = new Vector3(0.014f, 0, 0.086f),
            [XRHandJointID.RingIntermediate] = new Vector3(0.016f, -0.004f, 0.126f),
            [XRHandJointID.RingDistal] = new Vector3(0.017f, -0.006f, 0.152f),
            [XRHandJointID.RingTip] = new Vector3(0.018f, -0.008f, 0.172f),
            [XRHandJointID.LittleMetacarpal] = new Vector3(0.025f, 0, 0.012f),
            [XRHandJointID.LittleProximal] = new Vector3(0.03f, 0, 0.078f),
            [XRHandJointID.LittleIntermediate] = new Vector3(0.033f, -0.004f, 0.108f),
            [XRHandJointID.LittleDistal] = new Vector3(0.035f, -0.006f, 0.127f),
            [XRHandJointID.LittleTip] = new Vector3(0.036f, -0.008f, 0.145f),
            [XRHandJointID.ThumbMetacarpal] = new Vector3(-0.022f, -0.012f, 0.018f),
            [XRHandJointID.ThumbProximal] = new Vector3(-0.045f, -0.016f, 0.045f),
            [XRHandJointID.ThumbDistal] = new Vector3(-0.062f, -0.018f, 0.072f),
            [XRHandJointID.ThumbTip] = new Vector3(-0.074f, -0.02f, 0.094f),
        };

        // つまんだ形 (人差し指と親指の先が PINCH_POINT で合う)
        static readonly Dictionary<XRHandJointID, Vector3> PINCH = new Dictionary<XRHandJointID, Vector3>
        {
            [XRHandJointID.IndexIntermediate] = new Vector3(-0.03f, -0.022f, 0.125f),
            [XRHandJointID.IndexDistal] = new Vector3(-0.038f, -0.036f, 0.128f),
            [XRHandJointID.IndexTip] = PINCH_POINT + new Vector3(0.003f, 0.004f, 0.002f),
            [XRHandJointID.ThumbDistal] = new Vector3(-0.056f, -0.034f, 0.094f),
            [XRHandJointID.ThumbTip] = PINCH_POINT + new Vector3(-0.003f, -0.004f, -0.002f),
        };

        readonly Dictionary<XRHandJointID, Vector3> pose = new Dictionary<XRHandJointID, Vector3>();

        Dictionary<XRHandJointID, Vector3> Pose(float pinch)
        {
            pose.Clear();
            foreach (var kv in OPEN)
            {
                var p = kv.Value;
                if (PINCH.TryGetValue(kv.Key, out var q)) p = Vector3.Lerp(p, q, pinch);
                else if (kv.Key >= XRHandJointID.MiddleIntermediate && kv.Key <= XRHandJointID.LittleTip && p.z > 0.1f)
                    p += new Vector3(0, -0.03f, -0.025f) * pinch * Mathf.InverseLerp(0.1f, 0.19f, p.z); // ほかの指も軽く曲げる
                pose[kv.Key] = p;
            }
            return pose;
        }
    }
}
