import { afterAll, describe, expect, test } from "bun:test";
import { openDb } from "../src/db/client.ts";
import { createApp } from "../src/http/app.ts";

const { db, close } = await openDb({});
afterAll(close);
const app = createApp({ db, jwtSecret: "test-secret" });

async function call(method: string, path: string, body?: unknown, token?: string) {
  const res = await app.request(path, {
    method,
    headers: {
      "content-type": "application/json",
      ...(token ? { authorization: `Bearer ${token}` } : {}),
    },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  return { status: res.status, body: (await res.json()) as any };
}
const guest = async (name: string) => (await call("POST", "/v1/guests", { name })).body;

describe("REST", () => {
  test("ゲスト作成 → セッション作成 → 参加 → 取得", async () => {
    const host = await guest("Host");
    expect(host.user.name).toBe("Host");
    const created = await call("POST", "/v1/sessions", { mapId: "bibai-museum" }, host.accessToken);
    expect(created.status).toBe(201);
    expect(created.body).toMatchObject({ status: "waiting", round: 1, mapId: "bibai-museum" });

    const p2 = await guest("P2");
    const join1 = await call("POST", `/v1/sessions/${created.body.id}/join`, undefined, p2.accessToken);
    const join2 = await call("POST", `/v1/sessions/${created.body.id}/join`, undefined, p2.accessToken);
    expect(join1.status).toBe(200);
    expect(join2.body.playerId).toBe(join1.body.playerId);

    const detail = await call("GET", `/v1/sessions/${created.body.id}`, undefined, p2.accessToken);
    expect(detail.status).toBe(200);
    expect(detail.body.hostUserId).toBe(host.user.id);
    expect(detail.body.players.map((p: any) => p.name)).toEqual(["Host", "P2"]);
    expect(detail.body.players[0].id).toBe(created.body.playerId);
  });

  test("トークンなし・不正トークンは 401", async () => {
    expect((await call("POST", "/v1/sessions", { mapId: "m" })).status).toBe(401);
    expect((await call("POST", "/v1/sessions", { mapId: "m" }, "bad")).status).toBe(401);
  });

  test("参加者でなければ GET は 403、無いセッションは 404", async () => {
    const a = await guest("A");
    const b = await guest("B");
    const s = await call("POST", "/v1/sessions", { mapId: "m" }, a.accessToken);
    expect((await call("GET", `/v1/sessions/${s.body.id}`, undefined, b.accessToken)).status).toBe(403);
    const missing = "00000000-0000-4000-8000-000000000000";
    expect((await call("POST", `/v1/sessions/${missing}/join`, undefined, b.accessToken)).status).toBe(404);
  });

  test("入力が不正なら 400 と INVALID_REQUEST", async () => {
    const r = await call("POST", "/v1/guests", { name: "" });
    expect(r.status).toBe(400);
    expect(r.body.error.code).toBe("INVALID_REQUEST");
  });

  test("OpenAPI を返す", async () => {
    const r = await call("GET", "/v1/openapi.json");
    expect(Object.keys(r.body.paths)).toContain("/v1/sessions/{id}/join");
  });
});
