import type { ServerWebSocket, WebSocketHandler } from "bun";
import { verifyToken } from "../auth/token.ts";
import { normalizeTransform } from "../contracts/transform.ts";
import { ClientMessageSchema, type ServerMessage, WS_CLOSE } from "../contracts/ws.ts";
import { canSeePlayerPose, type GameRegistry, type GameState } from "../game/registry.ts";

export type WsData = {
  sessionId?: string;
  playerId?: string;
  authTimer?: ReturnType<typeof setTimeout>;
};
type Socket = ServerWebSocket<WsData>;

/** 接続後この時間内に auth が来なければ切る */
const AUTH_TIMEOUT_MS = 5_000;

export function createWsHandler(deps: { registry: GameRegistry; jwtSecret: string }) {
  const { registry, jwtSecret } = deps;
  /** sessionId → playerId → 接続 */
  const conns = new Map<string, Map<string, Socket>>();

  const send = (ws: Socket, msg: ServerMessage): void => void ws.send(JSON.stringify(msg));
  const sendError = (ws: Socket, code: string, message: string): void =>
    send(ws, { type: "error", timestamp: Date.now(), payload: { code, message } });

  function snapshotFor(state: GameState, playerId: string): ServerMessage {
    const me = state.players.get(playerId)!;
    return {
      type: "session.snapshot",
      round: state.round,
      seq: state.seq,
      timestamp: Date.now(),
      payload: {
        session: {
          id: state.sessionId,
          status: state.status,
          round: state.round,
          phaseEndsAt: state.phaseEndsAt?.toISOString() ?? null,
        },
        you: { playerId, role: me.role },
        players: [...state.players.values()].map((p) => ({
          playerId: p.playerId,
          name: p.name,
          role: p.role,
          isCaught: p.isCaught,
          connected: p.connected,
          transform: canSeePlayerPose(state, me, p) ? p.transform : null,
        })),
      },
    };
  }

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

    let room = conns.get(sessionId);
    if (!room) conns.set(sessionId, (room = new Map()));
    const old = room.get(player.playerId);
    room.set(player.playerId, ws);
    old?.close(WS_CLOSE.REPLACED, "replaced by a new connection");

    player.connected = true;
    // 再接続したら clientSeq は振り出しから数え直してよい
    player.lastClientSeq = -1;
    send(ws, snapshotFor(state, player.playerId));
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

      const { sessionId, playerId } = ws.data;
      if (!sessionId || !playerId) return sendError(ws, "UNAUTHORIZED", "Send auth first.");
      const state = await registry.load(sessionId);
      const me = state?.players.get(playerId);
      if (!state || !me) return;

      switch (msg.type) {
        case "player.transform": {
          if (msg.payload.clientSeq <= me.lastClientSeq) return; // 重複・逆順は捨てる
          me.lastClientSeq = msg.payload.clientSeq;
          me.transform = normalizeTransform(msg.payload.transform);
          state.seq += 1;
          const out: ServerMessage = {
            type: "player.transform",
            round: state.round,
            seq: state.seq,
            timestamp: Date.now(),
            payload: { playerId, transform: me.transform },
          };
          const text = JSON.stringify(out);
          for (const [otherId, sock] of conns.get(sessionId) ?? []) {
            const other = state.players.get(otherId);
            if (otherId !== playerId && other && canSeePlayerPose(state, other, me)) sock.send(text);
          }
          return;
        }
      }
    },

    async close(ws) {
      clearTimeout(ws.data.authTimer);
      const { sessionId, playerId } = ws.data;
      if (!sessionId || !playerId) return;
      const room = conns.get(sessionId);
      if (room?.get(playerId) !== ws) return; // 入れ替わった古い接続
      room.delete(playerId);
      if (room.size === 0) conns.delete(sessionId);
      const state = await registry.load(sessionId);
      const me = state?.players.get(playerId);
      if (me) me.connected = false;
    },
  };

  return { websocket };
}
