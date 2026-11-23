import { createRoute, type OpenAPIHono, z } from "@hono/zod-openapi";
import { eq } from "drizzle-orm";
import type { Context } from "hono";
import { issueToken, verifyToken } from "../auth/token.ts";
import {
  AssetDownloadQuerySchema,
  AssetDownloadResponseSchema,
  CaptureRequestSchema,
  CaptureResponseSchema,
  CreateAssetUploadRequestSchema,
  CreateAssetUploadResponseSchema,
  CreateGuestRequestSchema,
  CreateGuestResponseSchema,
  CreateObjectRequestSchema,
  CreateSessionRequestSchema,
  CreateSessionResponseSchema,
  ErrorResponseSchema,
  FinishRoundRequestSchema,
  GameObjectSchema,
  JoinSessionResponseSchema,
  MapDefinitionSchema,
  MapIdParamSchema,
  ObjectIdParamSchema,
  PlayRoundRequestSchema,
  ReplayParamSchema,
  ReplayResponseSchema,
  SessionDetailSchema,
  SessionIdParamSchema,
  StartRoundRequestSchema,
  UpdateObjectRequestSchema,
} from "../contracts/rest.ts";
import { SessionStateResponseSchema } from "../contracts/ws.ts";
import type { Db } from "../db/client.ts";
import { gamePlayers, gameSessions, users } from "../db/schema.ts";
import { ASSET_CONTENT_TYPES, type AssetContentType, MAX_ASSET_BYTES } from "../game/rules.ts";
import type { GameService } from "../game/service.ts";
import type { MapCatalog } from "../maps/maps.ts";
import { DOWNLOAD_URL_TTL_SECONDS, UPLOAD_URL_TTL_SECONDS, type UrlSigner } from "../storage/signed.ts";
import { isValidKey, type ObjectStorage } from "../storage/storage.ts";
import { ApiError } from "./errors.ts";

export type ApiDeps = {
  db: Db;
  jwtSecret: string;
  /** OpenAPI の servers に載せる URL */
  publicUrl?: string;
  /** 署名付き URL・modelUrl を作る URL（PUBLIC_URL を明示したとき）。無ければリクエストから作る */
  assetBaseUrl?: string;
  /** リクエストから URL を作るときに足す前置き */
  basePath?: string;
  game: GameService;
  storage: ObjectStorage;
  signer: UrlSigner;
  maps: MapCatalog;
};

const json = <T extends z.ZodType>(schema: T, description: string) => ({
  content: { "application/json": { schema } },
  description,
});
const body = <T extends z.ZodType>(schema: T, required = true) => ({
  body: { content: { "application/json": { schema } }, required },
});
const errors = (...codes: (400 | 401 | 403 | 404 | 409 | 429)[]) =>
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

/** 外から見たこの環境の URL（…/a-kun-01 まで） */
function externalBase(c: Context, deps: ApiDeps): string {
  if (deps.assetBaseUrl) return deps.assetBaseUrl;
  const u = new URL(c.req.url);
  const proto = c.req.header("x-forwarded-proto") ?? u.protocol.replace(/:$/, "");
  const host = c.req.header("x-forwarded-host") ?? c.req.header("host") ?? u.host;
  return `${proto}://${host}${deps.basePath ?? ""}`;
}

const ASSET_KEY = /^sessions\/([0-9a-f-]{36})\/assets\/[0-9a-f-]{36}\.[a-z0-9]+$/;

/** 形式ごとの先頭バイト（申告と中身が違うものを弾く） */
const MAGIC: Record<AssetContentType, number[]> = {
  "model/gltf-binary": [0x67, 0x6c, 0x54, 0x46], // "glTF"
  "image/png": [0x89, 0x50, 0x4e, 0x47],
  "image/jpeg": [0xff, 0xd8, 0xff],
  "image/ktx2": [0xab, 0x4b, 0x54, 0x58, 0x20, 0x32, 0x30, 0xbb],
};

export function registerRoutes(app: OpenAPIHono, deps: ApiDeps) {
  const { db, game, storage, signer, maps } = deps;

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
      request: body(CreateGuestRequestSchema),
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
      request: body(CreateSessionRequestSchema),
      responses: { 201: json(CreateSessionResponseSchema, "Created"), ...errors(400, 401) },
    }),
    async (c) => {
      const userId = await requireUser(c, deps);
      const { mapId } = c.req.valid("json");
      const { session, player } = await db.transaction(async (tx) => {
        const [session] = await tx.insert(gameSessions).values({ hostUserId: userId, mapId }).returning();
        const [player] = await tx.insert(gamePlayers).values({ sessionId: session!.id, userId }).returning();
        return { session: session!, player: player! };
      });
      return c.json(
        { id: session.id, status: session.status, round: session.round, mapId: session.mapId, playerId: player.id },
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
      const playerId = await game.join(sessionId, userId);
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
      const detail = await game.sessionDetail(sessionId);
      if (!detail.players.some((p) => p.userId === userId)) {
        throw new ApiError(403, "NOT_A_PARTICIPANT", "You are not a participant of this session.");
      }
      return c.json(detail, 200);
    },
  );

  // ---- GET /v1/sessions/{id}/state ----
  app.openapi(
    createRoute({
      method: "get",
      path: "/v1/sessions/{id}/state",
      summary: "権限に応じた最新 Snapshot を取得する（WS の session.snapshot と同じ公開範囲）",
      security,
      request: { params: SessionIdParamSchema },
      responses: { 200: json(SessionStateResponseSchema, "OK"), ...errors(401, 403, 404) },
    }),
    async (c) => {
      const userId = await requireUser(c, deps);
      const { id: sessionId } = c.req.valid("param");
      const { state, me } = await game.requireParticipant(sessionId, userId);
      return c.json(
        { round: state.round, seq: state.seq, timestamp: Date.now(), ...game.snapshotPayload(state, me) },
        200,
      );
    },
  );

  // ---- POST /v1/sessions/{id}/rounds/start | play | finish ----
  app.openapi(
    createRoute({
      method: "post",
      path: "/v1/sessions/{id}/rounds/start",
      summary: "準備を始めて役割を決める（waiting か finished から。finished からなら round + 1）",
      security,
      request: { params: SessionIdParamSchema, ...body(StartRoundRequestSchema) },
      responses: { 200: json(SessionDetailSchema, "OK"), ...errors(400, 401, 403, 404, 409) },
    }),
    async (c) => {
      const userId = await requireUser(c, deps);
      const { id } = c.req.valid("param");
      return c.json(await game.startRound(id, userId, c.req.valid("json")), 200);
    },
  );

  app.openapi(
    createRoute({
      method: "post",
      path: "/v1/sessions/{id}/rounds/play",
      summary: "本番を始める（準備の期限が来るとサーバーも自動で行う）",
      security,
      request: { params: SessionIdParamSchema, ...body(PlayRoundRequestSchema, false) },
      responses: { 200: json(SessionDetailSchema, "OK"), ...errors(400, 401, 403, 404, 409) },
    }),
    async (c) => {
      const userId = await requireUser(c, deps);
      const { id } = c.req.valid("param");
      return c.json(await game.play(id, userId, c.req.valid("json") ?? {}), 200);
    },
  );

  app.openapi(
    createRoute({
      method: "post",
      path: "/v1/sessions/{id}/rounds/finish",
      summary: "ラウンドを終える（本番の期限・全ドロの捕獲でもサーバーが自動で行う）",
      security,
      request: { params: SessionIdParamSchema, ...body(FinishRoundRequestSchema, false) },
      responses: { 200: json(SessionDetailSchema, "OK"), ...errors(401, 403, 404, 409) },
    }),
    async (c) => {
      const userId = await requireUser(c, deps);
      const { id } = c.req.valid("param");
      return c.json(await game.finish(id, userId), 200);
    },
  );

  // ---- GET /v1/sessions/{id}/rounds/{round}/replay ----
  app.openapi(
    createRoute({
      method: "get",
      path: "/v1/sessions/{id}/rounds/{round}/replay",
      summary: "終わったラウンドの Snapshot とイベントログ（擬態先を含む）",
      security,
      request: { params: ReplayParamSchema },
      responses: { 200: json(ReplayResponseSchema, "OK"), ...errors(401, 403, 404, 409) },
    }),
    async (c) => {
      const userId = await requireUser(c, deps);
      const { id, round } = c.req.valid("param");
      return c.json(await game.replay(id, userId, round), 200);
    },
  );

  // ---- POST /v1/assets/uploads ----
  app.openapi(
    createRoute({
      method: "post",
      path: "/v1/assets/uploads",
      summary: "3D アセットのアップロード用署名 URL を取得する（毎回新しいキー）",
      security,
      request: body(CreateAssetUploadRequestSchema),
      responses: { 201: json(CreateAssetUploadResponseSchema, "Created"), ...errors(400, 401, 403, 404) },
    }),
    async (c) => {
      const userId = await requireUser(c, deps);
      const { sessionId, contentType, sizeBytes } = c.req.valid("json");
      const { me } = await game.requireParticipant(sessionId, userId);
      const key = `sessions/${sessionId}/assets/${crypto.randomUUID()}.${ASSET_CONTENT_TYPES[contentType]}`;
      const exp = Math.floor(Date.now() / 1000) + UPLOAD_URL_TTL_SECONDS;
      const q = signer.uploadQuery({ key, exp, contentType, size: sizeBytes, ownerPlayerId: me.playerId });
      return c.json(
        {
          assetKey: key,
          uploadUrl: `${externalBase(c, deps)}/v1/storage/${key}?${q}`,
          method: "PUT" as const,
          headers: { "Content-Type": contentType },
          expiresAt: new Date(exp * 1000).toISOString(),
        },
        201,
      );
    },
  );

  // ---- GET /v1/assets/download-url ----
  app.openapi(
    createRoute({
      method: "get",
      path: "/v1/assets/download-url",
      summary: "assetKey をダウンロード用の署名 URL に引き換える（そのセッションの参加者だけ）",
      security,
      request: { query: AssetDownloadQuerySchema },
      responses: { 200: json(AssetDownloadResponseSchema, "OK"), ...errors(400, 401, 403, 404) },
    }),
    async (c) => {
      const userId = await requireUser(c, deps);
      const { key } = c.req.valid("query");
      const m = ASSET_KEY.exec(key);
      if (!m) throw new ApiError(400, "INVALID_ASSET_KEY", "Not an asset key.");
      await game.requireParticipant(m[1]!, userId);
      if (!(await storage.head(key))) throw new ApiError(404, "ASSET_NOT_FOUND", "Asset not found.");
      const exp = Math.floor(Date.now() / 1000) + DOWNLOAD_URL_TTL_SECONDS;
      return c.json(
        {
          url: `${externalBase(c, deps)}/v1/storage/${key}?${signer.downloadQuery({ key, exp })}`,
          expiresAt: new Date(exp * 1000).toISOString(),
        },
        200,
      );
    },
  );

  // ---- 署名付き URL の先（OpenAPI には載せない。URL は上の2つが返す） ----
  const storageKey = (c: Context) => decodeURIComponent(c.req.path.slice("/v1/storage/".length));

  app.put("/v1/storage/*", async (c) => {
    const key = storageKey(c);
    const grant = isValidKey(key) ? signer.verifyUpload(key, new URL(c.req.url).searchParams) : null;
    if (!grant) throw new ApiError(403, "INVALID_SIGNATURE", "The upload URL is invalid or expired.");
    const type = (c.req.header("content-type") ?? "").split(";")[0]!.trim().toLowerCase();
    if (type !== grant.contentType) {
      throw new ApiError(400, "CONTENT_TYPE_MISMATCH", `Content-Type must be ${grant.contentType}.`);
    }
    const declared = Number(c.req.header("content-length") ?? NaN);
    if (declared > grant.size || grant.size > MAX_ASSET_BYTES) {
      throw new ApiError(413, "PAYLOAD_TOO_LARGE", `The body must be ${grant.size} bytes.`);
    }
    const bytes = new Uint8Array(await c.req.arrayBuffer());
    if (bytes.byteLength !== grant.size) {
      throw new ApiError(400, "SIZE_MISMATCH", `The body must be ${grant.size} bytes (got ${bytes.byteLength}).`);
    }
    const magic = MAGIC[grant.contentType as AssetContentType];
    if (!magic || magic.some((b, i) => bytes[i] !== b)) {
      throw new ApiError(400, "INVALID_ASSET", `The body is not ${grant.contentType}.`);
    }
    const created = await storage.putNew(key, bytes, {
      contentType: grant.contentType,
      ownerPlayerId: grant.ownerPlayerId,
    });
    if (!created) throw new ApiError(409, "ASSET_EXISTS", "This asset key was already uploaded.");
    return c.json({ assetKey: key, sizeBytes: bytes.byteLength }, 201);
  });

  app.get("/v1/storage/*", async (c) => {
    const key = storageKey(c);
    if (!isValidKey(key) || !signer.verifyDownload(key, new URL(c.req.url).searchParams)) {
      throw new ApiError(403, "INVALID_SIGNATURE", "The download URL is invalid or expired.");
    }
    const got = await storage.get(key);
    if (!got) throw new ApiError(404, "ASSET_NOT_FOUND", "Asset not found.");
    return c.body(got.body as Uint8Array<ArrayBuffer>, 200, {
      "content-type": got.meta.contentType,
      "cache-control": `private, max-age=${DOWNLOAD_URL_TTL_SECONDS}`,
    });
  });

  // ---- オブジェクト ----
  app.openapi(
    createRoute({
      method: "post",
      path: "/v1/sessions/{id}/objects",
      summary: "オブジェクトを登録する（ドロ。アップロード済みの自分の GLB だけ）",
      security,
      request: { params: SessionIdParamSchema, ...body(CreateObjectRequestSchema) },
      responses: { 201: json(GameObjectSchema, "Created"), ...errors(400, 401, 403, 404, 409) },
    }),
    async (c) => {
      const userId = await requireUser(c, deps);
      const { id } = c.req.valid("param");
      return c.json(await game.createObject(id, userId, c.req.valid("json")), 201);
    },
  );

  app.openapi(
    createRoute({
      method: "patch",
      path: "/v1/sessions/{id}/objects/{objectId}",
      summary: "確定したメッシュ（assetKey）や Transform を更新する（所有ドロ）",
      security,
      request: { params: ObjectIdParamSchema, ...body(UpdateObjectRequestSchema) },
      responses: { 200: json(GameObjectSchema, "OK"), ...errors(400, 401, 403, 404, 409) },
    }),
    async (c) => {
      const userId = await requireUser(c, deps);
      const { id, objectId } = c.req.valid("param");
      return c.json(await game.updateObject(id, userId, objectId, c.req.valid("json")), 200);
    },
  );

  app.openapi(
    createRoute({
      method: "delete",
      path: "/v1/sessions/{id}/objects/{objectId}",
      summary: "オブジェクトを削除する（所有ドロ）",
      security,
      request: { params: ObjectIdParamSchema },
      responses: { 204: { description: "Deleted" }, ...errors(401, 403, 404, 409) },
    }),
    async (c) => {
      const userId = await requireUser(c, deps);
      const { id, objectId } = c.req.valid("param");
      await game.deleteObject(id, userId, objectId);
      return c.body(null, 204);
    },
  );

  app.openapi(
    createRoute({
      method: "post",
      path: "/v1/sessions/{id}/objects/{objectId}/disguise",
      summary: "このオブジェクトに擬態する（前の擬態先は外れる。所有ドロ）",
      security,
      request: { params: ObjectIdParamSchema },
      responses: { 200: json(GameObjectSchema, "OK"), ...errors(401, 403, 404, 409) },
    }),
    async (c) => {
      const userId = await requireUser(c, deps);
      const { id, objectId } = c.req.valid("param");
      return c.json(await game.disguise(id, userId, objectId), 200);
    },
  );

  // ---- POST /v1/sessions/{id}/captures ----
  app.openapi(
    createRoute({
      method: "post",
      path: "/v1/sessions/{id}/captures",
      summary: "捕獲を判定する（ケイ・本番中だけ。連打は 429）",
      security,
      request: { params: SessionIdParamSchema, ...body(CaptureRequestSchema) },
      responses: { 200: json(CaptureResponseSchema, "OK"), ...errors(400, 401, 403, 404, 409, 429) },
    }),
    async (c) => {
      const userId = await requireUser(c, deps);
      const { id } = c.req.valid("param");
      return c.json(await game.capture(id, userId, c.req.valid("json").objectId), 200);
    },
  );

  // ---- GET /v1/maps/{mapId} ----
  app.openapi(
    createRoute({
      method: "get",
      path: "/v1/maps/{mapId}",
      summary: "マップ定義を取得する",
      security,
      request: { params: MapIdParamSchema },
      responses: { 200: json(MapDefinitionSchema, "OK"), ...errors(401, 404) },
    }),
    async (c) => {
      await requireUser(c, deps);
      const { mapId } = c.req.valid("param");
      const m = maps.get(mapId);
      if (!m) throw new ApiError(404, "MAP_NOT_FOUND", "Map not found.");
      return c.json(
        {
          mapId,
          name: m.def.name,
          modelUrl: `${externalBase(c, deps)}/v1/maps/${mapId}/model.glb`,
          transform: m.def.transform,
        },
        200,
      );
    },
  );

  // マップの GLB は秘密ではないので認証なし（GLB ローダーがヘッダーを付けられなくても読めるように）
  app.get("/v1/maps/:mapId/model.glb", (c) => {
    const m = maps.get(c.req.param("mapId"));
    if (!m) throw new ApiError(404, "MAP_NOT_FOUND", "Map not found.");
    return c.body(m.glb as Uint8Array<ArrayBuffer>, 200, {
      "content-type": "model/gltf-binary",
      "cache-control": "public, max-age=300",
    });
  });

  app.doc31("/v1/openapi.json", {
    openapi: "3.1.0",
    info: { title: "めっちゃ美唄 Game Server API", version: "0.2.0" },
    servers: [{ url: deps.publicUrl ?? "https://bibai.t0waxx.com" }],
  });
}
