import { z } from "@hono/zod-openapi";
import { RoleSchema, SessionStatusSchema } from "./rest.ts";
import { TransformSchema } from "./transform.ts";

/**
 * WebSocket のメッセージ。仕様書 6章。
 * いま実装しているものだけを載せ、機能を足すたびにここへ追加する。
 */

// ---- Client → Server ----

export const AuthMessageSchema = z.object({
  type: z.literal("auth"),
  payload: z.object({ accessToken: z.string(), sessionId: z.uuid() }),
});

export const PlayerTransformInSchema = z.object({
  type: z.literal("player.transform"),
  payload: z.object({
    /** 送信者側の重複・逆順検出用。サーバーの seq とは別物 */
    clientSeq: z.int().min(0),
    transform: TransformSchema,
  }),
});

export const ObjectTransformInSchema = z.object({
  type: z.literal("object.transform"),
  payload: z.object({
    objectId: z.uuid(),
    clientSeq: z.int().min(0),
    transform: TransformSchema,
  }),
});

export const ClientMessageSchema = z.discriminatedUnion("type", [
  AuthMessageSchema,
  PlayerTransformInSchema,
  ObjectTransformInSchema,
]);
export type ClientMessage = z.infer<typeof ClientMessageSchema>;

// ---- Server → Client ----

/** サーバーが確定順に配るメッセージに付く。seq はラウンド内の連番 */
const ordered = {
  round: z.int().min(1),
  seq: z.int().min(0),
  timestamp: z.int().describe("Unix ms（サーバー時刻）"),
};

export const SnapshotPlayerSchema = z.object({
  playerId: z.uuid(),
  name: z.string(),
  role: RoleSchema.nullable(),
  isCaught: z.boolean(),
  connected: z.boolean(),
  /** 受け手に見せてよい場合だけ入る。まだ届いていなければ null */
  transform: TransformSchema.nullable(),
});

/** ケイにも見せてよい形。所有者・擬態フラグは含めない（仕様書 6.2-6） */
export const SnapshotObjectSchema = z.object({
  objectId: z.uuid(),
  /** 永続キー。クライアントのキャッシュキー・差し替え判定に使う（署名は毎回変わるため assetUrl は使えない） */
  assetKey: z.string(),
  /** そのまま GET できる期限つき署名URL。ストレージ未設定の環境では付かない */
  assetUrl: z.url().optional(),
  transform: TransformSchema,
});

export const SessionSnapshotSchema = z.object({
  type: z.literal("session.snapshot"),
  ...ordered,
  payload: z.object({
    session: z.object({
      id: z.uuid(),
      status: SessionStatusSchema,
      round: z.int().min(1),
      phaseEndsAt: z.iso.datetime().nullable(),
    }),
    you: z.object({ playerId: z.uuid(), role: RoleSchema.nullable() }),
    players: z.array(SnapshotPlayerSchema),
    /** 見せてよいオブジェクトだけ。準備中のケイには空 */
    objects: z.array(SnapshotObjectSchema),
  }),
});

export const PlayerTransformOutSchema = z.object({
  type: z.literal("player.transform"),
  ...ordered,
  payload: z.object({ playerId: z.uuid(), transform: TransformSchema }),
});

export const ObjectUpsertOutSchema = z.object({
  type: z.enum(["object.created", "object.updated"]),
  ...ordered,
  payload: SnapshotObjectSchema,
});

export const ObjectDeletedOutSchema = z.object({
  type: z.literal("object.deleted"),
  ...ordered,
  payload: z.object({ objectId: z.uuid() }),
});

export const ObjectTransformOutSchema = z.object({
  type: z.literal("object.transform"),
  ...ordered,
  payload: z.object({ objectId: z.uuid(), transform: TransformSchema }),
});

export const ErrorMessageSchema = z.object({
  type: z.literal("error"),
  timestamp: z.int(),
  payload: z.object({ code: z.string(), message: z.string() }),
});

export const ServerMessageSchema = z.discriminatedUnion("type", [
  SessionSnapshotSchema,
  PlayerTransformOutSchema,
  ObjectUpsertOutSchema,
  ObjectDeletedOutSchema,
  ObjectTransformOutSchema,
  ErrorMessageSchema,
]);
export type ServerMessage = z.infer<typeof ServerMessageSchema>;

/** 接続を閉じるときのコード（4000番台はアプリ定義） */
export const WS_CLOSE = {
  /** auth が届かない・トークンが不正 */
  UNAUTHORIZED: 4001,
  /** セッションの参加者ではない */
  FORBIDDEN: 4003,
  /** セッションが存在しない */
  NOT_FOUND: 4004,
  /** 同じプレイヤーが別の接続で入り直した */
  REPLACED: 4009,
} as const;
