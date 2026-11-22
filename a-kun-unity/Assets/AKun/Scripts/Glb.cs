using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace AKun
{
    // 頂点カラー付きのメッシュ 1 つを GLB (glTF 2.0 バイナリ) にする。
    // Web 版 (three.js の GLTFExporter) と同じ形: POSITION / NORMAL / COLOR_0 (リニア, float3) / indices、
    // 材質は pbrMetallicRoughness (白, metallic 0, roughness 0.78)。
    // 座標: Unity (左手系、+Z 奥) → glTF (右手系) は z を反転し、三角形の向きを逆にする
    public static class Glb
    {
        public static byte[] Write(Vector3[] pos, Vector3[] nor, Color[] col, int[] tri, string name = "clay")
        {
            int nv = pos.Length, ni = tri.Length;
            var bin = new MemoryStream();
            var w = new BinaryWriter(bin);
            Vector3 mn = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue), mx = -mn;
            for (int i = 0; i < nv; i++)
            {
                var p = new Vector3(pos[i].x, pos[i].y, -pos[i].z);
                mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p);
                w.Write(p.x); w.Write(p.y); w.Write(p.z);
            }
            int norOff = (int)bin.Length;
            for (int i = 0; i < nv; i++) { w.Write(nor[i].x); w.Write(nor[i].y); w.Write(-nor[i].z); }
            int colOff = (int)bin.Length;
            for (int i = 0; i < nv; i++) { w.Write(col[i].r); w.Write(col[i].g); w.Write(col[i].b); }
            int idxOff = (int)bin.Length;
            for (int i = 0; i < ni; i += 3) { w.Write((uint)tri[i]); w.Write((uint)tri[i + 2]); w.Write((uint)tri[i + 1]); }
            int binLen = (int)bin.Length;
            int binPad = (4 - binLen % 4) % 4;
            for (int i = 0; i < binPad; i++) w.Write((byte)0);
            w.Flush();
            int vb = nv * 12;

            string F(float v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            var json = new StringBuilder();
            json.Append("{\"asset\":{\"version\":\"2.0\",\"generator\":\"a-kun clay (Unity)\"},");
            json.Append("\"scene\":0,\"scenes\":[{\"nodes\":[0]}],");
            json.Append($"\"nodes\":[{{\"mesh\":0,\"name\":\"{name}\"}}],");
            json.Append("\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0,\"NORMAL\":1,\"COLOR_0\":2},\"indices\":3,\"mode\":4,\"material\":0}]}],");
            json.Append("\"materials\":[{\"pbrMetallicRoughness\":{\"baseColorFactor\":[1,1,1,1],\"metallicFactor\":0,\"roughnessFactor\":0.78}}],");
            json.Append("\"accessors\":[");
            json.Append($"{{\"bufferView\":0,\"componentType\":5126,\"count\":{nv},\"type\":\"VEC3\",\"min\":[{F(mn.x)},{F(mn.y)},{F(mn.z)}],\"max\":[{F(mx.x)},{F(mx.y)},{F(mx.z)}]}},");
            json.Append($"{{\"bufferView\":1,\"componentType\":5126,\"count\":{nv},\"type\":\"VEC3\"}},");
            json.Append($"{{\"bufferView\":2,\"componentType\":5126,\"count\":{nv},\"type\":\"VEC3\"}},");
            json.Append($"{{\"bufferView\":3,\"componentType\":5125,\"count\":{ni},\"type\":\"SCALAR\"}}],");
            json.Append("\"bufferViews\":[");
            json.Append($"{{\"buffer\":0,\"byteOffset\":0,\"byteLength\":{vb},\"target\":34962}},");
            json.Append($"{{\"buffer\":0,\"byteOffset\":{norOff},\"byteLength\":{vb},\"target\":34962}},");
            json.Append($"{{\"buffer\":0,\"byteOffset\":{colOff},\"byteLength\":{vb},\"target\":34962}},");
            json.Append($"{{\"buffer\":0,\"byteOffset\":{idxOff},\"byteLength\":{ni * 4},\"target\":34963}}],");
            json.Append($"\"buffers\":[{{\"byteLength\":{binLen + binPad}}}]}}");
            var jb = Encoding.UTF8.GetBytes(json.ToString());
            int jsonPad = (4 - jb.Length % 4) % 4;

            var outS = new MemoryStream();
            var o = new BinaryWriter(outS);
            int total = 12 + 8 + jb.Length + jsonPad + 8 + binLen + binPad;
            o.Write(0x46546C67u); o.Write(2u); o.Write((uint)total);
            o.Write((uint)(jb.Length + jsonPad)); o.Write(0x4E4F534Au);
            o.Write(jb);
            for (int i = 0; i < jsonPad; i++) o.Write((byte)0x20);
            o.Write((uint)(binLen + binPad)); o.Write(0x004E4942u);
            o.Write(bin.GetBuffer(), 0, binLen + binPad);
            o.Flush();
            return outS.ToArray();
        }

        // 閉じたメッシュの体積 (符号付きの四面体の合計)
        public static float Volume(Vector3[] p, int[] t)
        {
            double v = 0;
            for (int i = 0; i < t.Length; i += 3)
            {
                Vector3 a = p[t[i]], b = p[t[i + 1]], c = p[t[i + 2]];
                v += a.x * (b.y * c.z - b.z * c.y) - a.y * (b.x * c.z - b.z * c.x) + a.z * (b.x * c.y - b.y * c.x);
            }
            return (float)Math.Abs(v) / 6f;
        }
    }
}
