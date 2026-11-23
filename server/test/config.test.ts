import { describe, expect, test } from "bun:test";
import { loadConfig } from "../src/config.ts";

describe("loadConfig", () => {
  test("既定のポートは 8080", () => {
    expect(loadConfig({ JWT_SECRET: "s" }).port).toBe(8080);
  });

  test.each(["abc", "-1", "65536", "1.5"])("PORT=%s は弾く", (port) => {
    expect(() => loadConfig({ PORT: port, JWT_SECRET: "s" })).toThrow();
  });

  test("本番では JWT_SECRET が必須", () => {
    expect(() => loadConfig({ NODE_ENV: "production" })).toThrow("JWT_SECRET");
  });

  test("開発時は JWT_SECRET が無くても起動ごとに別の値を作る", () => {
    const a = loadConfig({}).jwtSecret;
    const b = loadConfig({}).jwtSecret;
    expect(a.length).toBeGreaterThan(32);
    expect(a).not.toBe(b);
  });

  test("空の DATABASE_URL / PGLITE_DIR は未指定扱い", () => {
    const c = loadConfig({ JWT_SECRET: "s", DATABASE_URL: "", PGLITE_DIR: "" });
    expect(c.databaseUrl).toBeUndefined();
    expect(c.pgliteDir).toBeUndefined();
  });
});

describe("S3 設定", () => {
  test("S3_* がすべて無ければ undefined（アップロードURL発行は無効）", () => {
    expect(loadConfig({ JWT_SECRET: "x" }).s3).toBeUndefined();
  });

  test("一部だけ設定されていると起動時に失敗する", () => {
    expect(() => loadConfig({ JWT_SECRET: "x", S3_BUCKET: "b" })).toThrow("S3_ENDPOINT");
  });

  test("すべてそろっていれば設定として読む", () => {
    const env = { JWT_SECRET: "x", S3_ENDPOINT: "https://e", S3_BUCKET: "b", S3_ACCESS_KEY_ID: "k", S3_SECRET_ACCESS_KEY: "s" };
    expect(loadConfig(env).s3?.bucket).toBe("b");
  });
});
