/**
 * 実機確認用のローカルサーバー。ストレージ(S3)の代わりに GLB を自分で配り、
 * セッション・オブジェクトを作って、ドロ役としてオブジェクトを動かし続ける。
 *
 *   HOST_IP=192.168.x.x bun scripts/dev-local.ts [出力するJSONのパス]
 *
 * 出力する JSON（baseUrl / sessionId / accessToken）は、ケイ側アプリの
 * Application.persistentDataPath/dev-connection.json に置く。
 */
import { writeFileSync } from "node:fs";
import { eq } from "drizzle-orm";
// @ts-ignore 型の無い JS。開発用スクリプトなので型は付けない
import { buildCubeGlb } from "../../mock-server/cube-glb.js";
import { openDb } from "../src/db/client.ts";
import { gamePlayers, gameSessions } from "../src/db/schema.ts";
import { createServer } from "../src/server.ts";

const PORT = Number(process.env.PORT ?? 8080);
const HOST_IP = process.env.HOST_IP;
if (!HOST_IP) throw new Error("HOST_IP（実機から見たこの Mac の IP）を指定してください");
const OUTPUT_PATH = process.argv[2];
const TICK_HZ = 15;
const RADIUS_M = 0.5;
const COLORS: number[][] = [
  [1, 0.2, 0.3],
  [0.3, 0.9, 0.4],
  [0.2, 0.6, 1],
];

const baseUrl = `http://${HOST_IP}:${PORT}`;
const assetUrlOf = (key: string) => `${baseUrl}/dev-assets/${key}`;
const glbByKey = new Map<string, Uint8Array>();

const { db } = await openDb({});
const srv = createServer({
  db,
  jwtSecret: "dev-local-secret",
  publicUrl: baseUrl,
  assetStorage: {
    presignUpload: (key) => assetUrlOf(key),
    presignDownload: (key) => assetUrlOf(key),
  },
});
Bun.serve({
  hostname: "0.0.0.0",
  port: PORT,
  websocket: srv.websocket,
  fetch(req, server) {
    const { pathname } = new URL(req.url);
    if (pathname.startsWith("/dev-assets/")) {
      const glb = glbByKey.get(pathname.slice("/dev-assets/".length));
      return glb ? new Response(glb, { headers: { "content-type": "model/gltf-binary" } }) : new Response(null, { status: 404 });
    }
    return srv.fetch(req, server);
  },
});

async function api(method: string, path: string, body?: unknown, token?: string) {
  const res = await fetch(baseUrl + path, {
    method,
    headers: { "content-type": "application/json", ...(token ? { authorization: `Bearer ${token}` } : {}) },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!res.ok) throw new Error(`${method} ${path} → ${res.status} ${await res.text()}`);
  return (await res.json()) as any;
}

const hider = await api("POST", "/v1/guests", { name: "Hider" });
const seeker = await api("POST", "/v1/guests", { name: "Seeker" });
const session = await api("POST", "/v1/sessions", { mapId: "bibai-museum" }, hider.accessToken);
const joined = await api("POST", `/v1/sessions/${session.id}/join`, {}, seeker.accessToken);
// ラウンド開始 API がまだ無いので、役割とフェーズは DB に直接入れる
await db.update(gamePlayers).set({ role: "hider" }).where(eq(gamePlayers.id, session.playerId));
await db.update(gamePlayers).set({ role: "seeker" }).where(eq(gamePlayers.id, joined.playerId));
await db.update(gameSessions).set({ status: "playing" }).where(eq(gameSessions.id, session.id));

const objectIds: string[] = [];
for (const color of COLORS) {
  const upload = await api(
    "POST",
    "/v1/assets/uploads",
    { sessionId: session.id, contentType: "model/gltf-binary", sizeBytes: 1024 },
    hider.accessToken,
  );
  glbByKey.set(upload.assetKey, buildCubeGlb(color));
  const created = await api(
    "POST",
    `/v1/sessions/${session.id}/objects`,
    { assetKey: upload.assetKey, transform: { position: [0, 0.15, 0], rotation: [0, 0, 0, 1], scale: [1, 1, 1] } },
    hider.accessToken,
  );
  objectIds.push(created.id);
}

const connection = { baseUrl, sessionId: session.id, accessToken: seeker.accessToken };
if (OUTPUT_PATH) writeFileSync(OUTPUT_PATH, JSON.stringify(connection));
console.log(`dev-local listening on ${baseUrl}\n${JSON.stringify(connection)}`);

// ドロとして接続し、オブジェクトを円運動させる（所有者の object.transform が他の参加者に中継される）
const ws = new WebSocket(`ws://localhost:${PORT}/v1/ws`);
await new Promise<void>((resolve) => (ws.onopen = () => resolve()));
ws.send(JSON.stringify({ type: "auth", payload: { accessToken: hider.accessToken, sessionId: session.id } }));
let clientSeq = 0;
setInterval(() => {
  const t = Date.now() / 1000;
  clientSeq += 1;
  objectIds.forEach((objectId, i) => {
    const angle = t * 0.8 + (i * 2 * Math.PI) / objectIds.length;
    const yaw = t * 1.5;
    ws.send(
      JSON.stringify({
        type: "object.transform",
        payload: {
          objectId,
          clientSeq,
          transform: {
            position: [Math.cos(angle) * RADIUS_M, 0.15 + Math.sin(t * 2 + i) * 0.05, Math.sin(angle) * RADIUS_M],
            rotation: [0, Math.sin(yaw / 2), 0, Math.cos(yaw / 2)],
            scale: [1, 1, 1],
          },
        },
      }),
    );
  });
}, 1000 / TICK_HZ);
