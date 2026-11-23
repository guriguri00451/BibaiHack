import { openDb } from "../src/db/client.ts";
import { createServer } from "../src/server.ts";
import type { ObjectStorage } from "../src/storage/storage.ts";

export const JWT_SECRET = "test-secret";

/** PGlite 上でサーバーを起動する。テストファイルの先頭で await し、afterAll で stop を呼ぶ */
export async function startServer(opts: { basePath?: string; storage?: ObjectStorage } = {}) {
  const { db, close } = await openDb({});
  const srv = createServer({ db, jwtSecret: JWT_SECRET, basePath: opts.basePath, storage: opts.storage });
  const server = Bun.serve({ port: 0, fetch: srv.fetch, websocket: srv.websocket });
  const base = `http://localhost:${server.port}${opts.basePath ?? ""}`;

  async function request(method: string, path: string, body?: unknown, token?: string) {
    const res = await fetch(base + path, {
      method,
      headers: {
        "content-type": "application/json",
        ...(token ? { authorization: `Bearer ${token}` } : {}),
      },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    const text = await res.text();
    return { status: res.status, headers: res.headers, body: (text ? JSON.parse(text) : null) as any };
  }
  const post = async (path: string, body: unknown, token?: string) =>
    (await request("POST", path, body, token)).body;

  /** 受信を溜め、条件に合うものを待てる WebSocket クライアント */
  function connect() {
    const ws = new WebSocket(`ws://localhost:${server.port}${opts.basePath ?? ""}/v1/ws`);
    const inbox: any[] = [];
    const waiters: { pred: (m: any) => boolean; resolve: (m: any) => void }[] = [];
    let closedFlag = false;
    ws.onmessage = (e) => {
      const m = JSON.parse(String(e.data));
      const i = waiters.findIndex((w) => w.pred(m));
      if (i >= 0) waiters.splice(i, 1)[0]!.resolve(m);
      else inbox.push(m);
    };
    const opened = new Promise<void>((r) => (ws.onopen = () => r()));
    const closed = new Promise<{ code: number }>(
      (r) =>
        (ws.onclose = (e) => {
          closedFlag = true;
          r({ code: e.code });
        }),
    );
    return {
      ws,
      inbox,
      opened,
      closed,
      get isClosed() {
        return closedFlag;
      },
      send: (m: unknown) => ws.send(typeof m === "string" ? m : JSON.stringify(m)),
      next(pred: (m: any) => boolean, ms = 2000) {
        const i = inbox.findIndex(pred);
        if (i >= 0) return Promise.resolve(inbox.splice(i, 1)[0]);
        return new Promise<any>((resolve, reject) => {
          waiters.push({ pred, resolve });
          setTimeout(() => reject(new Error("timeout waiting for message")), ms);
        });
      },
    };
  }

  async function authed(token: string, sessionId: string) {
    const c = connect();
    await c.opened;
    c.send({ type: "auth", payload: { accessToken: token, sessionId } });
    const snapshot = await c.next((m) => m.type === "session.snapshot");
    return { c, snapshot };
  }

  const guest = (name: string) => post("/v1/guests", { name });

  /** 署名付き URL を取り、GLB を PUT して assetKey を返す */
  async function uploadGlb(token: string, sessionId: string, bytes: Uint8Array = fakeGlb()) {
    const r = await request(
      "POST",
      "/v1/assets/uploads",
      { sessionId, contentType: "model/gltf-binary", sizeBytes: bytes.byteLength },
      token,
    );
    if (r.status !== 201) throw new Error(`uploads: ${r.status} ${JSON.stringify(r.body)}`);
    const put = await fetch(r.body.uploadUrl, {
      method: "PUT",
      headers: { "content-type": "model/gltf-binary" },
      body: bytes,
    });
    if (put.status !== 201) throw new Error(`PUT: ${put.status} ${await put.text()}`);
    return r.body.assetKey as string;
  }

  return {
    db,
    srv,
    base,
    request,
    post,
    connect,
    authed,
    guest,
    uploadGlb,
    async stop() {
      await srv.dispose();
      server.stop(true);
      await close();
    },
  };
}

/** 先頭が "glTF" の、中身は空の GLB もどき（サーバーは先頭と大きさだけ見る） */
export function fakeGlb(size = 64): Uint8Array {
  const b = new Uint8Array(size);
  b.set([0x67, 0x6c, 0x54, 0x46, 2, 0, 0, 0]);
  return b;
}

export const pose = (x: number) => ({
  position: [x, 0, 0],
  rotation: [0, 0, 0, 1],
  scale: [1, 1, 1],
});
