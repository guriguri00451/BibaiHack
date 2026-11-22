import { z } from "@hono/zod-openapi";

/**
 * 共有マップ座標系（右手系・Y-up・メートル）。仕様書 3.2。
 * 値域はチートや壊れた値を弾くための目安で、マップの大きさが決まったら見直す。
 */
export const POSITION_LIMIT_M = 10_000;
export const SCALE_MIN = 0.001;
export const SCALE_MAX = 1_000;
/** これより外れた長さのクォータニオンは「正規化されていない」として拒否する */
export const QUATERNION_TOLERANCE = 0.02;

const coord = z.number().min(-POSITION_LIMIT_M).max(POSITION_LIMIT_M);
const scaleComponent = z.number().min(SCALE_MIN).max(SCALE_MAX);
const quatComponent = z.number().min(-1 - QUATERNION_TOLERANCE).max(1 + QUATERNION_TOLERANCE);

export const Vec3Schema = z.tuple([coord, coord, coord]);

export const QuaternionSchema = z
  .tuple([quatComponent, quatComponent, quatComponent, quatComponent])
  .refine((q) => Math.abs(Math.hypot(...q) - 1) <= QUATERNION_TOLERANCE, {
    message: "rotation must be a normalized quaternion (x, y, z, w)",
  });

export const TransformSchema = z
  .object({
    position: Vec3Schema.describe("メートル (x, y, z)"),
    rotation: QuaternionSchema.describe("正規化したクォータニオン (x, y, z, w)"),
    scale: z.tuple([scaleComponent, scaleComponent, scaleComponent]).describe("(x, y, z)"),
  })
  .openapi("Transform");

export type Transform = z.infer<typeof TransformSchema>;

/** 検証を通った Transform の回転を、長さちょうど 1 に揃える（誤差の蓄積を防ぐ） */
export function normalizeTransform(t: Transform): Transform {
  const n = Math.hypot(...t.rotation);
  const [x, y, z, w] = t.rotation;
  return { ...t, rotation: [x / n, y / n, z / n, w / n] };
}
