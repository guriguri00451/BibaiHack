import { z } from "@hono/zod-openapi";

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
