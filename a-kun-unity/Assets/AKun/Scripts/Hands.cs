using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Hands;

namespace AKun
{
    // 手の震えを抑えるフィルタ (One Euro Filter)
    public class OneEuro3
    {
        readonly float minCutoff, beta, dCutoff;
        bool has;
        Vector3 x, dx;
        float t;

        public OneEuro3(float minCutoff = 1.2f, float beta = 6f, float dCutoff = 1f)
        {
            this.minCutoff = minCutoff; this.beta = beta; this.dCutoff = dCutoff;
        }

        static float Alpha(float dt, float cutoff)
        {
            float tau = 1 / (2 * Mathf.PI * cutoff);
            return 1 / (1 + tau / dt);
        }

        public Vector3 Filter(Vector3 v, float now)
        {
            if (!has || now - t > 0.2f)
            {
                has = true; x = v; dx = Vector3.zero; t = now;
                return x;
            }
            float dt = Mathf.Max(now - t, 1e-3f);
            t = now;
            var raw = (v - x) / dt;
            dx = Vector3.Lerp(dx, raw, Alpha(dt, dCutoff));
            float cutoff = minCutoff + beta * dx.magnitude;
            x = Vector3.Lerp(x, v, Alpha(dt, cutoff));
            return x;
        }
    }

    // 手の骨 (手のひら全体をカプセルで表す)。Web 版の BONES と同じ
    public static class HandBones
    {
        public const float PALM_R = 0.014f, FINGER_R = 0.009f; // m (0 = 関節の半径を使う)

        public struct Bone { public XRHandJointID a, b; public float r; }
        public static readonly Bone[] BONES;
        public static readonly XRHandJointID[] JOINTS;

        static HandBones()
        {
            var list = new List<Bone>();
            void Add(XRHandJointID a, XRHandJointID b, float r) => list.Add(new Bone { a = a, b = b, r = r });
            var fingers = new[]
            {
                (XRHandJointID.IndexProximal, XRHandJointID.IndexIntermediate, XRHandJointID.IndexDistal, XRHandJointID.IndexTip),
                (XRHandJointID.MiddleProximal, XRHandJointID.MiddleIntermediate, XRHandJointID.MiddleDistal, XRHandJointID.MiddleTip),
                (XRHandJointID.RingProximal, XRHandJointID.RingIntermediate, XRHandJointID.RingDistal, XRHandJointID.RingTip),
                (XRHandJointID.LittleProximal, XRHandJointID.LittleIntermediate, XRHandJointID.LittleDistal, XRHandJointID.LittleTip),
            };
            foreach (var (p, i, d, t) in fingers)
            {
                Add(XRHandJointID.Wrist, p, PALM_R);
                Add(p, i, 0);
                Add(i, d, 0);
                Add(d, t, 0);
            }
            Add(XRHandJointID.Wrist, XRHandJointID.ThumbMetacarpal, PALM_R);
            Add(XRHandJointID.ThumbMetacarpal, XRHandJointID.ThumbProximal, 0);
            Add(XRHandJointID.ThumbProximal, XRHandJointID.ThumbDistal, 0);
            Add(XRHandJointID.ThumbDistal, XRHandJointID.ThumbTip, 0);
            Add(XRHandJointID.ThumbMetacarpal, XRHandJointID.IndexProximal, PALM_R);   // 親指の付け根のふくらみ
            Add(XRHandJointID.IndexProximal, XRHandJointID.LittleProximal, PALM_R);    // 指の付け根の列
            Add(XRHandJointID.IndexMetacarpal, XRHandJointID.LittleMetacarpal, PALM_R); // 手のひらの中ほど
            BONES = list.ToArray();
            var set = new List<XRHandJointID>();
            foreach (var b in BONES)
            {
                if (!set.Contains(b.a)) set.Add(b.a);
                if (!set.Contains(b.b)) set.Add(b.b);
            }
            JOINTS = set.ToArray();
        }
    }

    // 片手の状態
    public class HandState
    {
        public readonly Handedness side;
        public bool connected;
        public bool reported;

        // 関節: 本物の手 (フィルタ後) / 見えている手 (表面で止まる) / 前のフレームの見えている手
        public readonly Dictionary<XRHandJointID, Vector3> jreal = new Dictionary<XRHandJointID, Vector3>();
        public readonly Dictionary<XRHandJointID, Vector3> jpos = new Dictionary<XRHandJointID, Vector3>();
        public readonly Dictionary<XRHandJointID, Vector3> jprev = new Dictionary<XRHandJointID, Vector3>();
        public readonly Dictionary<XRHandJointID, float> jradius = new Dictionary<XRHandJointID, float>();
        public readonly Dictionary<XRHandJointID, OneEuro3> jointF = new Dictionary<XRHandJointID, OneEuro3>();
        public readonly HashSet<XRHandJointID> valid = new HashSet<XRHandJointID>();
        public readonly HashSet<XRHandJointID> prevValid = new HashSet<XRHandJointID>();

        public readonly OneEuro3 tipF = new OneEuro3(), pinchF = new OneEuro3();
        public Vector3 tip, pinchPos, tipRaw, thumbRaw;

        // 押し込みの制限
        public Vector3 offset, n = Vector3.up;
        public float s, excess, depth, kneadK;

        // つまむ
        public bool pinching;
        public string pinchMode; // "pull" / "move" / "none"
        public Vector3 lastG, grabOffset;

        // さわる・塗る
        public bool touching, contact;
        public float lastContactT;
        public string pressed;

        // レイ
        public ButtonPanel.Hit uiHit;
        public ButtonPanel uiPinchPanel;
        public ButtonPanel.Btn uiPinchButton;
        public bool uiPinch;
        public readonly InputAction rayPos, rayRot, rayTracked;

        public HandState(Handedness side)
        {
            this.side = side;
            foreach (var j in HandBones.JOINTS) { jointF[j] = new OneEuro3(); jreal[j] = jpos[j] = jprev[j] = Vector3.zero; }
            string usage = side == Handedness.Left ? "{LeftHand}" : "{RightHand}";
            // OS と同じ手のレイ (OpenXR の Hand Interaction Profile の aim pose)
            rayPos = new InputAction($"rayPos{side}", InputActionType.Value, $"<HandInteraction>{usage}/pointerPosition");
            rayRot = new InputAction($"rayRot{side}", InputActionType.Value, $"<HandInteraction>{usage}/pointerRotation");
            rayTracked = new InputAction($"rayTracked{side}", InputActionType.Button, $"<HandInteraction>{usage}/isTracked");
            rayPos.Enable(); rayRot.Enable(); rayTracked.Enable();
        }

        public void ResetTouch()
        {
            offset = Vector3.zero; s = 0; excess = 0; depth = 0; kneadK = 0;
            touching = false; contact = false;
            prevValid.Clear();
        }

        public string Label => side == Handedness.Left ? "左" : "右";
    }

    // 見えている手: 骨ごとのカプセル (当たり判定と同じ形) を半透明で描く。
    // 重なった所が濃くならないように、まず深さだけを書き (HandDepth)、そのあと手前の面だけを塗る (HandColor)。
    // 部品 (筒と球) ごとに MeshRenderer を持ち、材質を 2 つ付けて 2 回描く
    public class HandLook
    {
        static Mesh cyl, sph;
        readonly Material depthMat, colorMat;
        readonly List<Transform> cyls = new List<Transform>(), sphs = new List<Transform>();
        readonly Transform root;
        int nc, ns;
        public float k, w; // こねる度合い / さわっている度合い (なめらかに追う)

        public HandLook(string name)
        {
            cyl ??= Gfx.Cylinder(16);
            sph ??= Gfx.Sphere(16, 12);
            depthMat = new Material(Gfx.Find("HandDepth"));
            colorMat = new Material(Gfx.Find("HandColor"));
            root = new GameObject(name).transform;
            int nb = HandBones.BONES.Length;
            for (int i = 0; i < nb; i++) cyls.Add(Part(cyl, "bone"));
            for (int i = 0; i < nb * 2; i++) sphs.Add(Part(sph, "joint"));
        }

        Transform Part(Mesh mesh, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterials = new[] { depthMat, colorMat };
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            go.SetActive(false);
            return go.transform;
        }

        public void Begin() { nc = 0; ns = 0; }

        public void AddBone(Vector3 a, Vector3 b, float r)
        {
            var d = b - a;
            float len = d.magnitude;
            var q = len > 1e-5f ? Quaternion.FromToRotation(Vector3.up, d / len) : Quaternion.identity;
            var c = cyls[nc++];
            c.SetPositionAndRotation((a + b) * 0.5f, q);
            c.localScale = new Vector3(r, Mathf.Max(len, 1e-4f), r);
            var ss = Vector3.one * (r * 0.985f); // 円柱と同じ大きさだと表面が重なってちらつくので、ほんの少し小さくする
            var sa = sphs[ns++]; sa.SetPositionAndRotation(a, Quaternion.identity); sa.localScale = ss;
            var sb = sphs[ns++]; sb.SetPositionAndRotation(b, Quaternion.identity); sb.localScale = ss;
        }

        public void SetColor(Color c) => colorMat.SetColor("_BaseColor", c);

        // 使った部品だけを出す
        public void Draw()
        {
            for (int i = 0; i < cyls.Count; i++) { bool on = i < nc; if (cyls[i].gameObject.activeSelf != on) cyls[i].gameObject.SetActive(on); }
            for (int i = 0; i < sphs.Count; i++) { bool on = i < ns; if (sphs[i].gameObject.activeSelf != on) sphs[i].gameObject.SetActive(on); }
        }
    }
}
