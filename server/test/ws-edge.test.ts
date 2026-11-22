import { afterAll, describe, expect, test } from "bun:test";
import { eq } from "drizzle-orm";
import { gamePlayers, gameSessions } from "../src/db/schema.ts";
import { pose, startServer } from "./helpers.ts";

const s = await startServer();
afterAll(s.stop);

async function session(...names: string[]) {
  const guests = await Promise.all(names.map((n) => s.guest(n)));
  const created = await s.post("/v1/sessions", { mapId: "bibai-museum" }, guests[0].accessToken);
  const playerIds = [created.playerId as string];
  for (const g of guests.slice(1)) {
    playerIds.push((await s.post(`/v1/sessions/${created.id}/join`, {}, g.accessToken)).playerId);
  }
  return { id: created.id as string, guests, playerIds };
}

const transforms = (c: { inbox: any[] }) => c.inbox.filter((m) => m.type === "player.transform");

describe("WebSocket の境界", () => {
  test("auth が来なければ 5 秒で 4001", async () => {
    const c = s.connect();
    await c.opened;
    const t0 = Date.now();
    expect((await c.closed).code).toBe(4001);
    expect(Date.now() - t0).toBeGreaterThanOrEqual(4500);
  }, 8000);

  test("JSON でないメッセージは INVALID_MESSAGE", async () => {
    const c = s.connect();
    await c.opened;
    c.send("{oops");
    expect((await c.next((m) => m.type === "error")).payload.code).toBe("INVALID_MESSAGE");
    c.ws.close();
  });

  test("2回目の auth は ALREADY_AUTHENTICATED", async () => {
    const x = await session("A");
    const a = await s.authed(x.guests[0].accessToken, x.id);
    a.c.send({ type: "auth", payload: { accessToken: x.guests[0].accessToken, sessionId: x.id } });
    expect((await a.c.next((m) => m.type === "error")).payload.code).toBe("ALREADY_AUTHENTICATED");
    a.c.ws.close();
  });

  test("存在しないセッションは 4004", async () => {
    const g = await s.guest("N");
    const c = s.connect();
    await c.opened;
    c.send({ type: "auth", payload: { accessToken: g.accessToken, sessionId: "00000000-0000-4000-8000-000000000000" } });
    expect((await c.closed).code).toBe(4004);
  });

  test("状態がメモリに載った後に REST で参加した人も auth でき、他の人の snapshot に載る", async () => {
    const x = await session("Host");
    const host = await s.authed(x.guests[0].accessToken, x.id); // ここでメモリに載る
    const late = await s.guest("Late");
    const joined = await s.post(`/v1/sessions/${x.id}/join`, {}, late.accessToken);
    const l = await s.authed(late.accessToken, x.id);
    expect(l.snapshot.payload.you.playerId).toBe(joined.playerId);

    host.c.ws.close();
    await host.c.closed;
    const again = await s.authed(x.guests[0].accessToken, x.id);
    const names = again.snapshot.payload.players.map((p: any) => p.name);
    expect(names).toContain("Late");
    l.c.ws.close();
    again.c.ws.close();
  });

  test("切断した人は次の snapshot で connected=false", async () => {
    const x = await session("A", "B");
    const a = await s.authed(x.guests[0].accessToken, x.id);
    const b = await s.authed(x.guests[1].accessToken, x.id);
    b.c.ws.close();
    await b.c.closed;
    await Bun.sleep(50);
    a.c.ws.close();
    await a.c.closed;
    const a2 = await s.authed(x.guests[0].accessToken, x.id);
    const bInSnap = a2.snapshot.payload.players.find((p: any) => p.playerId === x.playerIds[1]);
    expect(bInSnap.connected).toBe(false);
    a2.c.ws.close();
  });

  test("少しずれたクォータニオン（長さ1.01）は正規化して中継する", async () => {
    const x = await session("A", "B");
    const a = await s.authed(x.guests[0].accessToken, x.id);
    const b = await s.authed(x.guests[1].accessToken, x.id);
    a.c.send({ type: "player.transform", payload: { clientSeq: 1, transform: { ...pose(0), rotation: [0, 0, 0, 1.01] } } });
    const got = await b.c.next((m) => m.type === "player.transform");
    expect(got.payload.transform.rotation).toEqual([0, 0, 0, 1]);
    a.c.ws.close();
    b.c.ws.close();
  });

  test("ゲーム中、ケイの位置はドロに届く", async () => {
    const x = await session("Hider", "Seeker");
    await s.db.update(gameSessions).set({ status: "playing" }).where(eq(gameSessions.id, x.id));
    await s.db.update(gamePlayers).set({ role: "hider" }).where(eq(gamePlayers.id, x.playerIds[0]!));
    await s.db.update(gamePlayers).set({ role: "seeker" }).where(eq(gamePlayers.id, x.playerIds[1]!));
    const hider = await s.authed(x.guests[0].accessToken, x.id);
    const seeker = await s.authed(x.guests[1].accessToken, x.id);
    seeker.c.send({ type: "player.transform", payload: { clientSeq: 1, transform: pose(3) } });
    const got = await hider.c.next((m) => m.type === "player.transform");
    expect(got.payload.playerId).toBe(x.playerIds[1]);
    // 入り直したドロの snapshot にもケイの位置が入る
    hider.c.ws.close();
    await hider.c.closed;
    const again = await s.authed(x.guests[0].accessToken, x.id);
    const seekerInSnap = again.snapshot.payload.players.find((p: any) => p.playerId === x.playerIds[1]);
    expect(seekerInSnap.transform.position).toEqual([3, 0, 0]);
    seeker.c.ws.close();
    again.c.ws.close();
  });

  test("待機中は全員の位置が全員に届く（3人）", async () => {
    const x = await session("A", "B", "C");
    const cs = await Promise.all(x.guests.map((g) => s.authed(g.accessToken, x.id)));
    cs.forEach((c, i) => c.c.send({ type: "player.transform", payload: { clientSeq: 1, transform: pose(i + 1) } }));
    for (const [i, c] of cs.entries()) {
      const got = [
        await c.c.next((m) => m.type === "player.transform"),
        await c.c.next((m) => m.type === "player.transform"),
      ];
      const others = x.playerIds.filter((_, j) => j !== i).sort();
      expect(got.map((m) => m.payload.playerId).sort()).toEqual(others);
    }
    await Bun.sleep(50);
    expect(cs.every((c) => transforms(c.c).length === 0)).toBe(true); // 自分のものは来ない
    cs.forEach((c) => c.c.ws.close());
  });

  test("3人のうち1人の送信は他の2人にだけ届き、seq は全員で同じ", async () => {
    const x = await session("A", "B", "C");
    const [a, b, c] = await Promise.all(x.guests.map((g) => s.authed(g.accessToken, x.id)));
    a!.c.send({ type: "player.transform", payload: { clientSeq: 1, transform: pose(9) } });
    const [gb, gc] = await Promise.all([
      b!.c.next((m) => m.type === "player.transform"),
      c!.c.next((m) => m.type === "player.transform"),
    ]);
    expect(gb.payload.playerId).toBe(x.playerIds[0]);
    expect(gc.payload.playerId).toBe(x.playerIds[0]);
    expect(gb.seq).toBe(gc.seq);
    await Bun.sleep(50);
    expect(transforms(a!.c).length).toBe(0);
    [a, b, c].forEach((p) => p!.c.ws.close());
  });
});
