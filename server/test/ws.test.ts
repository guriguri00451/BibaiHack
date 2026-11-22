import { afterAll, describe, expect, test } from "bun:test";
import { eq } from "drizzle-orm";
import { openDb } from "../src/db/client.ts";
import { gamePlayers, gameSessions } from "../src/db/schema.ts";
import { createServer } from "../src/server.ts";

const { db, close } = await openDb({});
const srv = createServer({ db, jwtSecret: "test-secret" });
const server = Bun.serve({ port: 0, fetch: srv.fetch, websocket: srv.websocket });
afterAll(async () => {
  server.stop(true);
  await close();
});
const base = `http://localhost:${server.port}`;

async function post(path: string, body: unknown, token?: string) {
  const res = await fetch(base + path, {
    method: "POST",
    headers: { "content-type": "application/json", ...(token ? { authorization: `Bearer ${token}` } : {}) },
    body: JSON.stringify(body),
  });
  return (await res.json()) as any;
}

/** 受信したメッセージを溜め、条件に合うものを待てる WebSocket クライアント */
function connect() {
  const ws = new WebSocket(`ws://localhost:${server.port}/v1/ws`);
  const inbox: any[] = [];
  const waiters: { pred: (m: any) => boolean; resolve: (m: any) => void }[] = [];
  let closed: { code: number } | null = null;
  ws.onmessage = (e) => {
    const m = JSON.parse(String(e.data));
    const i = waiters.findIndex((w) => w.pred(m));
    if (i >= 0) waiters.splice(i, 1)[0]!.resolve(m);
    else inbox.push(m);
  };
  const opened = new Promise<void>((r) => (ws.onopen = () => r()));
  const closedP = new Promise<{ code: number }>((r) => (ws.onclose = (e) => r((closed = { code: e.code }))));
  return {
    ws,
    inbox,
    opened,
    closed: closedP,
    get isClosed() {
      return closed !== null;
    },
    send: (m: unknown) => ws.send(JSON.stringify(m)),
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

async function setup() {
  const host = await post("/v1/guests", { name: "Hider" });
  const seeker = await post("/v1/guests", { name: "Seeker" });
  const session = await post("/v1/sessions", { mapId: "bibai-museum" }, host.accessToken);
  const joined = await post(`/v1/sessions/${session.id}/join`, {}, seeker.accessToken);
  return { host, seeker, session, hostPlayerId: session.playerId, seekerPlayerId: joined.playerId };
}

async function authed(token: string, sessionId: string) {
  const c = connect();
  await c.opened;
  c.send({ type: "auth", payload: { accessToken: token, sessionId } });
  const snapshot = await c.next((m) => m.type === "session.snapshot");
  return { c, snapshot };
}

const pose = (x: number) => ({ position: [x, 0, 0], rotation: [0, 0, 0, 1], scale: [1, 1, 1] });

describe("WebSocket", () => {
  test("auth すると snapshot が届き、player.transform が相手に中継される", async () => {
    const s = await setup();
    const a = await authed(s.host.accessToken, s.session.id);
    expect(a.snapshot.payload.you.playerId).toBe(s.hostPlayerId);
    expect(a.snapshot.payload.session.status).toBe("waiting");
    const b = await authed(s.seeker.accessToken, s.session.id);
    expect(b.snapshot.payload.players.find((p: any) => p.playerId === s.hostPlayerId).connected).toBe(true);

    a.c.send({ type: "player.transform", payload: { clientSeq: 1, transform: pose(1) } });
    const got = await b.c.next((m) => m.type === "player.transform");
    expect(got.payload).toEqual({ playerId: s.hostPlayerId, transform: pose(1) });
    expect(got.round).toBe(1);
    expect(got.seq).toBe(1);

    // 送った本人には返さない
    await Bun.sleep(50);
    expect(a.c.inbox.some((m) => m.type === "player.transform")).toBe(false);
    a.c.ws.close();
    b.c.ws.close();
  });

  test("古い clientSeq は捨て、seq はサーバーが確定順に振る", async () => {
    const s = await setup();
    const a = await authed(s.host.accessToken, s.session.id);
    const b = await authed(s.seeker.accessToken, s.session.id);
    a.c.send({ type: "player.transform", payload: { clientSeq: 5, transform: pose(5) } });
    a.c.send({ type: "player.transform", payload: { clientSeq: 4, transform: pose(4) } });
    a.c.send({ type: "player.transform", payload: { clientSeq: 6, transform: pose(6) } });
    const first = await b.c.next((m) => m.type === "player.transform");
    const second = await b.c.next((m) => m.type === "player.transform");
    expect([first.payload.transform.position[0], second.payload.transform.position[0]]).toEqual([5, 6]);
    expect(second.seq).toBe(first.seq + 1);
    a.c.ws.close();
    b.c.ws.close();
  });

  test("ゲーム中はドロの位置をケイに送らない", async () => {
    const s = await setup();
    // ラウンド開始 API はまだ無いので、DB を直接書き換えてから接続する
    await db.update(gameSessions).set({ status: "playing" }).where(eq(gameSessions.id, s.session.id));
    await db.update(gamePlayers).set({ role: "hider" }).where(eq(gamePlayers.id, s.hostPlayerId));
    await db.update(gamePlayers).set({ role: "seeker" }).where(eq(gamePlayers.id, s.seekerPlayerId));

    const hider = await authed(s.host.accessToken, s.session.id);
    const seeker = await authed(s.seeker.accessToken, s.session.id);
    hider.c.send({ type: "player.transform", payload: { clientSeq: 1, transform: pose(1) } });
    seeker.c.send({ type: "player.transform", payload: { clientSeq: 1, transform: pose(2) } });

    const atHider = await hider.c.next((m) => m.type === "player.transform");
    expect(atHider.payload.playerId).toBe(s.seekerPlayerId);
    await Bun.sleep(100);
    expect(seeker.c.inbox.some((m) => m.type === "player.transform")).toBe(false);

    // 入り直したケイの snapshot にもドロの位置は入らない
    seeker.c.ws.close();
    const again = await authed(s.seeker.accessToken, s.session.id);
    const hiderInSnap = again.snapshot.payload.players.find((p: any) => p.playerId === s.hostPlayerId);
    expect(hiderInSnap.transform).toBeNull();
    hider.c.ws.close();
    again.c.ws.close();
  });

  test("不正なトークンは 4001、参加者でなければ 4003 で切る", async () => {
    const s = await setup();
    const bad = connect();
    await bad.opened;
    bad.send({ type: "auth", payload: { accessToken: "bad", sessionId: s.session.id } });
    expect((await bad.closed).code).toBe(4001);

    const outsider = await post("/v1/guests", { name: "X" });
    const o = connect();
    await o.opened;
    o.send({ type: "auth", payload: { accessToken: outsider.accessToken, sessionId: s.session.id } });
    expect((await o.closed).code).toBe(4003);
  });

  test("auth 前の操作と不正な Transform は error を返す", async () => {
    const s = await setup();
    const c = connect();
    await c.opened;
    c.send({ type: "player.transform", payload: { clientSeq: 1, transform: pose(1) } });
    expect((await c.next((m) => m.type === "error")).payload.code).toBe("UNAUTHORIZED");

    const a = await authed(s.host.accessToken, s.session.id);
    a.c.send({ type: "player.transform", payload: { clientSeq: 1, transform: { ...pose(1), rotation: [0, 0, 0, 0] } } });
    expect((await a.c.next((m) => m.type === "error")).payload.code).toBe("INVALID_MESSAGE");
    c.ws.close();
    a.c.ws.close();
  });

  test("同じプレイヤーが入り直すと古い接続は 4009 で切れる", async () => {
    const s = await setup();
    const first = await authed(s.host.accessToken, s.session.id);
    const second = await authed(s.host.accessToken, s.session.id);
    expect((await first.c.closed).code).toBe(4009);
    expect(second.c.isClosed).toBe(false);
    second.c.ws.close();
  });
});
