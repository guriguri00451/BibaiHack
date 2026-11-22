import { createRoute, type OpenAPIHono, z } from "@hono/zod-openapi";
import { and, asc, eq } from "drizzle-orm";
import type { Context } from "hono";
import { issueToken, verifyToken } from "../auth/token.ts";
import {
  ASSET_KEY_PATTERN,
  CreateGuestRequestSchema,
  CreateGuestResponseSchema,
  CreateSessionRequestSchema,
  CreateObjectRequestSchema,
  CreateObjectResponseSchema,
  CreateSessionResponseSchema,
  CreateUploadRequestSchema,
  CreateUploadResponseSchema,
  ErrorResponseSchema,
  JoinSessionResponseSchema,
  ObjectParamSchema,
  ObjectResponseSchema,
  SessionDetailSchema,
  SessionIdParamSchema,
  UpdateObjectRequestSchema,
} from "../contracts/rest.ts";
import { normalizeTransform } from "../contracts/transform.ts";
import type { Db } from "../db/client.ts";
import { gameObjects, gamePlayers, gameSessions, users } from "../db/schema.ts";
import { Connections } from "../game/connections.ts";
import { publishObjectEvent, toWireObject } from "../game/objects.ts";
import type { AssetStorage } from "../storage.ts";
import { GameRegistry, type GameState, type PlayerState } from "../game/registry.ts";
import { ApiError } from "./errors.ts";

/** 仕様書 9章: 署名付きアップロードURLには短い期限を設ける */
const UPLOAD_URL_TTL_SECONDS = 300;

export type ApiDeps = {
  db: Db;
  jwtSecret: string;
  /** OpenAPI の servers に載せる URL */
  publicUrl?: string;
  /** WS と共有する。省略時は REST だけの単独利用（配信先なし） */
  registry?: GameRegistry;
  connections?: Connections;
  /** 未設定なら POST /v1/assets/uploads は 503 */
  assetStorage?: AssetStorage;
};

const json = <T extends z.ZodType>(schema: T, description: string) => ({
  content: { "application/json": { schema } },
  description,
});
const errors = (...codes: (400 | 401 | 403 | 404 | 409 | 503)[]) =>
  Object.fromEntries(codes.map((c) => [c, json(ErrorResponseSchema, "Error")]));
const security = [{ bearerAuth: [] }];

async function requireUser(c: Context, deps: ApiDeps): Promise<string> {
  const header = c.req.header("authorization") ?? "";
  const token = header.startsWith("Bearer ") ? header.slice(7) : "";
  const userId = token ? await verifyToken(token, deps.jwtSecret) : null;
  if (!userId) throw new ApiError(401, "UNAUTHORIZED", "A valid access token is required.");
  // 署名が正しくても、ユーザーが DB に無ければ（DB を作り直した後など）認証失敗として扱う
  const [user] = await deps.db.select({ id: users.id }).from(users).where(eq(users.id, userId));
  if (!user) throw new ApiError(401, "UNAUTHORIZED", "The user for this token no longer exists.");
  return userId;
}

export function registerRoutes(app: OpenAPIHono, deps: ApiDeps) {
  const { db } = deps;
  const registry = deps.registry ?? new GameRegistry(db);
  const connections = deps.connections ?? new Connections();

  /** 認証して、参加者として扱えるセッションの状態を返す */
  async function requirePlayer(c: Context, sessionId: string): Promise<{ state: GameState; player: PlayerState }> {
    const userId = await requireUser(c, deps);
    const state = await registry.load(sessionId);
    if (!state) throw new ApiError(404, "SESSION_NOT_FOUND", "Session not found.");
    const player = await registry.findPlayerByUser(state, userId);
    if (!player) throw new ApiError(403, "NOT_A_PARTICIPANT", "You are not a participant of this session.");
    return { state, player };
  }

  /** オブジェクトを作る・変える・消せるのはドロ（役割決定前は誰でも）で、ラウンド終了後は不可 */
  function assertCanEditObjects(state: GameState, player: PlayerState) {
    if (state.status === "finished") throw new ApiError(409, "INVALID_GAME_PHASE", "The round has finished.");
    if (player.role === "seeker") throw new ApiError(403, "FORBIDDEN_ROLE", "Seekers cannot edit objects.");
  }

  /** 他セッションのアセットを自分のセッションのオブジェクトに指せないようにする */
  function assertAssetInSession(assetKey: string, sessionId: string) {
    if (ASSET_KEY_PATTERN.exec(assetKey)?.[1] !== sessionId) {
      throw new ApiError(400, "ASSET_NOT_IN_SESSION", "assetKey belongs to a different session.");
    }
  }

  function findOwnedObject(state: GameState, player: PlayerState, objectId: string) {
    const object = state.objects.get(objectId);
    if (!object) throw new ApiError(404, "OBJECT_NOT_FOUND", "Object not found.");
    if (object.playerId !== player.playerId) {
      throw new ApiError(403, "NOT_OBJECT_OWNER", "You do not own this object.");
    }
    return object;
  }

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
      if (!playerId) {
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

  // ---- POST /v1/sessions/{id}/objects ----
  app.openapi(
    createRoute({
      method: "post",
      path: "/v1/sessions/{id}/objects",
      summary: "オブジェクトを登録する（参加者全員の WS に object.created が流れる）",
      security,
      request: {
        params: SessionIdParamSchema,
        body: { content: { "application/json": { schema: CreateObjectRequestSchema } } },
      },
      responses: { 201: json(CreateObjectResponseSchema, "Created"), ...errors(400, 401, 403, 404, 409) },
    }),
    async (c) => {
      const { id: sessionId } = c.req.valid("param");
      const { assetKey, transform } = c.req.valid("json");
      const { state, player } = await requirePlayer(c, sessionId);
      assertCanEditObjects(state, player);
      assertAssetInSession(assetKey, sessionId);

      const normalized = normalizeTransform(transform);
      const [row] = await db
        .insert(gameObjects)
        .values({ playerId: player.playerId, round: state.round, assetUrl: assetKey, transform: normalized })
        .returning();
      const object = {
        objectId: row!.id,
        playerId: player.playerId,
        assetKey,
        transform: normalized,
        isDisguise: false,
        lastClientSeq: -1,
      };
      state.objects.set(object.objectId, object);
      publishObjectEvent(state, connections, { type: "object.created", payload: toWireObject(object, deps.assetStorage) });
      return c.json({ id: object.objectId, round: state.round }, 201);
    },
  );

  // ---- PATCH /v1/sessions/{id}/objects/{objectId} ----
  app.openapi(
    createRoute({
      method: "patch",
      path: "/v1/sessions/{id}/objects/{objectId}",
      summary: "確定アセット・Transform を更新する（所有者のみ）",
      security,
      request: {
        params: ObjectParamSchema,
        body: { content: { "application/json": { schema: UpdateObjectRequestSchema } } },
      },
      responses: { 200: json(ObjectResponseSchema, "OK"), ...errors(400, 401, 403, 404, 409) },
    }),
    async (c) => {
      const { id: sessionId, objectId } = c.req.valid("param");
      const { assetKey, transform } = c.req.valid("json");
      const { state, player } = await requirePlayer(c, sessionId);
      assertCanEditObjects(state, player);
      const object = findOwnedObject(state, player, objectId);
      if (assetKey !== undefined) assertAssetInSession(assetKey, sessionId);

      const nextTransform = transform ? normalizeTransform(transform) : object.transform;
      await db
        .update(gameObjects)
        .set({ assetUrl: assetKey ?? object.assetKey, transform: nextTransform, updatedAt: new Date() })
        .where(eq(gameObjects.id, objectId));
      object.assetKey = assetKey ?? object.assetKey;
      object.transform = nextTransform;
      publishObjectEvent(state, connections, { type: "object.updated", payload: toWireObject(object, deps.assetStorage) });
      return c.json({ id: objectId, assetKey: object.assetKey, transform: object.transform }, 200);
    },
  );

  // ---- DELETE /v1/sessions/{id}/objects/{objectId} ----
  app.openapi(
    createRoute({
      method: "delete",
      path: "/v1/sessions/{id}/objects/{objectId}",
      summary: "オブジェクトを削除する（所有者のみ）",
      security,
      request: { params: ObjectParamSchema },
      responses: { 204: { description: "Deleted" }, ...errors(401, 403, 404, 409) },
    }),
    async (c) => {
      const { id: sessionId, objectId } = c.req.valid("param");
      const { state, player } = await requirePlayer(c, sessionId);
      assertCanEditObjects(state, player);
      findOwnedObject(state, player, objectId);

      await db.delete(gameObjects).where(eq(gameObjects.id, objectId));
      state.objects.delete(objectId);
      publishObjectEvent(state, connections, { type: "object.deleted", payload: { objectId } });
      return c.body(null, 204);
    },
  );

  // ---- POST /v1/assets/uploads ----
  app.openapi(
    createRoute({
      method: "post",
      path: "/v1/assets/uploads",
      summary: "3Dアセットのアップロード用署名URLを取得する",
      security,
      request: { body: { content: { "application/json": { schema: CreateUploadRequestSchema } } } },
      responses: { 201: json(CreateUploadResponseSchema, "Created"), ...errors(400, 401, 403, 404, 503) },
    }),
    async (c) => {
      const { sessionId, contentType } = c.req.valid("json");
      await requirePlayer(c, sessionId);
      if (!deps.assetStorage) {
        throw new ApiError(503, "ASSET_STORAGE_NOT_CONFIGURED", "Asset storage is not configured on this server.");
      }
      // 過去のアセットは上書きしない（仕様書 5.1）ので、毎回新しいキーを発行する
      const assetKey = `sessions/${sessionId}/assets/${crypto.randomUUID()}.glb`;
      const uploadUrl = deps.assetStorage.presignUpload(assetKey, contentType, UPLOAD_URL_TTL_SECONDS);
      return c.json({ assetKey, uploadUrl }, 201);
    },
  );

  app.doc31("/v1/openapi.json", {
    openapi: "3.1.0",
    info: { title: "めっちゃ美唄 Game Server API", version: "0.1.0" },
    servers: [{ url: deps.publicUrl ?? "https://bibai.t0waxx.com" }],
  });
}
