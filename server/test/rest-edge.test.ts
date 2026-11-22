import { afterAll, describe, expect, test } from "bun:test";
import { eq } from "drizzle-orm";
import { issueToken } from "../src/auth/token.ts";
import { gamePlayers } from "../src/db/schema.ts";
import { JWT_SECRET, startServer } from "./helpers.ts";

const s = await startServer();
afterAll(s.stop);

const ISO = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?Z$/;

describe("REST の境界", () => {
  test("名前は前後の空白を落として保存する", async () => {
    const g = await s.guest("  Alice  ");
    expect(g.user.name).toBe("Alice");
  });

  test("名前は32文字まで、33文字は 400", async () => {
    expect((await s.request("POST", "/v1/guests", { name: "a".repeat(32) })).status).toBe(201);
    const r = await s.request("POST", "/v1/guests", { name: "a".repeat(33) });
    expect(r.status).toBe(400);
    expect(r.body.error.code).toBe("INVALID_REQUEST");
  });

  test("空白だけの名前は 400", async () => {
    expect((await s.request("POST", "/v1/guests", { name: "   " })).status).toBe(400);
  });

  test("mapId は64文字まで", async () => {
    const g = await s.guest("M");
    expect((await s.request("POST", "/v1/sessions", { mapId: "m".repeat(65) }, g.accessToken)).status).toBe(400);
  });

  test("壊れた JSON は 400 と仕様書の形式", async () => {
    const res = await fetch(`${s.base}/v1/guests`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: "{not json",
    });
    expect(res.status).toBe(400);
    const body = (await res.json()) as any;
    expect(typeof body.error?.code).toBe("string");
  });

  test("Bearer の付かない Authorization は 401", async () => {
    const g = await s.guest("NB");
    const res = await fetch(`${s.base}/v1/sessions`, {
      method: "POST",
      headers: { "content-type": "application/json", authorization: g.accessToken },
      body: JSON.stringify({ mapId: "m" }),
    });
    expect(res.status).toBe(401);
  });

  test("存在しないユーザーのトークンでセッションを作ると 401", async () => {
    const token = await issueToken("00000000-0000-4000-8000-000000000000", JWT_SECRET);
    const r = await s.request("POST", "/v1/sessions", { mapId: "m" }, token);
    expect(r.status).toBe(401);
  });

  test("同時に5回 join しても参加者は1人", async () => {
    const host = await s.guest("H");
    const p = await s.guest("P");
    const session = await s.post("/v1/sessions", { mapId: "m" }, host.accessToken);
    const results = await Promise.all(
      Array.from({ length: 5 }, () => s.request("POST", `/v1/sessions/${session.id}/join`, undefined, p.accessToken)),
    );
    expect(results.every((r) => r.status === 200)).toBe(true);
    expect(new Set(results.map((r) => r.body.playerId)).size).toBe(1);
    const rows = await s.db.select().from(gamePlayers).where(eq(gamePlayers.sessionId, session.id));
    expect(rows.length).toBe(2);
  });

  test("GET の詳細は ISO の時刻・role null・score 0", async () => {
    const host = await s.guest("H2");
    const session = await s.post("/v1/sessions", { mapId: "bibai-museum" }, host.accessToken);
    const r = await s.request("GET", `/v1/sessions/${session.id}`, undefined, host.accessToken);
    expect(r.status).toBe(200);
    expect(r.body.createdAt).toMatch(ISO);
    expect(r.body.phaseEndsAt).toBeNull();
    expect(r.body.players).toEqual([
      { id: session.playerId, userId: host.user.id, name: "H2", role: null, score: 0, isCaught: false },
    ]);
  });

  test("不正な UUID のセッション ID は 400", async () => {
    const g = await s.guest("U");
    expect((await s.request("GET", "/v1/sessions/not-a-uuid", undefined, g.accessToken)).status).toBe(400);
  });
});
