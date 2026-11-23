import { readFileSync } from "node:fs";
import { timingSafeEqual } from "node:crypto";
import type { OpenAPIHono } from "@hono/zod-openapi";
import type { Context } from "hono";
import { z } from "@hono/zod-openapi";
import type { AdminAccess } from "../config.ts";
import { TransformSchema } from "../contracts/transform.ts";
import type { GameService } from "../game/service.ts";
import type { MapCatalog } from "../maps/maps.ts";
import { externalBase } from "./base-url.ts";
import { ApiError } from "./errors.ts";

/** 管理画面から上げられるマップの GLB の大きさの上限（プレイヤーのアセットより大きい） */
export const MAX_MAP_BYTES = 100 * 1024 * 1024;

const GLB_MAGIC = [0x67, 0x6c, 0x54, 0x46];

const ADMIN_HTML = readFileSync(new URL("../admin/admin.html", import.meta.url), "utf8");

const MapPatchSchema = z.object({ name: z.string().min(1).max(100).optional(), transform: TransformSchema.optional() });
const SetMapSchema = z.object({ mapId: z.string() });
const SessionIdSchema = z.uuid();

export type AdminDeps = {
  admin: AdminAccess;
  game: GameService;
  maps: MapCatalog;
  assetBaseUrl?: string;
  basePath?: string;
};

/**
 * 管理画面。/admin が画面（HTML 1枚）、/v1/admin/* がその API。
 * OpenAPI には載せない（ゲームのクライアントは使わない）。認証は ADMIN_TOKEN（config.ts の AdminAccess）
 */
export function registerAdminRoutes(app: OpenAPIHono, deps: AdminDeps) {
  const { admin, game, maps } = deps;
  const base = (c: Context) => externalBase(c.req.raw, deps);

  app.get("/admin", (c) => c.html(ADMIN_HTML));

  app.use("/v1/admin/*", async (c, next) => {
    if (!admin.open) {
      if (!admin.token) throw new ApiError(404, "NOT_FOUND", "Not found.");
      const header = c.req.header("authorization") ?? "";
      const given = Buffer.from(header.startsWith("Bearer ") ? header.slice(7) : "");
      const want = Buffer.from(admin.token);
      if (given.length !== want.length || !timingSafeEqual(given, want)) {
        throw new ApiError(401, "UNAUTHORIZED", "管理用のトークンが違います。");
      }
    }
    await next();
  });

  app.get("/v1/admin/whoami", (c) => c.json({ ok: true, open: admin.open }));

  // ---------- マップ ----------

  const mapJson = async (c: Context) =>
    (await maps.list()).map((m) => ({
      ...m,
      modelUrl: `${base(c)}/v1/maps/${m.mapId}/model.glb` + (m.version === "builtin" ? "" : `?v=${m.version}`),
    }));

  app.get("/v1/admin/maps", async (c) => c.json({ maps: await mapJson(c) }));

  /** multipart/form-data: mapId, name, transform（JSON 文字列、任意）, model（GLB、新規のときは必須） */
  app.post("/v1/admin/maps", async (c) => {
    const form = await c.req.formData().catch(() => {
      throw new ApiError(400, "INVALID_REQUEST", "multipart/form-data で送ってください。");
    });
    const mapId = String(form.get("mapId") ?? "").trim();
    const name = String(form.get("name") ?? "").trim() || undefined;
    let transform;
    const rawTransform = String(form.get("transform") ?? "").trim();
    if (rawTransform) {
      const parsed = TransformSchema.safeParse(safeJson(rawTransform));
      if (!parsed.success) throw new ApiError(400, "INVALID_TRANSFORM", "transform は {position, rotation, scale} の JSON にしてください。");
      transform = parsed.data;
    }
    let glb: Uint8Array | undefined;
    const file = form.get("model");
    if (file && typeof file !== "string" && file.size > 0) {
      if (file.size > MAX_MAP_BYTES) throw new ApiError(400, "MODEL_TOO_LARGE", `GLB は ${MAX_MAP_BYTES / 1024 / 1024}MB までです。`);
      glb = new Uint8Array(await file.arrayBuffer());
      if (!GLB_MAGIC.every((b, i) => glb![i] === b)) throw new ApiError(400, "NOT_GLB", "GLB（バイナリの glTF）ではありません。");
    }
    const saved = await maps.save({ mapId, name, transform, glb });
    return c.json({ map: (await mapJson(c)).find((m) => m.mapId === saved.mapId) }, 201);
  });

  app.patch("/v1/admin/maps/:mapId", async (c) => {
    const parsed = MapPatchSchema.safeParse(await c.req.json().catch(() => null));
    if (!parsed.success) throw new ApiError(400, "INVALID_REQUEST", "name か transform を JSON で送ってください。");
    const mapId = c.req.param("mapId");
    const existing = await maps.get(mapId);
    if (!existing) throw new ApiError(404, "MAP_NOT_FOUND", "Map not found.");
    const saved = await maps.save({ mapId, ...parsed.data });
    return c.json({ map: (await mapJson(c)).find((m) => m.mapId === saved.mapId) });
  });

  app.delete("/v1/admin/maps/:mapId", async (c) => {
    await maps.remove(c.req.param("mapId"));
    return c.body(null, 204);
  });

  // ---------- セッション ----------

  app.get("/v1/admin/sessions", async (c) => {
    const limit = Math.min(200, Math.max(1, Number(c.req.query("limit") ?? 50) || 50));
    return c.json({ sessions: await game.adminSessions(limit), now: new Date().toISOString() });
  });

  const sessionId = (c: Context) => {
    const parsed = SessionIdSchema.safeParse(c.req.param("id"));
    if (!parsed.success) throw new ApiError(404, "SESSION_NOT_FOUND", "Session not found.");
    return parsed.data;
  };

  app.post("/v1/admin/sessions/:id/start", async (c) => c.json(await game.adminStart(sessionId(c))));
  app.post("/v1/admin/sessions/:id/play", async (c) => c.json(await game.play(sessionId(c), null, {})));
  app.post("/v1/admin/sessions/:id/finish", async (c) => c.json(await game.finish(sessionId(c), null)));
  app.post("/v1/admin/sessions/:id/stop-cycle", async (c) => c.json(await game.stopCycle(sessionId(c), null)));
  app.put("/v1/admin/sessions/:id/map", async (c) => {
    const parsed = SetMapSchema.safeParse(await c.req.json().catch(() => null));
    if (!parsed.success) throw new ApiError(400, "INVALID_REQUEST", "mapId を JSON で送ってください。");
    return c.json(await game.setMap(sessionId(c), null, parsed.data.mapId));
  });
}

function safeJson(s: string): unknown {
  try {
    return JSON.parse(s);
  } catch {
    return null;
  }
}
