using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.Rendering;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.Hands;
using UnityEngine.XR.Management;

namespace AKun
{
    // めっちゃ美唄: 粘土でモデルを作って、色を塗って、投稿する (Web 版 a-kun/src/main.js の XR 部分の移植)。
    // シーンには この部品 1 つだけを置き、カメラ・パススルー・粘土・パネルはすべて実行時に組み立てる。
    // 座標: 作業台 (workspace) の +Z が奥 (ユーザーから見て向こう)。パネルはユーザー側 (-Z) に置く
    public class ClayApp : MonoBehaviour
    {
        public const string VERSION = "1.0.0";

        // ================= 設定 =================
        // 粘土の大きさ: 最初の球 (半径 8.65cm。一辺 14cm の立方体と同じ体積) を 2.45 倍にして、直径 約 42cm にする。
        // 格子 (80³) は同じなので、1 マスは約 1.2cm になる
        const float CUBE_SCALE = 2.45f;
        const float START_R = 0.0865f;                 // 最初の球の半径 (粘土の座標, m)
        const float CUBE_HALF = START_R * CUBE_SCALE;  // 最初の球の半径 (m)
        const float PANEL_Z = CUBE_HALF + 0.06f;       // パネルを置く位置: 球の手前から 6cm 手前
        const float PROP_BEHIND = 0.45f;               // 粘土の性質のパネル: 頭からこれだけ後ろ (m)

        // 押し込みの深さ (本物の手が粘土の表面からめり込んだ深さ, m) と、なでる→こねるの切り替え
        static float kneadFrom = 0.015f;  // これより浅いと、こねずになでるだけ
        const float kneadSpan = 0.015f;   // ここから kneadFrom + kneadSpan まで、なでる→こねるへなめらかに切り替わる

        // 塗るときの色 (sRGB)
        static readonly string[] PALETTE =
        {
            "#c98060", "#8d5a3b", "#f2e6d0", "#ffffff", "#3b3b3b", "#d84a3a",
            "#f29a38", "#f2d14a", "#6bbf59", "#3f8fd2", "#7a5cc7", "#ef8fb5",
        };
        string mode = "touch";            // touch (なでる・こねる) / paint (塗る)
        float brushR = 0.03f;
        string color = PALETTE[0];
        float GrabR => brushR * 2.2f;
        const float PINCH_ON = 0.02f, PINCH_OFF = 0.04f, PRE_PINCH = 0.05f; // m

        // 粘性: 手が動いた量のうち、粘土が付いてくる割合
        static float viscF = 0.012f;      // 手の表面からどこまで粘土が引きずられるか (m)
        static float viscPush = 0.25f;    // 押し込む向き (小さいほど硬い)
        static float viscSlide = 0.45f;   // 表面に沿ってなでる向き
        static float viscStick = 0.25f;   // 手を離す向き (少しだけ引っ張られる)
        const float viscMaxStep = 0.012f; // 1 フレームで動かす上限 (m)

        // 押し込みの制限 (タッチリミッティング / 疑似触覚)
        // 本物の手が粘土にめり込んでも、見えている手は表面近くで徐々に止まる。
        // 見えている手との差 (押し込み過ぎた分) は「押す力」として、粘土をゆっくり流れさせる。
        static float limitDepth = 0.012f;   // 見えている手がめり込める深さの上限 (m)
        static float limitPressure = 1.0f;  // 押し込み過ぎた分 1m あたり、毎秒どれだけ粘土を流すか (1/s)
        const float limitFollow = 25f;      // 見えている手が目標に追いつく速さ (1/s)
        const float limitRelease = 10f;     // 離したとき見えている手が本物の手に戻る速さ (1/s)

        // 手の見た目 (半透明の肌色。なでている間は水色、こねている間は橙色に寄せて、今どちらかが分かるようにする)
        const float HAND_OPACITY = 0.5f;
        static readonly Color HAND_BASE = Gfx.Hex("#e3c4b3");
        static readonly Color HAND_STROKE = Gfx.Hex("#b9dcef");
        static readonly Color HAND_KNEAD = Gfx.Hex("#f2a66e");
        // 触れている所の輪の色 (モードのボタンと同じ水色 / こねるの橙色)
        static readonly Color RING_STROKE = Gfx.Hex("#4fc3f7");
        static readonly Color RING_KNEAD = Gfx.Hex("#ff9e40");

        // 粘土の表面からこの距離 (m) より近くでつまんだときは、粘土ごと動かす・回す操作を始めない
        const float NO_MOVE_NEAR = 0.06f;
        // 格子が粗いので、手の太さ・影響の幅が格子 1 マスより細くなりすぎないようにする (格子単位)
        const float MIN_R_GRID = 1.2f, MIN_F_GRID = 1.5f;
        // 頭の前に置く: forward = 粘土の手前の面までの距離 (m)。drop = 0: 粘土の中心が目と同じ高さ
        const float RECENTER_FORWARD = 0.45f, RECENTER_DROP = 0f;

        // 投稿するモデルの体積 (m³)。最初の球 (一辺 14cm の立方体と同じ体積) がほぼ等倍になる値
        const float POST_VOLUME = 0.0027f;
        const float POST_FACTOR_MIN = 0.2f, POST_FACTOR_MAX = 5f;

        // 隠す場所を選ぶマップの模型: 最初は横幅 (長い方) をこの長さ (m) にして作業台に置く。手で動かせる大きさの範囲 (倍率)
        const float MAP_WIDTH = 0.5f;
        const float MAP_SCALE_MIN = 0.002f, MAP_SCALE_MAX = 1f; // 1 = 実物大
        string mapId = "bibai-museum";

        // ================= シーン =================
        public ClayField field;
        Transform rig, workspace, clay;
        Camera cam;
        Material clayMat;
        readonly Dictionary<int, (Mesh mesh, MeshRenderer renderer)> chunks = new Dictionary<int, (Mesh, MeshRenderer)>();
        ButtonPanel panel, propPanel;
        Post post;
        public MapView map;
        Task mapLoading;
        string mapLoadingId;
        bool hiding, mapPlaced; // hiding: 投稿の前に、マップの上で隠す場所を選んでいる間
        readonly List<ButtonPanel> uiPanels = new List<ButtonPanel>();
        bool xrActive;
        bool recenterPending = true;
        float xrStartT;

        // 手
        XRHandSubsystem handSub;
        readonly List<XRHandSubsystem> subs = new List<XRHandSubsystem>();
        HandState[] hands;
        HandLook[] looks;
        Transform[] brushCursor;
        Material[] brushMat;
        (Transform ring, Transform dot, LineRenderer line)[] pointers;
        bool manipDirty = true;
        int manipCount;
        Vector3 manipOffset, manipV0, manipMid0, manipPos0;
        Quaternion manipQuat0;
        float manipScale0;

        // Undo
        readonly List<ClayField.Snapshot> undoStack = new List<ClayField.Snapshot>();

        // ログ (スタッフ用。背中側の「粘土の性質」のパネルの上の文字板)
        readonly List<string> logLines = new List<string>();
        TextMesh[] hudLines;
        TextMesh hudTitle;

        // 体験者への案内・演出
        public Hud hud;
        public Fx fx;
        WristMenu wrist;
        Tutorial tutorial;
        bool tutorialShown;
        Transform brushPreview;   // ブラシの大きさの見本 (実物大の球)
        float brushChangedAt = -1e9f;
        float resetArmedAt = -1e9f; // 「リセット」を 1 回押した時刻 (もう一度押すとリセット)
        const float RESET_CONFIRM = 3f;

        // 手ごとの演出の状態
        class HandFx
        {
            public Vector3 lastTip, touchPt, touchN = Vector3.up, pullStart;
            public float speed, proximity = -1, rippleT, crumbT, splashT;
            public bool wasContact, wasPaint;
        }
        HandFx[] hfx;

        // ================= 起動 =================
        void Awake()
        {
            Application.targetFrameRate = 90;
            SetupLights();
            BuildRig();
            Sfx.Init(cam.transform);
            fx = new Fx(cam);
            BuildWorkspace();
            BuildClay();
            BuildHands();
            BuildPanels();
            BuildHud();
            map = new MapView(clayMat);
            post = new Post(this, workspace, PANEL_Z);
            tutorial = new Tutorial();
            BuildWristMenu();
            uiPanels.Add(panel); uiPanels.Add(propPanel); uiPanels.Add(post.Panel); uiPanels.Add(post.HidePanel); uiPanels.Add(post.GamePanel);
            uiPanels.Add(wrist.panel);
            ButtonPanel.Pressed = p => Sfx.Play("click", p);
            ButtonPanel.Hovered = p => Sfx.Play("hover", p, 1, 1, 0.06f);
            ButtonPanel.Slid = p => Sfx.Play("tick", p, 0.7f, 1, 0.04f);
            LoadParams();
            Log($"起動しました (文字: {Gfx.FontName})");
            RequestPermissions();
            ApplyConfig();
        }

        void OnDestroy() => post?.Close();

        // Web 版と同じライト (three.js の座標 → Unity は z を反転)。色はリニアにして渡す
        static void SetupLights()
        {
            Vector4 Dir(float x, float y, float z) => new Vector3(x, y, -z).normalized;
            Vector4 Col(string hex, float k) { var c = Gfx.Hex(hex).linear; return new Vector4(c.r * k, c.g * k, c.b * k, 1); }
            Shader.SetGlobalVector("_AK_SunDir", Dir(0.6f, 2.5f, 0.8f));
            Shader.SetGlobalVector("_AK_SunColor", Col("#ffffff", 2.2f));
            Shader.SetGlobalVector("_AK_RimDir", Dir(-1f, 1.2f, -1.5f));
            Shader.SetGlobalVector("_AK_RimColor", Col("#aabbff", 0.7f));
            Shader.SetGlobalVector("_AK_SkyColor", Col("#ffffff", 1.3f));
            Shader.SetGlobalVector("_AK_GroundColor", Col("#3a3020", 1.3f));
        }

        // XR Origin + カメラ (頭) + パススルー (AR Session と AR Camera Manager)
        void BuildRig()
        {
            var rigGO = new GameObject("XR Origin");
            rigGO.SetActive(false);
            rig = rigGO.transform;
            var offset = new GameObject("Camera Offset").transform;
            offset.SetParent(rig, false);
            var camGO = new GameObject("Main Camera") { tag = "MainCamera" };
            camGO.transform.SetParent(offset, false);
            cam = camGO.AddComponent<Camera>();
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = 50f;
            // パススルー: 背景は透明 (アルファ 0) の単色
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0, 0, 0, 0);
            var tpd = camGO.AddComponent<TrackedPoseDriver>();
            tpd.positionInput = new InputActionProperty(new InputAction("headPos", InputActionType.Value, "<XRHMD>/centerEyePosition"));
            tpd.rotationInput = new InputActionProperty(new InputAction("headRot", InputActionType.Value, "<XRHMD>/centerEyeRotation"));
            tpd.ignoreTrackingState = true;
            camGO.AddComponent<ARCameraManager>();
            var origin = rigGO.AddComponent<XROrigin>();
            origin.Origin = rigGO;
            origin.CameraFloorOffsetObject = offset.gameObject;
            origin.Camera = cam;
            origin.RequestedTrackingOriginMode = XROrigin.TrackingOriginMode.Floor;
            rigGO.SetActive(true);

            var session = new GameObject("AR Session");
            session.AddComponent<ARSession>();

            headDriver = tpd;
        }

        TrackedPoseDriver headDriver;

        // XR の初期化は Awake のあとに終わるので、ここで XR が動いているかを見る
        void Start()
        {
            xrActive = XRGeneralSettings.Instance != null && XRGeneralSettings.Instance.Manager != null
                       && XRGeneralSettings.Instance.Manager.activeLoader != null;
            xrStartT = Time.time;
            Log(xrActive ? $"XR: {XRGeneralSettings.Instance.Manager.activeLoader.name}" : "XR がありません (画面で表示)");
            if (!xrActive)
            {
                // XR が無い (エディタなど): 斜め上から粘土を見る
                cam.backgroundColor = Gfx.Hex("#1d2026");
                headDriver.enabled = false;
                cam.transform.position = new Vector3(0.0f, 1.25f, 0.15f);
                Log("M キー: 手首メニューを出す / しまう");
                StartTutorial();
            }
        }

        void BuildWorkspace()
        {
            workspace = new GameObject("Workspace").transform;
            workspace.position = new Vector3(0, 1.2f, 0.75f);
            var ped = new GameObject("Pedestal");
            ped.transform.SetParent(workspace, false);
            ped.AddComponent<MeshFilter>().sharedMesh = PedestalMesh();
            var pr = ped.AddComponent<MeshRenderer>();
            var pm = new Material(Gfx.Find("Clay"));
            pm.SetColor("_BaseColor", Gfx.Hex("#4d525c"));
            pm.SetFloat("_UseVertexColor", 0);
            pm.SetFloat("_Spec", 0.15f);
            pr.sharedMaterial = pm;
            ped.transform.localScale = new Vector3(CUBE_SCALE, 1, CUBE_SCALE);
            ped.transform.localPosition = new Vector3(0, -CUBE_HALF - 0.015f, 0);
        }

        // 台座: 上の半径 0.11、下の半径 0.13、高さ 0.02 の円柱 (ふた付き)
        static Mesh PedestalMesh()
        {
            const int seg = 48;
            float rt = 0.11f, rb = 0.13f, hh = 0.01f;
            var v = new List<Vector3>(); var n = new List<Vector3>(); var t = new List<int>();
            float slope = (rb - rt) / (2 * hh);
            for (int s = 0; s <= seg; s++)
            {
                float a = 2 * Mathf.PI * s / seg;
                var d = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                var nn = (d + Vector3.up * slope).normalized;
                v.Add(d * rb + Vector3.down * hh); n.Add(nn);
                v.Add(d * rt + Vector3.up * hh); n.Add(nn);
            }
            for (int s = 0; s < seg; s++) { int a = s * 2; t.AddRange(new[] { a, a + 2, a + 1, a + 2, a + 3, a + 1 }); }
            int top = v.Count;
            v.Add(Vector3.up * hh); n.Add(Vector3.up);
            for (int s = 0; s <= seg; s++)
            {
                float a = 2 * Mathf.PI * s / seg;
                v.Add(new Vector3(Mathf.Cos(a) * rt, hh, Mathf.Sin(a) * rt)); n.Add(Vector3.up);
            }
            for (int s = 0; s < seg; s++) t.AddRange(new[] { top, top + 2 + s, top + 1 + s });
            var m = new Mesh { name = "pedestal" };
            m.SetVertices(v); m.SetNormals(n); m.SetTriangles(t, 0);
            // 三角形の向きを法線にそろえる
            var tri = m.triangles;
            for (int i = 0; i < tri.Length; i += 3)
            {
                var fn = Vector3.Cross(v[tri[i + 1]] - v[tri[i]], v[tri[i + 2]] - v[tri[i]]);
                if (Vector3.Dot(fn, n[tri[i]]) < 0) (tri[i + 1], tri[i + 2]) = (tri[i + 2], tri[i + 1]);
            }
            m.triangles = tri;
            m.RecalculateBounds();
            return m;
        }

        void BuildClay()
        {
            clay = new GameObject("Clay").transform;
            clay.SetPositionAndRotation(workspace.position, workspace.rotation);
            clay.localScale = Vector3.one * CUBE_SCALE;
            field = new ClayField(80, 0.4f);
            field.ResetSphere(START_R);
            clayMat = new Material(Gfx.Find("Clay"));
            clayMat.SetFloat("_UseVertexColor", 1);
            RemeshDirty();
        }

        void BuildHands()
        {
            hands = new[] { new HandState(Handedness.Left), new HandState(Handedness.Right) };
            looks = new[] { new HandLook("hand-left"), new HandLook("hand-right") };
            hfx = new[] { new HandFx(), new HandFx() };
            brushCursor = new Transform[2];
            brushMat = new Material[2];
            pointers = new (Transform, Transform, LineRenderer)[2];
            var sphere = Gfx.Sphere(24, 16);
            for (int i = 0; i < 2; i++)
            {
                var go = new GameObject($"brush{i}");
                go.AddComponent<MeshFilter>().sharedMesh = sphere;
                brushMat[i] = Gfx.Unlit(new Color(1, 1, 1, 0.28f), 3010);
                go.AddComponent<MeshRenderer>().sharedMaterial = brushMat[i];
                go.SetActive(false);
                brushCursor[i] = go.transform;

                // レイのカーソル (板の上の輪と点) と線
                var ring = ButtonPanel.Rect(null, 0, 0, 0, 0.022f, 0.022f, 3100).transform;
                ring.name = $"pointer{i}";
                ring.GetComponent<MeshRenderer>().sharedMaterial = Gfx.Shared("RoundRect", 3100, CompareFunction.Always);
                ButtonPanel.SetRect(ring.GetComponent<MeshRenderer>(), new Color(1, 1, 1, 0), Color.white, 0.022f, 0.022f, 0.011f, 0.004f, 0.95f);
                var dot = ButtonPanel.Rect(ring, 0, 0, -0.0005f, 0.0056f / 0.022f, 0.0056f / 0.022f, 3101).transform;
                dot.GetComponent<MeshRenderer>().sharedMaterial = Gfx.Shared("RoundRect", 3101, CompareFunction.Always);
                ButtonPanel.SetRect(dot.GetComponent<MeshRenderer>(), Color.white, Color.white, 0.0056f, 0.0056f, 0.0028f, 0, 1);
                ring.gameObject.SetActive(false);
                var lineGO = new GameObject($"ray{i}");
                var line = lineGO.AddComponent<LineRenderer>();
                line.positionCount = 2;
                line.useWorldSpace = true;
                line.widthMultiplier = 0.0015f;
                line.sharedMaterial = Gfx.Unlit(new Color(1, 1, 1, 0.35f), 3099, CompareFunction.Always);
                line.shadowCastingMode = ShadowCastingMode.Off;
                line.enabled = false;
                pointers[i] = (ring, dot, line);
            }
        }

        // ================= パネル =================
        static readonly (string id, string label, string color)[] MODES =
        {
            ("touch", "さわる (なでる・こねる)", "#4fc3f7"),
            ("paint", "塗る", "#f48fb1"),
        };

        class Param
        {
            public string id, label;
            public float min, max;
            public Func<float> get;
            public Action<float> set;
            public Func<float, string> format;
        }
        List<Param> PARAMS;
        Dictionary<string, float> paramDefaults;
        const string PARAM_KEY = "clayxr.params.v1";

        void BuildPanels()
        {
            // 道具のパネル: 上から、モード (大きく) / ブラシの大きさ / 戻す・手元へ・使い方 / 色見本。
            // 「リセット」は押し間違えないよう、少し離した下の段に置き、2 回押すとリセットする
            const float bw = 0.056f, bh = 0.04f, gap = 0.006f, mh = 0.05f;
            float Col(int i) => (i - 1.5f) * (bw + gap);
            const float PANEL_W = 4 * bw + 3 * gap;
            const float swatchW = (PANEL_W - 5 * gap) / 6, shh = 0.03f; // 色見本
            const float yMode = 0, ySize = -mh / 2 - gap - bh / 2, yTools = ySize - bh - gap;
            const float ySwatch = yTools - bh / 2 - gap - shh / 2, yReset = ySwatch - (shh + gap) - shh / 2 - 0.02f - bh / 2;
            var defs = new List<ButtonDef>();
            for (int i = 0; i < MODES.Length; i++)
            {
                var m = MODES[i];
                defs.Add(new ButtonDef
                {
                    id = m.id, label = () => m.label, color = m.color,
                    x = (Col(i * 2) + Col(i * 2 + 1)) / 2, y = yMode, w = 2 * bw + gap, h = mh,
                    onPress = () => mode = m.id, isActive = () => mode == m.id,
                });
            }
            defs.Add(new ButtonDef
            {
                id = "brush", slider = true, label = () => "ブラシの大きさ (塗る・つまむ)", color = "#f48fb1",
                x = 0, y = ySize, w = PANEL_W, h = bh, min = 0.01f, max = 0.12f,
                get = () => brushR, set = v => { SetBrush(v); brushChangedAt = Time.unscaledTime; },
                format = v => $"{Mathf.RoundToInt(v * 1000)}mm",
            });
            defs.Add(new ButtonDef { id = "undo", label = () => "↶ 戻す", x = (Col(0) + Col(1)) / 2, y = yTools, w = 2 * bw + gap, h = bh, onPress = Undo });
            defs.Add(new ButtonDef { id = "recenter", label = () => "手元へ", x = Col(2), y = yTools, w = bw, h = bh, onPress = Recenter });
            defs.Add(new ButtonDef { id = "howto", label = () => "使い方", x = Col(3), y = yTools, w = bw, h = bh, onPress = StartTutorial, isActive = () => tutorial != null && tutorial.Active });
            for (int i = 0; i < PALETTE.Length; i++)
            {
                string c = PALETTE[i];
                defs.Add(new ButtonDef
                {
                    id = $"color{i}", swatch = true, color = c,
                    x = -PANEL_W / 2 + swatchW / 2 + (i % 6) * (swatchW + gap), y = ySwatch - (i / 6) * (shh + gap),
                    w = swatchW, h = shh, onPress = () => SetColor(c), isActive = () => color == c,
                });
            }
            defs.Add(new ButtonDef
            {
                id = "reset", color = "#e57373", x = (Col(2) + Col(3)) / 2, y = yReset, w = 2 * bw + gap, h = bh,
                label = () => ResetArmed ? "もう一度押すとリセット" : "リセット (球に戻す)",
                onPress = PressReset, isActive = () => ResetArmed,
            });
            panel = new ButtonPanel(workspace, "tool-panel", defs, new[] { "さわる=手のひら・塗る=指先・つまむ=伸ばす" });
            panel.group.localPosition = new Vector3(-0.3f, 0.02f, -PANEL_Z);
            panel.group.localRotation = Quaternion.Euler(0.3f * Mathf.Rad2Deg, -0.6f * Mathf.Rad2Deg, 0);

            // ブラシの大きさの見本 (実物大の球)。大きさを変えている間だけ、パネルの上に出す
            var pv = new GameObject("brush-preview");
            pv.transform.SetParent(panel.group, false);
            pv.AddComponent<MeshFilter>().sharedMesh = Gfx.Sphere(24, 16);
            pv.AddComponent<MeshRenderer>().sharedMaterial = Gfx.Unlit(new Color(0.96f, 0.56f, 0.69f, 0.3f), 3010);
            pv.SetActive(false);
            brushPreview = pv.transform;

            // 粘土の性質 (スライダー)
            string Pct(float v) => $"{Mathf.RoundToInt(v * 100)}%";
            PARAMS = new List<Param>
            {
                new Param { id = "push", label = "柔らかさ", min = 0.05f, max = 0.9f, get = () => viscPush, set = v => viscPush = v, format = Pct },
                new Param { id = "pressure", label = "沈む速さ", min = 0, max = 4, get = () => limitPressure, set = v => limitPressure = v, format = v => v.ToString("0.0") },
                new Param { id = "depth", label = "手が止まる深さ", min = 0.003f, max = 0.04f, get = () => limitDepth, set = v => limitDepth = v, format = v => $"{Mathf.RoundToInt(v * 1000)}mm" },
                new Param { id = "stick", label = "くっつき", min = 0, max = 0.8f, get = () => viscStick, set = v => viscStick = v, format = Pct },
                new Param { id = "slide", label = "引きずり", min = 0.05f, max = 0.9f, get = () => viscSlide, set = v => viscSlide = v, format = Pct },
                new Param { id = "relax", label = "なめらかさ", min = 0, max = 0.5f, get = () => field.relax, set = v => field.relax = v, format = v => $"{Mathf.RoundToInt(v * 200)}%" },
                new Param { id = "kneadDepth", label = "こねる開始", min = 0.004f, max = 0.04f, get = () => kneadFrom, set = v => kneadFrom = v, format = v => $"{Mathf.RoundToInt(v * 1000)}mm" },
                new Param { id = "sfx", label = "効果音の音量", min = 0, max = 1, get = () => Sfx.sfxVolume, set = v => Sfx.sfxVolume = v, format = Pct },
                new Param { id = "bgm", label = "BGM の音量", min = 0, max = 1, get = () => Sfx.bgmVolume, set = v => Sfx.bgmVolume = v, format = Pct },
            };
            paramDefaults = new Dictionary<string, float>();
            foreach (var p in PARAMS) paramDefaults[p.id] = p.get();
            const float sw = 0.2f, sh = 0.04f, sgap = 0.006f;
            var pdefs = new List<ButtonDef>();
            for (int j = 0; j < PARAMS.Count; j++)
            {
                var p = PARAMS[j];
                pdefs.Add(new ButtonDef
                {
                    id = p.id, slider = true, label = () => p.label, x = 0, y = -j * (sh + sgap), w = sw, h = sh,
                    min = p.min, max = p.max, get = p.get, set = v => SetParam(p, v), format = p.format,
                });
            }
            pdefs.Add(new ButtonDef { id = "paramReset", label = () => "初期値に戻す", x = 0, y = -PARAMS.Count * (sh + sgap), w = sw, h = bh, onPress = ResetParams });
            propPanel = new ButtonPanel(workspace, "prop-panel", pdefs, new[] { "粘土の性質 (指でなぞって調整)" });
            // スタッフが調整する項目なので、体験者の目に入らないよう背後に置く (振り返ると手が届く)。
            // 頭は作業台の中心から RECENTER_FORWARD + 粘土の半径 だけ手前にいるので、そこからさらに PROP_BEHIND 後ろ。
            // 板の表を +Z (作業台の方 = 振り返った体験者の方) に向け、少し上を向けて傾ける
            propPanel.group.localPosition = new Vector3(0, 0, -(RECENTER_FORWARD + CUBE_HALF + PROP_BEHIND));
            propPanel.group.localRotation = Quaternion.Euler(0.3f * Mathf.Rad2Deg, 180f, 0);
        }

        void SetBrush(float r) => brushR = Mathf.Clamp(r, 0.01f, 0.12f);

        bool ResetArmed => Time.unscaledTime - resetArmedAt < RESET_CONFIRM;

        // 「リセット」: 1 回目は確かめるだけ。3 秒以内にもう一度押すとリセットする
        void PressReset()
        {
            if (!ResetArmed)
            {
                resetArmedAt = Time.unscaledTime;
                hud?.Toast("もう一度押すと、粘土を球に戻します", Hud.WARN, 2.5f);
                return;
            }
            resetArmedAt = -1e9f;
            ResetClay();
        }

        // 手首メニュー: よく使うもの (モード・色・戻す) と残り時間
        void BuildWristMenu()
        {
            const float sw = 0.024f, gap = 0.005f, bh = 0.03f;
            const float W = 6 * sw + 5 * gap;
            var defs = new List<ButtonDef>
            {
                new ButtonDef { id = "wTime", label = WristStatus, x = 0, y = 0, w = W, h = 0.022f },
            };
            for (int i = 0; i < MODES.Length; i++)
            {
                var m = MODES[i];
                defs.Add(new ButtonDef
                {
                    id = "w" + m.id, label = () => m.id == "touch" ? "さわる" : "塗る", color = m.color,
                    x = (i - 0.5f) * (W / 2 + gap / 2), y = -0.022f / 2 - gap - bh / 2, w = (W - gap) / 2, h = bh,
                    onPress = () => mode = m.id, isActive = () => mode == m.id,
                });
            }
            float ys = -0.011f - gap - bh - gap - sw / 2;
            for (int i = 0; i < PALETTE.Length; i++)
            {
                string c = PALETTE[i];
                defs.Add(new ButtonDef
                {
                    id = $"wcolor{i}", swatch = true, color = c,
                    x = -W / 2 + sw / 2 + (i % 6) * (sw + gap), y = ys - (i / 6) * (sw + gap), w = sw, h = sw,
                    onPress = () => SetColor(c), isActive = () => color == c,
                });
            }
            defs.Add(new ButtonDef { id = "wundo", label = () => "↶ 戻す", x = 0, y = ys - sw - gap - sw / 2 - gap - bh / 2, w = W, h = bh, onPress = Undo });
            wrist = new WristMenu(defs);
        }

        string WristStatus()
        {
            var rem = post?.Server?.game.Remaining(DateTime.UtcNow);
            string m = mode == "touch" ? "さわる" : "塗る";
            if (rem == null) return $"いま: {m}";
            int s = Mathf.CeilToInt((float)rem.Value);
            return $"いま: {m} · 残り {s / 60}:{s % 60:00}";
        }

        void StartTutorial()
        {
            tutorialShown = true;
            if (hiding) return;
            tutorial.Begin();
        }

        void SetColor(string c)
        {
            color = c;
            mode = "paint"; // 色を選んだら「塗る」にする
        }

        float saveAt = -1;
        void SetParam(Param p, float v)
        {
            p.set(Mathf.Clamp(v, p.min, p.max));
            saveAt = Time.unscaledTime + 0.3f;
        }

        void SaveParams()
        {
            var d = new Dictionary<string, object>();
            foreach (var p in PARAMS) d[p.id] = p.get();
            PlayerPrefs.SetString(PARAM_KEY, MiniJson.Write(d));
            PlayerPrefs.Save();
        }

        void LoadParams()
        {
            try
            {
                if (!(MiniJson.Parse(PlayerPrefs.GetString(PARAM_KEY, "{}")) is Dictionary<string, object> saved)) return;
                foreach (var p in PARAMS)
                    if (saved.TryGetValue(p.id, out var v) && v is double dv) p.set(Mathf.Clamp((float)dv, p.min, p.max));
            }
            catch { /* 初期値のまま */ }
        }

        void ResetParams()
        {
            foreach (var p in PARAMS) p.set(paramDefaults[p.id]);
            SaveParams();
        }

        // ================= 案内の帯とログの板 =================
        void BuildHud()
        {
            // 体験者には「今やること」の帯を、作業台の上 (前はログの板があった所) に出す
            hud = new Hud(workspace, new Vector3(0, 0.31f, -PANEL_Z), cam);
            // ログはスタッフ用: 背中側の「粘土の性質」のパネルの上に置く
            var board = new GameObject("log-board").transform;
            board.SetParent(propPanel.group, false);
            const float W = 0.34f, H = 0.106f;
            board.localPosition = new Vector3(0, propPanel.maxY + 0.01f + H / 2, 0);
            var bg = ButtonPanel.Rect(board, 0, 0, 0.002f, W, H, 2978);
            ButtonPanel.SetRect(bg, new Color(15 / 255f, 17 / 255f, 21 / 255f), new Color(15 / 255f, 17 / 255f, 21 / 255f), W, H, 0.008f, 0, 0.85f);
            float lh = 0.0105f, x0 = -W / 2 + 0.008f, y0 = H / 2 - 0.006f;
            hudTitle = Gfx.Text(board, $"めっちゃ美唄 Clay XR (Unity) v{VERSION}", lh, Gfx.Hex("#e8eaee"), TextAnchor.UpperLeft);
            hudTitle.transform.localPosition = new Vector3(x0, y0, 0);
            hudLines = new TextMesh[6];
            for (int i = 0; i < 6; i++)
            {
                hudLines[i] = Gfx.Text(board, "", lh, Gfx.Hex("#e8eaee"), TextAnchor.UpperLeft);
                hudLines[i].transform.localPosition = new Vector3(x0, y0 - 0.0155f - i * 0.0137f, 0);
            }
        }

        public void Log(string msg)
        {
            Debug.Log("[clay] " + msg);
            logLines.Add($"{DateTime.Now:HH:mm:ss} {msg}");
            while (logLines.Count > 6) logLines.RemoveAt(0);
            if (hudLines == null) return;
            const float maxW = 0.34f - 0.016f;
            for (int i = 0; i < hudLines.Length; i++)
            {
                var t = hudLines[i];
                string s = i < logLines.Count ? logLines[i] : "";
                t.characterSize = 0.0105f / 6.4f;
                t.text = s;
                float w = ButtonPanel.Measure(t, s);
                if (w > maxW) t.characterSize *= maxW / w;
            }
        }

        // ================= 起動時の設定 =================
        void RequestPermissions()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            const string HAND = "android.permission.HAND_TRACKING";
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(HAND))
            {
                var cb = new UnityEngine.Android.PermissionCallbacks();
                cb.PermissionGranted += _ => Log("手の追跡を許可されました");
                cb.PermissionDenied += _ => Log("手の追跡が許可されていません (設定で許可してください)");
                UnityEngine.Android.Permission.RequestUserPermission(HAND, cb);
            }
#endif
        }

        // サーバーの設定: Intent の extra か、persistentDataPath/config.json
        //   server: 接続先 (例 https://bibai.t0waxx.com/a-kun-01)。無ければ投稿は「この端末内」
        //   room:   入るセッションの ID。空なら部屋を作ってホストになる
        //   name / map / prepare (準備の秒数) / play (本番の秒数)
        void ApplyConfig()
        {
            string server = null, room = null, name = null, mapArg = null, prepare = null, play = null, result = null;
            try
            {
                string path = Path.Combine(Application.persistentDataPath, "config.json");
                if (File.Exists(path) && MiniJson.Parse(File.ReadAllText(path)) is Dictionary<string, object> cfg)
                {
                    server = cfg.Str("server"); room = cfg.Str("room"); name = cfg.Str("name"); mapArg = cfg.Str("map");
                    prepare = cfg.Str("prepare"); play = cfg.Str("play"); result = cfg.Str("result");
                }
            }
            catch (Exception e) { Log($"config.json を読めません: {e.Message}"); }
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                using var intent = activity.Call<AndroidJavaObject>("getIntent");
                server = intent.Call<string>("getStringExtra", "server") ?? server;
                room = intent.Call<string>("getStringExtra", "room") ?? room;
                name = intent.Call<string>("getStringExtra", "name") ?? name;
                mapArg = intent.Call<string>("getStringExtra", "map") ?? mapArg;
                prepare = intent.Call<string>("getStringExtra", "prepare") ?? prepare;
                play = intent.Call<string>("getStringExtra", "play") ?? play;
                result = intent.Call<string>("getStringExtra", "result") ?? result;
            }
            catch (Exception e) { Debug.Log($"[clay] intent を読めません: {e.Message}"); }
#endif
            if (!string.IsNullOrEmpty(mapArg)) mapId = mapArg;
            if (!string.IsNullOrEmpty(server))
            {
                Log(string.IsNullOrEmpty(room) ? $"サーバーに接続して部屋を作ります: {server}" : $"サーバーに接続します: {server} 部屋 {room}");
                // result: 結果を見せる秒数。過ぎるとサーバーが次のラウンドを始める。0 ならホストが「次のラウンドへ」
                double resultSeconds = string.IsNullOrEmpty(result) ? 20 : double.TryParse(result, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var rs) && rs >= 0 ? rs : 20;
                post.Connect(server, room ?? "", string.IsNullOrEmpty(name) ? "a-kun" : name, mapId, Seconds(prepare, 120), Seconds(play, 180), resultSeconds);
            }
        }

        static double Seconds(string s, double fallback)
            => double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0 ? v : fallback;

        // マップ: サーバーにつないでいれば GET /v1/maps/{mapId}。使えなければ StreamingAssets/mock/{mapId}.json
        async Task LoadMap(string id)
        {
            var s = post.Server;
            if (s != null)
            {
                try { await map.Load(id, s.apiBase, s.Token); return; }
                catch (Exception e)
                {
                    Log($"[未実装] GET /maps/{id} を使えないので、端末内のマップを使います");
                    Debug.LogWarning($"[clay] {e.Message}");
                }
            }
            await map.Load(id);
        }

        // ================= 粘土 =================
        void RemeshDirty()
        {
            foreach (int key in field.TakeDirty())
            {
                if (!chunks.TryGetValue(key, out var c))
                {
                    var go = new GameObject($"chunk{key}");
                    go.transform.SetParent(clay, false);
                    var mesh = new Mesh { name = $"chunk{key}" };
                    mesh.MarkDynamic();
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var r = go.AddComponent<MeshRenderer>();
                    r.sharedMaterial = clayMat;
                    r.shadowCastingMode = ShadowCastingMode.Off;
                    r.receiveShadows = false;
                    c = (mesh, r);
                    chunks[key] = c;
                }
                bool any = field.MeshChunk(key, c.mesh);
                if (any) c.mesh.RecalculateBounds();
                c.renderer.enabled = any;
            }
        }

        // 座標変換: ワールド (m) ⇔ 格子座標
        Vector3 WorldToGrid(Vector3 w) => (clay.InverseTransformPoint(w) + Vector3.one * field.half) / field.h;
        Vector3 GridToWorld(Vector3 g) => clay.TransformPoint(g * field.h - Vector3.one * field.half);
        float WorldLenToGrid(float len) => len / (field.h * clay.localScale.x);

        void PushUndo()
        {
            undoStack.Add(field.TakeSnapshot());
            if (undoStack.Count > 30) undoStack.RemoveAt(0);
        }

        void Undo()
        {
            if (undoStack.Count == 0)
            {
                Sfx.Play("error", clay.position, 0.6f);
                hud?.Toast("これ以上は戻せません", Hud.WARN, 2f);
                return;
            }
            var s = undoStack[undoStack.Count - 1];
            undoStack.RemoveAt(undoStack.Count - 1);
            field.Restore(s);
            // 戻したことが分かるよう、粘土のまわりに輪を一瞬広げる
            float r = ClayRadius;
            Sfx.Play("undo", clay.position);
            fx.Ripple(clay.position, Vector3.up, new Color(1, 1, 1, 0.7f), r * 1.02f, r * 1.35f, 0.45f);
            fx.Sparkle(clay.position, r * 0.9f, 14, new Color(1, 1, 1, 0.9f), 0.6f);
        }

        float ClayRadius => START_R * clay.localScale.x;

        public void RestoreSnapshot(ClayField.Snapshot snap)
        {
            PushUndo();
            field.Restore(snap);
        }

        // 球に戻して作業台の上に置き直す (「新しく作る」も同じ)
        public void ResetClay()
        {
            PushUndo();
            field.ResetSphere(START_R);
            StopTransition();
            clay.SetPositionAndRotation(workspace.position, workspace.rotation);
            clay.localScale = Vector3.one * CUBE_SCALE;
            manipDirty = true;
            // ぼよんと弾んで球に戻る。台座のまわりに砂ぼこり
            Sfx.Play("boing", clay.position);
            fx.Burst(workspace.position + Vector3.down * CUBE_HALF, 28, Gfx.Hex("#d9cbbd"), 0.35f, 0.006f, 0.7f, 0.4f, Vector3.up, 0.85f);
            clayRest = (clay.position, clay.rotation, Vector3.one * CUBE_SCALE);
            clayTween = fx.Tween(0.75f, u =>
            {
                float a = 0.22f * Fx.Wobble(u);
                clay.localScale = new Vector3(CUBE_SCALE * (1 + a * 0.5f), CUBE_SCALE * (1 - a), CUBE_SCALE * (1 + a * 0.5f));
            }, () => { clay.localScale = Vector3.one * CUBE_SCALE; clayRest = null; });
        }

        // 頭の前に作業台と粘土を持ってくる
        void Recenter() => RecenterFrom(cam.transform.position, cam.transform.rotation);

        void RecenterFrom(Vector3 head, Quaternion headRot)
        {
            StopTransition();
            var fwd = headRot * Vector3.forward;
            fwd.y = 0;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            // 頭 (目) の高さが基準: 水平の視線方向に FORWARD、目から DROP だけ下に置く
            workspace.position = head + fwd * (RECENTER_FORWARD + START_R * clay.localScale.x) + Vector3.down * RECENTER_DROP;
            workspace.rotation = Quaternion.LookRotation(fwd, Vector3.up);
            clay.SetPositionAndRotation(workspace.position, workspace.rotation);
            manipDirty = true;
        }

        // ================= 隠す場所を選ぶ =================
        // 「投稿する」のあと: 粘土と道具のパネルをしまって、マップの模型を出す。
        // spot: 前に隠した場所 (マップの root から見た位置)。あれば最初から選んでおく
        public async Task BeginHide(Mesh shape, (Vector3 pos, Quaternion rot)? spot)
        {
            // ホストがマップを替えていたら読み直す (ラウンドの途中では替わらない)
            string want = post.Server?.game.mapId ?? mapId;
            if (!map.IsLoaded || map.LoadedId != want)
            {
                if (mapLoading == null || mapLoadingId != want)
                {
                    Log($"マップを読み込みます: {post.Server?.MapName(want) ?? want}");
                    mapLoadingId = want;
                    mapLoading = LoadMap(want);
                    mapPlaced = false; // 大きさが違うので、作業台の上に置き直す
                }
                try { await mapLoading; }
                catch (Exception) { mapLoading = null; throw; } // 次の「投稿する」でもう一度読む
                Log($"マップを読み込みました: {map.Name}");
            }
            if (!mapPlaced) { PlaceMapOnTable(); mapPlaced = true; }
            if (spot != null) map.SetSpotLocal(spot.Value.pos, spot.Value.rot);
            else map.ClearSpot();
            map.SetSpotMesh(shape);
            SetHiding(true);
        }

        public void EndHide() => SetHiding(false);

        void SetHiding(bool on)
        {
            bool was = hiding;
            StopTransition();
            hiding = on;
            panel.Visible = !on;
            propPanel.Visible = !on;
            if (on) tutorial.Stop();
            foreach (var hs in hands)
            {
                EndPinch(hs);
                hs.uiPinch = false;
                hs.ResetTouch();
            }
            manipDirty = true;
            if (on == was)
            {
                map.Visible = on;
                clay.gameObject.SetActive(!on);
                return;
            }
            if (on)
            {
                // 粘土が縮みながらマップ (前に隠した場所があればそこ) へ吸い込まれ、マップの模型が台の上に膨らんで出てくる
                clayRest = (clay.position, clay.rotation, clay.localScale);
                var target = map.HasSpot ? map.root.TransformPoint(map.SpotPosition) : map.root.TransformPoint(map.LocalBounds.center);
                map.Visible = true;
                Sfx.Play("whoosh", clay.position);
                var from = clay.position; var s0 = clay.localScale;
                clayTween = fx.Tween(0.45f, u =>
                {
                    float e = Fx.InBack(u);
                    clay.position = Vector3.LerpUnclamped(from, target, e);
                    clay.localScale = s0 * Mathf.Max(0.001f, 1 - Mathf.Clamp01(e));
                }, () => { clay.gameObject.SetActive(false); RestoreClay(); });
                GrowMap(true);
            }
            else
            {
                // マップがしぼんで消え、粘土が台の上にぽんと戻ってくる
                clay.gameObject.SetActive(true);
                RestoreClay();
                Sfx.Play("whooshUp", clay.position);
                var s1 = clay.localScale;
                clayRest = (clay.position, clay.rotation, s1);
                clayTween = fx.Tween(0.5f, u => clay.localScale = s1 * Mathf.Max(0.001f, Fx.OutBack(u)), () => { clay.localScale = s1; clayRest = null; });
                GrowMap(false);
            }
        }

        // ---------- しまう・出すの動き ----------
        Fx.Handle clayTween, mapTween;
        (Vector3 pos, Quaternion rot, Vector3 scale)? clayRest;
        (Vector3 pos, float scale)? mapRest;

        void RestoreClay()
        {
            if (clayRest == null) return;
            var r = clayRest.Value;
            clay.SetPositionAndRotation(r.pos, r.rot);
            clay.localScale = r.scale;
            clayRest = null;
        }

        void RestoreMap()
        {
            if (mapRest == null) return;
            map.root.position = mapRest.Value.pos;
            map.root.localScale = Vector3.one * mapRest.Value.scale;
            mapRest = null;
        }

        // 動きの途中で次の操作が来たら、途中の動きをやめて、元の置き方に戻す
        void StopTransition()
        {
            bool clayAnim = clayTween != null && !clayTween.finished, mapAnim = mapTween != null && !mapTween.finished;
            clayTween?.Cancel(); mapTween?.Cancel();
            clayTween = mapTween = null;
            if (clayAnim)
            {
                RestoreClay();
                clay.gameObject.SetActive(!hiding);
            }
            if (mapAnim) { RestoreMap(); map.Visible = hiding; }
            if (!hiding && !clayAnim && clay.localScale.x < 0.01f) clay.localScale = Vector3.one * CUBE_SCALE;
        }

        // マップの模型を、台の上の点 (底面の中心) を中心に膨らませる / しぼませる
        void GrowMap(bool grow)
        {
            float s1 = map.root.localScale.x;
            var p1 = map.root.position;
            mapRest = (p1, s1);
            var pivot = grow ? workspace.position + Vector3.down * CUBE_HALF : map.root.TransformPoint(map.LocalBounds.center);
            void At(float k)
            {
                k = Mathf.Max(0.001f, k);
                map.root.localScale = Vector3.one * s1 * k;
                map.root.position = pivot + (p1 - pivot) * k;
            }
            if (grow)
                mapTween = fx.Tween(0.55f, u => At(Fx.OutBack(Mathf.Clamp01((u - 0.2f) / 0.8f))), () => { RestoreMap(); });
            else
                mapTween = fx.Tween(0.3f, u => At(1 - Fx.OutCubic(u)), () => { RestoreMap(); map.Visible = false; });
        }

        // マップを作業台の上に模型として置く (粘土の台座の上)
        void PlaceMapOnTable() => map.FitOnTable(workspace.position + Vector3.down * CUBE_HALF, workspace.rotation, MAP_WIDTH);

        // 「マップを手元へ」: 作業台ごと頭の前に持ってきて、マップを最初の大きさで置き直す
        public void RecenterMap()
        {
            StopTransition();
            if (xrActive) Recenter();
            PlaceMapOnTable();
        }

        // 隠す場所を選んでいる間の手: レイがマップに当たっていればそこを指し、つまむとそこに隠す (つまんだまま動かすと付いてくる)。
        // 何もない所をつまむとマップを動かす (両手で回転・拡大縮小)
        void UpdateHideHand(int hi, HandState hs, bool hasRay, Vector3 o, Vector3 d, float pinchDist)
        {
            MapView.RayHit hit = default;
            bool onMap = hasRay && map.Raycast(o, d, out hit);
            if (!hs.pinching && pinchDist < PINCH_ON)
            {
                hs.pinching = true;
                manipDirty = true;
                hs.pinchMode = onMap ? "spot" : "move";
                if (onMap)
                {
                    // 隠す場所を指した: 小さな輪と、ぽんという音
                    Sfx.Play("pop", hit.point, 0.5f, 1.4f);
                    fx.Ripple(hit.point + hit.normal * 0.002f, hit.normal, new Color(1, 0.84f, 0.31f, 0.9f), 0.01f, 0.05f, 0.4f);
                }
            }
            else if (hs.pinching && pinchDist > PINCH_OFF)
            {
                if (hs.pinchMode == "spot" && map.HasSpot) Sfx.Play("tick", hs.pinchPos, 0.6f);
                EndPinch(hs);
            }
            if (hs.pinching && hs.pinchMode == "move") { movers.Add(hs); return; }
            if (!onMap) return;
            if (hs.pinching && hs.pinchMode == "spot") map.SetSpot(hit, d);
            // レイの線と、当たった所の点
            var line = pointers[hi].line;
            line.enabled = true;
            line.SetPosition(0, o); // 親指と人差し指の間から出す (PinchRay)
            line.SetPosition(1, hit.point);
            brushMat[hi].SetColor("_BaseColor", hs.pinching ? new Color(1, 0.84f, 0.31f, 0.9f) : new Color(1, 1, 1, 0.7f));
            brushCursor[hi].position = hit.point;
            brushCursor[hi].localScale = Vector3.one * 0.01f;
            brushCursor[hi].gameObject.SetActive(true);
        }

        // ================= 投稿・化けるの演出 (Post から呼ぶ) =================
        // 隠し終えた: 隠した所に砂ぼこりと輪を出して、どすんと置く
        public void FxHidden()
        {
            if (!map.HasSpot) return;
            var p = map.root.TransformPoint(map.SpotPosition);
            var up = map.root.rotation * (map.SpotRotation * Vector3.up);
            Sfx.Play("land", p);
            Sfx.Play("pop", p, 0.8f, 1, 0);
            fx.Burst(p, 30, Gfx.Hex("#e8dccb"), 0.25f, 0.005f, 0.7f, 0.3f, up, 0.7f);
            fx.Ripple(p + up * 0.002f, up, new Color(0.51f, 0.78f, 0.52f, 0.9f), 0.01f, 0.09f, 0.6f);
            fx.Sparkle(p + up * 0.03f, 0.04f, 12, Gfx.Hex("#fff59d"));
        }

        // 化けた: きらきらしながら入れ替わる (粘土が出ていれば粘土のまわり、マップを出していれば隠した所)
        public void FxDisguised()
        {
            Vector3 p; float r;
            if (hiding && map.HasSpot) { p = map.root.TransformPoint(map.SpotPosition); r = 0.05f; }
            else { p = clay.position; r = ClayRadius; }
            Sfx.Play("shimmer", p);
            fx.Sparkle(p, r * 1.1f, 60, Gfx.Hex("#ffe082"), 1.4f);
            fx.Sparkle(p, r * 0.9f, 30, Gfx.Hex("#ffffff"), 1.0f);
            fx.Ripple(p, Vector3.up, new Color(1, 0.88f, 0.51f, 0.8f), r * 0.6f, r * 1.6f, 0.8f);
        }

        // ================= 投稿用の書き出し =================
        public struct ExportResult
        {
            public byte[] bytes;
            public Vector3 anchor;      // 底面の中心 (ワールド)
            public Quaternion rotation; // 粘土の向き (ワールド)
            public float factor;        // 大きさの補正の倍率
            public float volume;        // 投稿するモデルの体積 (m³)
            public Mesh mesh;           // 投稿する形 (Unity の座標、底面の中心が原点)。隠す場所の見本に使う
        }

        // 粘土を 1 つのメッシュ (頂点カラー付き) の GLB にする。
        // 体積が POST_VOLUME になるように大きさをそろえる (手で粘土を拡大・縮小していても、作った形の量が同じなら同じ大きさになる)。
        // 原点は底面の中心。配置 (Transform) はこの点の位置と粘土の回転で、scale は 1
        public ExportResult ExportClay()
        {
            var pos = new List<Vector3>(); var nor = new List<Vector3>(); var col = new List<Color>(); var tri = new List<int>();
            var tp = new List<Vector3>(); var tn = new List<Vector3>(); var tc = new List<Color>(); var ti = new List<int>();
            foreach (var c in chunks.Values)
            {
                if (!c.renderer.enabled) continue;
                c.mesh.GetVertices(tp); c.mesh.GetNormals(tn); c.mesh.GetColors(tc); c.mesh.GetTriangles(ti, 0);
                int baseV = pos.Count;
                pos.AddRange(tp); nor.AddRange(tn); col.AddRange(tc);
                foreach (int i in ti) tri.Add(baseV + i);
            }
            if (tri.Count == 0) throw new Exception("粘土がありません");
            var b = new Bounds(pos[0], Vector3.zero);
            foreach (var p in pos) b.Encapsulate(p);
            var anchorLocal = new Vector3(b.center.x, b.min.y, b.center.z);
            var P = pos.ToArray();
            for (int i = 0; i < P.Length; i++) P[i] -= anchorLocal;
            var T = tri.ToArray();
            // 大きさをそろえる (底面の中心を動かさずに拡大・縮小する)
            float raw = Glb.Volume(P, T);
            float factor = raw > 1e-9f ? Mathf.Clamp(Mathf.Pow(POST_VOLUME / raw, 1f / 3f), POST_FACTOR_MIN, POST_FACTOR_MAX) : 1;
            for (int i = 0; i < P.Length; i++) P[i] *= factor;
            var N = nor.ToArray(); var C = col.ToArray();
            var mesh = new Mesh { name = "post-preview" };
            if (P.Length > 65535) mesh.indexFormat = IndexFormat.UInt32;
            mesh.vertices = P; mesh.normals = N; mesh.colors = C; mesh.triangles = T;
            mesh.RecalculateBounds();
            return new ExportResult
            {
                bytes = Glb.Write(P, N, C, T),
                mesh = mesh,
                anchor = clay.TransformPoint(anchorLocal),
                rotation = clay.rotation,
                factor = factor,
                volume = raw * factor * factor * factor,
            };
        }

        // ================= 毎フレーム =================
        void Update()
        {
            float t = Time.time;
            float dt = Mathf.Min(0.05f, Mathf.Max(Time.deltaTime, 1e-4f));
            try
            {
                if (xrActive)
                {
                    if (recenterPending) TryRecenter(t);
                    UpdateXR(dt, t);
                }
                else
                {
                    UpdateDesktop();
                }
                var kb = Keyboard.current;
                if (!xrActive && kb != null && kb.mKey.wasPressedThisFrame) wrist.Toggle(cam.transform);
                panel.Refresh();
                propPanel.Refresh();
                if (wrist.panel.Visible) wrist.panel.Refresh();
                Gfx.SyncFontTexture();
                post.Update();
                RemeshDirty();
                if (saveAt > 0 && Time.unscaledTime > saveAt) { saveAt = -1; SaveParams(); }
                UpdateGuide();
                UpdateBrushPreview();
                if (tutorial.Active) tutorial.Update(clay.position, ClayRadius, cam.transform);
                fx.Update(dt);
                hud.Update(dt);
                Sfx.Update(dt);
            }
            catch (Exception e)
            {
                string msg = $"{e.GetType().Name}: {e.Message}";
                if (msg != lastErr) { lastErr = msg; Log($"エラー: {msg}"); Debug.LogException(e); }
            }
        }
        string lastErr = "";

        // 「今やること」の帯・残り時間の輪・秒読み・BGM の雰囲気
        int lastCount = -1;
        void UpdateGuide()
        {
            var g = post.Guide(mode, hiding, map.HasSpot);
            if (tutorial.Active && tutorial.Caption != null) hud.SetGuide(tutorial.Caption, "白いお手本の手をまねしてみよう", g.steps, g.step);
            else hud.SetGuide(g.main, g.sub, g.steps, g.step);
            var s = post.Server;
            bool inRound = s != null && s.game.InRound;
            double? rem = inRound ? s.game.Remaining(DateTime.UtcNow) : null;
            hud.SetTimer(s == null ? null : $"{s.game.round}|{s.game.status}|{s.game.phaseEndsAt}", rem, s == null ? "" : GameState.PhaseName(s.game.status));
            if (rem != null && rem.Value <= 10 && rem.Value > 0)
            {
                int sec = Mathf.CeilToInt((float)rem.Value);
                if (sec != lastCount) { lastCount = sec; Sfx.Play(sec <= 3 ? "count3" : "count", null, 0.8f); }
            }
            else lastCount = -1;
            Sfx.Mood(s != null && s.game.status == GameState.PLAYING ? "tense" : "calm");
        }

        // ブラシの大きさを変えている間 (と、スライダーを指している間) だけ、実物大の球を出す
        void UpdateBrushPreview()
        {
            bool show = !hiding && (Time.unscaledTime - brushChangedAt < 1.5f || panel.hover.Contains("brush"));
            if (brushPreview.gameObject.activeSelf != show) brushPreview.gameObject.SetActive(show);
            if (!show) return;
            brushPreview.localScale = Vector3.one * brushR;
            brushPreview.localPosition = new Vector3(0, panel.maxY + brushR + 0.01f, 0);
        }

        // 頭の姿勢が取れたら、目の前に粘土とパネルを置く
        void TryRecenter(float t)
        {
            var hp = cam.transform.localPosition;
            bool tracked = hp.sqrMagnitude > 1e-6f || cam.transform.localRotation != Quaternion.identity;
            if (!tracked)
            {
                if (t - xrStartT > 5)
                {
                    Log("頭の位置が取れません。初期位置に置きます");
                    recenterPending = false;
                }
                return;
            }
            if (t - xrStartT < 0.5f) return; // 追跡が落ち着くまで少し待つ
            Recenter();
            recenterPending = false;
            var w = clay.position;
            Log($"配置: 頭 y={cam.transform.position.y:0.00}m → 粘土 ({w.x:0.00}, {w.y:0.00}, {w.z:0.00})");
            if (!tutorialShown) StartTutorial(); // 粘土が目の前に来たら、お手本の手で使い方を見せる
        }

        // XR が無いとき (エディタ): マウスでパネルを押せるだけ
        void UpdateDesktop()
        {
            cam.transform.LookAt(hiding ? map.root.TransformPoint(map.LocalBounds.center) : clay.position + Vector3.up * 0.05f);
            foreach (var p in uiPanels) p.hover.Clear();
            var mouse = Mouse.current;
            if (mouse == null) return;
            var ray = cam.ScreenPointToRay(mouse.position.ReadValue());
            ButtonPanel.Hit hit = null;
            foreach (var pn in uiPanels)
            {
                var h = pn.RayHit(ray.origin, ray.direction);
                if (h != null && (hit == null || h.t < hit.t)) hit = h;
            }
            if (hit?.button != null)
            {
                hit.panel.hover.Add(hit.button.def.id);
                if (mouse.leftButton.wasPressedThisFrame) hit.panel.RayDown(hit);
            }
            else if (hit == null && hiding && mouse.leftButton.isPressed && map.Raycast(ray.origin, ray.direction, out var mh, 20f))
            {
                map.SetSpot(mh, ray.direction); // マウスで押した (押したまま動かした) 所に隠す
            }
        }

        // ---------- XR: 手 ----------
        XRHandSubsystem HandSubsystem()
        {
            if (handSub != null && handSub.running) return handSub;
            subs.Clear();
            SubsystemManager.GetSubsystems(subs);
            handSub = null;
            foreach (var s in subs) if (s.running) { handSub = s; break; }
            return handSub;
        }

        // 関節の位置を読み、震えを抑える
        void ReadJoints(HandState hs, XRHand hand, float t)
        {
            hs.valid.Clear();
            foreach (var id in HandBones.JOINTS)
            {
                var j = hand.GetJoint(id);
                if (!j.TryGetPose(out var pose)) continue;
                var w = rig.TransformPoint(pose.position);
                hs.jreal[id] = hs.jointF[id].Filter(w, t);
                hs.jradius[id] = j.TryGetRadius(out float r) ? r : 0;
                hs.valid.Add(id);
            }
        }

        bool RawJoint(XRHand hand, XRHandJointID id, out Vector3 w)
        {
            w = default;
            if (!hand.GetJoint(id).TryGetPose(out var pose)) return false;
            w = rig.TransformPoint(pose.position);
            return true;
        }

        float BoneRadius(HandState hs, HandBones.Bone b)
        {
            if (b.r > 0) return b.r;
            float ra = hs.jradius.TryGetValue(b.a, out var x) ? x : 0, rb = hs.jradius.TryGetValue(b.b, out var y) ? y : 0;
            return ra > 0 && rb > 0 ? (ra + rb) / 2 : HandBones.FINGER_R;
        }

        // 見えている手 (プロキシ) のずらし量を決める。
        // 粘土の内部の距離はくずれやすいので、表面近くにいる「見えている手」のめり込みだけを測り、
        // 本物の手の深さ = 見えている手のめり込み + これまでのずらし量 として扱う (god-object 方式)
        void UpdateTouchLimit(HandState hs, float dt, bool active)
        {
            float best = float.NegativeInfinity, bestSdf = 0;
            Vector3 ln = Vector3.up, bestP = Vector3.zero;
            if (active)
            {
                foreach (var b in HandBones.BONES)
                {
                    if (!hs.valid.Contains(b.a) || !hs.valid.Contains(b.b)) continue;
                    float r = WorldLenToGrid(b.r > 0 ? b.r : HandBones.FINGER_R);
                    Vector3 pa = hs.jreal[b.a], pb = hs.jreal[b.b];
                    for (int k = 0; k <= 2; k++)
                    {
                        var wp = Vector3.Lerp(pa, pb, k * 0.5f) + hs.offset;
                        var g = WorldToGrid(wp);
                        float sdf = field.Sample(g);
                        float depth = r - sdf; // 格子単位。正ならめり込み、負なら表面までのすき間
                        if (depth > best)
                        {
                            best = depth; bestSdf = sdf; bestP = wp;
                            if (depth > -2) ln = field.Gradient(g);
                        }
                    }
                }
            }
            var hf = hfx[hs == hands[0] ? 0 : 1];
            hf.proximity = -1;
            float sTarget = 0, depthNow = 0;
            if (!float.IsNegativeInfinity(best))
            {
                float proxyDepth = best * field.h * clay.localScale.x; // m
                // 触れている所 (表面の上の点) を、演出のために覚えておく
                var nw = clay.rotation * ln;
                hf.proximity = proxyDepth;
                hf.touchN = nw;
                hf.touchPt = bestP - nw * (bestSdf * field.h * clay.localScale.x);
                if (proxyDepth > -0.005f)
                {
                    // 押す向き (粘土の外向き) をなめらかに追う
                    var lnw = clay.rotation * ln;
                    hs.n = hs.s <= 1e-4f ? lnw : Vector3.Lerp(hs.n, lnw, 0.15f).normalized;
                }
                float D = Mathf.Max(0, proxyDepth + hs.s); // 本物の手のめり込み
                depthNow = D;
                // 見せるめり込み = L(1 - e^(-D/L)): 浅いうちはほぼそのまま、深いほど止まる
                float L = limitDepth;
                sTarget = D - L * (1 - Mathf.Exp(-D / L));
            }
            // 押し込みの深さをなめらかに追う (深くなるのは速く、浅くなるのはゆっくり)
            hs.depth = Mathf.Lerp(hs.depth, depthNow, 1 - Mathf.Exp(-(depthNow > hs.depth ? 30 : 12) * dt));
            float rate = sTarget > hs.s ? limitFollow : limitRelease;
            hs.s += (sTarget - hs.s) * (1 - Mathf.Exp(-rate * dt));
            if (hs.s < 1e-5f) hs.s = 0;
            hs.excess = hs.s;
            hs.offset = hs.n * hs.s;
            foreach (var id in hs.valid) hs.jpos[id] = hs.jreal[id] + hs.offset;
        }

        void RememberJoints(HandState hs)
        {
            hs.prevValid.Clear();
            foreach (var id in hs.valid)
            {
                hs.jprev[id] = hs.jpos[id];
                hs.prevValid.Add(id);
            }
        }

        // 手のカプセルを格子座標にして、粘土に近いものだけ返す
        readonly List<ClayField.Capsule> caps = new List<ClayField.Capsule>();
        bool HandCapsules(HandState hs, float dt, out float F)
        {
            caps.Clear();
            bool contact = false;
            F = Mathf.Max(WorldLenToGrid(viscF), MIN_F_GRID);
            float maxStep = WorldLenToGrid(viscMaxStep);
            foreach (var b in HandBones.BONES)
            {
                if (!hs.valid.Contains(b.a) || !hs.valid.Contains(b.b)) continue;
                float r = Mathf.Max(WorldLenToGrid(BoneRadius(hs, b)), MIN_R_GRID);
                Vector3 ga = WorldToGrid(hs.jpos[b.a]), gb = WorldToGrid(hs.jpos[b.b]);
                var gm = (ga + gb) * 0.5f;
                float sMin = Mathf.Min(field.Sample(ga), Mathf.Min(field.Sample(gb), field.Sample(gm)));
                if (sMin > r + F + Vector3.Distance(ga, gb) * 0.25f) continue;
                if (sMin < r) contact = true;

                // このフレームの手の動き (両端の平均)
                var u = Vector3.zero;
                if (hs.prevValid.Contains(b.a) && hs.prevValid.Contains(b.b))
                {
                    Vector3 qa = WorldToGrid(hs.jprev[b.a]), qb = WorldToGrid(hs.jprev[b.b]);
                    u = ((ga - qa) + (gb - qb)) * 0.5f;
                    // 粘性: 押し込む向きはよく付いてくる、横はやや遅れる、離す向きは少しだけ引っ張られる
                    var n = field.Gradient(gm);
                    float un = Vector3.Dot(u, n);
                    u = (u - n * un) * viscSlide + n * (un * (un > 0 ? viscStick : viscPush));
                }
                // 押し込み過ぎた分は「押す力」: 触れている所の粘土を内側へゆっくり流す
                if (hs.excess > 0 && sMin < r)
                {
                    var n = field.Gradient(gm);
                    u -= n * (limitPressure * WorldLenToGrid(hs.excess) * dt);
                }
                if (u.magnitude > maxStep) u = u.normalized * maxStep;
                caps.Add(new ClayField.Capsule { a = ga, b = gb, r = r, u = u });
            }
            return contact;
        }

        // 手のひら全体でさわる (なでる / こねる)
        readonly List<ClayField.Capsule> moving = new List<ClayField.Capsule>();
        void HandSculpt(HandState hs, float dt, float t)
        {
            bool contact = HandCapsules(hs, dt, out float F);
            if (contact)
            {
                if (!hs.contact) { PushUndo(); hs.contact = true; }
                hs.lastContactT = t;
            }
            else if (hs.contact && t - hs.lastContactT > 0.3f)
            {
                hs.contact = false;
            }
            if (caps.Count == 0) return;
            // 押し込みの深さで なでる ⇔ こねる を切り替える。k = 0: なでる / k = 1: こねる
            float k = SmoothStep(kneadFrom, kneadFrom + kneadSpan, hs.depth);
            hs.kneadK = k;
            if (k > 0.02f)
            {
                moving.Clear();
                foreach (var c0 in caps)
                {
                    var c = c0;
                    c.u *= k; // 浅いうちは粘土を動かす量も少なくする
                    if (c.u.magnitude > 0.01f) moving.Add(c);
                }
                field.Displace(moving, F);
            }
            if (contact && k < 0.98f)
            {
                float strength = dt * 5 * (1 - k);
                foreach (var c in caps)
                {
                    float R = c.r + F;
                    field.Smooth((c.a + c.b) * 0.5f, R, strength);
                    field.Smooth(c.b, R, strength);
                }
            }
        }

        static float SmoothStep(float a, float b, float x)
        {
            if (x <= a) return 0;
            if (x >= b) return 1;
            float t = (x - a) / (b - a);
            return t * t * (3 - 2 * t);
        }

        // ---------- つまむ ----------
        void StartPinch(HandState hs)
        {
            hs.pinching = true;
            manipDirty = true;
            var g = WorldToGrid(hs.pinchPos);
            float s = field.Sample(g);
            float R = WorldLenToGrid(GrabR);
            if (s < R * 0.5f)
            {
                hs.pinchMode = "pull";
                PushUndo();
                // つまんだ所から表面の少し内側をつかむ (つかむ点と指の位置関係は保つ)
                var n = field.Gradient(g);
                var inside = g - n * (s + R * 0.25f);
                hs.grabOffset = GridToWorld(inside) - hs.pinchPos;
                hs.lastG = inside;
                // つまんだ所に小さな輪と音 (伸ばす音の高さは、ここからの距離で決める)
                hfx[hs == hands[0] ? 0 : 1].pullStart = hs.pinchPos;
                var nw = clay.rotation * n;
                Sfx.Play("pinch", hs.pinchPos, 0.7f);
                fx.Ripple(GridToWorld(g - n * s) + nw * 0.002f, nw, new Color(1, 0.84f, 0.31f, 0.85f), 0.008f, 0.04f, 0.35f);
            }
            else if (s < WorldLenToGrid(NO_MOVE_NEAR))
            {
                hs.pinchMode = "none"; // 粘土のそばでつまんでも、粘土ごと動かさない
            }
            else
            {
                hs.pinchMode = "move";
            }
        }

        void EndPinch(HandState hs)
        {
            if (!hs.pinching) return;
            if (hs.pinchMode == "pull") Sfx.Play("release", hs.pinchPos, 0.6f); // 伸ばしていた粘土を離した
            hs.pinching = false;
            hs.pinchMode = null;
            manipDirty = true;
        }

        // 何もない所をつまむ: 片手で移動、両手で回転・拡大縮小 (隠す場所を選んでいる間はマップを動かす)
        void UpdateObjectManip(List<HandState> movers)
        {
            var target = hiding ? map.root : clay;
            float minScale = hiding ? MAP_SCALE_MIN : CUBE_SCALE * 0.25f, maxScale = hiding ? MAP_SCALE_MAX : CUBE_SCALE * 4;
            if (manipDirty || manipCount != movers.Count)
            {
                manipDirty = false;
                manipCount = movers.Count;
                if (movers.Count == 1)
                {
                    manipOffset = target.position - movers[0].pinchPos;
                }
                else if (movers.Count >= 2)
                {
                    manipV0 = movers[1].pinchPos - movers[0].pinchPos;
                    manipMid0 = (movers[0].pinchPos + movers[1].pinchPos) * 0.5f;
                    manipPos0 = target.position;
                    manipQuat0 = target.rotation;
                    manipScale0 = target.localScale.x;
                }
            }
            if (movers.Count == 1)
            {
                target.position = movers[0].pinchPos + manipOffset;
            }
            else if (movers.Count >= 2 && manipV0.magnitude > 0.01f)
            {
                var v = movers[1].pinchPos - movers[0].pinchPos;
                var mid = (movers[0].pinchPos + movers[1].pinchPos) * 0.5f;
                float newScale = Mathf.Clamp(manipScale0 * v.magnitude / manipV0.magnitude, minScale, maxScale);
                float s = newScale / manipScale0;
                var q = Quaternion.FromToRotation(manipV0.normalized, v.normalized);
                target.rotation = q * manipQuat0;
                target.localScale = Vector3.one * newScale;
                target.position = mid + q * (manipPos0 - manipMid0) * s;
            }
        }

        // ---------- レイで指す (OS と同じ: 手のレイにカーソルが出て、つまむとクリック) ----------
        bool HandRay(HandState hs, XRHand hand, out Vector3 origin, out Vector3 dir)
        {
            origin = default; dir = default;
            // 1) OpenXR の Hand Interaction Profile の aim pose (OS のレイと同じもの)
            if (hs.rayTracked.ReadValue<float>() > 0.5f)
            {
                var p = hs.rayPos.ReadValue<Vector3>();
                var q = hs.rayRot.ReadValue<Quaternion>();
                if (p != Vector3.zero && Mathf.Abs(q.w) + Mathf.Abs(q.x) + Mathf.Abs(q.y) + Mathf.Abs(q.z) > 0.5f)
                {
                    origin = rig.TransformPoint(p);
                    dir = rig.rotation * q * Vector3.forward;
                    return true;
                }
            }
            // 2) 無ければ、肩のあたりから親指と人差し指の付け根の間を通るレイにする
            if (!RawJoint(hand, XRHandJointID.IndexProximal, out var ip) || !RawJoint(hand, XRHandJointID.ThumbProximal, out var tp)) return false;
            var head = cam.transform;
            var flat = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
            var right = Vector3.Cross(Vector3.up, flat).normalized;
            var shoulder = head.position + Vector3.down * 0.15f + right * (hs.side == Handedness.Left ? -0.15f : 0.15f);
            origin = (ip + tp) * 0.5f;
            dir = (origin - shoulder).normalized;
            return true;
        }

        // 親指と人差し指の間から出るレイ (MRTK3 の HandRay と同じ考え方)。
        // 起点: 親指の付け根 (ThumbProximal) と人差し指の付け根 (IndexProximal) の中ほど。つまんでも動きにくい所なので、つまんだ瞬間にレイがぶれない。
        // 向き: 頭から決める「肩のあたりの支点」から起点を通る向き。支点は頭の 8cm 前、手の高さに応じて 20〜60cm 下、手の側へ 8〜15cm
        bool PinchRay(HandState hs, out Vector3 origin, out Vector3 dir)
        {
            origin = default; dir = default;
            if (!hs.valid.Contains(XRHandJointID.IndexProximal) || !hs.valid.Contains(XRHandJointID.ThumbProximal)) return false;
            origin = (hs.jreal[XRHandJointID.IndexProximal] + hs.jreal[XRHandJointID.ThumbProximal]) * 0.5f; // 震えを抑えた関節を使う
            var head = cam.transform;
            var h = head.InverseTransformPoint(origin);
            float relY = Mathf.Clamp(-0.1f + Mathf.Min(0.65f * h.y, 0), -0.6f, -0.2f);
            float relX = hs.side == Handedness.Right
                ? Mathf.Clamp(0.03f + 0.65f * h.x, 0.08f, 0.15f)
                : Mathf.Clamp(-0.03f + 0.65f * h.x, -0.15f, -0.08f);
            var pivot = head.position + head.TransformVector(new Vector3(relX, relY, 0.08f));
            var d = origin - pivot;
            if (d.sqrMagnitude < 1e-6f) return false;
            dir = d.normalized;
            return true;
        }

        void ShowUiPointer(int i, ButtonPanel.Hit hit, Vector3 origin, bool pressed)
        {
            var (ring, _, line) = pointers[i];
            var q = hit.panel.group.rotation;
            var nrm = q * Vector3.back; // 板の表の向き
            ring.gameObject.SetActive(true);
            line.enabled = true;
            ring.position = hit.point + nrm * 0.004f;
            ring.rotation = q;
            float sc = pressed ? 0.7f : hit.button != null ? 1f : 1.4f;
            ring.localScale = new Vector3(0.022f * sc, 0.022f * sc, 1);
            var r = ring.GetComponent<MeshRenderer>();
            ButtonPanel.SetRect(r, new Color(1, 1, 1, 0), pressed ? Gfx.Hex("#ffd54f") : Color.white, 0.022f * sc, 0.022f * sc, 0.011f * sc, 0.004f * sc, 0.95f);
            line.SetPosition(0, hiding ? origin : origin + Vector3.down * 0.02f);
            line.SetPosition(1, ring.position);
        }

        void HideUiPointer(int i)
        {
            pointers[i].ring.gameObject.SetActive(false);
            pointers[i].line.enabled = false;
        }

        // ---------- 1 フレームの XR の処理 ----------
        readonly List<HandState> movers = new List<HandState>();
        void UpdateXR(float dt, float t)
        {
            movers.Clear();
            foreach (var p in uiPanels) p.hover.Clear();
            var sub = HandSubsystem();
            for (int hi = 0; hi < 2; hi++)
            {
                var hs = hands[hi];
                var hand = sub == null ? default : hi == 0 ? sub.leftHand : sub.rightHand;
                bool ok = sub != null && hand.isTracked
                    && RawJoint(hand, XRHandJointID.IndexTip, out hs.tipRaw) && RawJoint(hand, XRHandJointID.ThumbTip, out hs.thumbRaw);
                brushCursor[hi].gameObject.SetActive(false);
                if (!ok)
                {
                    if (hs.connected) { hs.connected = false; }
                    hs.valid.Clear();
                    hs.kneadK = 0;
                    HideUiPointer(hi);
                    hs.uiPinch = false;
                    EndPinch(hs);
                    hs.ResetTouch();
                    continue;
                }
                if (!hs.connected)
                {
                    hs.connected = true;
                    if (!hs.reported) { hs.reported = true; Log($"手を認識: {hs.Label}"); }
                }
                ReadJoints(hs, hand, t);

                // レイが板を指しているか (粘土のすぐそばにある手は、粘土を触る方を優先して指さない)
                // 隠す場所を選んでいる間は、親指と人差し指の間から出るレイ (MRTK3 と同じ肩の支点の方式) にする
                bool hasRay = hiding ? PinchRay(hs, out var rayO, out var rayD) : HandRay(hs, hand, out rayO, out rayD);
                ButtonPanel.Hit hit = null;
                if (hasRay && (!hs.pinching || hs.uiPinch))
                {
                    bool nearClay = !hiding && !hs.uiPinch && hs.valid.Contains(XRHandJointID.IndexTip)
                        && field.Sample(WorldToGrid(hs.jreal[XRHandJointID.IndexTip])) < WorldLenToGrid(0.06f);
                    if (!nearClay)
                        foreach (var pn in uiPanels)
                        {
                            if (hi == 0 && pn == wrist.panel) continue; // 手首メニューは右手で操作する
                            var h = pn.RayHit(rayO, rayD);
                            if (h != null && (hit == null || h.t < hit.t)) hit = h;
                        }
                }
                if (hs.uiPinch && hit == null) hit = hs.uiHit; // つまんでいる間は、レイが板をはずれても同じ板を操作し続ける
                bool pointing = hit != null;
                UpdateTouchLimit(hs, dt, !hiding && !pointing && !hs.pinching && mode == "touch");
                hs.tip = hs.tipF.Filter(hs.tipRaw, t);
                hs.pinchPos = hs.pinchF.Filter((hs.tipRaw + hs.thumbRaw) * 0.5f, t);
                float pinchDist = Vector3.Distance(hs.tipRaw, hs.thumbRaw);

                // ボタンを指先で押す
                if (!hs.pinching)
                {
                    ButtonPanel.Btn fb = null; ButtonPanel fp = null; float lx = 0;
                    foreach (var pn in uiPanels)
                    {
                        if (hi == 0 && pn == wrist.panel) continue;
                        fb = pn.FingerHit(hs.tip, out lx);
                        if (fb != null) { fp = pn; break; }
                    }
                    if (fb != null)
                    {
                        fp.FingerPress(fb, lx, ref hs.pressed);
                        HideUiPointer(hi);
                        RememberJoints(hs);
                        continue;
                    }
                    hs.pressed = null;
                }

                // レイでクリック: 指して、つまむ。つまんでいる間はスライダーをなぞれる
                if (pointing && !hs.pinching)
                {
                    if (hs.uiPinch)
                    {
                        if (pinchDist > PINCH_OFF) hs.uiPinch = false;
                        else if (hasRay) hs.uiPinchPanel.RayDrag(hs.uiPinchButton, rayO, rayD);
                    }
                    else if (pinchDist < PINCH_ON)
                    {
                        hit.panel.RayDown(hit);
                        hs.uiPinch = true;
                        hs.uiPinchPanel = hit.panel;
                        hs.uiPinchButton = hit.button;
                    }
                    hs.uiHit = hit;
                    if (hit.button != null) hit.panel.hover.Add(hit.button.def.id);
                    ShowUiPointer(hi, hit, hasRay ? rayO : hs.pinchPos, hs.uiPinch);
                    RememberJoints(hs);
                    continue;
                }
                hs.uiPinch = false;
                HideUiPointer(hi);

                if (hiding)
                {
                    UpdateHideHand(hi, hs, hasRay, rayO, rayD, pinchDist);
                    RememberJoints(hs);
                    continue;
                }

                // ピンチ (ヒステリシス付き)
                if (!hs.pinching && pinchDist < PINCH_ON) StartPinch(hs);
                else if (hs.pinching && pinchDist > PINCH_OFF) EndPinch(hs);

                if (hs.pinching)
                {
                    if (hs.pinchMode == "pull")
                    {
                        var g = WorldToGrid(hs.pinchPos + hs.grabOffset);
                        field.Pull(hs.lastG, g - hs.lastG, WorldLenToGrid(GrabR));
                        hs.lastG = g;
                    }
                    else if (hs.pinchMode == "move")
                    {
                        movers.Add(hs);
                    }
                    RememberJoints(hs);
                    continue;
                }

                if (mode == "paint")
                {
                    // 塗る: 人差し指の先で。指を閉じかけている間は止める
                    bool allowed = pinchDist > PRE_PINCH;
                    float r = WorldLenToGrid(brushR);
                    var g = WorldToGrid(hs.tip);
                    float s = field.Sample(g);
                    if (allowed && s < r * 0.6f)
                    {
                        if (!hs.touching) { PushUndo(); hs.touching = true; }
                        field.Paint(g, r, Gfx.HexBytes(color), 0.35f);
                    }
                    else if (s > r || !allowed)
                    {
                        hs.touching = false;
                    }
                    var c = Gfx.Hex(color);
                    c.a = allowed ? (hs.touching ? 0.4f : 0.18f) : 0.06f;
                    brushMat[hi].SetColor("_BaseColor", c);
                    brushCursor[hi].position = hs.tip;
                    brushCursor[hi].localScale = Vector3.one * brushR;
                    brushCursor[hi].gameObject.SetActive(true);
                }
                else if (mode == "touch")
                {
                    HandSculpt(hs, dt, t);
                }
                RememberJoints(hs);
            }
            for (int hi = 0; hi < 2; hi++) { DrawHand(hi); HandFeedback(hi, dt, t); }
            UpdateObjectManip(movers);
            // 手首メニュー: 左手のひらを上に向けたら開く (粘土のそばや、つまんでいる間は開かない)
            var left = hands[0];
            bool farFromClay = !left.valid.Contains(XRHandJointID.Wrist)
                || field.Sample(WorldToGrid(left.jreal[XRHandJointID.Wrist])) > WorldLenToGrid(0.1f);
            wrist.Update(left, cam.transform, !hiding && !left.pinching && !left.uiPinch && farFromClay, dt);
        }

        // 手ごとの音と演出: 触れている所の輪、触れたときの波紋、こねたときの粘土くず、塗ったときのしぶき、手の音
        void HandFeedback(int hi, float dt, float t)
        {
            var hs = hands[hi]; var hf = hfx[hi];
            if (!hs.connected || hs.valid.Count == 0)
            {
                fx.Contact(hi, Vector3.zero, Vector3.up, 0, Color.white, 0);
                Sfx.Hand(hi, hf.lastTip, 0, 0, 0, 1, 0, dt);
                hf.wasContact = hf.wasPaint = false;
                return;
            }
            float sp = (hs.tip - hf.lastTip).magnitude / dt;
            if (sp > 5) sp = 0; // 手を見失って戻ったとき
            hf.speed += (sp - hf.speed) * (1 - Mathf.Exp(-10 * dt));
            hf.lastTip = hs.tip;
            float k = hs.kneadK;
            var col = Color.Lerp(RING_STROKE, RING_KNEAD, k);
            var n = hf.touchN;

            // 触れている所の輪: 近づくと薄く、触れるとはっきり。こねるほど橙色で大きく
            bool near = !hiding && mode == "touch" && !hs.pinching && hf.proximity > -0.012f;
            if (near)
            {
                float a = hs.contact ? 0.85f : 0.4f * Mathf.InverseLerp(-0.012f, 0.002f, hf.proximity);
                float radius = 0.022f + 0.018f * k + Mathf.Clamp(hs.depth, 0, 0.03f) * 0.5f;
                fx.Contact(hi, hf.touchPt + n * 0.003f, n, radius, col, a);
            }
            else fx.Contact(hi, Vector3.zero, Vector3.up, 0, col, 0);

            bool touchingClay = !hiding && mode == "touch" && hs.contact && !hs.pinching;
            if (touchingClay && !hf.wasContact)
            {
                var c = col; c.a = 0.8f;
                fx.Ripple(hf.touchPt + n * 0.002f, n, c, 0.01f, 0.06f, 0.5f);
                Sfx.Play("pop", hf.touchPt, 0.22f, 0.55f, 0.08f);
            }
            else if (touchingClay && hf.speed > 0.12f && t - hf.rippleT > 0.22f)
            {
                hf.rippleT = t;
                var c = col; c.a = 0.45f;
                fx.Ripple(hf.touchPt + n * 0.002f, n, c, 0.015f, 0.05f, 0.45f);
            }
            hf.wasContact = touchingClay;
            // こねている: 粘土くずが少し飛ぶ
            if (touchingClay && k > 0.6f && hf.speed > 0.1f && t - hf.crumbT > 0.07f)
            {
                hf.crumbT = t;
                fx.Burst(hf.touchPt + n * 0.005f, 2, ClayColorAt(hf.touchPt), 0.25f, 0.004f, 0.6f, 1.2f, n, 0.6f);
            }
            // 塗っている: その色のしぶき
            bool painting = !hiding && mode == "paint" && hs.touching;
            if (painting && hf.speed > 0.08f && t - hf.splashT > 0.05f)
            {
                hf.splashT = t;
                fx.Burst(hs.tip, 2, Gfx.Hex(color), 0.2f, 0.0035f, 0.5f, 0.8f, null, 1, 0.03f);
            }
            hf.wasPaint = painting;

            // 手の音
            float rub = 0, squish = 0, stretch = 0, pitch = 1, brush = 0;
            if (touchingClay)
            {
                rub = (1 - k) * Mathf.Clamp01(hf.speed / 0.25f);
                squish = k * Mathf.Clamp01(0.25f + hf.speed / 0.2f + hs.excess * 40f);
            }
            if (!hiding && hs.pinching && hs.pinchMode == "pull")
            {
                float dist = Vector3.Distance(hs.pinchPos, hf.pullStart);
                stretch = Mathf.Clamp01(0.15f + hf.speed / 0.15f);
                pitch = 0.8f + dist * 6f; // 伸ばすほど高く
            }
            if (painting) brush = Mathf.Clamp01(0.2f + hf.speed / 0.2f);
            Sfx.Hand(hi, hs.tip, rub, squish, stretch, pitch, brush, dt);
        }

        // 粘土の表面の色 (ワールドの点のいちばん近い格子点)
        Color ClayColorAt(Vector3 world)
        {
            var g = WorldToGrid(world);
            int N = field.N;
            int x = Mathf.Clamp(Mathf.RoundToInt(g.x), 0, N - 1), y = Mathf.Clamp(Mathf.RoundToInt(g.y), 0, N - 1), z = Mathf.Clamp(Mathf.RoundToInt(g.z), 0, N - 1);
            int i = (x + N * (y + N * z)) * 3;
            var c = field.col;
            return c == null || i + 2 >= c.Length ? Gfx.Hex("#c98060") : new Color(c[i] / 255f, c[i + 1] / 255f, c[i + 2] / 255f);
        }

        // 手の見た目: 見えている手 (表面で止まる位置) をカプセルで描く
        void DrawHand(int hi)
        {
            var hs = hands[hi];
            var L = looks[hi];
            L.Begin();
            if (hs.connected && hs.valid.Count > 0)
                foreach (var b in HandBones.BONES)
                {
                    if (!hs.valid.Contains(b.a) || !hs.valid.Contains(b.b)) continue;
                    L.AddBone(hs.jpos[b.a], hs.jpos[b.b], BoneRadius(hs, b));
                }
            // さわり方で、手の色を変える: なでる = 水色に寄せる / こねる = 橙色に寄せる (ふだんは元の肌色)
            bool touching = mode == "touch" && (hs.contact || hs.offset.sqrMagnitude > 1e-8f);
            L.w += ((touching ? 1 : 0) - L.w) * 0.2f;
            L.k += (hs.kneadK - L.k) * 0.25f;
            var c = Color.Lerp(HAND_BASE, Color.Lerp(HAND_STROKE, HAND_KNEAD, L.k), L.w);
            c.a = HAND_OPACITY;
            L.SetColor(c);
            L.Draw();
        }
    }
}
