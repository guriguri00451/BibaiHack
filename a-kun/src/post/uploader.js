// 粘土のモデルを投稿する部分。
// 仕様 (docs/whiteboard-brainstorm.md) の「Sculpt確定 → Object Storage + PostgreSQL」と
// WebSocket の object.created / object.updated / object.deleted に合わせる。
//
// 投稿先は 2 通りで、同じ形で使える:
//   await target.uploadAsset(glbBytes)                  → asset_url (版ごとに別のキー)
//   await target.createObject({ assetUrl, transform })  → { objectId }
//   await target.updateObject({ objectId, assetUrl, transform })
//   await target.deleteObject(objectId)
//
// LocalTarget:  サーバーがまだ無いときの代わり。このページの中に置くだけ (読み込み直すと消える)
// ServerTarget: ゲームサーバー。WebSocket で部屋に入り、GLB を HTTP で上げてから object.create を送る

export function makeUUID() {
  if (globalThis.crypto?.randomUUID) {
    try { return crypto.randomUUID(); } catch { /* http のページでは使えない */ }
  }
  const b = new Uint8Array(16);
  crypto.getRandomValues(b);
  b[6] = (b[6] & 0x0f) | 0x40; b[8] = (b[8] & 0x3f) | 0x80;
  const h = [...b].map((x) => x.toString(16).padStart(2, '0')).join('');
  return `${h.slice(0, 8)}-${h.slice(8, 12)}-${h.slice(12, 16)}-${h.slice(16, 20)}-${h.slice(20)}`;
}

export class LocalTarget {
  constructor() {
    this.assets = new Map();  // asset_url → GLB
    this.objects = new Map(); // objectId → { id, assetUrl, transform, version }
  }

  get kind() { return 'local'; }
  get label() { return 'このブラウザ内'; }

  async uploadAsset(bytes) {
    const url = `local-asset://${makeUUID()}.glb`;
    this.assets.set(url, bytes);
    return url;
  }

  async createObject({ assetUrl, transform }) {
    const id = makeUUID();
    this.objects.set(id, { id, assetUrl, transform, version: 1 });
    return { objectId: id };
  }

  async updateObject({ objectId, assetUrl, transform }) {
    const o = this.objects.get(objectId);
    if (!o) throw new Error('投稿が見つかりません');
    if (assetUrl) { o.assetUrl = assetUrl; o.version += 1; }
    if (transform) o.transform = transform;
    return { version: o.version };
  }

  async deleteObject(objectId) {
    this.objects.delete(objectId);
  }

  close() {}
}

// ゲームサーバー用。サーバー側とまだ決めていない部分は次のように仮定している (README 参照):
//   - メッセージは JSON の { type, payload, requestId }。返事は { type: 'ack' | 'error', payload: { requestId, ... } }
//   - session.join { code, name, playerId?, token? } → ack { sessionId, playerId, token }
//   - GLB は POST {httpBase}/assets (body = GLB) → { assetUrl }
//   - object.create { assetUrl, transform } → ack { objectId }   (サーバーが object.created を配る)
//   - object.update { objectId, assetUrl } / object.transform { objectId, transform } / object.delete { objectId }
export class ServerTarget {
  constructor(url) {
    this.url = url;
    const u = new URL(url);
    if (u.protocol !== 'ws:' && u.protocol !== 'wss:') throw new Error('サーバーの URL は ws:// か wss:// で始めてください');
    this.httpBase = `${u.protocol === 'wss:' ? 'https:' : 'http:'}//${u.host}`;
    this.ws = null;
    this.pending = new Map();
    this.seq = 0;
    this.onclose = null;
    this.session = null; // { sessionId, playerId, token, code }
  }

  get kind() { return 'ws'; }
  get label() { return this.session ? `部屋 ${this.session.code}` : 'サーバー'; }

  open(timeoutMs = 6000) {
    return new Promise((resolve, reject) => {
      const ws = new WebSocket(this.url);
      this.ws = ws;
      const timer = setTimeout(() => { ws.close(); reject(new Error('サーバーに接続できません (タイムアウト)')); }, timeoutMs);
      ws.onopen = () => { clearTimeout(timer); resolve(); };
      ws.onerror = () => { clearTimeout(timer); reject(new Error('サーバーに接続できません')); };
      ws.onmessage = (e) => {
        let msg;
        try { msg = JSON.parse(e.data); } catch { return; }
        this._onMessage(msg);
      };
      ws.onclose = () => {
        for (const p of this.pending.values()) { clearTimeout(p.timer); p.reject(new Error('接続が切れました')); }
        this.pending.clear();
        this.onclose?.();
      };
    });
  }

  async join({ code, name, resume }) {
    await this.open();
    const res = await this.request('session.join', { code, name, playerId: resume?.playerId, token: resume?.token });
    this.session = { sessionId: res.sessionId, playerId: res.playerId, token: res.token, code: res.code || code };
    return this.session;
  }

  request(type, payload, timeoutMs = 8000) {
    if (this.ws?.readyState !== WebSocket.OPEN) return Promise.reject(new Error('サーバーにつながっていません'));
    const requestId = ++this.seq;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => { this.pending.delete(requestId); reject(new Error('サーバーから応答がありません')); }, timeoutMs);
      this.pending.set(requestId, { resolve, reject, timer });
      this.ws.send(JSON.stringify({ type, payload, requestId }));
    });
  }

  _onMessage({ type, payload }) {
    if (type !== 'ack' && type !== 'error') return; // ほかのイベント (game.snapshot など) はここでは使わない
    const p = this.pending.get(payload?.requestId);
    if (!p) return;
    this.pending.delete(payload.requestId);
    clearTimeout(p.timer);
    if (type === 'ack') p.resolve(payload);
    else p.reject(Object.assign(new Error(payload.message || 'サーバーでエラーになりました'), { code: payload.code }));
  }

  async uploadAsset(bytes) {
    const res = await fetch(`${this.httpBase}/assets`, {
      method: 'POST', headers: { 'Content-Type': 'model/gltf-binary' }, body: bytes,
    });
    if (!res.ok) throw new Error(`アップロードに失敗しました (${res.status})`);
    const { assetUrl } = await res.json();
    if (!assetUrl) throw new Error('アップロード先から asset_url が返りませんでした');
    return assetUrl;
  }

  createObject({ assetUrl, transform }) {
    return this.request('object.create', { assetUrl, transform });
  }

  async updateObject({ objectId, assetUrl, transform }) {
    const res = await this.request('object.update', { objectId, assetUrl });
    if (transform) await this.request('object.transform', { objectId, transform });
    return res;
  }

  deleteObject(objectId) {
    return this.request('object.delete', { objectId });
  }

  close() {
    this.onclose = null;
    this.ws?.close();
  }
}
