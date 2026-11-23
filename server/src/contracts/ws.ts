import { z } from "@hono/zod-openapi";
import { RoleSchema, SessionStatusSchema } from "./rest.ts";
import { TransformSchema } from "./transform.ts";

/** WebSocket のメッセージ。仕様書 6章 */

/** 付けておくと、成功時に command.ack、失敗時に同じ requestId 付きの error が返る */
const requestId = z.string().min(1).max(64).optional();

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
    requestId,
  }),
});

/** 所有ドロがオブジェクトを動かしている途中の Transform。確定は REST の PATCH で行う */
export const ObjectTransformInSchema = z.object({
  type: z.literal("object.transform"),
  payload: z.object({
    objectId: z.uuid(),
    /** オブジェクトごとに数える */
    clientSeq: z.int().min(0),
    transform: TransformSchema,
    requestId,
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
  score: z.int(),
  isCaught: z.boolean(),
  connected: z.boolean(),
  /** 受け手に見せてよい場合だけ入る。まだ届いていなければ null */
  transform: TransformSchema.nullable(),
});

/**
 * 受け手に見せてよいオブジェクト。
 * ケイには objectId・assetKey・transform だけ。ドロと、ラウンド終了後の全員には作成者と擬態先も見せる
 */
export const PublicObjectSchema = z
  .object({
    objectId: z.uuid(),
    /** 永続キー。クライアントのキャッシュキー・差し替え判定に使う（署名は毎回変わるため assetUrl は使えない） */
    assetKey: z.string(),
    /** そのまま GET できる期限つき署名 URL（1時間） */
    assetUrl: z.url().optional(),
    transform: TransformSchema,
    ownerPlayerId: z.uuid().optional(),
    isDisguise: z.boolean().optional(),
  })
  .openapi("PublicObject");

export const SnapshotPayloadSchema = z
  .object({
    session: z.object({
      id: z.uuid(),
      status: SessionStatusSchema,
      round: z.int().min(1),
      mapId: z.string(),
      hostPlayerId: z.uuid().nullable(),
      phaseEndsAt: z.iso.datetime().nullable(),
    }),
    you: z.object({ playerId: z.uuid(), role: RoleSchema.nullable() }),
    players: z.array(SnapshotPlayerSchema),
    objects: z.array(PublicObjectSchema),
  })
  .openapi("SessionSnapshotPayload");

export const SessionSnapshotSchema = z.object({
  type: z.literal("session.snapshot"),
  ...ordered,
  payload: SnapshotPayloadSchema,
});

/** GET /v1/sessions/{id}/state（WS の session.snapshot と同じ公開範囲） */
export const SessionStateResponseSchema = z
  .object({ ...ordered, ...SnapshotPayloadSchema.shape })
  .openapi("SessionState");

export const FINISH_REASONS = ["time_up", "all_caught", "host"] as const;

export const PhaseChangedSchema = z.object({
  type: z.literal("session.phase_changed"),
  ...ordered,
  payload: z.object({
    status: SessionStatusSchema,
    round: z.int().min(1),
    phaseEndsAt: z.iso.datetime().nullable(),
    roles: z.array(z.object({ playerId: z.uuid(), role: RoleSchema.nullable() })),
    /** finished のときだけ。captured: ドロなら自分が見つかったか、ケイならドロを全員見つけたか */
    result: z
      .object({
        reason: z.enum(FINISH_REASONS),
        captured: z.boolean(),
        score: z.int(),
        scores: z.array(z.object({ playerId: z.uuid(), score: z.int() })),
      })
      .optional(),
  }),
});

export const PlayerTransformOutSchema = z.object({
  type: z.literal("player.transform"),
  ...ordered,
  payload: z.object({ playerId: z.uuid(), transform: TransformSchema }),
});

export const ObjectTransformOutSchema = z.object({
  type: z.literal("object.transform"),
  ...ordered,
  payload: z.object({ objectId: z.uuid(), transform: TransformSchema }),
});

export const ObjectCreatedSchema = z.object({
  type: z.literal("object.created"),
  ...ordered,
  payload: PublicObjectSchema,
});

export const ObjectUpdatedSchema = z.object({
  type: z.literal("object.updated"),
  ...ordered,
  payload: PublicObjectSchema,
});

export const ObjectDeletedSchema = z.object({
  type: z.literal("object.deleted"),
  ...ordered,
  payload: z.object({ objectId: z.uuid() }),
});

export const PlayerCaughtSchema = z.object({
  type: z.literal("player.caught"),
  ...ordered,
  payload: z.object({
    /** 見つかったドロ */
    playerId: z.uuid(),
    /** 見つけたケイ */
    byPlayerId: z.uuid(),
    objectId: z.uuid(),
    /** 見つかったドロの累計得点 */
    score: z.int(),
    /** 見つけたケイの累計得点 */
    seekerScore: z.int(),
  }),
});

export const CommandAckSchema = z.object({
  type: z.literal("command.ack"),
  timestamp: z.int(),
  payload: z.object({ requestId: z.string(), command: z.string() }),
});

export const ErrorMessageSchema = z.object({
  type: z.literal("error"),
  timestamp: z.int(),
  payload: z.object({ code: z.string(), message: z.string(), requestId: z.string().optional() }),
});

export const ServerMessageSchema = z.discriminatedUnion("type", [
  SessionSnapshotSchema,
  PhaseChangedSchema,
  PlayerTransformOutSchema,
  ObjectTransformOutSchema,
  ObjectCreatedSchema,
  ObjectUpdatedSchema,
  ObjectDeletedSchema,
  PlayerCaughtSchema,
  CommandAckSchema,
  ErrorMessageSchema,
]);
export type ServerMessage = z.infer<typeof ServerMessageSchema>;
export type PublicObject = z.infer<typeof PublicObjectSchema>;
export type SnapshotPayload = z.infer<typeof SnapshotPayloadSchema>;
export type FinishReason = (typeof FINISH_REASONS)[number];

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
