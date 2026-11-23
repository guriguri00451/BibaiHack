import type { ServerWebSocket, WebSocketHandler } from "bun";
import { verifyToken } from "../auth/token.ts";
import { ClientMessageSchema, type ServerMessage, WS_CLOSE } from "../contracts/ws.ts";
import type { GameService } from "../game/service.ts";
import { ApiError } from "../http/errors.ts";

export type WsData = {
  sessionId?: string;
  playerId?: string;
  authTimer?: ReturnType<typeof setTimeout>;
  /** この接続から見たこの環境の URL（配る assetUrl をそこへ向ける） */
  baseUrl?: string;
};
type Socket = ServerWebSocket<WsData>;

/** 接続後この時間内に auth が来なければ切る */
const AUTH_TIMEOUT_MS = 5_000;

export function createWsHandler(deps: { game: GameService; jwtSecret: string }) {
  const { game, jwtSecret } = deps;
  const { registry, hub } = game;

  const send = (ws: Socket, msg: ServerMessage): void => void ws.send(JSON.stringify(msg));
  const sendError = (ws: Socket, code: string, message: string, requestId?: string): void =>
    send(ws, {
      type: "error",
      timestamp: Date.now(),
      payload: { code, message, ...(requestId ? { requestId } : {}) },
    });

  async function handleAuth(ws: Socket, accessToken: string, sessionId: string): Promise<void> {
    const userId = await verifyToken(accessToken, jwtSecret);
    if (!userId) return ws.close(WS_CLOSE.UNAUTHORIZED, "invalid token");
    const state = await registry.load(sessionId);
    if (!state) return ws.close(WS_CLOSE.NOT_FOUND, "session not found");
    const player = await registry.findPlayerByUser(state, userId);
    if (!player) return ws.close(WS_CLOSE.FORBIDDEN, "not a participant");
    if (ws.readyState !== 1) return; // 認証中に切れた

    clearTimeout(ws.data.authTimer);
    ws.data.sessionId = sessionId;
    ws.data.playerId = player.playerId;

    hub.attach(sessionId, player.playerId, ws, ws.data.baseUrl)?.close(WS_CLOSE.REPLACED, "replaced by a new connection");

    player.connected = true;
    // 再接続したら clientSeq は振り出しから数え直してよい
    player.lastClientSeq = -1;
    for (const o of state.objects.values()) if (o.playerId === player.playerId) o.lastClientSeq = -1;
    send(ws, game.snapshotMessage(state, player, ws.data.baseUrl));
  }

  const websocket: WebSocketHandler<WsData> = {
    open(ws) {
      ws.data.authTimer = setTimeout(() => {
        if (!ws.data.playerId) ws.close(WS_CLOSE.UNAUTHORIZED, "auth timeout");
      }, AUTH_TIMEOUT_MS);
    },

    async message(ws, raw) {
      let parsed: unknown;
      try {
        parsed = JSON.parse(typeof raw === "string" ? raw : raw.toString());
      } catch {
        return sendError(ws, "INVALID_MESSAGE", "Message must be JSON.");
      }
      const result = ClientMessageSchema.safeParse(parsed);
      if (!result.success) {
        return sendError(ws, "INVALID_MESSAGE", result.error.issues.map((i) => i.message).join("; "));
      }
      const msg = result.data;

      if (msg.type === "auth") {
        if (ws.data.playerId) return sendError(ws, "ALREADY_AUTHENTICATED", "Already authenticated.");
        return handleAuth(ws, msg.payload.accessToken, msg.payload.sessionId);
      }

      const requestId = msg.payload.requestId;
      const { sessionId, playerId } = ws.data;
      if (!sessionId || !playerId) return sendError(ws, "UNAUTHORIZED", "Send auth first.", requestId);
      const state = await registry.load(sessionId);
      const me = state?.players.get(playerId);
      if (!state || !me) return;

      try {
        switch (msg.type) {
          case "player.transform":
            game.playerTransform(state, me, msg.payload.clientSeq, msg.payload.transform);
            break;
          case "object.transform":
            game.objectTransform(state, me, msg.payload.objectId, msg.payload.clientSeq, msg.payload.transform);
            break;
        }
        if (requestId) {
          send(ws, { type: "command.ack", timestamp: Date.now(), payload: { requestId, command: msg.type } });
        }
      } catch (e) {
        if (e instanceof ApiError) return sendError(ws, e.code, e.message, requestId);
        console.error(e);
        sendError(ws, "INTERNAL", "Internal server error.", requestId);
      }
    },

    async close(ws) {
      clearTimeout(ws.data.authTimer);
      const { sessionId, playerId } = ws.data;
      if (!sessionId || !playerId) return;
      if (!hub.detach(sessionId, playerId, ws)) return; // 入れ替わった古い接続
      const state = await registry.load(sessionId);
      const me = state?.players.get(playerId);
      if (me) me.connected = false;
    },
  };

  return { websocket };
}
