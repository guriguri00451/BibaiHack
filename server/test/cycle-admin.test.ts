import { afterAll, describe, expect, test } from "bun:test";
import { fakeGlb, startServer } from "./helpers.ts";

const ADMIN_TOKEN = "admin-token-for-tests";
const s = await startServer({ admin: { token: ADMIN_TOKEN, open: false } });
afterAll(s.stop);

async function room() {
  const host = await s.guest("Hider");
  const seeker = await s.guest("Seeker");
  const created = await s.post("/v1/sessions", { mapId: "bibai-museum" }, host.accessToken);
  const joined = await s.post(`/v1/sessions/${created.id}/join`, {}, seeker.accessToken);
  return {
    id: created.id as string,
    host,
    seeker,
    hiderId: created.playerId as string,
    seekerId: joined.playerId as string,
    call: (method: string, path: string, body?: unknown, who: "host" | "seeker" = "host") =>
      s.request(method, `/v1/sessions/${created.id}${path}`, body, (who === "host" ? host : seeker).accessToken),
  };
}

const admin = (method: string, path: string, body?: unknown, token = ADMIN_TOKEN) =>
  s.request(method, `/v1/admin${path}`, body, token);

/** multipart で管理画面のマップ登録を送る */
async function uploadMap(fields: Record<string, string>, glb?: Uint8Array, token = ADMIN_TOKEN) {
  const form = new FormData();
  for (const [k, v] of Object.entries(fields)) form.set(k, v);
  if (glb) form.set("model", new Blob([glb as Uint8Array<ArrayBuffer>], { type: "model/gltf-binary" }), "map.glb");
  const res = await fetch(`${s.base}/v1/admin/maps`, { method: "POST", headers: { authorization: `Bearer ${token}` }, body: form });
  return { status: res.status, body: (await res.json()) as any };
}

describe("ラウンドのサイクル", () => {
  test("resultSeconds を付けると、終わってからその秒数で次のラウンドを同じ役割で始める（後から入った人はケイ）", async () => {
    const r = await room();
    const k = await s.authed(r.seeker.accessToken, r.id);
    const started = await r.call("POST", "/rounds/start", {
      preparingSeconds: 1,
      playingSeconds: 1,
      resultSeconds: 1,
      roles: [
        { playerId: r.hiderId, role: "hider" },
        { playerId: r.seekerId, role: "seeker" },
      ],
    });
    expect(started.body.cycle).toEqual({ resultSeconds: 1 });

    const late = await s.guest("Late");
    const lateJoined = await s.post(`/v1/sessions/${r.id}/join`, {}, late.accessToken);

    const fin = await k.c.next((m) => m.type === "session.phase_changed" && m.payload.status === "finished", 4000);
    // finished の phaseEndsAt は次のラウンドが始まる時刻
    expect(fin.payload.phaseEndsAt).not.toBeNull();
    const next = await k.c.next(
      (m) => m.type === "session.phase_changed" && m.payload.status === "preparing" && m.payload.round === 2,
      4000,
    );
    expect(next.payload.roles).toContainEqual({ playerId: r.hiderId, role: "hider" });
    expect(next.payload.roles).toContainEqual({ playerId: r.seekerId, role: "seeker" });
    expect(next.payload.roles).toContainEqual({ playerId: lateJoined.playerId, role: "seeker" });

    // 止めると、終わった後は待ったまま（phaseEndsAt も外れる）
    const stopped = await r.call("POST", "/rounds/stop-cycle");
    expect(stopped.status).toBe(200);
    expect(stopped.body.cycle).toBeNull();
    const fin2 = await k.c.next((m) => m.type === "session.phase_changed" && m.payload.status === "finished", 4000);
    expect(fin2.payload.phaseEndsAt).toBeNull();
    await Bun.sleep(1500);
    expect((await r.call("GET", "")).body).toMatchObject({ status: "finished", round: 2 });
    k.c.ws.close();
  }, 15000);

  test("誰も繋いでいなければ、次のラウンドを始めずにサイクルを止める", async () => {
    const r = await room();
    await r.call("POST", "/rounds/start", {
      preparingSeconds: 60,
      resultSeconds: 1,
      roles: [{ playerId: r.hiderId, role: "hider" }],
    });
    await r.call("POST", "/rounds/finish", {});
    await Bun.sleep(1500);
    expect((await r.call("GET", "")).body).toMatchObject({ status: "finished", round: 1, cycle: null, phaseEndsAt: null });
  });

  test("resultSeconds を付けなければ今までどおり（終わったら待つ）。ケイは stop-cycle できない", async () => {
    const r = await room();
    const started = await r.call("POST", "/rounds/start", {
      preparingSeconds: 60,
      roles: [{ playerId: r.hiderId, role: "hider" }],
    });
    expect(started.body.cycle).toBeNull();
    expect((await r.call("POST", "/rounds/stop-cycle", undefined, "seeker")).status).toBe(403);
    const finished = await r.call("POST", "/rounds/finish", {});
    expect(finished.body.phaseEndsAt).toBeNull();
  });
});

describe("マップ", () => {
  test("登録したマップは一覧に出て、ホストが待機中に選べ、GLB を取れる", async () => {
    const glb = fakeGlb(128);
    const up = await uploadMap(
      { mapId: "bibai-station", name: "美唄駅", transform: JSON.stringify({ position: [1, 0, 0], rotation: [0, 0, 0, 1], scale: [2, 2, 2] }) },
      glb,
    );
    expect(up.status).toBe(201);
    expect(up.body.map).toMatchObject({ mapId: "bibai-station", name: "美唄駅", source: "registered", modelSize: 128 });

    const r = await room();
    const list = await s.request("GET", "/v1/maps", undefined, r.host.accessToken);
    expect(list.status).toBe(200);
    const ids = list.body.maps.map((m: any) => m.mapId);
    expect(ids).toContain("bibai-museum");
    expect(ids).toContain("bibai-station");

    const h = await s.authed(r.host.accessToken, r.id);
    expect((await r.call("PUT", "/map", { mapId: "bibai-station" }, "seeker")).status).toBe(403);
    expect((await r.call("PUT", "/map", { mapId: "nowhere" })).body.error.code).toBe("MAP_NOT_FOUND");
    const changed = await r.call("PUT", "/map", { mapId: "bibai-station" });
    expect(changed.status).toBe(200);
    expect(changed.body.mapId).toBe("bibai-station");
    const snap = await h.c.next((m) => m.type === "session.snapshot" && m.payload.session.mapId === "bibai-station");
    expect(snap.payload.session.mapId).toBe("bibai-station");

    const def = await s.request("GET", "/v1/maps/bibai-station", undefined, r.host.accessToken);
    expect(def.body.transform.scale).toEqual([2, 2, 2]);
    const got = await fetch(def.body.modelUrl);
    expect(got.status).toBe(200);
    expect([...new Uint8Array(await got.arrayBuffer())]).toEqual([...glb]);

    // 準備中は替えられない
    await r.call("POST", "/rounds/start", { preparingSeconds: 60, roles: [{ playerId: r.hiderId, role: "hider" }] });
    expect((await r.call("PUT", "/map", { mapId: "bibai-museum" })).status).toBe(409);
    h.c.ws.close();
  });

  test("GLB を差し替えると modelUrl が変わる。組み込みのマップは変えられない。消すと一覧から消える", async () => {
    const first = await uploadMap({ mapId: "swap-me", name: "A" }, fakeGlb(64));
    await Bun.sleep(5);
    const second = await uploadMap({ mapId: "swap-me" }, fakeGlb(96));
    expect(second.status).toBe(201);
    expect(second.body.map.name).toBe("A");
    expect(second.body.map.modelSize).toBe(96);
    expect(second.body.map.modelUrl).not.toBe(first.body.map.modelUrl);

    const renamed = await admin("PATCH", "/maps/swap-me", { name: "B" });
    expect(renamed.body.map.name).toBe("B");

    expect((await uploadMap({ mapId: "bibai-museum" }, fakeGlb())).body.error.code).toBe("BUILTIN_MAP");
    expect((await uploadMap({ mapId: "Bad_ID" }, fakeGlb())).body.error.code).toBe("INVALID_MAP_ID");
    expect((await uploadMap({ mapId: "no-model" })).body.error.code).toBe("MODEL_REQUIRED");
    expect((await uploadMap({ mapId: "not-glb" }, new Uint8Array(64))).body.error.code).toBe("NOT_GLB");

    expect((await admin("DELETE", "/maps/swap-me")).status).toBe(204);
    expect((await admin("DELETE", "/maps/bibai-museum")).status).toBe(409);
    const list = await admin("GET", "/maps");
    expect(list.body.maps.map((m: any) => m.mapId)).not.toContain("swap-me");
  });
});

describe("管理画面", () => {
  test("トークンが無い・違うと 401。画面の HTML は誰でも取れる", async () => {
    expect((await admin("GET", "/sessions", undefined, "")).status).toBe(401);
    expect((await admin("GET", "/sessions", undefined, "wrong-token-wrong-token")).status).toBe(401);
    const page = await fetch(`${s.base}/admin`);
    expect(page.status).toBe(200);
    expect(page.headers.get("content-type")).toContain("text/html");
  });

  test("セッションの一覧を見て、ホストでなくてもフェーズを進め、マップを替えられる", async () => {
    const r = await room();
    const list = await admin("GET", "/sessions");
    const row = list.body.sessions.find((x: any) => x.id === r.id);
    expect(row).toMatchObject({ status: "waiting", round: 1, mapId: "bibai-museum" });
    expect(row.players.map((p: any) => p.name)).toEqual(["Hider", "Seeker"]);

    // cycle が無ければ、ホストがドロ・ほかはケイで始める
    const started = await admin("POST", `/sessions/${r.id}/start`);
    expect(started.body.status).toBe("preparing");
    expect(started.body.players.find((p: any) => p.id === r.hiderId).role).toBe("hider");
    expect(started.body.players.find((p: any) => p.id === r.seekerId).role).toBe("seeker");
    expect((await admin("POST", `/sessions/${r.id}/play`)).body.status).toBe("playing");
    expect((await admin("POST", `/sessions/${r.id}/finish`)).body.status).toBe("finished");
    expect((await admin("PUT", `/sessions/${r.id}/map`, { mapId: "bibai-museum" })).status).toBe(200);
    expect((await admin("POST", `/sessions/${r.id}/play`)).status).toBe(409);
    expect((await admin("POST", "/sessions/not-a-uuid/play")).status).toBe(404);
  });
});

describe("管理画面を閉じているとき", () => {
  test("ADMIN_TOKEN が無い本番では /v1/admin は 404", async () => {
    const closed = await startServer({ admin: { open: false } });
    try {
      expect((await closed.request("GET", "/v1/admin/sessions")).status).toBe(404);
    } finally {
      await closed.stop();
    }
  });
});
