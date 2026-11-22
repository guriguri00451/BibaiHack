import { createRoute, type OpenAPIHono, z } from "@hono/zod-openapi";
import { and, asc, eq } from "drizzle-orm";
import type { Context } from "hono";
import { issueToken, verifyToken } from "../auth/token.ts";
import {
  CreateGuestRequestSchema,
  CreateGuestResponseSchema,
  CreateSessionRequestSchema,
  CreateSessionResponseSchema,
  ErrorResponseSchema,
  JoinSessionResponseSchema,
  SessionDetailSchema,
  SessionIdParamSchema,
} from "../contracts/rest.ts";
import type { Db } from "../db/client.ts";
import { gamePlayers, gameSessions, users } from "../db/schema.ts";
import { ApiError } from "./errors.ts";

export type ApiDeps = {
  db: Db;
  jwtSecret: string;
  /** REST で参加者が増えたことをゲーム状態（メモリ）へ伝える */
  onPlayerJoined?: (sessionId: string, player: { playerId: string; name: string }) => void;
};

const json = <T extends z.ZodType>(schema: T, description: string) => ({
  content: { "application/json": { schema } },
  description,
});
const errors = (...codes: (400 | 401 | 403 | 404 | 409)[]) =>
  Object.fromEntries(codes.map((c) => [c, json(ErrorResponseSchema, "Error")]));
const security = [{ bearerAuth: [] }];

async function requireUser(c: Context, deps: ApiDeps): Promise<string> {
  const header = c.req.header("authorization") ?? "";
  const token = header.startsWith("Bearer ") ? header.slice(7) : "";
  const userId = token ? await verifyToken(token, deps.jwtSecret) : null;
  if (!userId) throw new ApiError(401, "UNAUTHORIZED", "A valid access token is required.");
  return userId;
}

export function registerRoutes(app: OpenAPIHono, deps: ApiDeps) {
  const { db } = deps;

  app.openAPIRegistry.registerComponent("securitySchemes", "bearerAuth", {
    type: "http",
    scheme: "bearer",
    bearerFormat: "JWT",
  });

  // ---- POST /v1/guests ----
  app.openapi(
    createRoute({
      method: "post",
      path: "/v1/guests",
      summary: "ゲストを作成してアクセストークンを発行する",
      request: { body: { content: { "application/json": { schema: CreateGuestRequestSchema } } } },
      responses: { 201: json(CreateGuestResponseSchema, "Created"), ...errors(400) },
    }),
    async (c) => {
      const { name } = c.req.valid("json");
      const [user] = await db.insert(users).values({ name }).returning();
      const accessToken = await issueToken(user!.id, deps.jwtSecret);
      return c.json({ user: { id: user!.id, name: user!.name }, accessToken }, 201);
    },
  );

  // ---- POST /v1/sessions ----
  app.openapi(
    createRoute({
      method: "post",
      path: "/v1/sessions",
      summary: "セッションを作成する（作成者は自動で参加する）",
      security,
      request: { body: { content: { "application/json": { schema: CreateSessionRequestSchema } } } },
      responses: { 201: json(CreateSessionResponseSchema, "Created"), ...errors(400, 401) },
    }),
    async (c) => {
      const userId = await requireUser(c, deps);
      const { mapId } = c.req.valid("json");
      const { session, player } = await db.transaction(async (tx) => {
        const [session] = await tx
          .insert(gameSessions)
          .values({ hostUserId: userId, mapId })
          .returning();
        const [player] = await tx
          .insert(gamePlayers)
          .values({ sessionId: session!.id, userId })
          .returning();
        return { session: session!, player: player! };
      });
      return c.json(
        {
          id: session.id,
          status: session.status,
          round: session.round,
          mapId: session.mapId,
          playerId: player.id,
        },
        201,
      );
    },
  );

  // ---- POST /v1/sessions/{id}/join ----
  app.openapi(
    createRoute({
      method: "post",
      path: "/v1/sessions/{id}/join",
      summary: "セッションに参加する（再実行すると既存の playerId を返す）",
      security,
      request: { params: SessionIdParamSchema },
      responses: { 200: json(JoinSessionResponseSchema, "Joined"), ...errors(401, 404) },
    }),
    async (c) => {
      const userId = await requireUser(c, deps);
      const { id: sessionId } = c.req.valid("param");
      const [session] = await db.select().from(gameSessions).where(eq(gameSessions.id, sessionId));
      if (!session) throw new ApiError(404, "SESSION_NOT_FOUND", "Session not found.");

      const inserted = await db
        .insert(gamePlayers)
        .values({ sessionId, userId })
        .onConflictDoNothing({ target: [gamePlayers.sessionId, gamePlayers.userId] })
        .returning();
      let playerId = inserted[0]?.id;
      if (playerId) {
        const [user] = await db.select().from(users).where(eq(users.id, userId));
        deps.onPlayerJoined?.(sessionId, { playerId, name: user?.name ?? "" });
      } else {
        const [existing] = await db
          .select({ id: gamePlayers.id })
          .from(gamePlayers)
          .where(and(eq(gamePlayers.sessionId, sessionId), eq(gamePlayers.userId, userId)));
        playerId = existing!.id;
      }
      return c.json({ sessionId, playerId }, 200);
    },
  );

  // ---- GET /v1/sessions/{id} ----
  app.openapi(
    createRoute({
      method: "get",
      path: "/v1/sessions/{id}",
      summary: "参加者とゲームの基本情報を取得する",
      security,
      request: { params: SessionIdParamSchema },
      responses: { 200: json(SessionDetailSchema, "OK"), ...errors(401, 403, 404) },
    }),
    async (c) => {
      const userId = await requireUser(c, deps);
      const { id: sessionId } = c.req.valid("param");
      const [session] = await db.select().from(gameSessions).where(eq(gameSessions.id, sessionId));
      if (!session) throw new ApiError(404, "SESSION_NOT_FOUND", "Session not found.");

      const players = await db
        .select({
          id: gamePlayers.id,
          userId: gamePlayers.userId,
          name: users.name,
          role: gamePlayers.role,
          score: gamePlayers.score,
          isCaught: gamePlayers.isCaught,
        })
        .from(gamePlayers)
        .innerJoin(users, eq(users.id, gamePlayers.userId))
        .where(eq(gamePlayers.sessionId, sessionId))
        .orderBy(asc(gamePlayers.createdAt));
      if (!players.some((p) => p.userId === userId)) {
        throw new ApiError(403, "NOT_A_PARTICIPANT", "You are not a participant of this session.");
      }

      return c.json(
        {
          id: session.id,
          hostUserId: session.hostUserId,
          status: session.status,
          round: session.round,
          mapId: session.mapId,
          phaseEndsAt: session.phaseEndsAt?.toISOString() ?? null,
          createdAt: session.createdAt.toISOString(),
          players,
        },
        200,
      );
    },
  );

  app.doc31("/v1/openapi.json", {
    openapi: "3.1.0",
    info: { title: "めっちゃ美唄 Game Server API", version: "0.1.0" },
    servers: [{ url: "https://bibai.t0waxx.com" }],
  });
}
