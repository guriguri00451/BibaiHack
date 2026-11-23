import { constants, gunzipSync } from "node:zlib";

/** GLB（バイナリの glTF）の先頭 "glTF" */
const GLB_MAGIC = [0x67, 0x6c, 0x54, 0x46];

export function isGlb(bytes: Uint8Array): boolean {
  return GLB_MAGIC.every((b, i) => bytes[i] === b);
}

/** SPZ のヘッダの先頭 "NGSP"（リトルエンディアンの 0x5053474e） */
const SPZ_MAGIC = 0x5053474e;
/** seeker-unity の UnityGaussianSplatting（SPZFileReader.cs）が読める版 */
export const SPZ_VERSIONS = { min: 1, max: 4 } as const;

export type SpzHeader = { version: number; points: number; shDegree: number };

/**
 * SPZ（Gaussian Splat）のヘッダを読む。読めなければ理由を返す。
 * v1〜3 は全体が gzip、v4 はヘッダが生のまま（属性ごとに zstd）
 */
export function readSpzHeader(bytes: Uint8Array): SpzHeader | { error: string } {
  let head = bytes;
  if (bytes[0] === 0x1f && bytes[1] === 0x8b) {
    try {
      // 先頭だけ展開する（Z_SYNC_FLUSH なら途中で切れた入力でも、そこまでの出力が返る）
      head = gunzipSync(bytes.subarray(0, 64 * 1024), { finishFlush: constants.Z_SYNC_FLUSH });
    } catch {
      return { error: "gzip を展開できません。" };
    }
  }
  if (head.byteLength < 16) return { error: "SPZ のヘッダが短すぎます。" };
  const view = new DataView(head.buffer, head.byteOffset, head.byteLength);
  if (view.getUint32(0, true) !== SPZ_MAGIC) return { error: "SPZ（Gaussian Splat）ではありません。" };
  const version = view.getUint32(4, true);
  if (version < SPZ_VERSIONS.min || version > SPZ_VERSIONS.max) {
    return { error: `SPZ の版 ${version} には対応していません（${SPZ_VERSIONS.min}〜${SPZ_VERSIONS.max}）。` };
  }
  const points = view.getUint32(8, true);
  const shDegree = view.getUint8(12);
  if (points === 0) return { error: "SPZ に点がありません。" };
  if (shDegree > 3) return { error: `SPZ の SH 次数 ${shDegree} には対応していません（0〜3）。` };
  return { version, points, shDegree };
}

/**
 * 何も無いシーンだけの GLB。SPZ だけのマップでも model.glb が読める（GLB ローダーが失敗しない）ようにする
 */
export const EMPTY_GLB: Uint8Array = (() => {
  let json = JSON.stringify({ asset: { version: "2.0" }, scene: 0, scenes: [{ nodes: [] }] });
  while (json.length % 4 !== 0) json += " ";
  const body = new TextEncoder().encode(json);
  const out = new Uint8Array(12 + 8 + body.byteLength);
  const view = new DataView(out.buffer);
  out.set(GLB_MAGIC, 0);
  view.setUint32(4, 2, true);
  view.setUint32(8, out.byteLength, true);
  view.setUint32(12, body.byteLength, true);
  view.setUint32(16, 0x4e4f534a, true); // "JSON"
  out.set(body, 20);
  return out;
})();
