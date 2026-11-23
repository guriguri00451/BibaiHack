import { afterAll, describe, expect, test } from "bun:test";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { openDb } from "../src/db/client.ts";
import { createServer } from "../src/server.ts";
import { UrlSigner } from "../src/storage/signed.ts";
import { FsStorage, isValidKey } from "../src/storage/storage.ts";
import { JWT_SECRET } from "./helpers.ts";

const dirs: string[] = [];
const tempDir = async () => {
  const d = await mkdtemp(join(tmpdir(), "bibai-storage-"));
  dirs.push(d);
  return d;
};
afterAll(async () => {
  for (const d of dirs) await rm(d, { recursive: true, force: true });
});

describe("FsStorage", () => {
  test("putNew は上書きしない。meta・list・get が揃う", async () => {
    const st = new FsStorage(await tempDir());
    const key = "sessions/a/assets/b.glb";
    expect(await st.putNew(key, new Uint8Array([1, 2, 3]), { contentType: "model/gltf-binary", ownerPlayerId: "p" })).toBe(true);
    expect(await st.putNew(key, new Uint8Array([9]), { contentType: "model/gltf-binary" })).toBe(false);
    expect((await st.get(key))!.body).toEqual(new Uint8Array([1, 2, 3]));
    expect(await st.head(key)).toMatchObject({ size: 3, ownerPlayerId: "p" });
    await st.put("sessions/a/rounds/1/events/1-2.jsonl", new Uint8Array([0]), "application/x-ndjson");
    expect(await st.list("sessions/a/")).toEqual([key, "sessions/a/rounds/1/events/1-2.jsonl"]);
    expect(await st.list("sessions/zzz/")).toEqual([]);
  });

  test.each(["../etc/passwd", "a/../../b", "/abs", "a//b", "a/./b", ""])("キー %p は使えない", (key) => {
    expect(isValidKey(key)).toBe(false);
  });
});

describe("UrlSigner", () => {
  test("期限切れ・書き換えた条件は通らない", () => {
    const s = new UrlSigner("k");
    const g = { key: "sessions/a/assets/b.glb", exp: 2_000_000_000, contentType: "model/gltf-binary", size: 10, ownerPlayerId: "p" };
    const q = new URLSearchParams(s.uploadQuery(g));
    expect(s.verifyUpload(g.key, q)).toEqual(g);
    expect(s.verifyUpload(g.key, q, 2_000_000_001)).toBeNull();
    q.set("size", "11");
    expect(s.verifyUpload(g.key, q)).toBeNull();
    expect(new UrlSigner("other").verifyUpload(g.key, new URLSearchParams(s.uploadQuery(g)))).toBeNull();
    const d = new URLSearchParams(s.downloadQuery({ key: g.key, exp: 2_000_000_000 }));
    expect(s.verifyDownload(g.key, d)).toBe(true);
    expect(s.verifyDownload("sessions/a/assets/c.glb", d)).toBe(false);
  });
});

describe("再起動", () => {
  test("進行中のセッションはタイマーが再開し、seq はログの続きから数える", async () => {
    const dir = await tempDir();
    const { db, close } = await openDb({});
    afterAll(close);
    const storage = new FsStorage(dir);
    const call = async (base: string, method: string, path: string, body?: unknown, token?: string) => {
      const res = await fetch(base + path, {
        method,
        headers: { "content-type": "application/json", ...(token ? { authorization: `Bearer ${token}` } : {}) },
        body: body === undefined ? undefined : JSON.stringify(body),
      });
      return (await res.json()) as any;
    };

    // 1台目：準備を始めて、ログを書いてから止める
    const a = createServer({ db, jwtSecret: JWT_SECRET, storage });
    const sa = Bun.serve({ port: 0, fetch: a.fetch, websocket: a.websocket });
    const baseA = `http://localhost:${sa.port}`;
    const g = await call(baseA, "POST", "/v1/guests", { name: "H" });
    const sess = await call(baseA, "POST", "/v1/sessions", { mapId: "bibai-museum" }, g.accessToken);
    await call(baseA, "POST", `/v1/sessions/${sess.id}/rounds/start`, {
      preparingSeconds: 1,
      playingSeconds: 60,
      roles: [{ playerId: sess.playerId, role: "hider" }],
    }, g.accessToken);
    await a.dispose();
    sa.stop(true);

    // 2台目：同じ DB・保存先で起動し直す
    const b = createServer({ db, jwtSecret: JWT_SECRET, storage });
    expect(await b.registry.resumeActive()).toBe(1);
    const state = (await b.registry.load(sess.id))!;
    expect(state.seq).toBeGreaterThanOrEqual(1);
    const seqBefore = state.seq;
    await Bun.sleep(1300);
    expect(state.status).toBe("playing");
    expect(state.seq).toBeGreaterThan(seqBefore);
    await b.dispose();
  }, 6000);
});
