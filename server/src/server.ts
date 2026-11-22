import type { Server } from "bun";
import type { Db } from "./db/client.ts";
import { GameRegistry } from "./game/registry.ts";
import { createApp } from "./http/app.ts";
import { createWsHandler, type WsData } from "./ws/handler.ts";

/** REST（Hono）と WebSocket（/v1/ws）を1つの Bun.serve にまとめる */
export function createServer(deps: { db: Db; jwtSecret: string }) {
  const registry = new GameRegistry(deps.db);
  const app = createApp(deps);
  const { websocket } = createWsHandler({ registry, jwtSecret: deps.jwtSecret });

  return {
    registry,
    fetch(req: Request, server: Server<WsData>) {
      if (new URL(req.url).pathname === "/v1/ws") {
        if (server.upgrade(req, { data: {} })) return undefined;
        return new Response("Expected a WebSocket upgrade", { status: 426 });
      }
      return app.fetch(req);
    },
    websocket,
  };
}
