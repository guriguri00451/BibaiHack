using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace AKun
{
    // 見た目の演出: 粒 (はじける・飛び散る・きらきら)、広がる輪、触れている所の輪、動き (Tween)。
    // 粒は 1 つのメッシュ (カメラの方を向いた六角形) にまとめて、1 回で描く
    public class Fx
    {
        readonly Camera cam;

        // ---------- 粒 ----------
        struct P
        {
            public Vector3 pos, vel;
            public Color col;
            public float size, life, age, grav, drag;
            public bool twinkle;
        }
        const int MAX = 800, SIDES = 6;
        readonly List<P> ps = new List<P>(MAX);
        readonly Mesh mesh;
        readonly MeshRenderer pr;
        readonly Vector3[] verts = new Vector3[MAX * (SIDES + 1)];
        readonly Color[] cols = new Color[MAX * (SIDES + 1)];
        readonly int[] tris = new int[MAX * SIDES * 3];
        readonly System.Random rnd = new System.Random(11);

        // ---------- 輪 ----------
        class Ring { public Transform t; public Material m; public float age, life, r0, r1; public Color col; public bool on; }
        readonly List<Ring> rings = new List<Ring>();
        static Mesh ringMesh;
        readonly Ring[] contact = new Ring[2];

        // ---------- 動き ----------
        public class Handle
        {
            public float t0, dur;
            public Action<float> step;
            public Action done;
            public bool finished;
            public void Cancel() => finished = true;
        }
        readonly List<Handle> tweens = new List<Handle>();

        public Fx(Camera cam)
        {
            this.cam = cam;
            var go = new GameObject("fx-particles");
            mesh = new Mesh { name = "fx-particles" };
            mesh.MarkDynamic();
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            pr = go.AddComponent<MeshRenderer>();
            pr.sharedMaterial = Gfx.Unlit(Color.white, 3060);
            pr.shadowCastingMode = ShadowCastingMode.Off;
            pr.receiveShadows = false;
            for (int i = 0; i < 2; i++) { contact[i] = NewRing(3045); contact[i].on = true; }
        }

        float R01() => (float)rnd.NextDouble();
        Vector3 RSphere()
        {
            while (true)
            {
                var v = new Vector3(R01() * 2 - 1, R01() * 2 - 1, R01() * 2 - 1);
                if (v.sqrMagnitude <= 1 && v.sqrMagnitude > 1e-4f) return v.normalized;
            }
        }

        // はじける粒。dir があれば、その向きに寄せる (spread: 0 = まっすぐ、1 = 全方向)
        public void Burst(Vector3 pos, int n, Color col, float speed, float size, float life, float gravity = 0, Vector3? dir = null, float spread = 1, float jitterColor = 0.08f)
        {
            for (int i = 0; i < n && ps.Count < MAX; i++)
            {
                var d = RSphere();
                if (dir != null) d = Vector3.Slerp(dir.Value.normalized, d, spread).normalized;
                var c = col;
                float j = (R01() * 2 - 1) * jitterColor;
                c.r = Mathf.Clamp01(c.r + j); c.g = Mathf.Clamp01(c.g + j); c.b = Mathf.Clamp01(c.b + j);
                ps.Add(new P
                {
                    pos = pos, vel = d * speed * (0.5f + R01()), col = c, size = size * (0.6f + R01() * 0.8f),
                    life = life * (0.7f + R01() * 0.6f), grav = gravity, drag = 2.5f,
                });
            }
        }

        // きらきら: center のまわり (半径 radius) から浮き上がって瞬く粒
        public void Sparkle(Vector3 center, float radius, int n, Color col, float life = 1.2f)
        {
            for (int i = 0; i < n && ps.Count < MAX; i++)
            {
                var p = center + RSphere() * radius * Mathf.Sqrt(R01());
                ps.Add(new P
                {
                    pos = p, vel = Vector3.up * (0.03f + R01() * 0.08f) + RSphere() * 0.02f, col = col,
                    size = 0.004f + R01() * 0.006f, life = life * (0.5f + R01()), grav = -0.02f, drag = 0.5f, twinkle = true,
                });
            }
        }

        // 輪の見本: 半径 1、太さ 0.18 の平たい輪 (XZ 平面、上 = +Y)
        static Mesh RingMesh()
        {
            if (ringMesh != null) return ringMesh;
            const int seg = 48;
            var v = new List<Vector3>(); var t = new List<int>();
            for (int s = 0; s <= seg; s++)
            {
                float a = 2 * Mathf.PI * s / seg;
                var d = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                v.Add(d * 0.82f); v.Add(d);
            }
            for (int s = 0; s < seg; s++) { int a = s * 2; t.AddRange(new[] { a, a + 1, a + 2, a + 2, a + 1, a + 3 }); }
            ringMesh = new Mesh { name = "fx-ring" };
            ringMesh.SetVertices(v); ringMesh.SetTriangles(t, 0);
            ringMesh.RecalculateBounds();
            return ringMesh;
        }

        Ring NewRing(int queue)
        {
            var go = new GameObject("fx-ring");
            go.AddComponent<MeshFilter>().sharedMesh = RingMesh();
            var r = go.AddComponent<MeshRenderer>();
            var m = Gfx.Unlit(Color.white, queue);
            r.sharedMaterial = m;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            go.SetActive(false);
            var ring = new Ring { t = go.transform, m = m };
            rings.Add(ring);
            return ring;
        }

        // 広がって消える輪。normal: 輪の面の向き
        public void Ripple(Vector3 pos, Vector3 normal, Color col, float r0, float r1, float life)
        {
            Ring ring = null;
            foreach (var r in rings) if (!r.on && Array.IndexOf(contact, r) < 0) { ring = r; break; }
            if (ring == null)
            {
                if (rings.Count > 40) return;
                ring = NewRing(3050);
            }
            ring.on = true; ring.age = 0; ring.life = life; ring.r0 = r0; ring.r1 = r1; ring.col = col;
            ring.t.SetPositionAndRotation(pos, Quaternion.FromToRotation(Vector3.up, normal.sqrMagnitude > 1e-6f ? normal : Vector3.up));
            ring.t.localScale = Vector3.one * r0;
            ring.m.SetColor("_BaseColor", col);
            ring.t.gameObject.SetActive(true);
        }

        // 触れている所の輪 (手ごと)。alpha 0 で消える
        public void Contact(int hand, Vector3 pos, Vector3 normal, float radius, Color col, float alpha)
        {
            var ring = contact[hand];
            bool show = alpha > 0.01f;
            if (ring.t.gameObject.activeSelf != show) ring.t.gameObject.SetActive(show);
            if (!show) return;
            ring.t.SetPositionAndRotation(pos, Quaternion.FromToRotation(Vector3.up, normal));
            ring.t.localScale = Vector3.one * radius;
            col.a = alpha;
            ring.m.SetColor("_BaseColor", col);
        }

        // ---------- 動き ----------
        // dur 秒かけて step(0→1) を呼び、終わったら done
        public Handle Tween(float dur, Action<float> step, Action done = null)
        {
            var h = new Handle { t0 = Time.time, dur = Mathf.Max(1e-3f, dur), step = step, done = done };
            tweens.Add(h);
            step?.Invoke(0);
            return h;
        }

        public static float OutBack(float t) { const float c = 1.70158f; t -= 1; return 1 + (c + 1) * t * t * t + c * t * t; }
        public static float InBack(float t) { const float c = 1.70158f; return (c + 1) * t * t * t - c * t * t; }
        public static float OutCubic(float t) { t = 1 - t; return 1 - t * t * t; }
        // ぼよん: 1 に向かって揺れながら落ち着く
        public static float Wobble(float t) => Mathf.Exp(-5 * t) * Mathf.Sin(t * 22);

        // ---------- 毎フレーム ----------
        public void Update(float dt)
        {
            // 動き
            float now = Time.time;
            for (int i = 0; i < tweens.Count; i++)
            {
                var h = tweens[i];
                if (h.finished) continue;
                float u = Mathf.Clamp01((now - h.t0) / h.dur);
                try { h.step?.Invoke(u); } catch (Exception e) { Debug.LogException(e); h.finished = true; }
                if (u >= 1 && !h.finished)
                {
                    h.finished = true;
                    try { h.done?.Invoke(); } catch (Exception e) { Debug.LogException(e); }
                }
            }
            tweens.RemoveAll(h => h.finished);

            // 輪
            foreach (var r in rings)
            {
                if (!r.on || Array.IndexOf(contact, r) >= 0) continue;
                r.age += dt;
                float u = r.age / r.life;
                if (u >= 1) { r.on = false; r.t.gameObject.SetActive(false); continue; }
                r.t.localScale = Vector3.one * Mathf.Lerp(r.r0, r.r1, OutCubic(u));
                var c = r.col; c.a *= 1 - u;
                r.m.SetColor("_BaseColor", c);
            }

            // 粒
            for (int i = ps.Count - 1; i >= 0; i--)
            {
                var p = ps[i];
                p.age += dt;
                if (p.age >= p.life) { ps.RemoveAt(i); continue; }
                p.vel += Vector3.down * (p.grav * dt);
                p.vel *= Mathf.Exp(-p.drag * dt);
                p.pos += p.vel * dt;
                ps[i] = p;
            }
            BuildParticleMesh();
        }

        void BuildParticleMesh()
        {
            mesh.Clear();
            if (ps.Count == 0 || cam == null) { pr.enabled = false; return; }
            pr.enabled = true;
            var right = cam.transform.right; var up = cam.transform.up;
            int n = ps.Count, vi = 0, ti = 0;
            for (int i = 0; i < n; i++)
            {
                var p = ps[i];
                float u = p.age / p.life;
                float size = p.size * (p.twinkle ? 0.5f + 0.5f * Mathf.Abs(Mathf.Sin(p.age * 18 + i)) : 1 - u * 0.5f);
                var c = p.col.linear;
                c.a = p.col.a * (u < 0.1f ? u / 0.1f : 1 - Mathf.Max(0, u - 0.5f) * 2);
                int center = vi;
                verts[vi] = p.pos; cols[vi] = c; vi++;
                var edge = c; edge.a *= 0.6f;
                for (int s = 0; s < SIDES; s++)
                {
                    float a = 2 * Mathf.PI * s / SIDES;
                    verts[vi] = p.pos + (right * Mathf.Cos(a) + up * Mathf.Sin(a)) * size;
                    cols[vi] = edge; vi++;
                }
                for (int s = 0; s < SIDES; s++)
                {
                    tris[ti++] = center; tris[ti++] = center + 1 + s; tris[ti++] = center + 1 + (s + 1) % SIDES;
                }
            }
            mesh.SetVertices(verts, 0, vi);
            mesh.SetColors(cols, 0, vi);
            mesh.SetTriangles(tris, 0, ti, 0, false);
            mesh.bounds = new Bounds(cam.transform.position, Vector3.one * 100);
        }
    }
}
