// 外部ファイル無しで動かすため、色付きの 0.2m キューブを GLB(binary glTF 2.0) としてその場で組み立てる。
const HALF = 0.1;
const POSITIONS = [
  [-1, -1, 1], [1, -1, 1], [1, 1, 1], [-1, 1, 1],
  [-1, -1, -1], [-1, 1, -1], [1, 1, -1], [1, -1, -1],
];
const INDICES = [
  0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7, 3, 2, 6, 3, 6, 5,
  4, 7, 1, 4, 1, 0, 1, 7, 6, 1, 6, 2, 4, 0, 3, 4, 3, 5,
];

export function buildCubeGlb(rgb = [0.2, 0.7, 1]) {
  const positionBytes = Buffer.alloc(POSITIONS.length * 12);
  POSITIONS.forEach((p, i) => p.forEach((v, j) => positionBytes.writeFloatLE(v * HALF, i * 12 + j * 4)));
  const indexBytes = Buffer.alloc(INDICES.length * 2);
  INDICES.forEach((v, i) => indexBytes.writeUInt16LE(v, i * 2));
  const bin = Buffer.concat([positionBytes, indexBytes]);
  const binPadded = Buffer.concat([bin, Buffer.alloc((4 - (bin.length % 4)) % 4)]);

  const json = {
    asset: { version: '2.0' },
    scene: 0,
    scenes: [{ nodes: [0] }],
    nodes: [{ mesh: 0 }],
    meshes: [{ primitives: [{ attributes: { POSITION: 0 }, indices: 1, material: 0 }] }],
    materials: [{ pbrMetallicRoughness: { baseColorFactor: [...rgb, 1], metallicFactor: 0 } }],
    buffers: [{ byteLength: binPadded.length }],
    bufferViews: [
      { buffer: 0, byteOffset: 0, byteLength: positionBytes.length, target: 34962 },
      { buffer: 0, byteOffset: positionBytes.length, byteLength: indexBytes.length, target: 34963 },
    ],
    accessors: [
      { bufferView: 0, componentType: 5126, count: POSITIONS.length, type: 'VEC3', min: [-HALF, -HALF, -HALF], max: [HALF, HALF, HALF] },
      { bufferView: 1, componentType: 5123, count: INDICES.length, type: 'SCALAR' },
    ],
  };
  const jsonRaw = Buffer.from(JSON.stringify(json));
  const jsonPadded = Buffer.concat([jsonRaw, Buffer.alloc((4 - (jsonRaw.length % 4)) % 4, 0x20)]);

  const total = 12 + 8 + jsonPadded.length + 8 + binPadded.length;
  const header = Buffer.alloc(12);
  header.write('glTF', 0, 'ascii');
  header.writeUInt32LE(2, 4);
  header.writeUInt32LE(total, 8);
  const chunkHeader = (length, type) => {
    const b = Buffer.alloc(8);
    b.writeUInt32LE(length, 0);
    b.write(type, 4, 'ascii');
    return b;
  };
  return Buffer.concat([header, chunkHeader(jsonPadded.length, 'JSON'), jsonPadded, chunkHeader(binPadded.length, 'BIN\0'), binPadded]);
}
