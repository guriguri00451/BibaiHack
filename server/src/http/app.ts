import { OpenAPIHono } from "@hono/zod-openapi";
import { HTTPException } from "hono/http-exception";
import type { Db } from "../db/client.ts";
import { RateLimitError } from "../game/service.ts";
import { buildServices, type Services } from "../services.ts";
import type { AdminAccess } from "../config.ts";
import { registerAdminRoutes } from "./admin.ts";
import { ApiError, errorBody } from "./errors.ts";
import { registerRoutes } from "./routes.ts";

export type AppDeps = {
  db: Db;
  jwtSecret: string;
  publicUrl?: string;
  assetBaseUrl?: string;
  basePath?: string;
  /** 管理画面の入り方。無ければ管理画面は閉じる */
  admin?: AdminAccess;
} & Partial<Services>;

/** deps が無ければ /healthz だけの app（テスト用）。サービスが無ければここで作る */
export function createApp(deps?: AppDeps) {
  const app = new OpenAPIHono({
    // zod の検証に失敗したリクエストは、仕様書の形式で 400 を返す
    defaultHook: (result, c) => {
      if (!result.success) {
        const message = result.error.issues
          .map((i) => `${i.path.join(".") || "(root)"}: ${i.message}`)
          .join("; ");
        return c.json(errorBody("INVALID_REQUEST", message), 400);
      }
    },
  });

  app.onError((err, c) => {
    if (err instanceof RateLimitError) c.header("retry-after", String(Math.ceil(err.retryAfterMs / 1000)));
    if (err instanceof ApiError) return c.json(err.body, err.status);
    // Hono 自身が投げる 4xx（壊れた JSON など）は、そのステータスのまま仕様書の形式で返す
    if (err instanceof HTTPException && err.status < 500) {
      return c.json(errorBody("INVALID_REQUEST", err.message), err.status);
    }
    console.error(err);
    return c.json(errorBody("INTERNAL", "Internal server error."), 500);
  });

  app.notFound((c) => c.json(errorBody("NOT_FOUND", "Not found."), 404));

  app.get("/healthz", (c) => c.json({ ok: true }));
  if (deps) {
    const services = deps.game ? (deps as AppDeps & Services) : { ...deps, ...buildServices(deps) };
    registerRoutes(app, services);
    registerAdminRoutes(app, { ...services, admin: deps.admin ?? { open: false } });
  }

  return app;
}
