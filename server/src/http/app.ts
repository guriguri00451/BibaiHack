import { OpenAPIHono } from "@hono/zod-openapi";
import { ApiError, errorBody } from "./errors.ts";

export function createApp() {
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
    console.error(err);
    return c.json(errorBody("INTERNAL", "Internal server error."), 500);
  });

  app.notFound((c) => c.json(errorBody("NOT_FOUND", "Not found."), 404));

  app.get("/healthz", (c) => c.json({ ok: true }));

  return app;
}
