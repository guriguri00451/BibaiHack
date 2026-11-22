using System;
using System.Collections.Generic;
using UnityEngine;

namespace AKun
{
    // 粘土の形を「符号付き距離場 (SDF)」のボクセル格子で持つ (Web 版 a-kun/src/sdf.js の移植)。
    // 値は格子単位の距離。負 = 粘土の内側、正 = 外側。
    // 表面は Surface Nets でチャンク (16^3) ごとにメッシュ化し、編集したチャンクだけを作り直す。
    // 色は格子点ごとの RGB (sRGB, 0-255) で持ち、押す・引っ張るときは形と一緒に動かす。
    //
    // 座標: 格子 (x, y, z) → 粘土のローカル座標 (-half + x*h, -half + y*h, -half + z*h)。Unity のローカル軸と同じ向き
    public class ClayField
    {
        // 立方体セルの 12 本の辺 (角の番号のペア)。角の番号 = bx + 2*by + 4*bz
        static readonly int[] EA, EB;
        static readonly float[] SRGB_TO_LINEAR = new float[256];

        static ClayField()
        {
            var ea = new List<int>(); var eb = new List<int>();
            for (int i = 0; i < 8; i++)
                foreach (int bit in new[] { 1, 2, 4 })
                    if ((i & bit) == 0) { ea.Add(i); eb.Add(i | bit); }
            EA = ea.ToArray(); EB = eb.ToArray();
            for (int i = 0; i < 256; i++)
            {
                float c = i / 255f;
                SRGB_TO_LINEAR[i] = c <= 0.04045f ? c / 12.92f : Mathf.Pow((c + 0.055f) / 1.055f, 2.4f);
            }
        }

        public readonly int N;
        public readonly float size;   // 格子が覆う立方体の一辺 (ローカル座標, m)
        public readonly float h;      // 格子の間隔
        public readonly float half;
        public readonly int S;        // チャンクの一辺 (格子)
        public readonly int K;        // 1 辺のチャンクの数

        public float[] d;
        float[] tmp;
        public byte[] col;            // 格子点の色 (sRGB)
        byte[] tmpCol;
        public int colVersion;        // 色が変わるたびに増やす (Undo で色を共有するため)
        public byte[] baseColor = { 0xc9, 0x80, 0x60 };

        readonly bool[] dirtyFlag;
        readonly List<int> dirtyList = new List<int>();

        public float conserveBand = 6f;  // 押しのけた分を盛り上げる帯の幅 (格子)
        public float conserveMax = 1f;   // 1 回で盛り上げる上限 (格子)
        public float relax = 0.15f;      // 手が触れた所をなじませる強さ (0〜1)

        // メッシュ化の作業用
        readonly int L;
        readonly int[] map;
        readonly Vector3[] vPos, vNor;
        readonly Color[] vCol;
        readonly int[] idx;
        readonly float[] c8 = new float[8];
        readonly float[] wts = new float[8];
        readonly int[] ids = new int[8];

        // displace の作業用
        float[] ux, uy, uz, uw;
        int[] touch, shell;
        byte[] sh;

        public ClayField(int N = 80, float size = 0.4f, int chunk = 16)
        {
            this.N = N; this.size = size;
            h = size / (N - 1);
            half = size / 2;
            S = chunk;
            K = Mathf.CeilToInt(N / (float)chunk);
            int total = N * N * N;
            d = new float[total];
            tmp = new float[total];
            col = new byte[total * 3];
            tmpCol = new byte[total * 3];
            dirtyFlag = new bool[K * K * K];
            L = chunk + 1;
            map = new int[L * L * L];
            vPos = new Vector3[L * L * L];
            vNor = new Vector3[L * L * L];
            vCol = new Color[L * L * L];
            idx = new int[chunk * chunk * chunk * 3 * 6];
        }

        // ---------- 初期形状 ----------

        // 球 (半径 r m、格子の中心)
        public void ResetSphere(float r, byte[] color = null)
        {
            FillColor(color ?? baseColor);
            for (int z = 0; z < N; z++)
                for (int y = 0; y < N; y++)
                    for (int x = 0; x < N; x++)
                    {
                        float px = -half + x * h, py = -half + y * h, pz = -half + z * h;
                        float dist = (Mathf.Sqrt(px * px + py * py + pz * pz) - r) / h;
                        bool border = x == 0 || y == 0 || z == 0 || x == N - 1 || y == N - 1 || z == N - 1;
                        d[x + N * (y + N * z)] = border ? Mathf.Max(dist, 1) : dist;
                    }
            MarkAllDirty();
        }

        public void FillColor(byte[] rgb)
        {
            for (int i = 0; i < col.Length; i += 3) { col[i] = rgb[0]; col[i + 1] = rgb[1]; col[i + 2] = rgb[2]; }
            colVersion++;
            MarkAllDirty();
        }

        // ---------- 読み出し ----------

        float Tri(float[] arr, float x, float y, float z)
        {
            float m = N - 1.0001f;
            x = x < 0 ? 0 : x > m ? m : x;
            y = y < 0 ? 0 : y > m ? m : y;
            z = z < 0 ? 0 : z > m ? m : z;
            int ix = (int)x, iy = (int)y, iz = (int)z;
            float fx = x - ix, fy = y - iy, fz = z - iz;
            int NN = N * N;
            int i = ix + N * iy + NN * iz;
            float c00 = arr[i] * (1 - fx) + arr[i + 1] * fx;
            float c10 = arr[i + N] * (1 - fx) + arr[i + N + 1] * fx;
            float c01 = arr[i + NN] * (1 - fx) + arr[i + NN + 1] * fx;
            float c11 = arr[i + NN + N] * (1 - fx) + arr[i + NN + N + 1] * fx;
            float c0 = c00 * (1 - fy) + c10 * fy;
            float c1 = c01 * (1 - fy) + c11 * fy;
            return c0 * (1 - fz) + c1 * fz;
        }

        // 格子座標 g での距離 (格子単位)。格子の外なら +∞
        public float Sample(Vector3 g)
        {
            int m = N - 1;
            if (g.x < 0 || g.y < 0 || g.z < 0 || g.x > m || g.y > m || g.z > m) return float.PositiveInfinity;
            return Tri(d, g.x, g.y, g.z);
        }

        // 距離場の勾配 (外向き、正規化済み)
        public Vector3 Gradient(Vector3 g)
        {
            const float e = 0.5f;
            var o = new Vector3(
                Tri(d, g.x + e, g.y, g.z) - Tri(d, g.x - e, g.y, g.z),
                Tri(d, g.x, g.y + e, g.z) - Tri(d, g.x, g.y - e, g.z),
                Tri(d, g.x, g.y, g.z + e) - Tri(d, g.x, g.y, g.z - e));
            float l = o.magnitude;
            return l > 0 ? o / l : o;
        }

        // ---------- 編集 ----------

        void Box(Vector3 c, float r, out int x0, out int x1, out int y0, out int y1, out int z0, out int z1)
        {
            x0 = Math.Max(1, Mathf.FloorToInt(c.x - r)); x1 = Math.Min(N - 2, Mathf.CeilToInt(c.x + r));
            y0 = Math.Max(1, Mathf.FloorToInt(c.y - r)); y1 = Math.Min(N - 2, Mathf.CeilToInt(c.y + r));
            z0 = Math.Max(1, Mathf.FloorToInt(c.z - r)); z1 = Math.Min(N - 2, Mathf.CeilToInt(c.z + r));
        }

        void CopyBox(float xa, float xb, float ya, float yb, float za, float zb, bool withCol = false)
        {
            int ixa = Math.Max(0, Mathf.FloorToInt(xa)), ixb = Math.Min(N - 1, Mathf.CeilToInt(xb));
            int iya = Math.Max(0, Mathf.FloorToInt(ya)), iyb = Math.Min(N - 1, Mathf.CeilToInt(yb));
            int iza = Math.Max(0, Mathf.FloorToInt(za)), izb = Math.Min(N - 1, Mathf.CeilToInt(zb));
            if (ixa > ixb) return;
            int len = ixb - ixa + 1;
            for (int z = iza; z <= izb; z++)
                for (int y = iya; y <= iyb; y++)
                {
                    int row = N * (y + N * z);
                    Array.Copy(d, row + ixa, tmp, row + ixa, len);
                    if (withCol) Array.Copy(col, (row + ixa) * 3, tmpCol, (row + ixa) * 3, len * 3);
                }
        }

        void CopyToTmp(Vector3 c, float r, bool withCol = false)
            => CopyBox(c.x - r, c.x + r, c.y - r, c.y + r, c.z - r, c.z + r, withCol);

        // tmpCol の最寄りの格子点の色を i に写す
        void CopyColNearest(int i, float x, float y, float z)
        {
            int m = N - 1;
            int ix = x < 0 ? 0 : x > m ? m : Mathf.RoundToInt(x);
            int iy = y < 0 ? 0 : y > m ? m : Mathf.RoundToInt(y);
            int iz = z < 0 ? 0 : z > m ? m : Mathf.RoundToInt(z);
            int j = (ix + N * (iy + N * iz)) * 3, k = i * 3;
            col[k] = tmpCol[j]; col[k + 1] = tmpCol[j + 1]; col[k + 2] = tmpCol[j + 2];
        }

        // 塗る: 球の中の色を rgb に寄せる (中心ほど強く)。形は変えない
        public bool Paint(Vector3 c, float r, byte[] rgb, float strength = 0.5f)
        {
            Box(c, r, out int x0, out int x1, out int y0, out int y1, out int z0, out int z1);
            bool any = false;
            for (int z = z0; z <= z1; z++)
            {
                float dz = z - c.z;
                for (int y = y0; y <= y1; y++)
                {
                    float dy = y - c.y;
                    int i = x0 + N * (y + N * z);
                    for (int x = x0; x <= x1; x++, i++)
                    {
                        float dx = x - c.x;
                        float dist = Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
                        if (dist >= r) continue;
                        float t = dist / r;
                        float w = Mathf.Min(1, (1 - t * t) * strength * 3); // 中ほどはしっかり、縁はぼかす
                        int k = i * 3;
                        for (int ch = 0; ch < 3; ch++)
                        {
                            int v = col[k + ch];
                            int nv = (int)Math.Round(v + (rgb[ch] - v) * w, MidpointRounding.AwayFromZero);
                            if (nv != v) { col[k + ch] = (byte)nv; any = true; }
                        }
                    }
                }
            }
            if (any)
            {
                colVersion++;
                MarkDirtyPoints(x0, x1, y0, y1, z0, z1);
            }
            return any;
        }

        // なでる (ラプラシアン平滑化)
        public bool Smooth(Vector3 c, float r, float strength = 0.5f)
        {
            int NN = N * N;
            CopyToTmp(c, r + 2);
            Box(c, r, out int x0, out int x1, out int y0, out int y1, out int z0, out int z1);
            bool any = false;
            for (int z = z0; z <= z1; z++)
            {
                float dz = z - c.z;
                for (int y = y0; y <= y1; y++)
                {
                    float dy = y - c.y;
                    int i = x0 + N * (y + N * z);
                    for (int x = x0; x <= x1; x++, i++)
                    {
                        float dx = x - c.x;
                        float dist = Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
                        if (dist >= r) continue;
                        float t = dist / r;
                        float w = Mathf.Min(1, (1 - t * t) * (1 - t * t) * strength);
                        float avg = (tmp[i - 1] + tmp[i + 1] + tmp[i - N] + tmp[i + N] + tmp[i - NN] + tmp[i + NN]) / 6;
                        float nv = tmp[i] + (avg - tmp[i]) * w;
                        if (Mathf.Abs(nv - d[i]) > 1e-4f) { d[i] = nv; any = true; }
                    }
                }
            }
            if (any) MarkDirtyPoints(x0, x1, y0, y1, z0, z1);
            return any;
        }

        // つまんで引っ張る: c の周りの粘土を delta だけ減衰付きで動かす (スネークフック)
        public bool Pull(Vector3 c, Vector3 delta, float R)
        {
            float len = delta.magnitude;
            if (len < 1e-3f) return false;
            int steps = Math.Max(1, Mathf.CeilToInt(len / (R * 0.35f)));
            Vector3 step = delta / steps;
            Vector3 cur = c;
            // 引き出すと粘土が増えるので、動かす前の体積を測っておき、あとで戻す
            float m = R + 3;
            var box = new[] {
                Mathf.Min(c.x, c.x + delta.x) - m, Mathf.Max(c.x, c.x + delta.x) + m,
                Mathf.Min(c.y, c.y + delta.y) - m, Mathf.Max(c.y, c.y + delta.y) + m,
                Mathf.Min(c.z, c.z + delta.z) - m, Mathf.Max(c.z, c.z + delta.z) + m,
            };
            float before = BoxVolume(box);
            bool any = false;
            for (int s = 0; s < steps; s++)
            {
                any = PullStep(cur, step, R) || any;
                cur += step;
            }
            if (any) RestoreBoxVolume(box, before);
            return any;
        }

        void ClampBox(float[] box, out int x0, out int x1, out int y0, out int y1, out int z0, out int z1)
        {
            x0 = Math.Max(1, Mathf.FloorToInt(box[0])); x1 = Math.Min(N - 2, Mathf.CeilToInt(box[1]));
            y0 = Math.Max(1, Mathf.FloorToInt(box[2])); y1 = Math.Min(N - 2, Mathf.CeilToInt(box[3]));
            z0 = Math.Max(1, Mathf.FloorToInt(box[4])); z1 = Math.Min(N - 2, Mathf.CeilToInt(box[5]));
        }

        static float Occ(float t) => t <= -0.5f ? 1 : t >= 0.5f ? 0 : 0.5f - t;

        // 範囲 box = [x0, x1, y0, y1, z0, z1] (格子) の体積 (占有する格子の数。表面をまたぐ格子は割合)
        float BoxVolume(float[] box)
        {
            int NN = N * N;
            ClampBox(box, out int x0, out int x1, out int y0, out int y1, out int z0, out int z1);
            float v = 0;
            for (int z = z0; z <= z1; z++)
                for (int y = y0; y <= y1; y++)
                {
                    int i = x0 + N * y + NN * z;
                    for (int x = x0; x <= x1; x++, i++) v += Occ(d[i]);
                }
            return v;
        }

        // box の中の体積が target に戻るように、距離場をなめらかに少しずらして、表面を出し入れする。
        // 範囲の縁では効き目を 0 に近づけて、まわりと段差ができないようにする
        void RestoreBoxVolume(float[] box, float target)
        {
            int NN = N * N;
            ClampBox(box, out int x0, out int x1, out int y0, out int y1, out int z0, out int z1);
            float Ramp(int i, int a, int b) => Mathf.Min(1, Mathf.Min(i - a + 1, b - i + 1) / 4f); // 縁から 4 マスで 0 → 1
            float remaining = target - BoxVolume(box);
            bool changed = false;
            for (int iter = 0; iter < 10 && Mathf.Abs(remaining) >= 0.3f; iter++)
            {
                // 表面 (距離が -0.5〜0.5) の面積の見積もり: ずらした量 1 あたりの体積の変化
                float A = 0;
                for (int z = z0; z <= z1; z++)
                {
                    float wz = Ramp(z, z0, z1);
                    for (int y = y0; y <= y1; y++)
                    {
                        float wyz = wz * Ramp(y, y0, y1);
                        int i = x0 + N * y + NN * z;
                        for (int x = x0; x <= x1; x++, i++) { float v = d[i]; if (v > -0.5f && v < 0.5f) A += wyz * Ramp(x, x0, x1); }
                    }
                }
                if (A < 1) break;
                float delta = Mathf.Clamp(-remaining / A, -2, 2); // 体積を減らす (remaining < 0) ときは、距離を増やす
                for (int z = z0; z <= z1; z++)
                {
                    float wz = Ramp(z, z0, z1);
                    for (int y = y0; y <= y1; y++)
                    {
                        float wyz = wz * Ramp(y, y0, y1);
                        int i = x0 + N * y + NN * z;
                        for (int x = x0; x <= x1; x++, i++) d[i] += delta * wyz * Ramp(x, x0, x1);
                    }
                }
                changed = true;
                float now = BoxVolume(box);
                float prev = remaining;
                remaining = target - now;
                if (Mathf.Abs(remaining) > Mathf.Abs(prev) * 0.95f) break; // 効かなくなったら止める
            }
            if (changed) MarkDirtyPoints(x0, x1, y0, y1, z0, z1);
        }

        bool PullStep(Vector3 c, Vector3 v, float R)
        {
            Vector3 e = c + v;
            float vlen = v.magnitude;
            CopyToTmp(e, R + vlen + 2, true);
            Box(e, R, out int x0, out int x1, out int y0, out int y1, out int z0, out int z1);
            bool any = false;
            for (int z = z0; z <= z1; z++)
            {
                float dz = z - e.z;
                for (int y = y0; y <= y1; y++)
                {
                    float dy = y - e.y;
                    int i = x0 + N * (y + N * z);
                    for (int x = x0; x <= x1; x++, i++)
                    {
                        float dx = x - e.x;
                        float dist = Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
                        if (dist >= R) continue;
                        // 中心付近はまるごと付いてくる (細い糸にならないように)
                        float u = Mathf.Max(0, (dist / R - 0.35f) / 0.65f);
                        float w = (1 - u * u) * (1 - u * u);
                        float sx = x - w * v.x, sy = y - w * v.y, sz = z - w * v.z;
                        float nv = Tri(tmp, sx, sy, sz);
                        if (Mathf.Abs(nv - d[i]) > 1e-5f) { d[i] = nv; any = true; CopyColNearest(i, sx, sy, sz); }
                    }
                }
            }
            if (any) { colVersion++; MarkDirtyPoints(x0, x1, y0, y1, z0, z1); }
            return any;
        }

        // 手で押しのける (粘性のある変形) で使うカプセル (格子座標) と、このフレームの移動量 u
        public struct Capsule
        {
            public Vector3 a, b, u;
            public float r;
        }

        // カプセルの中と、その周り F までの粘土を u だけ動かす (中ほど強く、外側ほど弱く)
        // conserve: 押しのけて減った分を、手の縁のまわりの表面に盛り上げて戻す (粘土らしい体積の保存)
        public bool Displace(List<Capsule> caps, float F, bool conserve = true)
        {
            if (caps.Count == 0) return false;
            int NN = N * N;
            if (uw == null)
            {
                int total = N * N * N;
                ux = new float[total]; uy = new float[total]; uz = new float[total]; uw = new float[total];
                touch = new int[total]; sh = new byte[total]; shell = new int[total];
            }
            float B = conserve ? conserveBand : 0; // 盛り上げる帯の幅 (格子)
            int nt = 0, ns = 0;
            float maxU = 0;
            int gx0 = int.MaxValue, gx1 = int.MinValue, gy0 = int.MaxValue, gy1 = int.MinValue, gz0 = int.MaxValue, gz1 = int.MinValue;

            foreach (var c in caps)
            {
                float R = c.r + F + B;
                int x0 = Math.Max(1, Mathf.FloorToInt(Mathf.Min(c.a.x, c.b.x) - R)), x1 = Math.Min(N - 2, Mathf.CeilToInt(Mathf.Max(c.a.x, c.b.x) + R));
                int y0 = Math.Max(1, Mathf.FloorToInt(Mathf.Min(c.a.y, c.b.y) - R)), y1 = Math.Min(N - 2, Mathf.CeilToInt(Mathf.Max(c.a.y, c.b.y) + R));
                int z0 = Math.Max(1, Mathf.FloorToInt(Mathf.Min(c.a.z, c.b.z) - R)), z1 = Math.Min(N - 2, Mathf.CeilToInt(Mathf.Max(c.a.z, c.b.z) + R));
                if (x0 > x1 || y0 > y1 || z0 > z1) continue;
                gx0 = Math.Min(gx0, x0); gx1 = Math.Max(gx1, x1);
                gy0 = Math.Min(gy0, y0); gy1 = Math.Max(gy1, y1);
                gz0 = Math.Min(gz0, z0); gz1 = Math.Max(gz1, z1);
                maxU = Mathf.Max(maxU, c.u.magnitude);
                float ex = c.b.x - c.a.x, ey = c.b.y - c.a.y, ez = c.b.z - c.a.z;
                float ee = ex * ex + ey * ey + ez * ez; if (ee == 0) ee = 1e-9f;
                float coreR2 = (c.r + F) * (c.r + F), shellR2 = (c.r + F + B) * (c.r + F + B);
                for (int z = z0; z <= z1; z++)
                    for (int y = y0; y <= y1; y++)
                    {
                        int i = x0 + N * y + NN * z;
                        for (int x = x0; x <= x1; x++, i++)
                        {
                            float px = x - c.a.x, py = y - c.a.y, pz = z - c.a.z;
                            float t = (px * ex + py * ey + pz * ez) / ee;
                            t = t < 0 ? 0 : t > 1 ? 1 : t;
                            float qx = px - t * ex, qy = py - t * ey, qz = pz - t * ez;
                            float q2 = qx * qx + qy * qy + qz * qz;
                            if (q2 >= coreR2)
                            {
                                // 帯: 表面の近くだけ見ればよい
                                if (q2 < shellR2 && d[i] > -1 && d[i] < 2 && sh[i] == 0) { sh[i] = 1; shell[ns++] = i; }
                                continue;
                            }
                            float sd = Mathf.Sqrt(q2) - c.r;
                            float w = 1;
                            if (sd > 0) { float s = sd / F; w = (1 - s * s) * (1 - s * s); }
                            if (uw[i] == 0) touch[nt++] = i;
                            if (w > uw[i]) { uw[i] = w; ux[i] = c.u.x; uy[i] = c.u.y; uz[i] = c.u.z; }
                        }
                    }
            }
            if (nt == 0)
            {
                for (int k = 0; k < ns; k++) sh[shell[k]] = 0;
                return false;
            }
            // 別のカプセルの内側に入っている点は帯から外す
            int nk = 0;
            for (int k = 0; k < ns; k++)
            {
                int i = shell[k];
                sh[i] = 0;
                if (uw[i] == 0) shell[nk++] = i;
            }

            // 動かす前の値を tmp に写してから、そこを引いて読む
            float mm = maxU + 2;
            CopyBox(gx0 - mm, gx1 + mm, gy0 - mm, gy1 + mm, gz0 - mm, gz1 + mm, true);
            int bx0 = int.MaxValue, bx1 = -1, by0 = int.MaxValue, by1 = -1, bz0 = int.MaxValue, bz1 = -1;
            float lost = 0;
            for (int k = 0; k < nt; k++)
            {
                int i = touch[k];
                float w = uw[i];
                uw[i] = 0;
                int x = i % N, y = (i / N) % N, z = i / NN;
                float sx = x - w * ux[i], sy = y - w * uy[i], sz = z - w * uz[i];
                float v = Tri(tmp, sx, sy, sz);
                if (Mathf.Abs(v - d[i]) > 1e-5f)
                {
                    lost += Occ(d[i]) - Occ(v);
                    d[i] = v;
                    CopyColNearest(i, sx, sy, sz);
                    if (x < bx0) bx0 = x; if (x > bx1) bx1 = x;
                    if (y < by0) by0 = y; if (y > by1) by1 = y;
                    if (z < bz0) bz0 = z; if (z > bz1) bz1 = z;
                }
            }

            // 減った体積を、手の縁の表面を少し外へ押し出して戻す。
            // 一度に大きく押し出すと戻る量が頭打ちになるので、実際に戻った量を測りながら数回に分ける
            float remaining = lost;
            for (int iter = 0; iter < 4 && remaining > 0.5f && nk > 0; iter++)
            {
                float A = 0;
                for (int k = 0; k < nk; k++)
                {
                    float v = d[shell[k]];
                    if (v > -0.5f && v < 1.5f) A += 1 - Mathf.Abs(v - 0.5f) / 1.5f;
                }
                if (A <= 0) break;
                float delta = Mathf.Min(remaining / A, conserveMax);
                float gained = 0;
                for (int k = 0; k < nk; k++)
                {
                    int i = shell[k];
                    float v = d[i];
                    float a = 1 - Mathf.Abs(v - 0.5f) / 1.5f;
                    if (a <= 0) continue;
                    float nv = v - delta * a;
                    gained += Occ(nv) - Occ(v);
                    d[i] = nv;
                    int x = i % N, y = (i / N) % N, z = i / NN;
                    if (x < bx0) bx0 = x; if (x > bx1) bx1 = x;
                    if (y < by0) by0 = y; if (y > by1) by1 = y;
                    if (z < bz0) bz0 = z; if (z > bz1) bz1 = z;
                }
                remaining -= gained;
                if (gained < 0.1f) break;
            }
            if (bx1 < 0) return false;
            colVersion++;

            // なじませる: 手が触れた所と縁を、まわりの平均へ少し寄せる (筋やギザギザを消し、柔らかく見せる)
            if (relax > 0)
            {
                CopyBox(bx0 - 1, bx1 + 1, by0 - 1, by1 + 1, bz0 - 1, bz1 + 1);
                float r = relax;
                for (int pass = 0; pass < 2; pass++)
                {
                    int[] list = pass == 0 ? touch : shell;
                    int n = pass == 0 ? nt : nk;
                    for (int k = 0; k < n; k++)
                    {
                        int i = list[k];
                        float v = tmp[i];
                        if (v > 3 || v < -3) continue; // 表面から遠い所は触らない
                        int x = i % N, y = (i / N) % N, z = i / NN;
                        if (x < bx0 || x > bx1 || y < by0 || y > by1 || z < bz0 || z > bz1) continue;
                        float avg = (tmp[i - 1] + tmp[i + 1] + tmp[i - N] + tmp[i + N] + tmp[i - NN] + tmp[i + NN]) / 6;
                        d[i] = v + (avg - v) * r;
                    }
                }
            }
            MarkDirtyPoints(bx0, bx1, by0, by1, bz0, bz1);
            return true;
        }

        // ---------- Undo ----------
        // 色は変わっていなければ前のスナップショットと同じ配列を共有する (メモリ節約。スナップショットは書き換えない)

        public class Snapshot { public float[] d; public byte[] col; }
        byte[] colSnap;
        int colSnapVersion = -1;

        public Snapshot TakeSnapshot()
        {
            if (colSnap == null || colSnapVersion != colVersion)
            {
                colSnap = (byte[])col.Clone();
                colSnapVersion = colVersion;
            }
            return new Snapshot { d = (float[])d.Clone(), col = colSnap };
        }

        public void Restore(Snapshot s)
        {
            Array.Copy(s.d, d, d.Length);
            Array.Copy(s.col, col, col.Length);
            colVersion++;
            MarkAllDirty();
        }

        // ---------- チャンク管理 ----------

        public int ChunkCount => K * K * K;

        void MarkDirty(int key)
        {
            if (dirtyFlag[key]) return;
            dirtyFlag[key] = true;
            dirtyList.Add(key);
        }

        public void MarkAllDirty()
        {
            for (int k = 0; k < K * K * K; k++) MarkDirty(k);
        }

        // 格子点 [xa..xb] などが変わったとき、その点を読むチャンクを全部ダーティにする
        public void MarkDirtyPoints(int xa, int xb, int ya, int yb, int za, int zb)
        {
            int kx0 = Math.Max(0, FloorDiv(xa - 1, S)), kx1 = Math.Min(K - 1, FloorDiv(xb + 1, S));
            int ky0 = Math.Max(0, FloorDiv(ya - 1, S)), ky1 = Math.Min(K - 1, FloorDiv(yb + 1, S));
            int kz0 = Math.Max(0, FloorDiv(za - 1, S)), kz1 = Math.Min(K - 1, FloorDiv(zb + 1, S));
            for (int kz = kz0; kz <= kz1; kz++)
                for (int ky = ky0; ky <= ky1; ky++)
                    for (int kx = kx0; kx <= kx1; kx++) MarkDirty(kx + K * (ky + K * kz));
        }

        static int FloorDiv(int a, int b) => (int)Math.Floor(a / (double)b);

        public List<int> TakeDirty()
        {
            var list = new List<int>(dirtyList);
            foreach (int k in dirtyList) dirtyFlag[k] = false;
            dirtyList.Clear();
            return list;
        }

        // ---------- Surface Nets ----------
        // チャンクは格子点 [x0, x0+S) を「持つ」。持っている点から出る辺の面を出す。
        // 面の頂点はセル [x0-1, x0+S) にあるので、そこまで頂点を計算する。
        // 結果は mesh に書き込む (頂点がなければ空にして false)
        public bool MeshChunk(int key, Mesh mesh)
        {
            int NN = N * N;
            int kx = key % K, ky = (key / K) % K, kz = key / (K * K);
            int x0 = kx * S, y0 = ky * S, z0 = kz * S;
            int ox = x0 - 1, oy = y0 - 1, oz = z0 - 1;
            for (int i = 0; i < map.Length; i++) map[i] = -1;

            // 1) セルごとの頂点
            int nv = 0;
            int cxa = Math.Max(ox, 0), cxb = Math.Min(x0 + S - 1, N - 2);
            int cya = Math.Max(oy, 0), cyb = Math.Min(y0 + S - 1, N - 2);
            int cza = Math.Max(oz, 0), czb = Math.Min(z0 + S - 1, N - 2);
            var c = c8;
            for (int z = cza; z <= czb; z++)
                for (int y = cya; y <= cyb; y++)
                    for (int x = cxa; x <= cxb; x++)
                    {
                        int i0 = x + N * y + NN * z;
                        c[0] = d[i0]; c[1] = d[i0 + 1];
                        c[2] = d[i0 + N]; c[3] = d[i0 + N + 1];
                        c[4] = d[i0 + NN]; c[5] = d[i0 + NN + 1];
                        c[6] = d[i0 + NN + N]; c[7] = d[i0 + NN + N + 1];
                        int mask = 0;
                        for (int k = 0; k < 8; k++) if (c[k] < 0) mask |= 1 << k;
                        if (mask == 0 || mask == 255) continue;

                        float sx = 0, sy = 0, sz = 0; int cnt = 0;
                        for (int e = 0; e < 12; e++)
                        {
                            int a = EA[e], b = EB[e];
                            float va = c[a], vb = c[b];
                            if ((va < 0) != (vb < 0))
                            {
                                float t = va / (va - vb);
                                int ax = a & 1, ay = (a >> 1) & 1, az = (a >> 2) & 1;
                                sx += ax + t * ((b & 1) - ax);
                                sy += ay + t * (((b >> 1) & 1) - ay);
                                sz += az + t * (((b >> 2) & 1) - az);
                                cnt++;
                            }
                        }
                        float fx = sx / cnt, fy = sy / cnt, fz = sz / cnt;

                        // 法線 = 三線形補間した距離場の勾配
                        float gx = (1 - fy) * (1 - fz) * (c[1] - c[0]) + fy * (1 - fz) * (c[3] - c[2]) + (1 - fy) * fz * (c[5] - c[4]) + fy * fz * (c[7] - c[6]);
                        float gy = (1 - fx) * (1 - fz) * (c[2] - c[0]) + fx * (1 - fz) * (c[3] - c[1]) + (1 - fx) * fz * (c[6] - c[4]) + fx * fz * (c[7] - c[5]);
                        float gz = (1 - fx) * (1 - fy) * (c[4] - c[0]) + fx * (1 - fy) * (c[5] - c[1]) + (1 - fx) * fy * (c[6] - c[2]) + fx * fy * (c[7] - c[3]);
                        float gl = Mathf.Sqrt(gx * gx + gy * gy + gz * gz); if (gl == 0) gl = 1;

                        vPos[nv] = new Vector3(-half + (x + fx) * h, -half + (y + fy) * h, -half + (z + fz) * h);
                        vNor[nv] = new Vector3(gx / gl, gy / gl, gz / gl);
                        // 色 = 8 隅の色の三線形補間 (リニア)
                        float w0x = 1 - fx, w0y = 1 - fy, w0z = 1 - fz;
                        wts[0] = w0x * w0y * w0z; wts[1] = fx * w0y * w0z; wts[2] = w0x * fy * w0z; wts[3] = fx * fy * w0z;
                        wts[4] = w0x * w0y * fz; wts[5] = fx * w0y * fz; wts[6] = w0x * fy * fz; wts[7] = fx * fy * fz;
                        ids[0] = i0; ids[1] = i0 + 1; ids[2] = i0 + N; ids[3] = i0 + N + 1;
                        ids[4] = i0 + NN; ids[5] = i0 + NN + 1; ids[6] = i0 + NN + N; ids[7] = i0 + NN + N + 1;
                        float cr = 0, cg = 0, cb = 0;
                        for (int k = 0; k < 8; k++)
                        {
                            int j = ids[k] * 3; float wk = wts[k];
                            cr += SRGB_TO_LINEAR[col[j]] * wk; cg += SRGB_TO_LINEAR[col[j + 1]] * wk; cb += SRGB_TO_LINEAR[col[j + 2]] * wk;
                        }
                        vCol[nv] = new Color(cr, cg, cb, 1);
                        map[(x - ox) + L * ((y - oy) + L * (z - oz))] = nv++;
                    }

            // 2) 符号が変わる辺ごとに四角形
            int ni = 0;
            int pxb = Math.Min(x0 + S, N), pyb = Math.Min(y0 + S, N), pzb = Math.Min(z0 + S, N);
            for (int z = z0; z < pzb; z++)
                for (int y = y0; y < pyb; y++)
                    for (int x = x0; x < pxb; x++)
                    {
                        int i = x + N * y + NN * z;
                        bool in0 = d[i] < 0;
                        bool yzOK = y >= 1 && z >= 1 && y <= N - 2 && z <= N - 2;
                        bool xzOK = x >= 1 && z >= 1 && x <= N - 2 && z <= N - 2;
                        bool xyOK = x >= 1 && y >= 1 && x <= N - 2 && y <= N - 2;
                        if (x < N - 1 && yzOK && in0 != (d[i + 1] < 0))
                            Emit(ref ni, Cell(x, y - 1, z - 1, ox, oy, oz), Cell(x, y, z - 1, ox, oy, oz), Cell(x, y, z, ox, oy, oz), Cell(x, y - 1, z, ox, oy, oz), in0);
                        if (y < N - 1 && xzOK && in0 != (d[i + N] < 0))
                            Emit(ref ni, Cell(x - 1, y, z - 1, ox, oy, oz), Cell(x - 1, y, z, ox, oy, oz), Cell(x, y, z, ox, oy, oz), Cell(x, y, z - 1, ox, oy, oz), in0);
                        if (z < N - 1 && xyOK && in0 != (d[i + NN] < 0))
                            Emit(ref ni, Cell(x - 1, y - 1, z, ox, oy, oz), Cell(x, y - 1, z, ox, oy, oz), Cell(x, y, z, ox, oy, oz), Cell(x - 1, y, z, ox, oy, oz), in0);
                    }

            mesh.Clear();
            if (ni == 0) return false;
            mesh.SetVertices(vPos, 0, nv);
            mesh.SetNormals(vNor, 0, nv);
            mesh.SetColors(vCol, 0, nv);
            mesh.SetIndices(idx, 0, ni, MeshTopology.Triangles, 0, true);
            return true;
        }

        int Cell(int x, int y, int z, int ox, int oy, int oz) => map[(x - ox) + L * ((y - oy) + L * (z - oz))];

        // Web 版と同じ並び (外から見て、数式どおりの外積が外を向く向き)
        void Emit(ref int ni, int a, int b, int cc, int dd, bool inside)
        {
            if (a < 0 || b < 0 || cc < 0 || dd < 0) return;
            if (inside)
            {
                idx[ni++] = a; idx[ni++] = b; idx[ni++] = cc;
                idx[ni++] = a; idx[ni++] = cc; idx[ni++] = dd;
            }
            else
            {
                idx[ni++] = a; idx[ni++] = cc; idx[ni++] = b;
                idx[ni++] = a; idx[ni++] = dd; idx[ni++] = cc;
            }
        }
    }
}
