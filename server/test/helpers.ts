import { openDb } from "../src/db/client.ts";
import type { AssetStorage } from "../src/storage.ts";
import { createServer } from "../src/server.ts";

export const JWT_SECRET = "test-secret";

/** PGlite 上でサーバーを起動する。テストファイルの先頭で await し、afterAll で stop を呼ぶ */
export async function startServer(opts: { basePath?: string; assetStorage?: AssetStorage } = {}) {
  const { db, close } = await openDb({});
  const srv = createServer({ db, jwtSecret: JWT_SECRET, basePath: opts.basePath, assetStorage: opts.assetStorage });
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
    // 204 など本文の無い応答もある
    const text = await res.text();
    return { status: res.status, body: (text ? JSON.parse(text) : undefined) as any };
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

  return {
    db,
    base,
    request,
    post,
    connect,
    authed,
    guest,
    async stop() {
      server.stop(true);
      await close();
    },
  };
}

export const pose = (x: number) => ({
  position: [x, 0, 0],
  rotation: [0, 0, 0, 1],
  scale: [1, 1, 1],
});
