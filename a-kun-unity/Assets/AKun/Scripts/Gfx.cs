using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace AKun
{
    // 材質・メッシュ・文字の用意 (シーンはすべてコードで組み立てる)
    public static class Gfx
    {
        public static Shader Find(string name)
        {
            var s = Resources.Load<Shader>("Shaders/" + name);
            if (s == null) Debug.LogError($"[clay] シェーダーが見つかりません: {name}");
            return s;
        }

        static readonly Dictionary<string, Material> cache = new Dictionary<string, Material>();

        // 同じ種類の物で共有する材質 (色などは MaterialPropertyBlock で変える)
        public static Material Shared(string shader, int queue, CompareFunction ztest = CompareFunction.LessEqual)
        {
            string key = $"{shader}|{queue}|{ztest}";
            if (cache.TryGetValue(key, out var m)) return m;
            m = new Material(Find(shader)) { renderQueue = queue, enableInstancing = true };
            if (m.HasProperty("_ZTest")) m.SetFloat("_ZTest", (float)ztest);
            cache[key] = m;
            return m;
        }

        public static Material Unlit(Color c, int queue = 3000, CompareFunction ztest = CompareFunction.LessEqual)
        {
            var m = new Material(Find("Unlit")) { renderQueue = queue };
            m.SetColor("_BaseColor", c);
            m.SetFloat("_ZTest", (float)ztest);
            return m;
        }

        // #rrggbb → Color (sRGB の値のまま。Linear のプロジェクトでは SetColor が変換する)
        public static Color Hex(string hex)
        {
            ColorUtility.TryParseHtmlString(hex, out var c);
            return c;
        }

        public static byte[] HexBytes(string hex)
        {
            int n = System.Convert.ToInt32(hex.Substring(1), 16);
            return new[] { (byte)((n >> 16) & 255), (byte)((n >> 8) & 255), (byte)(n & 255) };
        }

        // 一辺 1 の四角 (中心が原点、XY 平面、表は -Z を向く: カメラが +Z を見ると見える)
        static Mesh quad;
        public static Mesh Quad
        {
            get
            {
                if (quad != null) return quad;
                quad = new Mesh { name = "akun-quad" };
                quad.vertices = new[] { new Vector3(-0.5f, -0.5f, 0), new Vector3(0.5f, -0.5f, 0), new Vector3(-0.5f, 0.5f, 0), new Vector3(0.5f, 0.5f, 0) };
                quad.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
                quad.normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
                quad.triangles = new[] { 0, 2, 1, 2, 3, 1 };
                quad.RecalculateBounds();
                return quad;
            }
        }

        // 半径 1 の球
        public static Mesh Sphere(int seg = 16, int rings = 12)
        {
            var v = new List<Vector3>(); var t = new List<int>();
            for (int r = 0; r <= rings; r++)
            {
                float phi = Mathf.PI * r / rings;
                for (int s = 0; s <= seg; s++)
                {
                    float th = 2 * Mathf.PI * s / seg;
                    v.Add(new Vector3(Mathf.Sin(phi) * Mathf.Cos(th), Mathf.Cos(phi), Mathf.Sin(phi) * Mathf.Sin(th)));
                }
            }
            for (int r = 0; r < rings; r++)
                for (int s = 0; s < seg; s++)
                {
                    int a = r * (seg + 1) + s, b = a + seg + 1;
                    t.Add(a); t.Add(a + 1); t.Add(b);
                    t.Add(b); t.Add(a + 1); t.Add(b + 1);
                }
            var m = new Mesh { name = "akun-sphere" };
            m.SetVertices(v); m.SetNormals(v); m.SetTriangles(t, 0);
            FixWinding(m);
            m.RecalculateBounds();
            return m;
        }

        // 半径 1、高さ 1 (y = -0.5〜0.5) の筒 (ふたなし)
        public static Mesh Cylinder(int seg = 16)
        {
            var v = new List<Vector3>(); var n = new List<Vector3>(); var t = new List<int>();
            for (int s = 0; s <= seg; s++)
            {
                float th = 2 * Mathf.PI * s / seg;
                var d = new Vector3(Mathf.Cos(th), 0, Mathf.Sin(th));
                v.Add(d + Vector3.down * 0.5f); n.Add(d);
                v.Add(d + Vector3.up * 0.5f); n.Add(d);
            }
            for (int s = 0; s < seg; s++)
            {
                int a = s * 2;
                t.Add(a); t.Add(a + 1); t.Add(a + 2);
                t.Add(a + 2); t.Add(a + 1); t.Add(a + 3);
            }
            var m = new Mesh { name = "akun-cylinder" };
            m.SetVertices(v); m.SetNormals(n); m.SetTriangles(t, 0);
            FixWinding(m);
            m.RecalculateBounds();
            return m;
        }

        // 三角形の向きを法線にそろえる (表 = 外側)
        static void FixWinding(Mesh m)
        {
            var v = m.vertices; var n = m.normals; var t = m.triangles;
            for (int i = 0; i < t.Length; i += 3)
            {
                var fn = Vector3.Cross(v[t[i + 1]] - v[t[i]], v[t[i + 2]] - v[t[i]]);
                if (Vector3.Dot(fn, n[t[i]] + n[t[i + 1]] + n[t[i + 2]]) < 0) { (t[i + 1], t[i + 2]) = (t[i + 2], t[i + 1]); }
            }
            m.triangles = t;
        }

        // ---------- 文字 ----------
        // 日本語の出る OS のフォントを使う (Android: Noto Sans CJK)
        static Font font;
        static readonly List<TextMesh> texts = new List<TextMesh>();
        public static string FontName { get; private set; } = "";

        public static Font Font
        {
            get
            {
                if (font != null) return font;
                var names = new List<string>();
                try
                {
                    foreach (var n in Font.GetOSInstalledFontNames())
                    {
                        string l = n.ToLowerInvariant();
                        if (l.Contains("cjk") && l.Contains("jp")) names.Insert(0, n);
                        else if (l.Contains("cjk") || l.Contains("hiragino") || l.Contains("noto sans jp") || l.Contains("droidsansfallback")) names.Add(n);
                    }
                }
                catch { /* 一覧が取れない環境 */ }
                names.AddRange(new[] { "Noto Sans CJK JP", "NotoSansCJK-Regular", "Hiragino Sans", "Roboto", "Arial" });
                font = Font.CreateDynamicFontFromOSFont(names.ToArray(), 64);
                FontName = names[0];
                Font.textureRebuilt += OnFontRebuilt;
                return font;
            }
        }

        public static Material TextMaterial(CompareFunction ztest = CompareFunction.LessEqual, int queue = 2990)
        {
            var m = Shared("Text", queue, ztest);
            m.mainTexture = Font.material.mainTexture;
            return m;
        }

        // フォントのアトラスが作り直されたら、文字の材質のテクスチャを差し替える
        public static void SyncFontTexture()
        {
            if (font == null) return;
            var tex = font.material.mainTexture;
            foreach (var m in cache.Values)
                if (m.shader != null && m.shader.name == "AKun/Text" && m.mainTexture != tex) m.mainTexture = tex;
        }

        static void OnFontRebuilt(Font f)
        {
            if (f != font) return;
            foreach (var m in cache.Values) if (m.shader != null && m.shader.name == "AKun/Text") m.mainTexture = f.material.mainTexture;
            // アトラスが作り直されたので、文字のメッシュを作り直させる
            foreach (var t in texts)
            {
                if (t == null) continue;
                string s = t.text;
                t.text = "";
                t.text = s;
            }
        }

        // 文字。anchor は TextAnchor。lineHeight: 1 行の高さ (m)
        public static TextMesh Text(Transform parent, string s, float lineHeight, Color color, TextAnchor anchor = TextAnchor.MiddleCenter, int queue = 2990)
        {
            var go = new GameObject("text");
            go.transform.SetParent(parent, false);
            var tm = go.AddComponent<TextMesh>();
            tm.font = Font;
            tm.fontSize = 64;
            tm.anchor = anchor;
            tm.alignment = anchor == TextAnchor.MiddleLeft || anchor == TextAnchor.UpperLeft ? TextAlignment.Left
                : anchor == TextAnchor.MiddleRight || anchor == TextAnchor.UpperRight ? TextAlignment.Right : TextAlignment.Center;
            // TextMesh の 1 行の高さ ≒ fontSize × characterSize × 0.1 (ローカル単位)
            tm.characterSize = lineHeight / (64 * 0.1f);
            tm.color = color;
            tm.richText = false;
            tm.text = s;
            Font.RequestCharactersInTexture(s, 64);
            go.GetComponent<MeshRenderer>().sharedMaterial = TextMaterial(CompareFunction.LessEqual, queue);
            texts.Add(tm);
            return tm;
        }
    }
}
