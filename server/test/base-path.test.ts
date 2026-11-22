import { afterAll, describe, expect, test } from "bun:test";
import { loadConfig } from "../src/config.ts";
import { startServer } from "./helpers.ts";

describe("BASE_PATH", () => {
  test("末尾の / を外し、形の悪い値は拒否する", () => {
    expect(loadConfig({ JWT_SECRET: "x", BASE_PATH: "/a-kun-01/" }).basePath).toBe("/a-kun-01");
    expect(loadConfig({ JWT_SECRET: "x" }).basePath).toBe("");
    expect(() => loadConfig({ JWT_SECRET: "x", BASE_PATH: "a-kun-01" })).toThrow();
    expect(() => loadConfig({ JWT_SECRET: "x", BASE_PATH: "/A_KUN" })).toThrow();
  });

  test("PUBLIC_URL に前置きを足して OpenAPI の servers に使う", () => {
    const c = loadConfig({ JWT_SECRET: "x", BASE_PATH: "/stg", PUBLIC_URL: "https://bibai.t0waxx.com/" });
    expect(c.publicUrl).toBe("https://bibai.t0waxx.com/stg");
  });
});

describe("前置き付きで起動したサーバー", async () => {
  const s = await startServer({ basePath: "/a-kun-01" });
  afterAll(s.stop);

  test("前置きの下で REST が動き、前置き無しは 404", async () => {
    expect((await fetch(`${s.base}/healthz`)).status).toBe(200);
    const root = s.base.replace("/a-kun-01", "");
    expect((await fetch(`${root}/healthz`)).status).toBe(404);
    expect((await fetch(`${root}/a-kun-01x/healthz`)).status).toBe(404);
    const g = await s.guest("BP");
    expect(g.user.name).toBe("BP");
  });

  test("前置きの下で WebSocket が動く", async () => {
    const host = await s.guest("H");
    const session = await s.post("/v1/sessions", { mapId: "m" }, host.accessToken);
    const { c, snapshot } = await s.authed(host.accessToken, session.id);
    expect(snapshot.payload.you.playerId).toBe(session.playerId);
    c.ws.close();
  });
});
