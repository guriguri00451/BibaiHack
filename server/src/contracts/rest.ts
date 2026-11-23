import { z } from "@hono/zod-openapi";
import { ASSET_CONTENT_TYPE, DEFAULT_PLAYING_SECONDS, MAX_ASSET_BYTES, MAX_PHASE_SECONDS } from "../game/rules.ts";
import { TransformSchema } from "./transform.ts";

export const ROLES = ["hider", "seeker"] as const;
export const SESSION_STATUSES = ["waiting", "preparing", "playing", "finished"] as const;

export const RoleSchema = z.enum(ROLES).openapi("Role");
export const SessionStatusSchema = z.enum(SESSION_STATUSES).openapi("SessionStatus");

export const ErrorResponseSchema = z
  .object({
    error: z.object({
      code: z.string().openapi({ example: "INVALID_GAME_PHASE" }),
      message: z.string(),
    }),
  })
  .openapi("ErrorResponse");

export const SessionIdParamSchema = z.object({
  id: z.uuid().openapi({ param: { name: "id", in: "path" } }),
});

// POST /v1/guests
export const CreateGuestRequestSchema = z
  .object({ name: z.string().trim().min(1).max(32).openapi({ example: "Player1" }) })
  .openapi("CreateGuestRequest");

export const UserSchema = z.object({ id: z.uuid(), name: z.string() }).openapi("User");

export const CreateGuestResponseSchema = z
  .object({ user: UserSchema, accessToken: z.string() })
  .openapi("CreateGuestResponse");

// POST /v1/sessions
export const CreateSessionRequestSchema = z
  .object({ mapId: z.string().trim().min(1).max(64).openapi({ example: "bibai-museum" }) })
  .openapi("CreateSessionRequest");

export const CreateSessionResponseSchema = z
  .object({
    id: z.uuid(),
    status: SessionStatusSchema,
    round: z.int().min(1),
    mapId: z.string(),
    playerId: z.uuid().describe("作成者自身の playerId（作成者は自動で参加する）"),
  })
  .openapi("CreateSessionResponse");

// POST /v1/sessions/{id}/join
export const JoinSessionResponseSchema = z
  .object({ sessionId: z.uuid(), playerId: z.uuid() })
  .openapi("JoinSessionResponse");

// GET /v1/sessions/{id}
export const PlayerSchema = z
  .object({
    id: z.uuid().describe("playerId"),
    userId: z.uuid(),
    name: z.string(),
    role: RoleSchema.nullable().describe("ラウンド開始前は null"),
    score: z.int(),
    isCaught: z.boolean(),
  })
  .openapi("Player");

export const SessionDetailSchema = z
  .object({
    id: z.uuid(),
    hostUserId: z.uuid(),
    status: SessionStatusSchema,
    round: z.int().min(1),
    mapId: z.string(),
    phaseEndsAt: z.iso.datetime().nullable(),
    createdAt: z.iso.datetime(),
    players: z.array(PlayerSchema),
  })
  .openapi("SessionDetail");

// ---- ラウンド（仕様書 4章・5.1） ----

const phaseSeconds = z.int().min(1).max(MAX_PHASE_SECONDS);

export const StartRoundRequestSchema = z
  .object({
    preparingSeconds: phaseSeconds.openapi({ example: 120 }),
    playingSeconds: phaseSeconds
      .optional()
      .describe(`準備の期限が来てサーバーが自動で本番に入るときの長さ。省くと ${DEFAULT_PLAYING_SECONDS} 秒`),
    roles: z
      .array(z.object({ playerId: z.uuid(), role: RoleSchema }))
      .min(1)
      .describe("載せなかった参加者は役割なし（null）になる。ドロが1人以上必要"),
  })
  .openapi("StartRoundRequest");

export const PlayRoundRequestSchema = z
  .object({
    playingSeconds: phaseSeconds
      .optional()
      .openapi({ example: 180 })
      .describe("省くと rounds/start の playingSeconds（それも無ければ既定値）"),
  })
  .openapi("PlayRoundRequest");

export const FinishRoundRequestSchema = z.object({}).openapi("FinishRoundRequest");

// ---- アセット（仕様書 5.1） ----

/** 仕様書 5.1 の assetKey。所属セッションはキーの中に入っている */
export const ASSET_KEY_PATTERN = /^sessions\/([0-9a-f-]{36})\/assets\/[A-Za-z0-9._-]{1,128}\.glb$/;

export const AssetKeySchema = z
  .string()
  .regex(ASSET_KEY_PATTERN, "assetKey must look like sessions/<session-id>/assets/<name>.glb")
  .openapi({ example: "sessions/00000000-0000-4000-8000-000000000000/assets/a.glb" });

export const CreateAssetUploadRequestSchema = z
  .object({
    sessionId: z.uuid(),
    contentType: z.literal(ASSET_CONTENT_TYPE),
    sizeBytes: z.int().min(1).max(MAX_ASSET_BYTES).openapi({ example: 204800 }),
  })
  .openapi("CreateAssetUploadRequest");

export const CreateAssetUploadResponseSchema = z
  .object({
    assetKey: AssetKeySchema,
    uploadUrl: z.url().describe("この URL へ本体を PUT する。Content-Type と大きさは申告どおりにする"),
    method: z.literal("PUT"),
    headers: z.record(z.string(), z.string()),
    expiresAt: z.iso.datetime(),
  })
  .openapi("CreateAssetUploadResponse");

// ---- オブジェクト（仕様書 5.1） ----

export const ObjectIdParamSchema = z.object({
  id: z.uuid().openapi({ param: { name: "id", in: "path" } }),
  objectId: z.uuid().openapi({ param: { name: "objectId", in: "path" } }),
});

export const CreateObjectRequestSchema = z
  .object({ assetKey: AssetKeySchema, transform: TransformSchema })
  .openapi("CreateObjectRequest");

export const UpdateObjectRequestSchema = z
  .object({ assetKey: AssetKeySchema.optional(), transform: TransformSchema.optional() })
  .refine((b) => b.assetKey !== undefined || b.transform !== undefined, {
    message: "assetKey か transform のどちらかが必要です",
  })
  .openapi("UpdateObjectRequest");

/** 所有ドロに返すオブジェクト */
export const GameObjectSchema = z
  .object({
    id: z.uuid(),
    round: z.int().min(1),
    assetKey: z.string(),
    transform: TransformSchema,
    isDisguise: z.boolean(),
    createdAt: z.iso.datetime(),
    updatedAt: z.iso.datetime(),
  })
  .openapi("GameObject");

// ---- 捕獲（仕様書 5.1） ----

export const CaptureRequestSchema = z.object({ objectId: z.uuid() }).openapi("CaptureRequest");

export const CaptureResponseSchema = z
  .object({
    result: z.enum(["caught", "miss"]),
    scoreDelta: z.int(),
    score: z.int().describe("このケイの累計得点"),
    caughtPlayerId: z.uuid().optional().describe("caught のとき、見つかったドロ"),
  })
  .openapi("CaptureResponse");

// ---- マップ ----

export const MapIdParamSchema = z.object({
  mapId: z
    .string()
    .regex(/^[a-z0-9-]{1,64}$/)
    .openapi({ param: { name: "mapId", in: "path" }, example: "bibai-museum" }),
});

export const MapDefinitionSchema = z
  .object({
    mapId: z.string(),
    name: z.string(),
    modelUrl: z.url().describe("マップの GLB（認証なしで取れる）"),
    transform: TransformSchema.describe("マップの GLB を共有マップ座標に置く Transform"),
  })
  .openapi("MapDefinition");

// ---- リプレイ（仕様書 7章） ----

export const ReplayParamSchema = z.object({
  id: z.uuid().openapi({ param: { name: "id", in: "path" } }),
  round: z.coerce.number().int().min(1).openapi({ param: { name: "round", in: "path" } }),
});

export const ReplayResponseSchema = z
  .object({
    sessionId: z.uuid(),
    round: z.int(),
    snapshots: z.array(z.record(z.string(), z.unknown())).describe("内部 Snapshot（擬態先を含む）。seq 順"),
    events: z.array(z.record(z.string(), z.unknown())).describe("JSONL のイベント。seq 順"),
  })
  .openapi("Replay");
