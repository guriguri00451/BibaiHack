import { describe, expect, test } from "bun:test";
import { sign } from "hono/jwt";
import { issueToken, verifyToken } from "../src/auth/token.ts";

const secret = "test-secret";
const now = () => Math.floor(Date.now() / 1000);

describe("token", () => {
  test("発行したトークンから userId が戻る", async () => {
    const t = await issueToken("user-1", secret);
    expect(await verifyToken(t, secret)).toBe("user-1");
  });

  test("別の鍵で署名されたものは null", async () => {
    const t = await issueToken("user-1", "other");
    expect(await verifyToken(t, secret)).toBeNull();
  });

  test("期限切れは null", async () => {
    const t = await sign({ sub: "user-1", exp: now() - 10 }, secret, "HS256");
    expect(await verifyToken(t, secret)).toBeNull();
  });

  test("sub が無いものは null", async () => {
    const t = await sign({ exp: now() + 60 }, secret, "HS256");
    expect(await verifyToken(t, secret)).toBeNull();
  });

  test("壊れた文字列は null", async () => {
    expect(await verifyToken("not.a.jwt", secret)).toBeNull();
    expect(await verifyToken("", secret)).toBeNull();
  });
});
