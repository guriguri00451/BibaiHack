import type { Server } from "bun";
import type { Db } from "./db/client.ts";
import { createApp } from "./http/app.ts";
import { errorBody } from "./http/errors.ts";
import type { MapCatalog } from "./maps/maps.ts";
import { buildServices } from "./services.ts";
import type { ObjectStorage } from "./storage/storage.ts";
import { createWsHandler, type WsData } from "./ws/handler.ts";

/**
 * REST（Hono）と WebSocket（/v1/ws）を1つの Bun.serve にまとめる。
 * basePath（例 "/a-kun-01"）があれば、その前置きを外してから振り分ける。前置きの無いリクエストは 404
 */
export function createServer(deps: {
  db: Db;
  jwtSecret: string;
  basePath?: string;
  publicUrl?: string;
  /** 署名付き URL・modelUrl を作る URL。無ければリクエストの Host から作る */
  assetBaseUrl?: string;
  storage?: ObjectStorage;
  maps?: MapCatalog;
  logFlushIntervalMs?: number;
}) {
  const basePath = deps.basePath ?? "";
  const services = buildServices(deps);
  const app = createApp({ ...deps, ...services });
  const { websocket } = createWsHandler({ game: services.game, jwtSecret: deps.jwtSecret });

  return {
    ...services,
    registry: services.game.registry,
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
    /** タイマーを止め、残っているログを書き出す */
    async dispose() {
      services.game.hub.closeAll();
      await services.game.dispose();
    },
  };
}
