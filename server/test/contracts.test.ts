import { describe, expect, test } from "bun:test";
import { ClientMessageSchema } from "../src/contracts/ws.ts";
import { normalizeTransform, TransformSchema } from "../src/contracts/transform.ts";

const identity = { position: [1, 0, 2], rotation: [0, 0.7071, 0, 0.7071], scale: [1, 1, 1] };

describe("Transform", () => {
  test("仕様書の例は通る", () => {
    expect(TransformSchema.safeParse(identity).success).toBe(true);
  });

  test.each([
    ["正規化されていない回転", { ...identity, rotation: [0, 0, 0, 2] }],
    ["ゼロの回転", { ...identity, rotation: [0, 0, 0, 0] }],
    ["NaN の位置", { ...identity, position: [Number.NaN, 0, 0] }],
    ["Infinity の位置", { ...identity, position: [Infinity, 0, 0] }],
    ["範囲外の位置", { ...identity, position: [0, 0, 1e6] }],
    ["0 のスケール", { ...identity, scale: [1, 0, 1] }],
    ["負のスケール", { ...identity, scale: [1, -1, 1] }],
    ["要素数が違う", { ...identity, position: [0, 0] }],
  ])("%s は弾く", (_, t) => {
    expect(TransformSchema.safeParse(t).success).toBe(false);
  });

  test("normalizeTransform は回転の長さを 1 に揃える", () => {
    const t = TransformSchema.parse({ ...identity, rotation: [0, 0, 0, 1.01] });
    expect(normalizeTransform(t).rotation).toEqual([0, 0, 0, 1]);
  });
});

describe("ClientMessage", () => {
  test("仕様書の auth メッセージは通る", () => {
    const msg = {
      type: "auth",
      payload: { accessToken: "t", sessionId: "6f1c1c1e-1d2b-4c3a-9e4f-0a1b2c3d4e5f" },
    };
    expect(ClientMessageSchema.safeParse(msg).success).toBe(true);
  });

  test("未知の type は弾く", () => {
    expect(ClientMessageSchema.safeParse({ type: "nope", payload: {} }).success).toBe(false);
  });
});
