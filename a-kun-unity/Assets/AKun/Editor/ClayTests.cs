using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace AKun.EditorTools
{
    // 粘土の計算の確認 (Web 版と同じ結果になるか)。
    //   Unity -batchmode -quit -projectPath a-kun-unity -executeMethod AKun.EditorTools.ClayTests.Run
    public static class ClayTests
    {
        [MenuItem("AKun/Run Clay Tests")]
        public static void Run()
        {
            int fails = 0;
            void Check(bool ok, string msg) { Debug.Log($"[test] {(ok ? "OK  " : "FAIL")} {msg}"); if (!ok) fails++; }

            var f = new ClayField(80, 0.4f);
            const float R = 0.0865f;
            f.ResetSphere(R);
            var sw = Stopwatch.StartNew();
            var meshes = MeshAll(f, out var P, out var N, out var T);
            Debug.Log($"[test] mesh all: {sw.ElapsedMilliseconds} ms, {P.Length} verts, {T.Length / 3} tris");
            float vol = Glb.Volume(P, T);
            float expect = 4f / 3f * Mathf.PI * R * R * R;
            Check(Mathf.Abs(vol / expect - 1) < 0.02f, $"球の体積 {vol:0.000000} m³ (理論値 {expect:0.000000})");
            Check(Mathf.Abs(Mathf.Pow(0.0027f / vol, 1f / 3f) - 1) < 0.02f, $"投稿の倍率 ≈ 1 ({Mathf.Pow(0.0027f / vol, 1f / 3f):0.000})");

            // 三角形の向き: 数式どおりの外積が外 (法線の向き) を向く
            int outward = 0;
            for (int i = 0; i < T.Length; i += 3)
            {
                var fn = Vector3.Cross(P[T[i + 1]] - P[T[i]], P[T[i + 2]] - P[T[i]]);
                if (Vector3.Dot(fn, N[T[i]]) > 0) outward++;
            }
            Check(outward == T.Length / 3, $"三角形の向き {outward}/{T.Length / 3}");

            // 引っ張っても体積がほぼ変わらない
            float g0 = f.N / 2f;
            var c = new Vector3(g0, g0 + R / f.h - 0.5f, g0);
            sw.Restart();
            for (int i = 0; i < 10; i++) f.Pull(c + Vector3.up * i * 0.8f, Vector3.up * 0.8f, 6f);
            Debug.Log($"[test] pull x10: {sw.ElapsedMilliseconds} ms");
            MeshAll(f, out P, out N, out T);
            float vol2 = Glb.Volume(P, T);
            Check(Mathf.Abs(vol2 / vol - 1) < 0.03f, $"引っ張ったあとの体積 {vol2 / vol * 100:0.0}%");

            // 押しのける (手のカプセル)
            var caps = new List<ClayField.Capsule> { new ClayField.Capsule { a = new Vector3(g0 - 3, g0, g0 - R / f.h - 1), b = new Vector3(g0 + 3, g0, g0 - R / f.h - 1), r = 1.5f, u = new Vector3(0, 0, 0.8f) } };
            sw.Restart();
            bool moved = false;
            for (int i = 0; i < 10; i++) { moved |= f.Displace(caps, 1.5f); var cc = caps[0]; cc.a.z += 0.8f; cc.b.z += 0.8f; caps[0] = cc; }
            Debug.Log($"[test] displace x10: {sw.ElapsedMilliseconds} ms");
            Check(moved, "押しのけると形が変わる");

            // 塗る
            bool painted = f.Paint(new Vector3(g0, g0, g0 - R / f.h), 4, new byte[] { 0x3f, 0x8f, 0xd2 });
            Check(painted, "塗ると色が変わる");

            // GLB
            var C = new Color[P.Length];
            for (int i = 0; i < C.Length; i++) C[i] = Color.white;
            var glb = Glb.Write(P, N, C, T);
            Check(BitConverter.ToUInt32(glb, 0) == 0x46546C67 && BitConverter.ToUInt32(glb, 8) == glb.Length, $"GLB のヘッダ ({glb.Length / 1024} KB)");
            Directory.CreateDirectory("Builds");
            File.WriteAllBytes("Builds/test-clay.glb", glb);

            // Undo
            var snap = f.TakeSnapshot();
            f.ResetSphere(R);
            f.Restore(snap);
            Check(f.d[f.N * f.N * 40 + f.N * 40 + 40] == snap.d[f.N * f.N * 40 + f.N * 40 + 40], "スナップショットから戻せる");

            foreach (var m in meshes) UnityEngine.Object.DestroyImmediate(m);

            // 隠す場所: マップの模型をどう置いても、送る Transform はマップ座標 (右手系) で同じ
            var mv = new MapView(null);
            mv.SetPose(new Vector3(0.3f, 1.1f, 0.8f), Quaternion.Euler(0, 37, 0), 0.05f);
            var rt = mv.root;
            mv.SetSpot(new MapView.RayHit { point = rt.TransformPoint(new Vector3(1, 0, 2)), normal = rt.up }, rt.TransformDirection(new Vector3(0, -1, 1)));
            var st = mv.SpotTransform();
            Check(Near(st["position"], 1, 0, -2), $"隠す場所の位置はマップ座標 (z 反転) {MiniJson.Write(st["position"])}");
            Check(NearQ(st["rotation"], 0, 0, 0, 1), $"奥を向けて置くと回転なし {MiniJson.Write(st["rotation"])}");
            mv.SetPose(new Vector3(-2, 0, 5), Quaternion.Euler(10, -80, 5), 0.8f);
            var st2 = mv.SpotTransform();
            Check(Near(st2["position"], 1, 0, -2) && NearQ(st2["rotation"], 0, 0, 0, 1), "マップを動かしても隠す場所の Transform は変わらない");
            mv.SetSpot(new MapView.RayHit { point = rt.TransformPoint(new Vector3(-1, 0.5f, 0)), normal = rt.up }, rt.TransformDirection(new Vector3(1, -1, 0)));
            float h = Mathf.Sqrt(0.5f);
            Check(NearQ(mv.SpotTransform()["rotation"], 0, -h, 0, h), $"+x を向けると右手系で y 軸まわり -90° {MiniJson.Write(mv.SpotTransform()["rotation"])}");
            UnityEngine.Object.DestroyImmediate(mv.root.gameObject);

            Debug.Log($"[test] {(fails == 0 ? "ALL PASSED" : $"{fails} FAILED")}");
            if (Application.isBatchMode) EditorApplication.Exit(fails == 0 ? 0 : 1);
        }

        // クォータニオンは q と -q が同じ回転
        static bool NearQ(object list, float x, float y, float z, float w) => Near(list, x, y, z, w) || Near(list, -x, -y, -z, -w);

        static bool Near(object list, params float[] expect)
        {
            var l = (System.Collections.Generic.List<object>)list;
            if (l.Count != expect.Length) return false;
            for (int i = 0; i < l.Count; i++) if (Mathf.Abs(Convert.ToSingle(l[i]) - expect[i]) > 1e-4f) return false;
            return true;
        }

        static List<Mesh> MeshAll(ClayField f, out Vector3[] P, out Vector3[] N, out int[] T)
        {
            f.MarkAllDirty();
            var meshes = new List<Mesh>();
            var p = new List<Vector3>(); var n = new List<Vector3>(); var t = new List<int>();
            var tp = new List<Vector3>(); var tn = new List<Vector3>(); var ti = new List<int>();
            foreach (int key in f.TakeDirty())
            {
                var m = new Mesh();
                meshes.Add(m);
                if (!f.MeshChunk(key, m)) continue;
                m.GetVertices(tp); m.GetNormals(tn); m.GetTriangles(ti, 0);
                int b = p.Count;
                p.AddRange(tp); n.AddRange(tn);
                foreach (int i in ti) t.Add(b + i);
            }
            P = p.ToArray(); N = n.ToArray(); T = t.ToArray();
            return meshes;
        }
    }
}
