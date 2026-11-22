// seeker-unity の同期表示を確認するためのモック。本物のサーバー(仕様 §6)の代わりに、
// 3つのキューブをマーカー周りで動かす object.transform を 15Hz で配信する。
import http from 'node:http';
import { WebSocketServer } from 'ws';
import { buildCubeGlb } from './cube-glb.js';

const PORT = Number(process.env.PORT ?? 8787);
const TICK_HZ = 15;
const ROUND = 1;
const COLORS = { 'mock/red.glb': [1, 0.2, 0.3], 'mock/green.glb': [0.3, 0.9, 0.4], 'mock/blue.glb': [0.2, 0.6, 1] };
const OBJECTS = Object.keys(COLORS).map((assetKey, i) => ({ objectId: `obj-${i + 1}`, assetKey, phase: (i * 2 * Math.PI) / 3 }));

let seq = 0;

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

wss.on('connection', (socket, request) => {
  // 本物は期限付き署名URLを assetUrl に載せる。モックは署名なしのこのサーバー自身のURL
  const assetUrl = (key) => `http://${request.headers.host}/files/${key}`;
  socket.on('message', (raw) => {
    const message = JSON.parse(raw.toString());
    if (message.type !== 'auth') return;
    authenticated.add(socket);
    console.log(`auth ok session=${message.payload?.sessionId}`);
    const now = Date.now() / 1000;
    send(socket, 'session.snapshot', {
      status: 'playing',
      objects: OBJECTS.map((o) => ({ objectId: o.objectId, assetKey: o.assetKey, assetUrl: assetUrl(o.assetKey), transform: transformAt(o, now) })),
    });
  });
  socket.on('close', () => authenticated.delete(socket));
});

function send(socket, type, payload) {
  socket.send(JSON.stringify({ type, round: ROUND, seq, timestamp: Date.now(), payload }));
}

setInterval(() => {
  if (authenticated.size === 0) return;
  const now = Date.now() / 1000;
  for (const object of OBJECTS) {
    seq += 1;
    for (const socket of authenticated) send(socket, 'object.transform', { objectId: object.objectId, transform: transformAt(object, now) });
  }
}, 1000 / TICK_HZ);

server.listen(PORT, '0.0.0.0', () => console.log(`mock server: ws://<this-pc-ip>:${PORT}/v1/ws`));
