import { afterAll, describe, expect, test } from "bun:test";
import { and, eq } from "drizzle-orm";
import { gameObjects, gamePlayers } from "../src/db/schema.ts";
import { CAPTURE_SCORE, SURVIVE_SCORE } from "../src/game/rules.ts";
import { fakeGlb, pose, startServer } from "./helpers.ts";

const s = await startServer();
afterAll(s.stop);

/** ホスト（ドロ）とケイの2人。roles まで渡せば準備を始める */
async function room(opts: { start?: boolean; preparingSeconds?: number; playingSeconds?: number } = {}) {
  const host = await s.guest("Hider");
  const seeker = await s.guest("Seeker");
  const created = await s.post("/v1/sessions", { mapId: "bibai-museum" }, host.accessToken);
  const joined = await s.post(`/v1/sessions/${created.id}/join`, {}, seeker.accessToken);
  const r = {
    id: created.id as string,
    host,
    seeker,
    hiderId: created.playerId as string,
    seekerId: joined.playerId as string,
    call: (method: string, path: string, body?: unknown, who: "host" | "seeker" = "host") =>
      s.request(method, `/v1/sessions/${created.id}${path}`, body, (who === "host" ? host : seeker).accessToken),
    roles() {
      return [
        { playerId: this.hiderId, role: "hider" },
        { playerId: this.seekerId, role: "seeker" },
      ];
    },
  };
  if (opts.start) {
    const res = await r.call("POST", "/rounds/start", {
      preparingSeconds: opts.preparingSeconds ?? 60,
      playingSeconds: opts.playingSeconds,
      roles: r.roles(),
    });
    expect(res.status).toBe(200);
  }
  return r;
}

/** GLB を上げてオブジェクトを置く */
async function place(r: Awaited<ReturnType<typeof room>>, x = 0) {
  const assetKey = await s.uploadGlb(r.host.accessToken, r.id);
  const res = await r.call("POST", "/objects", { assetKey, transform: pose(x) });
  expect(res.status).toBe(201);
  return res.body as { id: string; assetKey: string; round: number };
}

describe("ラウンドの進行", () => {
  test("start → play → finish で状態と役割が変わり、全員に phase_changed と snapshot が届く", async () => {
    const r = await room();
    const h = await s.authed(r.host.accessToken, r.id);
    const k = await s.authed(r.seeker.accessToken, r.id);

    const started = await r.call("POST", "/rounds/start", { preparingSeconds: 60, roles: r.roles() });
    expect(started.status).toBe(200);
    expect(started.body.status).toBe("preparing");
    expect(started.body.round).toBe(1);
    expect(started.body.players.find((p: any) => p.id === r.seekerId).role).toBe("seeker");
    const ends = Date.parse(started.body.phaseEndsAt) - Date.now();
    expect(ends).toBeGreaterThan(55_000);

    const ph = await k.c.next((m) => m.type === "session.phase_changed");
    expect(ph.payload).toMatchObject({ status: "preparing", round: 1 });
    expect(ph.payload.roles).toContainEqual({ playerId: r.hiderId, role: "hider" });
    const snap = await k.c.next((m) => m.type === "session.snapshot");
    expect(snap.payload.you.role).toBe("seeker");
    expect(snap.seq).toBe(ph.seq);
    await h.c.next((m) => m.type === "session.phase_changed");

    const played = await r.call("POST", "/rounds/play", { playingSeconds: 90 });
    expect(played.body.status).toBe("playing");
    expect((await k.c.next((m) => m.type === "session.phase_changed" && m.payload.status === "playing")).round).toBe(1);

    const finished = await r.call("POST", "/rounds/finish", {});
    expect(finished.body.status).toBe("finished");
    expect(finished.body.phaseEndsAt).toBeNull();
    const fin = await h.c.next((m) => m.type === "session.phase_changed" && m.payload.status === "finished");
    expect(fin.payload.result).toMatchObject({ reason: "host", captured: false, score: SURVIVE_SCORE });

    // 次のラウンドは round + 1、seq は振り出し
    const next = await r.call("POST", "/rounds/start", { preparingSeconds: 60, roles: r.roles() });
    expect(next.body.round).toBe(2);
    const ph2 = await k.c.next((m) => m.type === "session.phase_changed" && m.round === 2);
    expect(ph2.seq).toBe(1);
    h.c.ws.close();
    k.c.ws.close();
  });

  test("ホスト以外は 403、同じ遷移を2回すると 409", async () => {
    const r = await room();
    const bySeeker = await r.call("POST", "/rounds/start", { preparingSeconds: 5, roles: r.roles() }, "seeker");
    expect(bySeeker.status).toBe(403);
    expect(bySeeker.body.error.code).toBe("NOT_HOST");

    const [a, b] = await Promise.all([
      r.call("POST", "/rounds/start", { preparingSeconds: 5, roles: r.roles() }),
      r.call("POST", "/rounds/start", { preparingSeconds: 5, roles: r.roles() }),
    ]);
    expect([a.status, b.status].sort()).toEqual([200, 409]);
    expect((await r.call("POST", "/rounds/finish", {})).status).toBe(200);
    expect((await r.call("POST", "/rounds/play", {})).status).toBe(409);
  });

  test("役割の検証：ドロがいない・知らない参加者・重複は 400", async () => {
    const r = await room();
    const noHider = await r.call("POST", "/rounds/start", {
      preparingSeconds: 5,
      roles: [{ playerId: r.seekerId, role: "seeker" }],
    });
    expect(noHider.body.error.code).toBe("NO_HIDER");
    const unknown = await r.call("POST", "/rounds/start", {
      preparingSeconds: 5,
      roles: [{ playerId: crypto.randomUUID(), role: "hider" }],
    });
    expect(unknown.body.error.code).toBe("UNKNOWN_PLAYER");
    const dup = await r.call("POST", "/rounds/start", {
      preparingSeconds: 5,
      roles: [
        { playerId: r.hiderId, role: "hider" },
        { playerId: r.hiderId, role: "seeker" },
      ],
    });
    expect(dup.body.error.code).toBe("DUPLICATE_PLAYER");
  });

  test("期限が来るとサーバーが自分で本番へ進め、本番の期限で終わる（見つからなかったドロに得点）", async () => {
    const r = await room({ start: true, preparingSeconds: 1, playingSeconds: 1 });
    const k = await s.authed(r.seeker.accessToken, r.id);
    await k.c.next((m) => m.type === "session.phase_changed" && m.payload.status === "playing", 3000);
    const fin = await k.c.next((m) => m.type === "session.phase_changed" && m.payload.status === "finished", 3000);
    expect(fin.payload.result.reason).toBe("time_up");
    expect(fin.payload.result.captured).toBe(false);
    const detail = await r.call("GET", "");
    expect(detail.body.status).toBe("finished");
    expect(detail.body.players.find((p: any) => p.id === r.hiderId).score).toBe(SURVIVE_SCORE);
    k.c.ws.close();
  }, 8000);
});

describe("アセット", () => {
  test("署名 URL に PUT した GLB を、オブジェクトの assetUrl でそのまま取り出せる", async () => {
    const r = await room({ start: true });
    const glb = fakeGlb(100);
    const key = await s.uploadGlb(r.host.accessToken, r.id, glb);
    expect(key).toMatch(new RegExp(`^sessions/${r.id}/assets/[0-9a-f-]{36}\\.glb$`));
    await r.call("POST", "/objects", { assetKey: key, transform: pose(0) });
    await r.call("POST", "/rounds/play", {});

    const state = await r.call("GET", "/state", undefined, "seeker");
    const url = state.body.objects[0].assetUrl as string;
    const got = await fetch(url);
    expect(got.headers.get("content-type")).toBe("model/gltf-binary");
    expect([...new Uint8Array(await got.arrayBuffer())]).toEqual([...glb]);

    // 署名を壊すと 403
    expect((await fetch(url.replace(/sig=[^&]+/, "sig=x"))).status).toBe(403);
  });

  test("アップロードしていない・大きさの違う GLB ではオブジェクトを作れない", async () => {
    const r = await room({ start: true });
    const up = await s.request(
      "POST",
      "/v1/assets/uploads",
      { sessionId: r.id, contentType: "model/gltf-binary", sizeBytes: 64 },
      r.host.accessToken,
    );
    const create = () => r.call("POST", "/objects", { assetKey: up.body.assetKey, transform: pose(0) });
    expect((await create()).body.error.code).toBe("ASSET_NOT_UPLOADED");
    await fetch(up.body.uploadUrl, { method: "PUT", headers: { "content-type": "model/gltf-binary" }, body: fakeGlb(64) });
    expect((await create()).status).toBe(201);
  });

  test("申告と違う大きさ・形式、同じ URL での上書き、参加者でない人は弾く", async () => {
    const r = await room();
    const up = await s.request(
      "POST",
      "/v1/assets/uploads",
      { sessionId: r.id, contentType: "model/gltf-binary", sizeBytes: 64 },
      r.host.accessToken,
    );
    const put = (body: Uint8Array, type = "model/gltf-binary") =>
      fetch(up.body.uploadUrl, { method: "PUT", headers: { "content-type": type }, body });
    expect((await put(fakeGlb(63))).status).toBe(400);
    expect((await put(fakeGlb(64), "image/png")).status).toBe(400);
    expect((await put(new Uint8Array(64))).status).toBe(400); // 先頭が glTF でない
    expect((await put(fakeGlb(64))).status).toBe(201);
    expect((await put(fakeGlb(64))).status).toBe(409);

    const stranger = await s.guest("X");
    const denied = await s.request(
      "POST",
      "/v1/assets/uploads",
      { sessionId: r.id, contentType: "model/gltf-binary", sizeBytes: 64 },
      stranger.accessToken,
    );
    expect(denied.status).toBe(403);
    const tooBig = await s.request(
      "POST",
      "/v1/assets/uploads",
      { sessionId: r.id, contentType: "model/gltf-binary", sizeBytes: 1e9 },
      r.host.accessToken,
    );
    expect(tooBig.status).toBe(400);
  });
});

describe("オブジェクト", () => {
  test("準備中はドロにだけ配り、本番になるとケイにも見える（擬態先・作成者は見せない）", async () => {
    const r = await room({ start: true });
    const h = await s.authed(r.host.accessToken, r.id);
    const k = await s.authed(r.seeker.accessToken, r.id);
    const o = await place(r, 1);
    expect(o.round).toBe(1);
    expect((await r.call("POST", `/objects/${o.id}/disguise`)).body.isDisguise).toBe(true);

    const created = await h.c.next((m) => m.type === "object.created");
    expect(created.payload).toMatchObject({ objectId: o.id, ownerPlayerId: r.hiderId, isDisguise: false });
    await Bun.sleep(50);
    expect(k.c.inbox.some((m) => m.type.startsWith("object."))).toBe(false);
    const prepState = await r.call("GET", "/state", undefined, "seeker");
    expect(prepState.body.objects).toEqual([]);

    await r.call("POST", "/rounds/play", {});
    const snap = await k.c.next((m) => m.type === "session.snapshot" && m.payload.session.status === "playing");
    expect(snap.payload.objects).toEqual([
      { objectId: o.id, assetKey: o.assetKey, assetUrl: expect.any(String), transform: pose(1) },
    ]);
    const playState = await r.call("GET", "/state", undefined, "seeker");
    expect(playState.body.objects[0].isDisguise).toBeUndefined();
    expect(playState.body.objects[0].ownerPlayerId).toBeUndefined();

    // 本番中の PATCH・DELETE はケイにも届く
    const patched = await r.call("PATCH", `/objects/${o.id}`, { transform: pose(3) });
    expect(patched.status).toBe(200);
    const upd = await k.c.next((m) => m.type === "object.updated");
    expect(upd.payload).toEqual({ objectId: o.id, assetKey: o.assetKey, assetUrl: expect.any(String), transform: pose(3) });
    expect((await r.call("DELETE", `/objects/${o.id}`)).status).toBe(204);
    expect((await k.c.next((m) => m.type === "object.deleted")).payload).toEqual({ objectId: o.id });
    h.c.ws.close();
    k.c.ws.close();
  });

  test("ケイ・待機中・他人のアセット・他人のオブジェクトは弾く", async () => {
    const r = await room();
    const key = await s.uploadGlb(r.host.accessToken, r.id);
    const waiting = await r.call("POST", "/objects", { assetKey: key, transform: pose(0) });
    expect(waiting.body.error.code).toBe("INVALID_GAME_PHASE");

    await r.call("POST", "/rounds/start", { preparingSeconds: 60, roles: r.roles() });
    const bySeeker = await r.call("POST", "/objects", { assetKey: key, transform: pose(0) }, "seeker");
    expect(bySeeker.body.error.code).toBe("FORBIDDEN_ROLE");
    const notUploaded = await r.call("POST", "/objects", {
      assetKey: `sessions/${r.id}/assets/${crypto.randomUUID()}.glb`,
      transform: pose(0),
    });
    expect(notUploaded.body.error.code).toBe("ASSET_NOT_UPLOADED");
    const seekersAsset = await s.uploadGlb(r.seeker.accessToken, r.id);
    const foreign = await r.call("POST", "/objects", { assetKey: seekersAsset, transform: pose(0) });
    expect(foreign.status).toBe(403);
    const other = await room();
    const otherKey = await s.uploadGlb(other.host.accessToken, other.id);
    expect((await r.call("POST", "/objects", { assetKey: otherKey, transform: pose(0) })).body.error.code).toBe(
      "ASSET_NOT_IN_SESSION",
    );

    // 別のドロのオブジェクトは触れない
    const o = await place(r);
    const third = await s.guest("Hider2");
    const j = await s.post(`/v1/sessions/${r.id}/join`, {}, third.accessToken);
    await r.call("POST", "/rounds/finish", {});
    await r.call("POST", "/rounds/start", {
      preparingSeconds: 60,
      roles: [...r.roles(), { playerId: j.playerId, role: "hider" }],
    });
    // 前のラウンドのオブジェクトは今のラウンドには無い
    expect((await r.call("PATCH", `/objects/${o.id}`, { transform: pose(1) })).body.error.code).toBe("OBJECT_NOT_FOUND");
    const mine = await place(r);
    const byOther = await s.request(
      "PATCH",
      `/v1/sessions/${r.id}/objects/${mine.id}`,
      { transform: pose(2) },
      third.accessToken,
    );
    expect(byOther.body.error.code).toBe("NOT_OBJECT_OWNER");
  });

  test("擬態先は1人1つ。切り替えると前の擬態先が外れ、ケイには知らせない", async () => {
    const r = await room({ start: true });
    const a = await place(r, 0);
    const b = await place(r, 1);
    await r.call("POST", `/rounds/play`, {});
    const k = await s.authed(r.seeker.accessToken, r.id);
    await r.call("POST", `/objects/${a.id}/disguise`);
    await r.call("POST", `/objects/${b.id}/disguise`);
    const rows = await s.db
      .select({ id: gameObjects.id })
      .from(gameObjects)
      .where(and(eq(gameObjects.playerId, r.hiderId), eq(gameObjects.isDisguise, true)));
    expect(rows).toEqual([{ id: b.id }]);
    await Bun.sleep(50);
    expect(k.c.inbox.some((m) => m.type === "object.updated")).toBe(false);
    k.c.ws.close();
  });

  test("WS の object.transform：所有ドロだけ。本番中はケイに中継し、requestId に ack を返す", async () => {
    const r = await room({ start: true });
    const o = await place(r);
    const h = await s.authed(r.host.accessToken, r.id);
    const k = await s.authed(r.seeker.accessToken, r.id);
    h.c.send({ type: "object.transform", payload: { objectId: o.id, clientSeq: 1, transform: pose(2), requestId: "a1" } });
    expect((await h.c.next((m) => m.type === "command.ack")).payload).toEqual({ requestId: "a1", command: "object.transform" });
    await Bun.sleep(50);
    expect(k.c.inbox.some((m) => m.type === "object.transform")).toBe(false); // 準備中

    await r.call("POST", "/rounds/play", {});
    h.c.send({ type: "object.transform", payload: { objectId: o.id, clientSeq: 2, transform: pose(4) } });
    const moved = await k.c.next((m) => m.type === "object.transform");
    expect(moved.payload).toEqual({ objectId: o.id, transform: pose(4) });

    k.c.send({ type: "object.transform", payload: { objectId: o.id, clientSeq: 1, transform: pose(0), requestId: "k1" } });
    expect((await k.c.next((m) => m.type === "error")).payload).toMatchObject({ code: "FORBIDDEN_ROLE", requestId: "k1" });

    // 確定しなかった位置も、ラウンド終了時に DB へ書かれる
    await r.call("POST", "/rounds/finish", {});
    const [row] = await s.db.select().from(gameObjects).where(eq(gameObjects.id, o.id));
    expect(row!.transform as unknown).toEqual(pose(4));
    h.c.ws.close();
    k.c.ws.close();
  });
});

describe("捕獲", () => {
  test("外れ → 連打は 429 → 当たりで player.caught、全員見つかればラウンドが終わる", async () => {
    const r = await room({ start: true });
    const decoy = await place(r, 0);
    const real = await place(r, 1);
    await r.call("POST", `/objects/${real.id}/disguise`);
    const early = await r.call("POST", "/captures", { objectId: real.id }, "seeker");
    expect(early.body.error.code).toBe("INVALID_GAME_PHASE");
    await r.call("POST", "/rounds/play", {});
    const h = await s.authed(r.host.accessToken, r.id);

    const byHider = await r.call("POST", "/captures", { objectId: real.id });
    expect(byHider.body.error.code).toBe("FORBIDDEN_ROLE");

    const miss = await r.call("POST", "/captures", { objectId: decoy.id }, "seeker");
    expect(miss.body).toEqual({ result: "miss", scoreDelta: 0, score: 0 });
    const spam = await r.call("POST", "/captures", { objectId: real.id }, "seeker");
    expect(spam.status).toBe(429);
    expect(Number(spam.headers.get("retry-after"))).toBeGreaterThan(0);

    await Bun.sleep(1600);
    const hit = await r.call("POST", "/captures", { objectId: real.id }, "seeker");
    expect(hit.body).toEqual({ result: "caught", scoreDelta: CAPTURE_SCORE, score: CAPTURE_SCORE, caughtPlayerId: r.hiderId });
    const caught = await h.c.next((m) => m.type === "player.caught");
    expect(caught.payload).toMatchObject({ playerId: r.hiderId, byPlayerId: r.seekerId, objectId: real.id });
    const fin = await h.c.next((m) => m.type === "session.phase_changed" && m.payload.status === "finished");
    expect(fin.payload.result).toMatchObject({ reason: "all_caught", captured: true, score: 0 });
    // 終わった後は全員に擬態先を見せる
    const snap = await h.c.next((m) => m.type === "session.snapshot" && m.payload.session.status === "finished");
    expect(snap.payload.objects.find((o: any) => o.objectId === real.id).isDisguise).toBe(true);

    const [p] = await s.db.select().from(gamePlayers).where(eq(gamePlayers.id, r.hiderId));
    expect(p!.isCaught).toBe(true);
    h.c.ws.close();
  }, 8000);

  test("見つかったドロはそのラウンドではもう動かせない", async () => {
    const host = await s.guest("Host");
    const created = await s.post("/v1/sessions", { mapId: "bibai-museum" }, host.accessToken);
    const others = await Promise.all(["H2", "S"].map((n) => s.guest(n)));
    const ids = [created.playerId];
    for (const g of others) ids.push((await s.post(`/v1/sessions/${created.id}/join`, {}, g.accessToken)).playerId);
    const call = (m: string, p: string, b: unknown, t: string) => s.request(m, `/v1/sessions/${created.id}${p}`, b, t);
    await call("POST", "/rounds/start", {
      preparingSeconds: 60,
      roles: [
        { playerId: ids[0], role: "hider" },
        { playerId: ids[1], role: "hider" },
        { playerId: ids[2], role: "seeker" },
      ],
    }, host.accessToken);
    const key = await s.uploadGlb(host.accessToken, created.id);
    const o = (await call("POST", "/objects", { assetKey: key, transform: pose(0) }, host.accessToken)).body;
    await call("POST", `/objects/${o.id}/disguise`, undefined, host.accessToken);
    await call("POST", "/rounds/play", {}, host.accessToken);
    expect((await call("POST", "/captures", { objectId: o.id }, others[1]!.accessToken)).body.result).toBe("caught");

    // もう1人ドロが残っているのでラウンドは続く
    expect((await call("GET", "", undefined, host.accessToken)).body.status).toBe("playing");
    const edit = await call("PATCH", `/objects/${o.id}`, { transform: pose(1) }, host.accessToken);
    expect(edit.body.error.code).toBe("PLAYER_CAUGHT");
  });
});

describe("マップ", () => {
  test("マップ定義と GLB を返す。無いマップは MAP_NOT_FOUND", async () => {
    const g = await s.guest("M");
    const def = await s.request("GET", "/v1/maps/bibai-museum", undefined, g.accessToken);
    expect(def.status).toBe(200);
    expect(def.body).toMatchObject({ mapId: "bibai-museum", transform: pose(0) });
    const glb = await fetch(def.body.modelUrl);
    expect(glb.status).toBe(200);
    expect(new Uint8Array(await glb.arrayBuffer()).slice(0, 4)).toEqual(new Uint8Array([0x67, 0x6c, 0x54, 0x46]));
    const none = await s.request("GET", "/v1/maps/nowhere", undefined, g.accessToken);
    expect(none.body.error.code).toBe("MAP_NOT_FOUND");
    expect((await s.request("GET", "/v1/maps/bibai-museum")).status).toBe(401);
  });
});

describe("参加とリプレイ", () => {
  test("後から参加した人は、繋いでいるホストに snapshot で知らされる", async () => {
    const host = await s.guest("Host");
    const created = await s.post("/v1/sessions", { mapId: "bibai-museum" }, host.accessToken);
    const h = await s.authed(host.accessToken, created.id);
    const late = await s.guest("Late");
    const j = await s.post(`/v1/sessions/${created.id}/join`, {}, late.accessToken);
    const snap = await h.c.next((m) => m.type === "session.snapshot");
    expect(snap.payload.players.map((p: any) => p.playerId)).toContain(j.playerId);
    h.c.ws.close();
  });

  test("ラウンドが終わると、Snapshot と seq 順のイベントログを読める", async () => {
    const r = await room({ start: true });
    expect((await r.call("GET", "/rounds/1/replay")).body.error.code).toBe("ROUND_NOT_FINISHED");
    const o = await place(r);
    await r.call("POST", `/objects/${o.id}/disguise`);
    await r.call("POST", "/rounds/play", {});
    await r.call("POST", "/captures", { objectId: o.id }, "seeker");

    const replay = await r.call("GET", "/rounds/1/replay", undefined, "seeker");
    expect(replay.status).toBe(200);
    const types = replay.body.events.map((e: any) => e.type);
    expect(types).toEqual(
      expect.arrayContaining(["session.phase_changed", "object.created", "object.disguised", "player.caught"]),
    );
    const seqs = replay.body.events.map((e: any) => e.seq);
    expect(seqs).toEqual([...seqs].sort((a: number, b: number) => a - b));
    expect(replay.body.snapshots.length).toBeGreaterThanOrEqual(2);
    expect(replay.body.snapshots.at(-1).objects[0]).toMatchObject({ objectId: o.id, isDisguise: true });
    expect((await r.call("GET", "/rounds/2/replay")).status).toBe(404);
  });
});
