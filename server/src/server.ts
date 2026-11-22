import type { Server } from "bun";
import type { Db } from "./db/client.ts";
import { Connections } from "./game/connections.ts";
import { GameRegistry } from "./game/registry.ts";
import type { AssetStorage } from "./storage.ts";
import { createApp } from "./http/app.ts";
import { errorBody } from "./http/errors.ts";
import { createWsHandler, type WsData } from "./ws/handler.ts";

/**
 * REST（Hono）と WebSocket（/v1/ws）を1つの Bun.serve にまとめる。
 * basePath（例 "/a-kun-01"）があれば、その前置きを外してから振り分ける。前置きの無いリクエストは 404
 */
export function createServer(deps: { db: Db; jwtSecret: string; basePath?: string; publicUrl?: string; assetStorage?: AssetStorage }) {
  const basePath = deps.basePath ?? "";
  const registry = new GameRegistry(deps.db);
  const connections = new Connections();
  const app = createApp({ ...deps, registry, connections });
  const { websocket } = createWsHandler({
    registry,
    connections,
    jwtSecret: deps.jwtSecret,
    assetStorage: deps.assetStorage,
  });

  return {
    registry,
    fetch(req: Request, server: Server<WsData>) {
      const url = new URL(req.url);
      if (basePath) {
        if (url.pathname !== basePath && !url.pathname.startsWith(basePath + "/")) {
          return Response.json(errorBody("NOT_FOUND", "Not found."), { status: 404 });
        }
        url.pathname = url.pathname.slice(basePath.length) || "/";
      }
      if (url.pathname === "/v1/ws") {
        // upgrade は元の Request で行う（作り直した Request では接続を引き継げない）
        if (server.upgrade(req, { data: {} })) return undefined;
        return new Response("Expected a WebSocket upgrade", { status: 426 });
      }
      return app.fetch(basePath ? new Request(url.toString(), req) : req);
    },
    websocket,
  };
}
