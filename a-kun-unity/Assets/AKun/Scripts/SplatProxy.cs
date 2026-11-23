using System;
using UnityEngine;

namespace AKun
{
    // Gaussian Splat の点から作る、レイを当てるための粗い占有格子。
    // Splat には Collider が無いので、隠す場所を指すレイはこの格子に当てる。
    //   - 点を一定の大きさの升に数え、点の多い升だけを「中身がある」とする (浮いたノイズの点は数えない)
    //   - レイは升を順にたどり (DDA)、最初に中身のある升で止める
    //   - 面の向きは、まわりの中身のある升から遠ざかる向き
    // 座標は Splat の GameObject のローカル。
    public sealed class SplatProxy
    {
        const int MaxCells = 96;   // 一番長い辺を何升に分けるか
        const int NormalRadius = 2; // 面の向きを見る範囲 (升)

        readonly Vector3 min;
        readonly float cell;
        readonly int nx, ny, nz;
        readonly bool[] solid;

        // 点の外れ値を除いた範囲 (Splat のローカル)
        public Bounds Bounds { get; }

        SplatProxy(Vector3 min, float cell, int nx, int ny, int nz, bool[] solid)
        {
            this.min = min; this.cell = cell; this.nx = nx; this.ny = ny; this.nz = nz; this.solid = solid;
            Bounds = new Bounds(min + new Vector3(nx, ny, nz) * (cell * 0.5f), new Vector3(nx, ny, nz) * cell);
        }

        // xyz: 点の座標を x, y, z の順に並べたもの
        public static SplatProxy Build(float[] xyz)
        {
            int n = xyz.Length / 3;
            if (n < 16) return null;

            // 外れ値を除いた範囲 (間引いた点の 0.5%〜99.5%)
            int stride = Math.Max(1, n / 60000);
            int m = (n + stride - 1) / stride;
            var sx = new float[m]; var sy = new float[m]; var sz = new float[m];
            int k = 0;
            for (int i = 0; i < n && k < m; i += stride, k++)
            {
                sx[k] = Finite(xyz[3 * i]); sy[k] = Finite(xyz[3 * i + 1]); sz[k] = Finite(xyz[3 * i + 2]);
            }
            var lo = new Vector3(Percentile(sx, k, 0.005f), Percentile(sy, k, 0.005f), Percentile(sz, k, 0.005f));
            var hi = new Vector3(Percentile(sx, k, 0.995f), Percentile(sy, k, 0.995f), Percentile(sz, k, 0.995f));
            var ext = hi - lo;
            float longest = Mathf.Max(ext.x, Mathf.Max(ext.y, ext.z));
            if (!(longest > 1e-4f)) return null;
            float cell = longest / MaxCells;
            int nx = Mathf.Max(1, Mathf.CeilToInt(ext.x / cell)), ny = Mathf.Max(1, Mathf.CeilToInt(ext.y / cell)), nz = Mathf.Max(1, Mathf.CeilToInt(ext.z / cell));

            var count = new int[nx * ny * nz];
            for (int i = 0; i < n; i++)
            {
                float x = xyz[3 * i], y = xyz[3 * i + 1], z = xyz[3 * i + 2];
                int ix = Mathf.FloorToInt((x - lo.x) / cell), iy = Mathf.FloorToInt((y - lo.y) / cell), iz = Mathf.FloorToInt((z - lo.z) / cell);
                if ((uint)ix >= (uint)nx || (uint)iy >= (uint)ny || (uint)iz >= (uint)nz) continue;
                count[(iz * ny + iy) * nx + ix]++;
            }

            // 点のある升の点の数の中央値の、3 割以上を「中身がある」とする (最低 3 点)
            int nonzero = 0;
            foreach (int c in count) if (c > 0) nonzero++;
            if (nonzero == 0) return null;
            var counts = new int[nonzero];
            int j = 0;
            foreach (int c in count) if (c > 0) counts[j++] = c;
            Array.Sort(counts);
            int threshold = Math.Max(3, Mathf.RoundToInt(counts[nonzero / 2] * 0.3f));
            var solid = new bool[count.Length];
            for (int i = 0; i < count.Length; i++) solid[i] = count[i] >= threshold;
            return new SplatProxy(lo, cell, nx, ny, nz, solid);
        }

        static float Finite(float v) => float.IsNaN(v) || float.IsInfinity(v) ? 0f : v;

        static float Percentile(float[] a, int count, float p)
        {
            Array.Sort(a, 0, count);
            return a[Mathf.Clamp(Mathf.RoundToInt((count - 1) * p), 0, count - 1)];
        }

        bool Solid(int x, int y, int z)
            => (uint)x < (uint)nx && (uint)y < (uint)ny && (uint)z < (uint)nz && solid[(z * ny + y) * nx + x];

        // origin + dir * t が最初に中身のある升に入る所。t は dir の長さを単位にする
        public bool Raycast(Vector3 origin, Vector3 dir, float maxT, out float t, out Vector3 normal)
        {
            t = 0; normal = Vector3.up;
            var max = min + new Vector3(nx, ny, nz) * cell;
            float t0 = 0, t1 = maxT;
            for (int a = 0; a < 3; a++)
            {
                float o = origin[a], d = dir[a];
                if (Mathf.Abs(d) < 1e-9f)
                {
                    if (o < min[a] || o > max[a]) return false;
                    continue;
                }
                float ta = (min[a] - o) / d, tb = (max[a] - o) / d;
                if (ta > tb) { float tmp = ta; ta = tb; tb = tmp; }
                t0 = Mathf.Max(t0, ta); t1 = Mathf.Min(t1, tb);
                if (t0 > t1) return false;
            }

            var p = origin + dir * (t0 + 1e-4f * cell / Mathf.Max(dir.magnitude, 1e-9f));
            int ix = Mathf.Clamp(Mathf.FloorToInt((p.x - min.x) / cell), 0, nx - 1);
            int iy = Mathf.Clamp(Mathf.FloorToInt((p.y - min.y) / cell), 0, ny - 1);
            int iz = Mathf.Clamp(Mathf.FloorToInt((p.z - min.z) / cell), 0, nz - 1);
            int sx = dir.x > 0 ? 1 : -1, sy = dir.y > 0 ? 1 : -1, sz = dir.z > 0 ? 1 : -1;
            float tdx = Mathf.Abs(dir.x) < 1e-9f ? float.PositiveInfinity : cell / Mathf.Abs(dir.x);
            float tdy = Mathf.Abs(dir.y) < 1e-9f ? float.PositiveInfinity : cell / Mathf.Abs(dir.y);
            float tdz = Mathf.Abs(dir.z) < 1e-9f ? float.PositiveInfinity : cell / Mathf.Abs(dir.z);
            float tmx = Mathf.Abs(dir.x) < 1e-9f ? float.PositiveInfinity : (min.x + (ix + (sx > 0 ? 1 : 0)) * cell - origin.x) / dir.x;
            float tmy = Mathf.Abs(dir.y) < 1e-9f ? float.PositiveInfinity : (min.y + (iy + (sy > 0 ? 1 : 0)) * cell - origin.y) / dir.y;
            float tmz = Mathf.Abs(dir.z) < 1e-9f ? float.PositiveInfinity : (min.z + (iz + (sz > 0 ? 1 : 0)) * cell - origin.z) / dir.z;

            float tCur = t0;
            int limit = nx + ny + nz + 4;
            for (int step = 0; step < limit; step++)
            {
                if (solid[(iz * ny + iy) * nx + ix])
                {
                    t = tCur;
                    normal = NormalAt(ix, iy, iz, dir);
                    return true;
                }
                if (tmx <= tmy && tmx <= tmz) { tCur = tmx; tmx += tdx; ix += sx; if ((uint)ix >= (uint)nx) return false; }
                else if (tmy <= tmz) { tCur = tmy; tmy += tdy; iy += sy; if ((uint)iy >= (uint)ny) return false; }
                else { tCur = tmz; tmz += tdz; iz += sz; if ((uint)iz >= (uint)nz) return false; }
                if (tCur > maxT) return false;
            }
            return false;
        }

        // まわりの中身のある升から遠ざかる向き。レイの来た側に向ける
        Vector3 NormalAt(int ix, int iy, int iz, Vector3 dir)
        {
            var n = Vector3.zero;
            for (int dz = -NormalRadius; dz <= NormalRadius; dz++)
                for (int dy = -NormalRadius; dy <= NormalRadius; dy++)
                    for (int dx = -NormalRadius; dx <= NormalRadius; dx++)
                    {
                        if (dx == 0 && dy == 0 && dz == 0) continue;
                        if (!Solid(ix + dx, iy + dy, iz + dz)) continue;
                        var o = new Vector3(dx, dy, dz);
                        n -= o / o.sqrMagnitude;
                    }
            if (n.sqrMagnitude < 1e-6f) return (-dir).normalized;
            n.Normalize();
            if (Vector3.Dot(n, dir) > 0) n = -n;
            return n;
        }
    }
}
