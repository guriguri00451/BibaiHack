import { expect, test } from "bun:test";
import { createApp } from "../src/http/app.ts";

test("GET /healthz は 200 を返す", async () => {
  const res = await createApp().request("/healthz");
  expect(res.status).toBe(200);
  expect(await res.json()).toEqual({ ok: true });
});

test("存在しないパスは仕様書の形式で 404 を返す", async () => {
  const res = await createApp().request("/nope");
  expect(res.status).toBe(404);
  expect(await res.json()).toEqual({ error: { code: "NOT_FOUND", message: "Not found." } });
});
