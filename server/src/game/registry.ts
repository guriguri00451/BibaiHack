import { and, asc, eq, inArray } from "drizzle-orm";
import type { Transform } from "../contracts/transform.ts";
import type { Db } from "../db/client.ts";
import { gameObjects, gamePlayers, gameSessions, users } from "../db/schema.ts";

export type Role = "hider" | "seeker";
export type Status = "waiting" | "preparing" | "playing" | "finished";

export type PlayerState = {
  playerId: string;
  userId: string;
  name: string;
  role: Role | null;
  score: number;
  isCaught: boolean;
  connected: boolean;
  transform: Transform | null;
  /** 最後に受け付けた clientSeq。これ以下は古い送信として捨てる */
  lastClientSeq: number;
  /** 最後に捕獲を試した時刻（Unix ms）。連打防止 */
  lastCaptureAt: number;
};

export type ObjectState = {
  objectId: string;
  playerId: string;
  round: number;
  assetKey: string;
  transform: Transform;
  isDisguise: boolean;
  createdAt: Date;
  updatedAt: Date;
  /** object.transform で受けた最後の clientSeq */
  lastClientSeq: number;
  /** WS で動いたが、まだ DB に書いていない */
  transformDirty: boolean;
};

/** 仕様書 6.3。ゲーム中の動く状態はここ（メモリ）にだけ持つ */
export type GameState = {
  sessionId: string;
  hostUserId: string;
  mapId: string;
  status: Status;
  round: number;
  phaseEndsAt: Date | null;
  /** ラウンド内のサーバー確定順序 */
  seq: number;
  players: Map<string, PlayerState>;
  /** 今のラウンドのオブジェクトだけ */
  objects: Map<string, ObjectState>;
  /** rounds/start で受けた playingSeconds（準備の期限で自動的に本番へ入るときに使う） */
  playingSeconds: number | null;
  timer: ReturnType<typeof setTimeout> | null;
  /** フェーズ変更・オブジェクト操作・捕獲を1つずつ順に処理するための鎖 */
  lock: Promise<unknown>;
  /** 最後に Snapshot をログへ書いたときの seq */
  snapshotSeq: number;
};

/** 同じセッションへの操作を1つずつ順に実行する（DB の待ち中に他の操作が割り込まない） */
export function withLock<T>(state: GameState, fn: () => Promise<T>): Promise<T> {
  const run = state.lock.then(fn, fn);
  state.lock = run.catch(() => {});
  return run;
}

const playerColumns = {
  playerId: gamePlayers.id,
  userId: gamePlayers.userId,
  name: users.name,
  role: gamePlayers.role,
  score: gamePlayers.score,
  isCaught: gamePlayers.isCaught,
};

type PlayerRow = { playerId: string; userId: string; name: string; role: Role | null; score: number; isCaught: boolean };

const toPlayer = (r: PlayerRow): PlayerState => ({
  ...r,
  connected: false,
  transform: null,
  lastClientSeq: -1,
  lastCaptureAt: 0,
});

export class GameRegistry {
  private readonly sessions = new Map<string, Promise<GameState | null>>();
  /** メモリに載せた直後に呼ぶ（タイマーの再開・seq の復元など） */
  onLoad: ((state: GameState) => Promise<void>) | null = null;

  constructor(private readonly db: Db) {}

  /** DB から読み込んでメモリに載せる（2回目以降はメモリの状態を返す）。無ければ null */
  load(sessionId: string): Promise<GameState | null> {
    let p = this.sessions.get(sessionId);
    if (!p) {
      p = this.loadFromDb(sessionId);
      this.sessions.set(sessionId, p);
      // 見つからなかったもの・失敗したものはキャッシュしない
      p.then(
        (s) => s ?? this.sessions.delete(sessionId),
        () => this.sessions.delete(sessionId),
      );
    }
    return p;
  }

  /** メモリに載っていればその状態、無ければ null（DB からは読まない） */
  async peek(sessionId: string): Promise<GameState | null> {
    return (await this.sessions.get(sessionId)?.catch(() => null)) ?? null;
  }

  /** メモリに載っている状態（読み込み中のものは除く） */
  async loaded(): Promise<GameState[]> {
    const all = await Promise.all([...this.sessions.values()].map((p) => p.catch(() => null)));
    return all.filter((s): s is GameState => s !== null);
  }

  /** 準備中・本番中のセッションを起動時に読み込む（期限のタイマーを動かすため） */
  async resumeActive(): Promise<number> {
    const rows = await this.db
      .select({ id: gameSessions.id })
      .from(gameSessions)
      .where(inArray(gameSessions.status, ["preparing", "playing"]));
    await Promise.all(rows.map((r) => this.load(r.id)));
    return rows.length;
  }

  /**
   * userId のプレイヤーを返す。メモリに無ければ DB を見る
   * （メモリに載せた後に REST で参加した人もここで拾う）。参加者でなければ null
   */
  async findPlayerByUser(state: GameState, userId: string): Promise<PlayerState | null> {
    for (const p of state.players.values()) if (p.userId === userId) return p;
    const [row] = await this.db
      .select(playerColumns)
      .from(gamePlayers)
      .innerJoin(users, eq(users.id, gamePlayers.userId))
      .where(and(eq(gamePlayers.sessionId, state.sessionId), eq(gamePlayers.userId, userId)));
    if (!row) return null;
    const existing = state.players.get(row.playerId);
    if (existing) return existing;
    const player = toPlayer(row);
    state.players.set(player.playerId, player);
    return player;
  }

  /** DB にいてメモリにまだいない参加者を足す（ラウンド開始の前に、全員の役割を決めるため） */
  async syncPlayers(state: GameState): Promise<void> {
    const rows = await this.selectPlayers(state.sessionId);
    for (const r of rows) if (!state.players.has(r.playerId)) state.players.set(r.playerId, toPlayer(r));
  }

  private selectPlayers(sessionId: string): Promise<PlayerRow[]> {
    return this.db
      .select(playerColumns)
      .from(gamePlayers)
      .innerJoin(users, eq(users.id, gamePlayers.userId))
      .where(eq(gamePlayers.sessionId, sessionId))
      .orderBy(asc(gamePlayers.createdAt));
  }

  private async loadFromDb(sessionId: string): Promise<GameState | null> {
    const [session] = await this.db.select().from(gameSessions).where(eq(gameSessions.id, sessionId));
    if (!session) return null;
    const players = await this.selectPlayers(sessionId);
    const objects = players.length
      ? await this.db
          .select()
          .from(gameObjects)
          .where(
            and(
              inArray(
                gameObjects.playerId,
                players.map((p) => p.playerId),
              ),
              eq(gameObjects.round, session.round),
            ),
          )
          .orderBy(asc(gameObjects.createdAt))
      : [];
    const state: GameState = {
      sessionId,
      hostUserId: session.hostUserId,
      mapId: session.mapId,
      status: session.status,
      round: session.round,
      phaseEndsAt: session.phaseEndsAt,
      seq: 0,
      players: new Map(players.map((r) => [r.playerId, toPlayer(r)])),
      objects: new Map(
        objects.map((o) => [
          o.id,
          {
            objectId: o.id,
            playerId: o.playerId,
            round: o.round,
            assetKey: o.assetUrl,
            transform: o.transform,
            isDisguise: o.isDisguise,
            createdAt: o.createdAt,
            updatedAt: o.updatedAt,
            lastClientSeq: -1,
            transformDirty: false,
          } satisfies ObjectState,
        ]),
      ),
      playingSeconds: null,
      timer: null,
      lock: Promise.resolve(),
      snapshotSeq: -1,
    };
    await this.onLoad?.(state);
    return state;
  }
}
