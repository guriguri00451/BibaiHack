import { afterAll, describe, expect, test } from "bun:test";
import { eq } from "drizzle-orm";
import { gamePlayers, gameSessions } from "../src/db/schema.ts";
import { pose, startServer } from "./helpers.ts";

const t = await startServer({
  assetStorage: { presignUpload: (key, type, ttl) => `https://storage.example.com/${key}?type=${type}&ttl=${ttl}`,
    presignDownload: (key, ttl) => `https://storage.example.com/${key}?get&ttl=${ttl}`,
  },
});
afterAll(() => t.stop());

const assetKey = (sessionId: string, name = "a") => `sessions/${sessionId}/assets/${name}.glb`;

/**
 * ホスト(hider)とゲスト(seeker)のセッションを作る。役割とフェーズは DB に直接入れる
 * （ラウンド開始 API を通した流れは game.test.ts。オブジェクトを置けるのは準備中・本番中だけ）
 */
async function setup(status: "waiting" | "preparing" | "playing" | "finished" = "preparing") {
  const host = await t.guest("Hider");
  const guest = await t.guest("Seeker");
  const session = await t.post("/v1/sessions", { mapId: "bibai-museum" }, host.accessToken);
  const joined = await t.post(`/v1/sessions/${session.id}/join`, {}, guest.accessToken);
  await t.db.update(gameSessions).set({ status }).where(eq(gameSessions.id, session.id));
  await t.db.update(gamePlayers).set({ role: "hider" }).where(eq(gamePlayers.id, session.playerId));
  await t.db.update(gamePlayers).set({ role: "seeker" }).where(eq(gamePlayers.id, joined.playerId));
  return { host, guest, sessionId: session.id as string };
}

describe("オブジェクト REST", () => {
  test("ドロが登録すると 201 で id と round が返り、snapshot にも載る", async () => {
    const s = await setup();
    const res = await t.request(
      "POST",
      `/v1/sessions/${s.sessionId}/objects`,
      { assetKey: assetKey(s.sessionId), transform: pose(1) },
      s.host.accessToken,
    );
    expect(res.status).toBe(201);
    expect(res.body.round).toBe(1);

    // ドロ自身には作成者・擬態先も見せる（ケイに見せない形は WS 配信のテスト）
    const { snapshot } = await t.authed(s.host.accessToken, s.sessionId);
    expect(snapshot.payload.objects).toEqual([
      {
        objectId: res.body.id,
        assetKey: assetKey(s.sessionId),
        assetUrl: `https://storage.example.com/${assetKey(s.sessionId)}?get&ttl=3600`,
        transform: pose(1),
        ownerPlayerId: snapshot.payload.you.playerId,
        isDisguise: false,
      },
    ]);
  });

  test("ケイは登録できない（403 FORBIDDEN_ROLE）", async () => {
    const s = await setup();
    const res = await t.request(
      "POST",
      `/v1/sessions/${s.sessionId}/objects`,
      { assetKey: assetKey(s.sessionId), transform: pose(0) },
      s.guest.accessToken,
    );
    expect([res.status, res.body.error.code]).toEqual([403, "FORBIDDEN_ROLE"]);
  });

  test("別セッションの assetKey は 400", async () => {
    const s = await setup();
    const other = await setup();
    const res = await t.request(
      "POST",
      `/v1/sessions/${s.sessionId}/objects`,
      { assetKey: assetKey(other.sessionId), transform: pose(0) },
      s.host.accessToken,
    );
    expect([res.status, res.body.error.code]).toEqual([400, "ASSET_NOT_IN_SESSION"]);
  });

  test("ラウンド終了後は登録できない（409）", async () => {
    const s = await setup("finished");
    const res = await t.request(
      "POST",
      `/v1/sessions/${s.sessionId}/objects`,
      { assetKey: assetKey(s.sessionId), transform: pose(0) },
      s.host.accessToken,
    );
    expect(res.status).toBe(409);
  });

  test("所有者以外の PATCH は 403、存在しない id は 404", async () => {
    const s = await setup();
    const other = await t.guest("Other");
    const joined = await t.post(`/v1/sessions/${s.sessionId}/join`, {}, other.accessToken);
    // 別のドロ（ケイだと所有者を見る前に FORBIDDEN_ROLE になる）
    await t.db.update(gamePlayers).set({ role: "hider" }).where(eq(gamePlayers.id, joined.playerId));
    const created = await t.post(
      `/v1/sessions/${s.sessionId}/objects`,
      { assetKey: assetKey(s.sessionId), transform: pose(0) },
      s.host.accessToken,
    );
    const stranger = await t.request(
      "PATCH",
      `/v1/sessions/${s.sessionId}/objects/${created.id}`,
      { transform: pose(3) },
      other.accessToken,
    );
    const missing = await t.request(
      "PATCH",
      `/v1/sessions/${s.sessionId}/objects/${crypto.randomUUID()}`,
      { transform: pose(3) },
      s.host.accessToken,
    );
    expect([stranger.body.error.code, missing.status]).toEqual(["NOT_OBJECT_OWNER", 404]);
  });
});

describe("オブジェクトの WS 配信", () => {
  test("登録・更新・削除が他の参加者に created / updated / deleted で届き、所有者情報は含まれない", async () => {
    const s = await setup("playing");
    const seeker = await t.authed(s.guest.accessToken, s.sessionId);
    const url = `/v1/sessions/${s.sessionId}/objects`;

    const created = await t.post(url, { assetKey: assetKey(s.sessionId), transform: pose(1) }, s.host.accessToken);
    const gotCreated = await seeker.c.next((m) => m.type === "object.created");
    await t.request("PATCH", `${url}/${created.id}`, { transform: pose(2) }, s.host.accessToken);
    const gotUpdated = await seeker.c.next((m) => m.type === "object.updated");
    await t.request("DELETE", `${url}/${created.id}`, undefined, s.host.accessToken);
    const gotDeleted = await seeker.c.next((m) => m.type === "object.deleted");

    expect(gotCreated.payload).toEqual({
      objectId: created.id,
      assetKey: assetKey(s.sessionId),
      assetUrl: `https://storage.example.com/${assetKey(s.sessionId)}?get&ttl=3600`,
      transform: pose(1),
    });
    expect(gotUpdated.payload.transform).toEqual(pose(2));
    expect(gotDeleted.payload).toEqual({ objectId: created.id });
    expect([gotCreated.seq, gotUpdated.seq, gotDeleted.seq]).toEqual([1, 2, 3]);
    seeker.c.ws.close();
  });

  test("準備中はケイに object.* を送らず、snapshot の objects も空", async () => {
    const s = await setup("preparing");
    const seeker = await t.authed(s.guest.accessToken, s.sessionId);
    await t.post(
      `/v1/sessions/${s.sessionId}/objects`,
      { assetKey: assetKey(s.sessionId), transform: pose(1) },
      s.host.accessToken,
    );
    await Bun.sleep(100);

    const reconnected = await t.authed(s.guest.accessToken, s.sessionId);
    expect([seeker.c.inbox.some((m) => m.type.startsWith("object.")), reconnected.snapshot.payload.objects]).toEqual([
      false,
      [],
    ]);
    reconnected.c.ws.close();
  });

  test("object.transform は所有者から他の参加者へだけ中継され、古い clientSeq は捨てる", async () => {
    const s = await setup("playing");
    const created = await t.post(
      `/v1/sessions/${s.sessionId}/objects`,
      { assetKey: assetKey(s.sessionId), transform: pose(0) },
      s.host.accessToken,
    );
    const owner = await t.authed(s.host.accessToken, s.sessionId);
    const seeker = await t.authed(s.guest.accessToken, s.sessionId);
    const send = (c: typeof owner.c, clientSeq: number, x: number) =>
      c.send({ type: "object.transform", payload: { objectId: created.id, clientSeq, transform: pose(x) } });

    send(seeker.c, 1, 99); // 所有者でない → 無視
    send(owner.c, 5, 5);
    send(owner.c, 4, 4); // 古い → 無視
    send(owner.c, 6, 6);
    const first = await seeker.c.next((m) => m.type === "object.transform");
    const second = await seeker.c.next((m) => m.type === "object.transform");
    await Bun.sleep(50);

    expect([first.payload.transform.position[0], second.payload.transform.position[0]]).toEqual([5, 6]);
    expect(owner.c.inbox.some((m) => m.type === "object.transform")).toBe(false);
    owner.c.ws.close();
    seeker.c.ws.close();
  });
});

describe("POST /v1/assets/uploads", () => {
  const body = (sessionId: string, extra: object = {}) => ({
    sessionId,
    contentType: "model/gltf-binary",
    sizeBytes: 1024,
    ...extra,
  });

  test("参加者には新しい assetKey と期限つきの uploadUrl を返し、毎回別のキーになる", async () => {
    const s = await setup();
    const first = await t.request("POST", "/v1/assets/uploads", body(s.sessionId), s.guest.accessToken);
    const second = await t.request("POST", "/v1/assets/uploads", body(s.sessionId), s.guest.accessToken);

    expect(first.status).toBe(201);
    expect(first.body.assetKey).toMatch(new RegExp(`^sessions/${s.sessionId}/assets/[0-9a-f-]{36}\\.glb$`));
    expect(first.body.uploadUrl).toContain(first.body.assetKey);
    expect(second.body.assetKey).not.toBe(first.body.assetKey);
  });

  test("発行した assetKey はそのままオブジェクト登録に使える", async () => {
    const s = await setup();
    const upload = await t.request("POST", "/v1/assets/uploads", body(s.sessionId), s.host.accessToken);
    const res = await t.request(
      "POST",
      `/v1/sessions/${s.sessionId}/objects`,
      { assetKey: upload.body.assetKey, transform: pose(0) },
      s.host.accessToken,
    );
    expect(res.status).toBe(201);
  });

  test("セッションの参加者でなければ 403", async () => {
    const s = await setup();
    const outsider = await t.guest("Outsider");
    const res = await t.request("POST", "/v1/assets/uploads", body(s.sessionId), outsider.accessToken);
    expect(res.status).toBe(403);
  });

  test.each([
    ["GLB 以外の contentType", { contentType: "image/png" }],
    ["サイズ上限超え", { sizeBytes: 20 * 1024 * 1024 + 1 }],
    ["サイズ 0", { sizeBytes: 0 }],
  ])("%s は 400", async (_name, extra) => {
    const s = await setup();
    const res = await t.request("POST", "/v1/assets/uploads", body(s.sessionId, extra), s.host.accessToken);
    expect(res.status).toBe(400);
  });
});
