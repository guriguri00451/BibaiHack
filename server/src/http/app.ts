import { OpenAPIHono } from "@hono/zod-openapi";
import { HTTPException } from "hono/http-exception";
import { ApiError, errorBody } from "./errors.ts";
import { type ApiDeps, registerRoutes } from "./routes.ts";

export function createApp(deps?: ApiDeps) {
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
  if (deps) registerRoutes(app, deps);

  return app;
}
