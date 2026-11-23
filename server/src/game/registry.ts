import { and, asc, eq, inArray } from "drizzle-orm";
import type { Transform } from "../contracts/transform.ts";
import type { Db } from "../db/client.ts";
import { gameObjects, gamePlayers, gameSessions, users } from "../db/schema.ts";
import type { ObjectState } from "./objects.ts";

type Role = "hider" | "seeker";
type Status = "waiting" | "preparing" | "playing" | "finished";

export type PlayerState = {
  playerId: string;
  userId: string;
  name: string;
  role: Role | null;
  isCaught: boolean;
  connected: boolean;
  transform: Transform | null;
  /** 最後に受け付けた clientSeq。これ以下は古い送信として捨てる */
  lastClientSeq: number;
};

/** 仕様書 6.3。ゲーム中の動く状態はここ（メモリ）にだけ持つ */
export type GameState = {
  sessionId: string;
  status: Status;
  round: number;
  phaseEndsAt: Date | null;
  /** ラウンド内のサーバー確定順序 */
  seq: number;
  players: Map<string, PlayerState>;
  /** 現在のラウンドのオブジェクト（objectId → 状態） */
  objects: Map<string, ObjectState>;
};

/**
 * 受け手 viewer に、subject の位置を見せてよいか。
 * 仕様書 6.2-7「ドロの位置は原則ケイへ送らない」。ケイの位置はドロに見せる（未確定の仮ルール）。
 * 待機中は役割がないので全員に見せる。
 */
export function canSeePlayerPose(state: GameState, viewer: PlayerState, subject: PlayerState) {
  if (state.status === "waiting") return true;
  if (subject.role !== "hider") return true;
  return viewer.role === "hider";
}

export class GameRegistry {
  private readonly sessions = new Map<string, Promise<GameState | null>>();

  constructor(private readonly db: Db) {}

  /** DB から読み込んでメモリに載せる（2回目以降はメモリの状態を返す）。無ければ null */
  load(sessionId: string): Promise<GameState | null> {
    let p = this.sessions.get(sessionId);
    if (!p) {
      p = this.loadFromDb(sessionId);
      this.sessions.set(sessionId, p);
      // 見つからなかったものはキャッシュしない（後から作られることはないが、メモリを溜めない）
      p.then((s) => s ?? this.sessions.delete(sessionId));
    }
    return p;
  }

  /**
   * userId のプレイヤーを返す。メモリに無ければ DB を見る
   * （メモリに載せた後に REST で参加した人もここで拾う）。参加者でなければ null
   */
  async findPlayerByUser(state: GameState, userId: string): Promise<PlayerState | null> {
    for (const p of state.players.values()) if (p.userId === userId) return p;
    const [row] = await this.db
      .select({
        playerId: gamePlayers.id,
        userId: gamePlayers.userId,
        name: users.name,
        role: gamePlayers.role,
        isCaught: gamePlayers.isCaught,
      })
      .from(gamePlayers)
      .innerJoin(users, eq(users.id, gamePlayers.userId))
      .where(and(eq(gamePlayers.sessionId, state.sessionId), eq(gamePlayers.userId, userId)));
    if (!row) return null;
    const existing = state.players.get(row.playerId);
    if (existing) return existing;
    const player: PlayerState = { ...row, connected: false, transform: null, lastClientSeq: -1 };
    state.players.set(player.playerId, player);
    return player;
  }

  private async loadFromDb(sessionId: string): Promise<GameState | null> {
    const [session] = await this.db.select().from(gameSessions).where(eq(gameSessions.id, sessionId));
    if (!session) return null;
    const rows = await this.db
      .select({
        playerId: gamePlayers.id,
        userId: gamePlayers.userId,
        name: users.name,
        role: gamePlayers.role,
        isCaught: gamePlayers.isCaught,
      })
      .from(gamePlayers)
      .innerJoin(users, eq(users.id, gamePlayers.userId))
      .where(eq(gamePlayers.sessionId, sessionId))
      .orderBy(asc(gamePlayers.createdAt));
    const objectRows = await this.db
      .select()
      .from(gameObjects)
      .where(and(eq(gameObjects.round, session.round), inArray(gameObjects.playerId, rows.map((r) => r.playerId))))
      .orderBy(asc(gameObjects.createdAt));
    return {
      sessionId,
      status: session.status,
      round: session.round,
      phaseEndsAt: session.phaseEndsAt,
      seq: 0,
      objects: new Map(
        objectRows.map((o) => [
          o.id,
          {
            objectId: o.id,
            playerId: o.playerId,
            assetKey: o.assetUrl,
            transform: o.transform,
            isDisguise: o.isDisguise,
            lastClientSeq: -1,
          } satisfies ObjectState,
        ]),
      ),
      players: new Map(
        rows.map((r) => [
          r.playerId,
          { ...r, connected: false, transform: null, lastClientSeq: -1 } satisfies PlayerState,
        ]),
      ),
    };
  }
}
