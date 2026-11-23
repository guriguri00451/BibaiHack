import { afterAll, describe, expect, test } from "bun:test";
import { gameSessions } from "../src/db/schema.ts";
import { startServer } from "./helpers.ts";

const s = await startServer();
afterAll(s.stop);

const list = (token?: string, query = "") => s.request("GET", `/v1/sessions${query}`, undefined, token);

async function room(mapId = "bibai-museum") {
  const host = await s.guest("Host");
  const seeker = await s.guest("Seeker");
  const created = await s.post("/v1/sessions", { mapId }, host.accessToken);
  const joined = await s.post(`/v1/sessions/${created.id}/join`, {}, seeker.accessToken);
  return { id: created.id as string, host, seeker, hiderId: created.playerId as string, seekerId: joined.playerId as string };
}

describe("GET /v1/sessions", () => {
  test("トークンなしは 401", async () => {
    expect((await list()).status).toBe(401);
  });

  test("limit が上限を超えると 400", async () => {
    const me = await s.guest("Me");
    expect((await list(me.accessToken, "?limit=51")).status).toBe(400);
  });

  test("ホスト名と人数を返し、役割・スコア・userId は含まない", async () => {
    const r = await room();
    const res = await list(r.seeker.accessToken);
    const found = res.body.sessions.find((x: any) => x.id === r.id);
    expect(Object.keys(found).sort()).toEqual(
      ["connectedCount", "createdAt", "hostName", "id", "mapId", "phaseEndsAt", "playerCount", "round", "status"].sort(),
    );
  });

  test("メモリ上の状態（ラウンド開始・WS 接続）が反映される", async () => {
    const r = await room();
    await s.request(
      "POST",
      `/v1/sessions/${r.id}/rounds/start`,
      { preparingSeconds: 120, roles: [{ playerId: r.hiderId, role: "hider" }, { playerId: r.seekerId, role: "seeker" }] },
      r.host.accessToken,
    );
    const { c } = await s.authed(r.seeker.accessToken, r.id);
    try {
      const found = (await list(r.seeker.accessToken)).body.sessions.find((x: any) => x.id === r.id);
      expect(found).toMatchObject({ status: "preparing", hostName: "Host", playerCount: 2, connectedCount: 1 });
    } finally {
      c.ws.close();
    }
  });

  test("新しい順に並ぶ", async () => {
    const a = await room();
    const b = await room();
    const ids = (await list(a.host.accessToken)).body.sessions.map((x: any) => x.id);
    expect(ids.indexOf(b.id)).toBeLessThan(ids.indexOf(a.id));
  });

  test("最後の動きから 6 時間より前のセッションは出ない", async () => {
    const me = await s.guest("Old");
    const old = new Date(Date.now() - 7 * 60 * 60 * 1000);
    const [row] = await s.db
      .insert(gameSessions)
      .values({ hostUserId: me.user.id, mapId: "m", createdAt: old })
      .returning();
    const ids = (await list(me.accessToken)).body.sessions.map((x: any) => x.id);
    expect(ids).not.toContain(row!.id);
  });
});
