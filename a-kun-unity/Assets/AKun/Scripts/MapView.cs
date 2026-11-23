using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using GaussianSplatting.Runtime;
using GLTFast;
using GLTFast.Logging;
using GLTFast.Materials;
using UnityEngine;
using UnityEngine.Networking;

namespace AKun
{
    // 隠す場所を選ぶためのマップの模型。
    // seeker-unity の MapService / MapDefinition / MapPresenter (マップ定義の JSON を読み、glTFast で GLB を置く) をもとにした。
    //
    // 階層: root  … マップ座標系の原点。動かす・回す・拡大縮小するのはここだけ
    //        ├ model … マップ定義の transform (マップ座標系の中で GLB を置く位置)
    //        └ spot  … 隠す場所 (粘土の形をマップと同じ縮尺で置く)
    // root をどう動かしても、隠す場所は root から見た位置 (= マップ座標) で決まるので変わらない。
    // マップ座標は右手系・Y 上・メートル (仕様 3.2)。Unity (左手系) とは z の反転で行き来する (CoordinateAdapter と同じ)
    public class MapView
    {
        public readonly Transform root;
        readonly Transform model, spot;
        readonly MeshFilter spotMesh;
        readonly Material spotMat;
        readonly List<Collider> colliders = new List<Collider>();
        // Gaussian Splat (SPZ)。GLB の代わりに (または GLB と一緒に) 置く。Collider が無いので、レイは SplatProxy に当てる
        GaussianSplatRenderer splat;
        SplatProxy splatProxy;

        public bool IsLoaded { get; private set; }
        public string Name { get; private set; } = "";
        // 読み込んだマップの ID (ホストがマップを替えたら読み直す)
        public string LoadedId { get; private set; }
        // root から見たマップの大きさ (読み込んだ GLB の範囲)
        public Bounds LocalBounds { get; private set; }

        // 隠す場所 (root から見た位置と向き。Unity の座標)
        public bool HasSpot { get; private set; }
        public Vector3 SpotPosition { get; private set; }
        public Quaternion SpotRotation { get; private set; } = Quaternion.identity;

        public MapView(Material spotMaterial)
        {
            root = new GameObject("Map").transform;
            model = new GameObject("MapModel").transform;
            model.SetParent(root, false);
            spot = new GameObject("HideSpot").transform;
            spot.SetParent(root, false);
            spotMesh = spot.gameObject.AddComponent<MeshFilter>();
            var r = spot.gameObject.AddComponent<MeshRenderer>();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            spotMat = spotMaterial;
            r.sharedMaterial = spotMat;
            spot.gameObject.SetActive(false);
            root.gameObject.SetActive(false);
        }

        public bool Visible
        {
            get => root.gameObject.activeSelf;
            set => root.gameObject.SetActive(value);
        }

        // ---------- 読み込み (MapService.FetchAsync + MapPresenter.LoadAsync) ----------

        // apiBase が空なら StreamingAssets/mock/{mapId}.json を読む。あれば {apiBase}/maps/{mapId}
        public async Task Load(string mapId, string apiBase = null, string token = null)
        {
            string url = string.IsNullOrEmpty(apiBase)
                ? ResolveStreamingAssets($"mock/{mapId}.json")
                : $"{apiBase.TrimEnd('/')}/maps/{UnityWebRequest.EscapeURL(mapId)}";
            string json = await GetText(url, token);
            if (!(MiniJson.Parse(json) is Dictionary<string, object> def)) throw new Exception("マップ定義が JSON ではありません");
            Clear();
            string modelUrl = def.Str("modelUrl");
            string splatUrl = def.Str("splatUrl");
            if (string.IsNullOrEmpty(modelUrl) && string.IsNullOrEmpty(splatUrl)) throw new Exception("マップ定義に modelUrl も splatUrl もありません");
            Name = def.Str("name") ?? mapId;

            // GLB。SPZ だけのマップでは空のシーンが入っている
            if (!string.IsNullOrEmpty(modelUrl))
            {
                // 再生していないとき (エディタでの確認) は、glTFast の既定の DeferAgent が使えない (DontDestroyOnLoad するため)
                var gltf = new GltfImport(
                    deferAgent: Application.isPlaying ? null : new UninterruptedDeferAgent(),
                    materialGenerator: new ClayMaterialGenerator());
                string glbUrl = ResolveStreamingAssets(modelUrl);
                if (!await gltf.Load(glbUrl)) throw new Exception($"マップの GLB を読めません: {glbUrl}");
                if (!await gltf.InstantiateMainSceneAsync(model)) throw new Exception("マップの GLB を置けません");
            }

            // glTFast が GLB の中身は Unity の座標に直している。残るマップ定義の transform だけ z を反転して当てる
            var t = def.TryGetValue("transform", out var tv) ? tv as Dictionary<string, object> : null;
            var p = ReadFloats(t, "position", 3) ?? new[] { 0f, 0f, 0f };
            var q = ReadFloats(t, "rotation", 4) ?? new[] { 0f, 0f, 0f, 1f };
            var s = ReadFloats(t, "scale", 3) ?? new[] { 1f, 1f, 1f };
            model.localPosition = FlipZ(new Vector3(p[0], p[1], p[2]));
            model.localRotation = FlipZ(new Quaternion(q[0], q[1], q[2], q[3]).normalized);
            model.localScale = new Vector3(s[0], s[1], s[2]);

            // Gaussian Splat (置き方は GLB と同じ model の下なので、上の transform がそのまま効く)
            if (!string.IsNullOrEmpty(splatUrl)) await LoadSplat(mapId, ResolveStreamingAssets(splatUrl));

            // レイで隠す場所を指せるように、マップのメッシュに当たり判定を付ける
            Bounds? b = null;
            foreach (var mf in model.GetComponentsInChildren<MeshFilter>())
            {
                if (mf.sharedMesh == null) continue;
                colliders.Add(mf.gameObject.AddComponent<MeshCollider>());
                foreach (var corner in Corners(mf.sharedMesh.bounds))
                {
                    var lp = root.InverseTransformPoint(mf.transform.TransformPoint(corner));
                    if (b == null) b = new Bounds(lp, Vector3.zero);
                    else { var bb = b.Value; bb.Encapsulate(lp); b = bb; }
                }
            }
            if (splat != null)
            {
                // SPZ の範囲 (外れ値を除いた点の範囲) を、root から見た範囲にして足す
                var sb = splatProxy != null ? splatProxy.Bounds : splat.localBounds;
                var toRoot = Matrix4x4.TRS(model.localPosition, model.localRotation, model.localScale);
                foreach (var corner in Corners(sb))
                {
                    var lp = toRoot.MultiplyPoint3x4(corner);
                    if (b == null) b = new Bounds(lp, Vector3.zero);
                    else { var bb = b.Value; bb.Encapsulate(lp); b = bb; }
                }
            }
            LocalBounds = b ?? new Bounds(Vector3.zero, Vector3.one);
            LoadedId = mapId;
            IsLoaded = true;
        }

        // 前に読んだマップを外す (別のマップを読み直すとき)
        void Clear()
        {
            IsLoaded = false;
            LoadedId = null;
            colliders.Clear();
            splat = null; // GameObject は下で model の子として消える
            splatProxy = null;
            for (int i = model.childCount - 1; i >= 0; i--)
            {
                var child = model.GetChild(i).gameObject;
                if (Application.isPlaying) UnityEngine.Object.Destroy(child);
                else UnityEngine.Object.DestroyImmediate(child);
            }
            model.localPosition = Vector3.zero;
            model.localRotation = Quaternion.identity;
            model.localScale = Vector3.one;
            ClearSpot();
        }

        // ---------- Gaussian Splat ----------

        // SPZ を端末に落としてから読み込む。GaussianSplatRenderer は無効にすると GPU のデータを捨て、有効にするとまた読み込むので、
        // 隠す場所を選ぶたびに (マップの表示を切り替えるたびに) 通信しなくて済むよう、読み込み元は端末内のファイルにする。
        async Task LoadSplat(string mapId, string url)
        {
            string path = await DownloadSplat(mapId, url);
            var go = new GameObject("MapSplat");
            go.SetActive(false);
            go.transform.SetParent(model, false);
            var r = go.AddComponent<GaussianSplatRenderer>();
            r.m_SourcePath = path;
            r.m_OpacityScale = 0f; // 読み込みが終わってから、表示を切り替えるまでは出さない
            splat = r;

            // GaussianSplatRenderer は有効な間にしか読み込まないので、マップが隠れていても、読み込む間だけ root を有効にする
            bool rootWas = root.gameObject.activeSelf;
            root.gameObject.SetActive(true);
            go.SetActive(true);
            try
            {
                while (r != null && r.isLoading) await Task.Yield();
                if (r == null) throw new Exception("マップが替わりました");
                if (r.loadError != null) throw new Exception($"SPZ を読めません: {r.loadError}");
                Debug.Log($"[clay] SPZ を読み込んだ {mapId} 点={r.splatCount} 範囲 中心={r.localBounds.center} 大きさ={r.localBounds.size}");

                // 隠す場所を指すレイを当てるため、点の座標を取り出して粗い格子にする
                var xyz = ReadSplatPositions(r);
                splatProxy = xyz != null ? SplatProxy.Build(xyz) : null;
                if (splatProxy == null) Debug.LogWarning("[clay] SPZ の点を取り出せないので、隠す場所はマップの範囲の箱に当てます");
            }
            finally
            {
                if (r != null) r.m_OpacityScale = 1f;
                if (!rootWas) root.gameObject.SetActive(false); // 無効になると GPU のデータを捨てる。表示するときにまた読み込む
            }
            if (splatProxy == null)
            {
                var box = go.AddComponent<BoxCollider>();
                box.center = r.localBounds.center;
                box.size = r.localBounds.size;
                colliders.Add(box);
            }
        }

        static async Task<string> DownloadSplat(string mapId, string url)
        {
            string dir = Path.Combine(Application.temporaryCachePath, "splat");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"{mapId}.spz");
            using var req = UnityWebRequest.Get(url);
            req.downloadHandler = new DownloadHandlerFile(path) { removeFileOnAbort = true };
            var op = req.SendWebRequest();
            while (!op.isDone) await Task.Yield();
            if (req.result != UnityWebRequest.Result.Success) throw new Exception($"SPZ を取得できません: {url} ({req.error})");
            return path;
        }

        // 点の座標 (float3) は GPU のバッファにだけあり、パッケージの外からは取れないので、型の中の欄を名前で引く (Assets/link.xml で残している)
        static float[] ReadSplatPositions(GaussianSplatRenderer r)
        {
            try
            {
                var field = typeof(GaussianSplatRenderer).GetField("m_GpuPosData", BindingFlags.NonPublic | BindingFlags.Instance);
                if (!(field?.GetValue(r) is GraphicsBuffer buf) || buf.count < 3) return null;
                var raw = new uint[buf.count];
                buf.GetData(raw);
                var xyz = new float[raw.Length];
                Buffer.BlockCopy(raw, 0, xyz, 0, raw.Length * sizeof(float));
                return xyz;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[clay] SPZ の点を読めません: {e.Message}");
                return null;
            }
        }

        // 相対パスは StreamingAssets の中、絶対 URL はそのまま (MapService.ResolveStreamingAssets と同じ)
        public static string ResolveStreamingAssets(string pathOrUrl)
        {
            if (Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var uri) && uri.Scheme != "file") return pathOrUrl;
            // Android の StreamingAssets は jar の中なので、UnityWebRequest でしか読めない
            string dir = Application.streamingAssetsPath;
            return dir.Contains("://") ? $"{dir}/{pathOrUrl}" : $"file://{dir}/{pathOrUrl}";
        }

        static async Task<string> GetText(string url, string token)
        {
            using var req = UnityWebRequest.Get(url);
            if (!string.IsNullOrEmpty(token)) req.SetRequestHeader("Authorization", $"Bearer {token}");
            var op = req.SendWebRequest();
            while (!op.isDone) await Task.Yield();
            if (req.result != UnityWebRequest.Result.Success) throw new Exception($"マップ定義を読めません: {url} ({req.error})");
            return req.downloadHandler.text;
        }

        static float[] ReadFloats(Dictionary<string, object> t, string key, int n)
        {
            if (t == null || !t.TryGetValue(key, out var v) || !(v is List<object> list) || list.Count == 0) return null;
            if (list.Count != n) throw new Exception($"マップ定義の transform.{key} は {n} 個の数にしてください");
            var r = new float[n];
            for (int i = 0; i < n; i++) r[i] = Convert.ToSingle(list[i]);
            return r;
        }

        static IEnumerable<Vector3> Corners(Bounds b)
        {
            for (int i = 0; i < 8; i++)
                yield return b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
        }

        // ---------- 置き方 (移動・回転・拡大縮小) ----------

        // 「何らかの情報」でマップの置き方を決めるときの入り口 (マーカーの位置、保存しておいた置き方など)
        public void SetPose(Vector3 position, Quaternion rotation, float scale)
        {
            root.SetPositionAndRotation(position, rotation);
            root.localScale = Vector3.one * scale;
        }

        // 机の上に模型として置く: 横幅 (長い方) を width にして、底面の中心を bottomCenter に合わせる
        public void FitOnTable(Vector3 bottomCenter, Quaternion rotation, float width)
        {
            var b = LocalBounds;
            float size = Mathf.Max(b.size.x, b.size.z, 1e-3f);
            float s = width / size;
            var localBottom = new Vector3(b.center.x, b.min.y, b.center.z);
            SetPose(bottomCenter - rotation * (localBottom * s), rotation, s);
        }

        // ---------- 隠す場所 ----------

        public struct RayHit
        {
            public Vector3 point, normal; // ワールド
            public float distance;
        }

        // レイがマップに当たる所 (マップ以外のものには当てない)
        public bool Raycast(Vector3 origin, Vector3 dir, out RayHit hit, float maxDistance = 10f)
        {
            hit = default;
            if (!IsLoaded || !Visible) return false;
            Physics.SyncTransforms(); // root を手で動かした直後でも当たり判定が付いてくるように
            float best = float.PositiveInfinity;
            var ray = new Ray(origin, dir);
            foreach (var c in colliders)
            {
                if (c == null || !c.Raycast(ray, out var h, maxDistance) || h.distance >= best) continue;
                best = h.distance;
                hit = new RayHit { point = h.point, normal = h.normal, distance = h.distance };
            }
            // Gaussian Splat: 点から作った格子に、Splat のローカルでレイを当てる
            if (splat != null && splatProxy != null)
            {
                var tr = splat.transform;
                var d = dir.normalized;
                if (splatProxy.Raycast(tr.InverseTransformPoint(origin), tr.InverseTransformVector(d), maxDistance, out float t, out var n) && t < best)
                {
                    best = t;
                    hit = new RayHit { point = origin + d * t, normal = tr.TransformDirection(n).normalized, distance = t };
                }
            }
            return best < float.PositiveInfinity;
        }

        // 粘土の形 (底面の中心が原点、メートル) を、隠す場所に置く見本として使う
        public void SetSpotMesh(Mesh mesh)
        {
            var old = spotMesh.sharedMesh;
            if (old != null && old != mesh) UnityEngine.Object.Destroy(old); // 前に投稿しかけた形 (ExportClay が毎回作る)
            spotMesh.sharedMesh = mesh;
            spot.gameObject.SetActive(HasSpot && mesh != null);
        }

        // 隠す場所に置いたモデルを手でつまむための球 (ワールド)。モデルの形の外接球
        public bool SpotGrabSphere(out Vector3 center, out float radius)
        {
            center = default; radius = 0;
            var m = spotMesh.sharedMesh;
            if (!HasSpot || m == null) return false;
            center = spot.TransformPoint(m.bounds.center);
            radius = m.bounds.extents.magnitude * Mathf.Abs(spot.lossyScale.x);
            return true;
        }

        // レイが当たった所に隠す: 底面をマップの面に付け、上を面の向きに、正面をレイの来た向き (見ている人の方) に向ける
        public void SetSpot(RayHit hit, Vector3 rayDir)
        {
            var up = root.InverseTransformDirection(hit.normal).normalized;
            var toViewer = Vector3.ProjectOnPlane(root.InverseTransformDirection(-rayDir), up);
            if (toViewer.sqrMagnitude < 1e-6f) toViewer = Vector3.ProjectOnPlane(Vector3.back, up);
            if (toViewer.sqrMagnitude < 1e-6f) toViewer = Vector3.ProjectOnPlane(Vector3.right, up);
            SetSpotLocal(root.InverseTransformPoint(hit.point), Quaternion.LookRotation(-toViewer.normalized, up));
        }

        public void SetSpotLocal(Vector3 position, Quaternion rotation)
        {
            HasSpot = true;
            SpotPosition = position;
            SpotRotation = rotation;
            spot.localPosition = position;
            spot.localRotation = rotation;
            spot.localScale = Vector3.one; // マップと同じ縮尺 (投稿する粘土は scale 1)
            spot.gameObject.SetActive(spotMesh.sharedMesh != null);
        }

        public void ClearSpot()
        {
            HasSpot = false;
            spot.gameObject.SetActive(false);
        }

        // 隠す場所を仕様 3.5 の Transform にする (マップ座標 = 右手系なので z を反転)
        public Dictionary<string, object> SpotTransform()
        {
            var p = FlipZ(SpotPosition);
            var q = FlipZ(SpotRotation).normalized;
            return new Dictionary<string, object>
            {
                ["position"] = new List<object> { p.x, p.y, p.z },
                ["rotation"] = new List<object> { q.x, q.y, q.z, q.w },
                ["scale"] = new List<object> { 1f, 1f, 1f },
            };
        }

        // 右手系 ⇔ 左手系 (z の反転)。回転は x, y の符号が変わる (seeker-unity の CoordinateAdapter と同じ)
        public static Vector3 FlipZ(Vector3 v) => new Vector3(v.x, v.y, -v.z);
        public static Quaternion FlipZ(Quaternion q) => new Quaternion(-q.x, -q.y, q.z, q.w);

        // マップの材質: glTFast の Shader Graph は使わず、粘土と同じシェーダー (同じライト) で基本色だけを出す。
        // ビルドで Shader Graph が落ちてピンクになるのを避けるため (テクスチャは今は出さない)
        class ClayMaterialGenerator : IMaterialGenerator
        {
            UnityEngine.Material fallback;

            static UnityEngine.Material Make(Color srgb)
            {
                var m = new UnityEngine.Material(Gfx.Find("Clay"));
                m.SetColor("_BaseColor", srgb);
                m.SetFloat("_UseVertexColor", 0);
                m.SetFloat("_Spec", 0.08f);
                return m;
            }

            public UnityEngine.Material GetDefaultMaterial(bool pointsSupport = false)
                => fallback != null ? fallback : fallback = Make(Gfx.Hex("#9aa3ad"));

            public UnityEngine.Material GenerateMaterial(GLTFast.Schema.MaterialBase gltfMaterial, IGltfReadable gltf, bool pointsSupport = false)
            {
                var c = gltfMaterial?.PbrMetallicRoughness?.BaseColor ?? Color.white; // glTF の色はリニア
                var m = Make(new Color(c.r, c.g, c.b, 1).gamma);
                m.name = gltfMaterial?.name ?? "map";
                return m;
            }

            public void SetLogger(ICodeLogger logger) { }
        }
    }
}
