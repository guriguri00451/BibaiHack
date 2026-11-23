using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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
            if (string.IsNullOrEmpty(modelUrl)) throw new Exception("マップ定義に modelUrl がありません");
            Name = def.Str("name") ?? mapId;

            // 再生していないとき (エディタでの確認) は、glTFast の既定の DeferAgent が使えない (DontDestroyOnLoad するため)
            var gltf = new GltfImport(
                deferAgent: Application.isPlaying ? null : new UninterruptedDeferAgent(),
                materialGenerator: new ClayMaterialGenerator());
            string glbUrl = ResolveStreamingAssets(modelUrl);
            if (!await gltf.Load(glbUrl)) throw new Exception($"マップの GLB を読めません: {glbUrl}");
            if (!await gltf.InstantiateMainSceneAsync(model)) throw new Exception("マップの GLB を置けません");

            // glTFast が GLB の中身は Unity の座標に直している。残るマップ定義の transform だけ z を反転して当てる
            var t = def.TryGetValue("transform", out var tv) ? tv as Dictionary<string, object> : null;
            var p = ReadFloats(t, "position", 3) ?? new[] { 0f, 0f, 0f };
            var q = ReadFloats(t, "rotation", 4) ?? new[] { 0f, 0f, 0f, 1f };
            var s = ReadFloats(t, "scale", 3) ?? new[] { 1f, 1f, 1f };
            model.localPosition = FlipZ(new Vector3(p[0], p[1], p[2]));
            model.localRotation = FlipZ(new Quaternion(q[0], q[1], q[2], q[3]).normalized);
            model.localScale = new Vector3(s[0], s[1], s[2]);

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
