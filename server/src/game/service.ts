import { and, asc, eq, ne, sql } from "drizzle-orm";
import type { Transform } from "../contracts/transform.ts";
import { normalizeTransform } from "../contracts/transform.ts";
import type { FinishReason, ServerMessage, SnapshotPayload } from "../contracts/ws.ts";
import type { Db } from "../db/client.ts";
import { gameObjects, gamePlayers, gameSessions, users } from "../db/schema.ts";
import { ApiError } from "../http/errors.ts";
import type { EventLog } from "../log/event-log.ts";
import type { ObjectStorage } from "../storage/storage.ts";
import type { Hub } from "./hub.ts";
import {
  type GameRegistry,
  type GameState,
  type ObjectState,
  type PlayerState,
  type Role,
  type Status,
  withLock,
} from "./registry.ts";
import {
  CAPTURE_COOLDOWN_MS,
  CAPTURE_SCORE,
  DEFAULT_PLAYING_SECONDS,
  MAX_OBJECTS_PER_PLAYER_ROUND,
  MISS_SCORE,
  SURVIVE_SCORE,
} from "./rules.ts";
import { canSeeObjects, canSeePlayerPose, publicObject } from "./visibility.ts";

/** Snapshot をログに書く間隔（仕様書 7.1「一定時間ごと（例:30秒）」） */
const SNAPSHOT_INTERVAL_MS = 30_000;
/** 高頻度の Transform をログに書く間隔（仕様書 7.1「Transform（一部間引き）」） */
const TRANSFORM_LOG_INTERVAL_MS = 1_000;
/** 期限のタイマーが少し早く鳴っても、これより近ければ期限が来たとみなす */
const DEADLINE_SLACK_MS = 50;

const iso = (d: Date | null) => d?.toISOString() ?? null;

/**
 * ゲームのルールと状態の変化（仕様書 4章・5章・6章）。REST・WS・タイマーのどれから来ても、ここを通る。
 * セッションごとの操作は withLock で1つずつ順に処理し、DB に確定させてからメモリを変えて配信する。
 */
export class GameService {
  private readonly snapshotTimer: ReturnType<typeof setInterval>;
  /** objectId / playerId → 最後に Transform をログへ書いた時刻 */
  private readonly lastTransformLog = new Map<string, number>();
  private disposed = false;

  constructor(
    private readonly db: Db,
    readonly registry: GameRegistry,
    readonly hub: Hub,
    readonly storage: ObjectStorage,
    readonly log: EventLog,
  ) {
    registry.onLoad = async (state) => {
      // 再起動を挟んでも seq を巻き戻さない（書けたログの続きから数える）
      if (state.status !== "waiting") state.seq = await log.lastSeq(state.sessionId, state.round);
      this.schedule(state);
    };
    this.snapshotTimer = setInterval(() => void this.periodicSnapshots(), SNAPSHOT_INTERVAL_MS);
    this.snapshotTimer.unref?.();
  }

  async dispose(): Promise<void> {
    this.disposed = true;
    clearInterval(this.snapshotTimer);
    for (const s of await this.registry.loaded()) {
      if (s.timer) clearTimeout(s.timer);
      s.timer = null;
    }
    await this.log.dispose();
  }

  // ================= 参照 =================

  async requireSession(sessionId: string): Promise<GameState> {
    const state = await this.registry.load(sessionId);
    if (!state) throw new ApiError(404, "SESSION_NOT_FOUND", "Session not found.");
    return state;
  }

  async requireParticipant(sessionId: string, userId: string): Promise<{ state: GameState; me: PlayerState }> {
    const state = await this.requireSession(sessionId);
    const me = await this.registry.findPlayerByUser(state, userId);
    if (!me) throw new ApiError(403, "NOT_A_PARTICIPANT", "You are not a participant of this session.");
    return { state, me };
  }

  private requireHost(state: GameState, userId: string) {
    if (state.hostUserId !== userId) throw new ApiError(403, "NOT_HOST", "Only the host can do this.");
  }

  private hostPlayerId(state: GameState): string | null {
    for (const p of state.players.values()) if (p.userId === state.hostUserId) return p.playerId;
    return null;
  }

  /** GET /v1/sessions/{id} の形（DB から読む） */
  async sessionDetail(sessionId: string) {
    const [session] = await this.db.select().from(gameSessions).where(eq(gameSessions.id, sessionId));
    if (!session) throw new ApiError(404, "SESSION_NOT_FOUND", "Session not found.");
    const players = await this.db
      .select({
        id: gamePlayers.id,
        userId: gamePlayers.userId,
        name: users.name,
        role: gamePlayers.role,
        score: gamePlayers.score,
        isCaught: gamePlayers.isCaught,
      })
      .from(gamePlayers)
      .innerJoin(users, eq(users.id, gamePlayers.userId))
      .where(eq(gamePlayers.sessionId, sessionId))
      .orderBy(asc(gamePlayers.createdAt));
    return {
      id: session.id,
      hostUserId: session.hostUserId,
      status: session.status,
      round: session.round,
      mapId: session.mapId,
      phaseEndsAt: iso(session.phaseEndsAt),
      createdAt: session.createdAt.toISOString(),
      players,
    };
  }

  /** 受け手に見せてよい最新状態（WS の session.snapshot と GET /state で同じもの） */
  snapshotPayload(state: GameState, me: PlayerState): SnapshotPayload {
    const objects = [];
    for (const o of state.objects.values()) {
      const view = publicObject(state, me, o);
      if (view) objects.push(view);
    }
    return {
      session: {
        id: state.sessionId,
        status: state.status,
        round: state.round,
        mapId: state.mapId,
        hostPlayerId: this.hostPlayerId(state),
        phaseEndsAt: iso(state.phaseEndsAt),
      },
      you: { playerId: me.playerId, role: me.role },
      players: [...state.players.values()].map((p) => ({
        playerId: p.playerId,
        name: p.name,
        role: p.role,
        score: p.score,
        isCaught: p.isCaught,
        connected: p.connected,
        transform: canSeePlayerPose(state, me, p) ? p.transform : null,
      })),
      objects,
    };
  }

  snapshotMessage(state: GameState, me: PlayerState): ServerMessage {
    return {
      type: "session.snapshot",
      round: state.round,
      seq: state.seq,
      timestamp: Date.now(),
      payload: this.snapshotPayload(state, me),
    };
  }

  /** 繋いでいる全員に、それぞれに見せてよい snapshot を送り直す */
  private pushSnapshots(state: GameState) {
    this.hub.fanout(state.sessionId, (pid) => {
      const p = state.players.get(pid);
      return p ? this.snapshotMessage(state, p) : null;
    });
  }

  // ================= 参加・接続 =================

  /** POST /sessions/{id}/join。初めて入った人がいれば、繋いでいる人に snapshot を送り直す */
  async join(sessionId: string, userId: string): Promise<string> {
    const [session] = await this.db.select({ id: gameSessions.id }).from(gameSessions).where(eq(gameSessions.id, sessionId));
    if (!session) throw new ApiError(404, "SESSION_NOT_FOUND", "Session not found.");
    const inserted = await this.db
      .insert(gamePlayers)
      .values({ sessionId, userId })
      .onConflictDoNothing({ target: [gamePlayers.sessionId, gamePlayers.userId] })
      .returning({ id: gamePlayers.id });
    if (inserted[0]) {
      // まだ誰も繋いでいない（メモリに無い）なら、後で読み込むときに DB から拾われる
      const state = await this.registry.peek(sessionId);
      if (state && (await this.registry.findPlayerByUser(state, userId))) this.pushSnapshots(state);
      return inserted[0].id;
    }
    const [existing] = await this.db
      .select({ id: gamePlayers.id })
      .from(gamePlayers)
      .where(and(eq(gamePlayers.sessionId, sessionId), eq(gamePlayers.userId, userId)));
    return existing!.id;
  }

  // ================= ラウンド（仕様書 4章） =================

  async startRound(
    sessionId: string,
    userId: string,
    body: { preparingSeconds: number; playingSeconds?: number; roles: { playerId: string; role: Role }[] },
  ) {
    const state = await this.requireSession(sessionId);
    this.requireHost(state, userId);
    await withLock(state, async () => {
      if (state.status !== "waiting" && state.status !== "finished") {
        throw new ApiError(409, "INVALID_GAME_PHASE", `A round cannot start while ${state.status}.`);
      }
      await this.registry.syncPlayers(state);
      const roles = new Map<string, Role>();
      for (const r of body.roles) {
        if (!state.players.has(r.playerId)) {
          throw new ApiError(400, "UNKNOWN_PLAYER", `Player ${r.playerId} is not in this session.`);
        }
        if (roles.has(r.playerId)) throw new ApiError(400, "DUPLICATE_PLAYER", `Player ${r.playerId} is listed twice.`);
        roles.set(r.playerId, r.role);
      }
      if (![...roles.values()].includes("hider")) throw new ApiError(400, "NO_HIDER", "At least one hider is required.");

      const from = state.status;
      const round = from === "finished" ? state.round + 1 : state.round;
      const endsAt = new Date(Date.now() + body.preparingSeconds * 1000);
      await this.db.transaction(async (tx) => {
        await this.transition(tx, state, from, { status: "preparing", round, phaseEndsAt: endsAt });
        for (const p of state.players.values()) {
          await tx
            .update(gamePlayers)
            .set({ role: roles.get(p.playerId) ?? null, isCaught: false })
            .where(eq(gamePlayers.id, p.playerId));
        }
      });

      if (round !== state.round) {
        state.round = round;
        state.seq = 0;
        state.objects.clear(); // 前のラウンドのオブジェクトは出さない（DB には残る）
        state.snapshotSeq = -1;
      }
      state.status = "preparing";
      state.phaseEndsAt = endsAt;
      state.playingSeconds = body.playingSeconds ?? null;
      for (const p of state.players.values()) {
        p.role = roles.get(p.playerId) ?? null;
        p.isCaught = false;
        p.lastCaptureAt = 0;
      }
      this.announcePhase(state, null, {
        roles: [...state.players.values()].map((p) => ({ playerId: p.playerId, role: p.role })),
      });
    });
    return this.sessionDetail(sessionId);
  }

  async play(sessionId: string, userId: string, body: { playingSeconds?: number }) {
    const state = await this.requireSession(sessionId);
    this.requireHost(state, userId);
    await withLock(state, () => this.playLocked(state, body.playingSeconds, null));
    return this.sessionDetail(sessionId);
  }

  async finish(sessionId: string, userId: string) {
    const state = await this.requireSession(sessionId);
    this.requireHost(state, userId);
    await withLock(state, () => this.finishLocked(state, "host"));
    return this.sessionDetail(sessionId);
  }

  private async playLocked(state: GameState, playingSeconds: number | undefined, byPlayerId: string | null) {
    if (state.status !== "preparing") {
      throw new ApiError(409, "INVALID_GAME_PHASE", `Cannot start playing while ${state.status}.`);
    }
    const secs = playingSeconds ?? state.playingSeconds ?? DEFAULT_PLAYING_SECONDS;
    const endsAt = new Date(Date.now() + secs * 1000);
    await this.db.transaction((tx) =>
      this.transition(tx, state, "preparing", { status: "playing", round: state.round, phaseEndsAt: endsAt }),
    );
    state.status = "playing";
    state.phaseEndsAt = endsAt;
    this.announcePhase(state, byPlayerId, {});
  }

  /**
   * ラウンドを終える。本番中に終わったら、見つからなかったドロに SURVIVE_SCORE。
   * WS で動かしただけでまだ DB に無い Transform も、ここで書いておく
   */
  private async finishLocked(state: GameState, reason: FinishReason) {
    if (state.status !== "preparing" && state.status !== "playing") {
      throw new ApiError(409, "INVALID_GAME_PHASE", `Cannot finish while ${state.status}.`);
    }
    const from = state.status;
    const survivors =
      from === "playing" ? [...state.players.values()].filter((p) => p.role === "hider" && !p.isCaught) : [];
    const newScores = new Map<string, number>();
    await this.db.transaction(async (tx) => {
      await this.transition(tx, state, from, { status: "finished", round: state.round, phaseEndsAt: null });
      for (const p of survivors) {
        const [row] = await tx
          .update(gamePlayers)
          .set({ score: sql`${gamePlayers.score} + ${SURVIVE_SCORE}` })
          .where(eq(gamePlayers.id, p.playerId))
          .returning({ score: gamePlayers.score });
        newScores.set(p.playerId, row!.score);
      }
      for (const o of state.objects.values()) {
        if (!o.transformDirty) continue;
        await tx
          .update(gameObjects)
          .set({ transform: o.transform, updatedAt: new Date() })
          .where(eq(gameObjects.id, o.objectId));
      }
    });
    for (const o of state.objects.values()) o.transformDirty = false;
    for (const [pid, score] of newScores) state.players.get(pid)!.score = score;
    state.status = "finished";
    state.phaseEndsAt = null;
    for (const p of survivors) {
      this.appendLog(state, p.playerId, "score.changed", { playerId: p.playerId, delta: SURVIVE_SCORE, score: p.score, reason: "survived" });
    }
    this.announcePhase(state, null, { reason });
    await this.log.flush(state.sessionId);
  }

  /** game_sessions を from から次の状態へ。from のままでなければ（二重実行など）409 */
  private async transition(
    tx: Pick<Db, "update">,
    state: GameState,
    from: Status,
    to: { status: Status; round: number; phaseEndsAt: Date | null },
  ) {
    const updated = await tx
      .update(gameSessions)
      .set(to)
      .where(and(eq(gameSessions.id, state.sessionId), eq(gameSessions.status, from), eq(gameSessions.round, state.round)))
      .returning({ id: gameSessions.id });
    if (updated.length === 0) throw new ApiError(409, "CONFLICT", "The session state changed. Reload and retry.");
  }

  /** フェーズが変わったことを全員に知らせ、見せてよい範囲が変わるので snapshot も送り直す */
  private announcePhase(
    state: GameState,
    byPlayerId: string | null,
    extra: { reason?: FinishReason; roles?: { playerId: string; role: Role | null }[] },
  ) {
    state.seq += 1;
    const seq = state.seq;
    const timestamp = Date.now();
    const roles = [...state.players.values()].map((p) => ({ playerId: p.playerId, role: p.role }));
    const scores = [...state.players.values()].map((p) => ({ playerId: p.playerId, score: p.score }));
    const hiders = [...state.players.values()].filter((p) => p.role === "hider");
    const allCaught = hiders.length > 0 && hiders.every((p) => p.isCaught);

    this.hub.fanout(state.sessionId, (pid) => {
      const me = state.players.get(pid);
      if (!me) return null;
      return {
        type: "session.phase_changed",
        round: state.round,
        seq,
        timestamp,
        payload: {
          status: state.status,
          round: state.round,
          phaseEndsAt: iso(state.phaseEndsAt),
          roles,
          ...(state.status === "finished"
            ? {
                result: {
                  reason: extra.reason ?? "host",
                  captured: me.role === "hider" ? me.isCaught : allCaught,
                  score: me.score,
                  scores,
                },
              }
            : {}),
        },
      };
    });
    this.pushSnapshots(state);

    this.appendLog(state, byPlayerId, "session.phase_changed", {
      status: state.status,
      phaseEndsAt: iso(state.phaseEndsAt),
      ...(extra.roles ? { roles: extra.roles } : {}),
      ...(extra.reason ? { reason: extra.reason } : {}),
    });
    void this.saveSnapshot(state);
    this.schedule(state);
  }

  // ================= 期限（サーバー時刻が正） =================

  /** 準備中・本番中なら期限にタイマーを掛ける */
  private schedule(state: GameState) {
    if (state.timer) clearTimeout(state.timer);
    state.timer = null;
    if (this.disposed || !state.phaseEndsAt) return;
    if (state.status !== "preparing" && state.status !== "playing") return;
    const { round, status } = state;
    const delay = Math.max(0, state.phaseEndsAt.getTime() - Date.now());
    state.timer = setTimeout(() => void this.onDeadline(state, round, status), delay);
    state.timer.unref?.();
  }

  private async onDeadline(state: GameState, round: number, status: Status) {
    try {
      await withLock(state, async () => {
        if (state.round !== round || state.status !== status || !state.phaseEndsAt) return; // もう進んだ
        if (state.phaseEndsAt.getTime() - Date.now() > DEADLINE_SLACK_MS) return this.schedule(state);
        if (status === "preparing") await this.playLocked(state, undefined, null);
        else await this.finishLocked(state, "time_up");
      });
    } catch (e) {
      // ホストが同時に進めた（409）などは無視してよい
      if (!(e instanceof ApiError && e.status === 409)) console.error("期限での自動遷移に失敗しました", e);
    }
  }

  // ================= オブジェクト（ドロ） =================

  private requireEditableBy(state: GameState, me: PlayerState) {
    if (state.status !== "preparing" && state.status !== "playing") {
      throw new ApiError(409, "INVALID_GAME_PHASE", "This action is not allowed in the current phase.");
    }
    if (me.role !== "hider") throw new ApiError(403, "NOT_A_HIDER", "Only hiders can do this.");
    if (me.isCaught) throw new ApiError(409, "PLAYER_CAUGHT", "You were caught in this round.");
  }

  private requireOwnObject(state: GameState, me: PlayerState, objectId: string): ObjectState {
    const o = state.objects.get(objectId);
    if (!o) throw new ApiError(404, "OBJECT_NOT_FOUND", "Object not found in the current round.");
    if (o.playerId !== me.playerId) throw new ApiError(403, "NOT_OWNER", "This object belongs to another player.");
    return o;
  }

  /** assetKey がこのセッションの、この人が上げた、確認済みの GLB か */
  private async requireAsset(state: GameState, me: PlayerState, assetKey: string) {
    if (!assetKey.startsWith(`sessions/${state.sessionId}/assets/`)) {
      throw new ApiError(400, "INVALID_ASSET", "The asset does not belong to this session.");
    }
    const meta = await this.storage.head(assetKey);
    if (!meta) throw new ApiError(400, "ASSET_NOT_UPLOADED", "The asset has not been uploaded.");
    if (meta.ownerPlayerId !== me.playerId) throw new ApiError(403, "NOT_OWNER", "The asset was uploaded by another player.");
    if (meta.contentType !== "model/gltf-binary") {
      throw new ApiError(400, "INVALID_ASSET", "Objects need a model/gltf-binary asset.");
    }
  }

  private objectDto(o: ObjectState) {
    return {
      id: o.objectId,
      round: o.round,
      assetKey: o.assetKey,
      transform: o.transform,
      isDisguise: o.isDisguise,
      createdAt: o.createdAt.toISOString(),
      updatedAt: o.updatedAt.toISOString(),
    };
  }

  /** オブジェクトの変化を、見せてよい人にだけ配る。secretOnly は擬態先の切り替えなど、ケイに知らせない変化 */
  private broadcastObject(
    state: GameState,
    type: "object.created" | "object.updated",
    o: ObjectState,
    except: string | null,
    secretOnly = false,
  ) {
    const seq = state.seq;
    const timestamp = Date.now();
    this.hub.fanout(
      state.sessionId,
      (pid) => {
        const viewer = state.players.get(pid);
        if (!viewer) return null;
        if (secretOnly && viewer.role !== "hider" && state.status !== "finished") return null;
        const payload = publicObject(state, viewer, o);
        return payload ? { type, round: state.round, seq, timestamp, payload } : null;
      },
      except ?? undefined,
    );
  }

  async createObject(sessionId: string, userId: string, body: { assetKey: string; transform: Transform }) {
    const { state, me } = await this.requireParticipant(sessionId, userId);
    return withLock(state, async () => {
      this.requireEditableBy(state, me);
      const mine = [...state.objects.values()].filter((o) => o.playerId === me.playerId).length;
      if (mine >= MAX_OBJECTS_PER_PLAYER_ROUND) {
        throw new ApiError(409, "TOO_MANY_OBJECTS", `Up to ${MAX_OBJECTS_PER_PLAYER_ROUND} objects per round.`);
      }
      await this.requireAsset(state, me, body.assetKey);
      const transform = normalizeTransform(body.transform);
      const [row] = await this.db
        .insert(gameObjects)
        .values({ playerId: me.playerId, round: state.round, assetUrl: body.assetKey, transform })
        .returning();
      const o: ObjectState = {
        objectId: row!.id,
        playerId: me.playerId,
        round: row!.round,
        assetKey: row!.assetUrl,
        transform: row!.transform,
        isDisguise: row!.isDisguise,
        createdAt: row!.createdAt,
        updatedAt: row!.updatedAt,
        lastClientSeq: -1,
        transformDirty: false,
      };
      state.objects.set(o.objectId, o);
      state.seq += 1;
      this.broadcastObject(state, "object.created", o, null);
      this.appendLog(state, me.playerId, "object.created", this.internalObject(o));
      return this.objectDto(o);
    });
  }

  async updateObject(
    sessionId: string,
    userId: string,
    objectId: string,
    body: { assetKey?: string; transform?: Transform },
  ) {
    const { state, me } = await this.requireParticipant(sessionId, userId);
    return withLock(state, async () => {
      this.requireEditableBy(state, me);
      const o = this.requireOwnObject(state, me, objectId);
      if (body.assetKey !== undefined) await this.requireAsset(state, me, body.assetKey);
      const transform = body.transform ? normalizeTransform(body.transform) : undefined;
      const [row] = await this.db
        .update(gameObjects)
        .set({
          ...(body.assetKey !== undefined ? { assetUrl: body.assetKey } : {}),
          ...(transform ? { transform } : {}),
          updatedAt: new Date(),
        })
        .where(eq(gameObjects.id, objectId))
        .returning();
      o.assetKey = row!.assetUrl;
      o.transform = row!.transform;
      o.updatedAt = row!.updatedAt;
      if (transform) o.transformDirty = false;
      state.seq += 1;
      this.broadcastObject(state, "object.updated", o, null);
      this.appendLog(state, me.playerId, "object.updated", {
        objectId,
        ...(body.assetKey !== undefined ? { assetKey: o.assetKey } : {}),
        ...(transform ? { transform } : {}),
      });
      return this.objectDto(o);
    });
  }

  async deleteObject(sessionId: string, userId: string, objectId: string) {
    const { state, me } = await this.requireParticipant(sessionId, userId);
    await withLock(state, async () => {
      this.requireEditableBy(state, me);
      const o = this.requireOwnObject(state, me, objectId);
      await this.db.delete(gameObjects).where(eq(gameObjects.id, objectId));
      // 消す前に見えていた人にだけ知らせる
      const viewers = new Set(
        [...state.players.values()].filter((p) => canSeeObjects(state, p)).map((p) => p.playerId),
      );
      state.objects.delete(objectId);
      state.seq += 1;
      const seq = state.seq;
      const timestamp = Date.now();
      this.hub.fanout(state.sessionId, (pid) =>
        viewers.has(pid) ? { type: "object.deleted", round: state.round, seq, timestamp, payload: { objectId } } : null,
      );
      this.appendLog(state, me.playerId, "object.deleted", { objectId, wasDisguise: o.isDisguise });
    });
  }

  /** 擬態先を切り替える。前の擬態先は同じトランザクションで外す（1人1つまで） */
  async disguise(sessionId: string, userId: string, objectId: string) {
    const { state, me } = await this.requireParticipant(sessionId, userId);
    return withLock(state, async () => {
      this.requireEditableBy(state, me);
      const o = this.requireOwnObject(state, me, objectId);
      const previous = [...state.objects.values()].filter(
        (x) => x.playerId === me.playerId && x.isDisguise && x.objectId !== objectId,
      );
      if (!o.isDisguise) {
        const now = new Date();
        await this.db.transaction(async (tx) => {
          await tx
            .update(gameObjects)
            .set({ isDisguise: false, updatedAt: now })
            .where(
              and(
                eq(gameObjects.playerId, me.playerId),
                eq(gameObjects.round, state.round),
                eq(gameObjects.isDisguise, true),
                ne(gameObjects.id, objectId),
              ),
            );
          await tx.update(gameObjects).set({ isDisguise: true, updatedAt: now }).where(eq(gameObjects.id, objectId));
        });
        for (const x of previous) {
          x.isDisguise = false;
          x.updatedAt = now;
        }
        o.isDisguise = true;
        o.updatedAt = now;
        state.seq += 1;
        for (const x of [...previous, o]) this.broadcastObject(state, "object.updated", x, null, true);
        this.appendLog(state, me.playerId, "object.disguised", {
          objectId,
          previousObjectIds: previous.map((x) => x.objectId),
        });
      }
      return this.objectDto(o);
    });
  }

  // ================= 捕獲（ケイ） =================

  async capture(sessionId: string, userId: string, objectId: string) {
    const { state, me } = await this.requireParticipant(sessionId, userId);
    return withLock(state, async () => {
      if (me.role !== "seeker") throw new ApiError(403, "NOT_A_SEEKER", "Only seekers can capture.");
      if (state.status !== "playing") {
        throw new ApiError(409, "INVALID_GAME_PHASE", "Captures are only allowed while playing.");
      }
      const now = Date.now();
      const wait = me.lastCaptureAt + CAPTURE_COOLDOWN_MS - now;
      if (wait > 0) throw new RateLimitError(wait);
      me.lastCaptureAt = now;

      const o = state.objects.get(objectId);
      if (!o) throw new ApiError(404, "OBJECT_NOT_FOUND", "Object not found in the current round.");
      const owner = state.players.get(o.playerId);
      const hit = o.isDisguise && owner !== undefined && owner.role === "hider" && !owner.isCaught;

      if (!hit) {
        if (MISS_SCORE !== 0) {
          const [row] = await this.db
            .update(gamePlayers)
            .set({ score: sql`${gamePlayers.score} + ${MISS_SCORE}` })
            .where(eq(gamePlayers.id, me.playerId))
            .returning({ score: gamePlayers.score });
          me.score = row!.score;
        }
        state.seq += 1;
        this.appendLog(state, me.playerId, "capture.missed", { objectId, scoreDelta: MISS_SCORE, score: me.score });
        return { result: "miss" as const, scoreDelta: MISS_SCORE, score: me.score };
      }

      const scores = await this.db.transaction(async (tx) => {
        const caught = await tx
          .update(gamePlayers)
          .set({ isCaught: true })
          .where(and(eq(gamePlayers.id, owner.playerId), eq(gamePlayers.isCaught, false)))
          .returning({ score: gamePlayers.score });
        if (caught.length === 0) throw new ApiError(409, "CONFLICT", "The hider was already caught.");
        const [seeker] = await tx
          .update(gamePlayers)
          .set({ score: sql`${gamePlayers.score} + ${CAPTURE_SCORE}` })
          .where(eq(gamePlayers.id, me.playerId))
          .returning({ score: gamePlayers.score });
        return { hider: caught[0]!.score, seeker: seeker!.score };
      });
      owner.isCaught = true;
      owner.score = scores.hider;
      me.score = scores.seeker;
      state.seq += 1;
      const msg: ServerMessage = {
        type: "player.caught",
        round: state.round,
        seq: state.seq,
        timestamp: Date.now(),
        payload: {
          playerId: owner.playerId,
          byPlayerId: me.playerId,
          objectId,
          score: owner.score,
          seekerScore: me.score,
        },
      };
      this.hub.fanout(state.sessionId, () => msg);
      this.appendLog(state, me.playerId, "player.caught", { ...msg.payload, scoreDelta: CAPTURE_SCORE });

      // 仕様書 4章「全ドロの捕獲、または制限時間到来でラウンド終了」
      const hiders = [...state.players.values()].filter((p) => p.role === "hider");
      if (hiders.every((p) => p.isCaught)) await this.finishLocked(state, "all_caught");

      return { result: "caught" as const, scoreDelta: CAPTURE_SCORE, score: me.score, caughtPlayerId: owner.playerId };
    });
  }

  // ================= WS の高頻度 Transform =================

  /** プレイヤーの Pose。見せてよい相手にだけ中継する（DB には書かない） */
  playerTransform(state: GameState, me: PlayerState, clientSeq: number, transform: Transform): boolean {
    if (clientSeq <= me.lastClientSeq) return false; // 重複・逆順は捨てる
    me.lastClientSeq = clientSeq;
    me.transform = normalizeTransform(transform);
    state.seq += 1;
    const out: ServerMessage = {
      type: "player.transform",
      round: state.round,
      seq: state.seq,
      timestamp: Date.now(),
      payload: { playerId: me.playerId, transform: me.transform },
    };
    const text = JSON.stringify(out);
    for (const [otherId, conn] of this.hub.members(state.sessionId)) {
      const other = state.players.get(otherId);
      if (otherId !== me.playerId && other && canSeePlayerPose(state, other, me)) conn.send(text);
    }
    return true;
  }

  /**
   * ドラッグ中のオブジェクトの Transform（仕様書 6.2）。メモリに反映して配るだけで、確定は PATCH。
   * ラウンド終了時には、確定されなかった最後の位置も DB に書く
   */
  objectTransform(state: GameState, me: PlayerState, objectId: string, clientSeq: number, transform: Transform): boolean {
    this.requireEditableBy(state, me);
    const o = this.requireOwnObject(state, me, objectId);
    if (clientSeq <= o.lastClientSeq) return false;
    o.lastClientSeq = clientSeq;
    o.transform = normalizeTransform(transform);
    o.transformDirty = true;
    state.seq += 1;
    const seq = state.seq;
    const timestamp = Date.now();
    this.hub.fanout(
      state.sessionId,
      (pid) => {
        const viewer = state.players.get(pid);
        return viewer && canSeeObjects(state, viewer)
          ? { type: "object.transform", round: state.round, seq, timestamp, payload: { objectId, transform: o.transform } }
          : null;
      },
      me.playerId,
    );
    const last = this.lastTransformLog.get(objectId) ?? 0;
    if (timestamp - last >= TRANSFORM_LOG_INTERVAL_MS) {
      this.lastTransformLog.set(objectId, timestamp);
      this.appendLog(state, me.playerId, "object.transform", { objectId, transform: o.transform });
    }
    return true;
  }

  // ================= リプレイ（仕様書 7章） =================

  async replay(sessionId: string, userId: string, round: number) {
    const { state } = await this.requireParticipant(sessionId, userId);
    if (round > state.round) throw new ApiError(404, "ROUND_NOT_FOUND", "No such round.");
    if (round === state.round && state.status !== "finished") {
      throw new ApiError(409, "ROUND_NOT_FINISHED", "A replay is available after the round finishes.");
    }
    const { snapshots, events } = await this.log.read(sessionId, round);
    return { sessionId, round, snapshots: snapshots as Record<string, unknown>[], events };
  }

  // ================= ログ =================

  private internalObject(o: ObjectState) {
    return {
      objectId: o.objectId,
      playerId: o.playerId,
      assetKey: o.assetKey,
      transform: o.transform,
      isDisguise: o.isDisguise,
    };
  }

  private appendLog(state: GameState, playerId: string | null, type: string, payload: unknown) {
    this.log.append({
      sessionId: state.sessionId,
      round: state.round,
      seq: state.seq,
      timestamp: Date.now(),
      playerId,
      type,
      payload,
    });
  }

  /** 内部 Snapshot（擬態先を含む）。ラウンド開始・30秒ごと・終了時に書く */
  private async saveSnapshot(state: GameState) {
    if (state.snapshotSeq === state.seq) return;
    state.snapshotSeq = state.seq;
    await this.log.saveSnapshot(state.sessionId, state.round, state.seq, {
      sessionId: state.sessionId,
      round: state.round,
      seq: state.seq,
      timestamp: Date.now(),
      status: state.status,
      phaseEndsAt: iso(state.phaseEndsAt),
      players: [...state.players.values()].map((p) => ({
        playerId: p.playerId,
        name: p.name,
        role: p.role,
        score: p.score,
        isCaught: p.isCaught,
        transform: p.transform,
      })),
      objects: [...state.objects.values()].map((o) => this.internalObject(o)),
    });
  }

  private async periodicSnapshots() {
    for (const s of await this.registry.loaded()) {
      if (s.status === "preparing" || s.status === "playing") await this.saveSnapshot(s);
    }
  }
}

/** 捕獲の連打（仕様書 9章 429） */
export class RateLimitError extends ApiError {
  constructor(readonly retryAfterMs: number) {
    super(429, "RATE_LIMITED", "Too many capture attempts. Wait a moment.");
  }
}
