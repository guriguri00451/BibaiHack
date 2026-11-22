// seeker-unity の同期表示を確認するためのモック。本物のサーバー(仕様 §6)の代わりに、
// 3つのキューブをマーカー周りで動かす object.transform を 15Hz で配信する。
import http from 'node:http';
import { WebSocketServer } from 'ws';
import { buildCubeGlb } from './cube-glb.js';

const PORT = Number(process.env.PORT ?? 8787);
const TICK_HZ = 15;
// PHASE_CYCLE=1 でフェーズを巡回させる（waiting→preparing→playing→finished→round++）。既定は playing 固定
const IS_PHASE_CYCLE = process.env.PHASE_CYCLE === '1';
const PHASES = [['waiting', 5], ['preparing', 5], ['playing', 20], ['finished', 8]];
const COLORS = { 'mock/red.glb': [1, 0.2, 0.3], 'mock/green.glb': [0.3, 0.9, 0.4], 'mock/blue.glb': [0.2, 0.6, 1] };
const PARTICIPANTS = ['ケイ', 'ドロ'];
const OBJECTS = Object.keys(COLORS).map((assetKey, i) => ({ objectId: `obj-${i + 1}`, assetKey, phase: (i * 2 * Math.PI) / 3 }));

let seq = 0;
let round = 1;
let phase = 'playing';

const identityRotation = [0, 0, 0, 1];
const transformAt = (object, timeSeconds) => {
  // マップ座標は右手系・Y-up・メートル。半径0.5mの円運動 + 上下に揺れる + Y軸回転
  const angle = timeSeconds * 0.8 + object.phase;
  const yaw = timeSeconds * 1.5;
  return {
    position: [Math.cos(angle) * 0.5, 0.15 + Math.sin(timeSeconds * 2 + object.phase) * 0.05, Math.sin(angle) * 0.5],
    rotation: [0, Math.sin(yaw / 2), 0, Math.cos(yaw / 2)],
    scale: [1, 1, 1],
  };
};

const server = http.createServer((request, response) => {
  const base = `http://${request.headers.host}`;
  const url = new URL(request.url, base);
  if (url.pathname === '/v1/assets/download-url') {
    // 署名URL取得の代役。本物は期限付きURLを返す
    const key = url.searchParams.get('key');
    if (!(key in COLORS)) { response.writeHead(404).end(); return; }
    response.writeHead(200, { 'content-type': 'application/json' });
    response.end(JSON.stringify({ url: `${base}/files/${key}` }));
    return;
  }
  if (url.pathname.startsWith('/files/')) {
    const key = url.pathname.slice('/files/'.length);
    if (!(key in COLORS)) { response.writeHead(404).end(); return; }
    response.writeHead(200, { 'content-type': 'model/gltf-binary' });
    response.end(buildCubeGlb(COLORS[key]));
    return;
  }
  response.writeHead(404).end();
});

const wss = new WebSocketServer({ server, path: '/v1/ws' });
const authenticated = new Set();

wss.on('connection', (socket) => {
  socket.on('message', (raw) => {
    const message = JSON.parse(raw.toString());
    if (message.type !== 'auth') return;
    authenticated.add(socket);
    console.log(`auth ok session=${message.payload?.sessionId}`);
    const now = Date.now() / 1000;
    send(socket, 'session.snapshot', {
      status: phase,
      participants: PARTICIPANTS,
      // preparing 中は配置情報を返さない（ドロの位置を漏らさない）
      objects: phase !== 'playing' ? [] : OBJECTS.map((o) => ({ objectId: o.objectId, assetKey: o.assetKey, transform: transformAt(o, now) })),
    });
  });
  socket.on('close', () => authenticated.delete(socket));
});

function send(socket, type, payload) {
  socket.send(JSON.stringify({ type, round, seq, timestamp: Date.now(), payload }));
}

function broadcastPhase() {
  seq += 1;
  const payload = { status: phase, participants: PARTICIPANTS };
  if (phase === 'finished') payload.result = { captured: round % 2 === 1, score: 100 * round };
  for (const socket of authenticated) send(socket, 'session.phase_changed', payload);
  console.log(`phase=${phase} round=${round}`);
}

async function runPhaseCycle() {
  for (;;) {
    for (const [name, seconds] of PHASES) {
      phase = name;
      broadcastPhase();
      await new Promise((resolve) => setTimeout(resolve, seconds * 1000));
    }
    round += 1;
    seq = 0;
  }
}
if (IS_PHASE_CYCLE) runPhaseCycle();

setInterval(() => {
  if (authenticated.size === 0 || phase !== 'playing') return;
  const now = Date.now() / 1000;
  for (const object of OBJECTS) {
    seq += 1;
    for (const socket of authenticated) send(socket, 'object.transform', { objectId: object.objectId, transform: transformAt(object, now) });
  }
}, 1000 / TICK_HZ);

server.listen(PORT, '0.0.0.0', () => console.log(`mock server: ws://<this-pc-ip>:${PORT}/v1/ws`));
