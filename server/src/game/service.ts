import { and, asc, count, desc, eq, gt, inArray, ne, or, sql } from "drizzle-orm";
import type { Transform } from "../contracts/transform.ts";
import { normalizeTransform } from "../contracts/transform.ts";
import type { FinishReason, PublicObject, ServerMessage, SnapshotPayload } from "../contracts/ws.ts";
import type { Db } from "../db/client.ts";
import { gameObjects, gamePlayers, gameSessions, type RoundCycle, users } from "../db/schema.ts";
import { ApiError } from "../http/errors.ts";
import type { EventLog } from "../log/event-log.ts";
import type { MapCatalog } from "../maps/maps.ts";
import type { AssetStorage } from "../storage/assets.ts";
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
  ASSET_CONTENT_TYPE,
  CAPTURE_COOLDOWN_MS,
  CAPTURE_SCORE,
  DEFAULT_PLAYING_SECONDS,
  DEFAULT_PREPARING_SECONDS,
  DOWNLOAD_URL_TTL_SECONDS,
  UPLOAD_URL_TTL_SECONDS,
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

/** 一覧で返すセッションの進行状態。メモリに載っていればそちらが新しい（WS の接続・タイマーで変わる） */
function liveHeader(r: typeof gameSessions.$inferSelect, live: GameState | null) {
  return {
    status: live?.status ?? r.status,
    round: live?.round ?? r.round,
    mapId: live?.mapId ?? r.mapId,
    phaseEndsAt: iso(live ? live.phaseEndsAt : r.phaseEndsAt),
  };
}

/**
 * ゲームのルールと状態の変化（仕様書 4章・5章・6章）。REST・WS・タイマーのどれから来ても、ここを通る。
 * セッションごとの操作は withLock で1つずつ順に処理し、DB に確定させてからメモリを変えて配信する。
 */
export class GameService {
  private readonly snapshotTimer: ReturnType<typeof setInterval>;
  /** objectId / playerId → 最後に Transform をログへ書いた時刻 */
  private readonly lastTransformLog = new Map<string, number>();
  /**
   * assetKey → アップロード URL を渡した相手と申告した大きさ。
   * 所有者を残せない置き場（S3）でも、オブジェクト登録のときに所有者・大きさを確かめるため（再起動で消える）
   */
  private readonly uploadGrants = new Map<string, { playerId: string; sizeBytes: number; expiresAt: number }>();
  private disposed = false;

  constructor(
    private readonly db: Db,
    readonly registry: GameRegistry,
    readonly hub: Hub,
    readonly storage: ObjectStorage,
    readonly assets: AssetStorage,
    readonly log: EventLog,
    readonly maps: MapCatalog,
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

  /** userId が null なら管理画面から（ホストでなくてよい） */
  private requireHost(state: GameState, userId: string | null) {
    if (userId !== null && state.hostUserId !== userId) throw new ApiError(403, "NOT_HOST", "Only the host can do this.");
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
      cycle: session.cycle ? { resultSeconds: session.cycle.resultSeconds } : null,
      createdAt: session.createdAt.toISOString(),
      players,
    };
  }

  /** 受け手に合わせたオブジェクトに、そのまま GET できる assetUrl を付ける。見せてはいけなければ null */
  private wireObject(state: GameState, viewer: PlayerState, o: ObjectState, baseUrl?: string): PublicObject | null {
    const view = publicObject(state, viewer, o);
    if (!view) return null;
    return { ...view, assetUrl: this.assets.presignDownload(o.assetKey, DOWNLOAD_URL_TTL_SECONDS, baseUrl) };
  }

  /** 受け手に見せてよい最新状態（WS の session.snapshot と GET /state で同じもの） */
  snapshotPayload(state: GameState, me: PlayerState, baseUrl?: string): SnapshotPayload {
    const objects = [];
    for (const o of state.objects.values()) {
      const view = this.wireObject(state, me, o, baseUrl);
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

  snapshotMessage(state: GameState, me: PlayerState, baseUrl?: string): ServerMessage {
    return {
      type: "session.snapshot",
      round: state.round,
      seq: state.seq,
      timestamp: Date.now(),
      payload: this.snapshotPayload(state, me, baseUrl),
    };
  }

  /** 繋いでいる全員に、それぞれに見せてよい snapshot を送り直す */
  private pushSnapshots(state: GameState) {
    this.hub.fanout(state.sessionId, (pid, baseUrl) => {
      const p = state.players.get(pid);
      return p ? this.snapshotMessage(state, p, baseUrl) : null;
    });
  }

  // ================= アセット =================

  /** POST /v1/assets/uploads。毎回新しいキー（仕様書「過去のアセットは上書きせず新規キーを発行」） */
  async issueUpload(sessionId: string, userId: string, sizeBytes: number, baseUrl?: string) {
    const { me } = await this.requireParticipant(sessionId, userId);
    const assetKey = `sessions/${sessionId}/assets/${crypto.randomUUID()}.glb`;
    const expiresAt = Date.now() + UPLOAD_URL_TTL_SECONDS * 1000;
    const uploadUrl = this.assets.presignUpload(assetKey, ASSET_CONTENT_TYPE, UPLOAD_URL_TTL_SECONDS, {
      sizeBytes,
      ownerPlayerId: me.playerId,
      baseUrl,
    });
    // 期限切れの記録は掃除する（アップロード済みのものは置き場の meta / stat で確かめられる）
    for (const [k, g] of this.uploadGrants) if (g.expiresAt + 24 * 3600_000 < Date.now()) this.uploadGrants.delete(k);
    this.uploadGrants.set(assetKey, { playerId: me.playerId, sizeBytes, expiresAt });
    return {
      assetKey,
      uploadUrl,
      method: "PUT" as const,
      headers: { "Content-Type": ASSET_CONTENT_TYPE },
      expiresAt: new Date(expiresAt).toISOString(),
    };
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
    body: {
      preparingSeconds: number;
      playingSeconds?: number;
      resultSeconds?: number;
      roles: { playerId: string; role: Role }[];
    },
  ) {
    const state = await this.requireSession(sessionId);
    this.requireHost(state, userId);
    await withLock(state, () => this.startLocked(state, body, null));
    return this.sessionDetail(sessionId);
  }

  /**
   * 準備を始める（waiting か finished から）。resultSeconds があれば、ラウンドが終わってからその秒数で
   * 次のラウンドを同じ設定で自動で始める（cycle）。無ければ cycle は外れ、次はホストが始める
   */
  private async startLocked(
    state: GameState,
    body: { preparingSeconds: number; playingSeconds?: number; resultSeconds?: number; roles: { playerId: string; role: Role }[] },
    byPlayerId: string | null,
  ) {
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

    const cycle: RoundCycle | null =
      body.resultSeconds !== undefined
        ? {
            preparingSeconds: body.preparingSeconds,
            playingSeconds: body.playingSeconds ?? DEFAULT_PLAYING_SECONDS,
            resultSeconds: body.resultSeconds,
            roles: [...roles].map(([playerId, role]) => ({ playerId, role })),
          }
        : null;
    const from = state.status;
    const round = from === "finished" ? state.round + 1 : state.round;
    const endsAt = new Date(Date.now() + body.preparingSeconds * 1000);
    await this.db.transaction(async (tx) => {
      await this.transition(tx, state, from, { status: "preparing", round, phaseEndsAt: endsAt, cycle });
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
    state.cycle = cycle;
    for (const p of state.players.values()) {
      p.role = roles.get(p.playerId) ?? null;
      p.isCaught = false;
      p.lastCaptureAt = 0;
    }
    this.announcePhase(state, byPlayerId, {
      roles: [...state.players.values()].map((p) => ({ playerId: p.playerId, role: p.role })),
    });
  }

  /**
   * cycle の役割で次のラウンドを始める。cycle に無い参加者（後から入った人）はケイ。
   * 誰も繋いでいなければ（みんな帰った部屋）始めずにサイクルを止める
   */
  private async startNextLocked(state: GameState, force = false) {
    const cycle = state.cycle;
    if (!cycle) return;
    if (!force && ![...state.players.values()].some((p) => p.connected)) {
      await this.stopCycleLocked(state, "nobody_connected");
      return;
    }
    await this.registry.syncPlayers(state);
    const listed = new Map(cycle.roles.map((r) => [r.playerId, r.role]));
    const roles = [...state.players.values()].map((p) => ({ playerId: p.playerId, role: listed.get(p.playerId) ?? ("seeker" as Role) }));
    await this.startLocked(
      state,
      { preparingSeconds: cycle.preparingSeconds, playingSeconds: cycle.playingSeconds, resultSeconds: cycle.resultSeconds, roles },
      null,
    );
  }

  /** userId が null なら管理画面から */
  async play(sessionId: string, userId: string | null, body: { playingSeconds?: number }) {
    const state = await this.requireSession(sessionId);
    this.requireHost(state, userId);
    await withLock(state, () => this.playLocked(state, body.playingSeconds, null));
    return this.sessionDetail(sessionId);
  }

  async finish(sessionId: string, userId: string | null) {
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
      this.transition(tx, state, "preparing", { status: "playing", round: state.round, phaseEndsAt: endsAt, cycle: state.cycle }),
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
    // cycle があれば、finished の期限 = 次のラウンドの準備が始まる時刻
    const nextAt = state.cycle ? new Date(Date.now() + state.cycle.resultSeconds * 1000) : null;
    await this.db.transaction(async (tx) => {
      await this.transition(tx, state, from, { status: "finished", round: state.round, phaseEndsAt: nextAt, cycle: state.cycle });
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
    state.phaseEndsAt = nextAt;
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
    to: { status: Status; round: number; phaseEndsAt: Date | null; cycle: RoundCycle | null },
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

  // ================= サイクル・マップ（ホストと管理画面） =================

  /** 次のラウンドへの自動の進行をやめる。finished で待っていたら期限も外す。userId が null なら管理画面から */
  async stopCycle(sessionId: string, userId: string | null) {
    const state = await this.requireSession(sessionId);
    this.requireHost(state, userId);
    await withLock(state, () => this.stopCycleLocked(state, userId === null ? "admin" : "host"));
    return this.sessionDetail(sessionId);
  }

  private async stopCycleLocked(state: GameState, reason: "host" | "admin" | "nobody_connected") {
    if (!state.cycle) return;
    const phaseEndsAt = state.status === "finished" ? null : state.phaseEndsAt;
    await this.db.transaction((tx) =>
      this.transition(tx, state, state.status, { status: state.status, round: state.round, phaseEndsAt, cycle: null }),
    );
    state.cycle = null;
    state.phaseEndsAt = phaseEndsAt;
    this.schedule(state);
    this.appendLog(state, null, "session.cycle_stopped", { reason });
    this.pushSnapshots(state);
  }

  /**
   * マップを替える（待機中と、ラウンドが終わって次を待っている間だけ）。繋いでいる全員に snapshot を送り直す。
   * userId が null なら管理画面から
   */
  async setMap(sessionId: string, userId: string | null, mapId: string) {
    const state = await this.requireSession(sessionId);
    this.requireHost(state, userId);
    if (!(await this.maps.has(mapId))) throw new ApiError(404, "MAP_NOT_FOUND", "Map not found.");
    await withLock(state, async () => {
      if (state.status !== "waiting" && state.status !== "finished") {
        throw new ApiError(409, "INVALID_GAME_PHASE", `The map cannot change while ${state.status}.`);
      }
      if (state.mapId === mapId) return;
      const updated = await this.db
        .update(gameSessions)
        .set({ mapId })
        .where(and(eq(gameSessions.id, state.sessionId), eq(gameSessions.status, state.status)))
        .returning({ id: gameSessions.id });
      if (updated.length === 0) throw new ApiError(409, "CONFLICT", "The session state changed. Reload and retry.");
      state.mapId = mapId;
      this.appendLog(state, null, "session.map_changed", { mapId });
      this.pushSnapshots(state);
    });
    return this.sessionDetail(sessionId);
  }

  /**
   * 管理画面の「次のラウンドを始める」。cycle があればその設定で、無ければホストがドロ・ほかはケイ、既定の長さで
   */
  async adminStart(sessionId: string) {
    const state = await this.requireSession(sessionId);
    await withLock(state, async () => {
      if (state.cycle) return this.startNextLocked(state, true);
      await this.registry.syncPlayers(state);
      const roles = [...state.players.values()].map((p) => ({
        playerId: p.playerId,
        role: (p.userId === state.hostUserId ? "hider" : "seeker") as Role,
      }));
      await this.startLocked(state, { preparingSeconds: DEFAULT_PREPARING_SECONDS, roles }, null);
    });
    return this.sessionDetail(sessionId);
  }

  /** 管理画面のセッション一覧（新しい順）。接続中かどうかはメモリに載っているものだけわかる */
  async adminSessions(limit: number) {
    const rows = await this.db.select().from(gameSessions).orderBy(desc(gameSessions.createdAt)).limit(limit);
    if (rows.length === 0) return [];
    const players = await this.db
      .select({
        id: gamePlayers.id,
        sessionId: gamePlayers.sessionId,
        userId: gamePlayers.userId,
        name: users.name,
        role: gamePlayers.role,
        score: gamePlayers.score,
        isCaught: gamePlayers.isCaught,
      })
      .from(gamePlayers)
      .innerJoin(users, eq(users.id, gamePlayers.userId))
      .where(inArray(gamePlayers.sessionId, rows.map((r) => r.id)))
      .orderBy(asc(gamePlayers.createdAt));
    const out = [];
    for (const r of rows) {
      const live = await this.registry.peek(r.id);
      out.push({
        id: r.id,
        hostUserId: r.hostUserId,
        ...liveHeader(r, live),
        cycle: (live ? live.cycle : r.cycle) ? { resultSeconds: (live ? live.cycle : r.cycle)!.resultSeconds } : null,
        createdAt: r.createdAt.toISOString(),
        players: players
          .filter((p) => p.sessionId === r.id)
          .map(({ sessionId: _, ...p }) => {
            const lp = live?.players.get(p.id);
            return {
              ...p,
              role: lp ? lp.role : p.role,
              score: lp?.score ?? p.score,
              isCaught: lp?.isCaught ?? p.isCaught,
              connected: lp?.connected ?? false,
            };
          }),
      });
    }
    return out;
  }

  /**
   * 参加前のセッション一覧（GET /v1/sessions）。役割・スコア・userId は返さない。
   * game_sessions に更新時刻の列が無いので、「最後の動き」は作成・最後の参加・最後の期限のうち新しいものとみなし、
   * それが staleMs より前なら外す。メモリに載っているもの（誰かが WS で触った・タイマーが動いている）は常に残す
   */
  async listSessions(limit: number, staleMs: number, now = new Date()) {
    const cutoff = new Date(now.getTime() - staleMs);
    const liveIds = (await this.registry.loaded()).map((s) => s.sessionId);
    const lastJoin = this.db
      .select({ sessionId: gamePlayers.sessionId, at: sql<Date>`max(${gamePlayers.createdAt})`.as("last_join_at") })
      .from(gamePlayers)
      .groupBy(gamePlayers.sessionId)
      .as("last_join");
    const rows = await this.db
      .select({ session: gameSessions, hostName: users.name })
      .from(gameSessions)
      .innerJoin(users, eq(users.id, gameSessions.hostUserId))
      .leftJoin(lastJoin, eq(lastJoin.sessionId, gameSessions.id))
      .where(
        or(
          gt(gameSessions.createdAt, cutoff),
          gt(gameSessions.phaseEndsAt, cutoff),
          gt(lastJoin.at, cutoff),
          liveIds.length > 0 ? inArray(gameSessions.id, liveIds) : undefined,
        ),
      )
      .orderBy(desc(gameSessions.createdAt))
      .limit(limit);
    if (rows.length === 0) return [];
    const counts = await this.db
      .select({ sessionId: gamePlayers.sessionId, n: count() })
      .from(gamePlayers)
      .where(inArray(gamePlayers.sessionId, rows.map((r) => r.session.id)))
      .groupBy(gamePlayers.sessionId);
    const countOf = new Map(counts.map((c) => [c.sessionId, c.n]));
    const out = [];
    for (const { session: r, hostName } of rows) {
      const live = await this.registry.peek(r.id);
      out.push({
        id: r.id,
        ...liveHeader(r, live),
        hostName,
        // WS で後から入った分もあるので、メモリの方が多ければそちらを使う
        playerCount: Math.max(countOf.get(r.id) ?? 0, live?.players.size ?? 0),
        connectedCount: live ? [...live.players.values()].filter((p) => p.connected).length : 0,
        createdAt: r.createdAt.toISOString(),
      });
    }
    return out;
  }

  // ================= 期限（サーバー時刻が正） =================

  /** 準備中・本番中と、次のラウンドを待っている finished なら期限にタイマーを掛ける */
  private schedule(state: GameState) {
    if (state.timer) clearTimeout(state.timer);
    state.timer = null;
    if (this.disposed || !state.phaseEndsAt) return;
    const waitsNext = state.status === "finished" && state.cycle !== null;
    if (state.status !== "preparing" && state.status !== "playing" && !waitsNext) return;
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
        else if (status === "playing") await this.finishLocked(state, "time_up");
        else await this.startNextLocked(state);
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
    if (me.role !== "hider") throw new ApiError(403, "FORBIDDEN_ROLE", "Only hiders can edit objects.");
    if (me.isCaught) throw new ApiError(409, "PLAYER_CAUGHT", "You were caught in this round.");
  }

  private requireOwnObject(state: GameState, me: PlayerState, objectId: string): ObjectState {
    const o = state.objects.get(objectId);
    if (!o) throw new ApiError(404, "OBJECT_NOT_FOUND", "Object not found in the current round.");
    if (o.playerId !== me.playerId) throw new ApiError(403, "NOT_OBJECT_OWNER", "You do not own this object.");
    return o;
  }

  /**
   * assetKey がこのセッションの、この人が上げた、アップロード済みの GLB か。
   * 置き場が stat を持たない（テストの差し替えなど）ときは、分かる範囲（セッション・渡した相手）だけ確かめる
   */
  private async requireAsset(state: GameState, me: PlayerState, assetKey: string) {
    if (!assetKey.startsWith(`sessions/${state.sessionId}/assets/`)) {
      throw new ApiError(400, "ASSET_NOT_IN_SESSION", "assetKey belongs to a different session.");
    }
    const grant = this.uploadGrants.get(assetKey);
    const stat = this.assets.stat ? await this.assets.stat(assetKey) : undefined;
    if (stat === null) throw new ApiError(400, "ASSET_NOT_UPLOADED", "The asset has not been uploaded.");
    const owner = stat?.ownerPlayerId ?? grant?.playerId;
    if (owner && owner !== me.playerId) {
      throw new ApiError(403, "NOT_OBJECT_OWNER", "The asset was uploaded by another player.");
    }
    if (stat && grant && stat.size !== grant.sizeBytes) {
      throw new ApiError(400, "ASSET_SIZE_MISMATCH", `The uploaded asset is not ${grant.sizeBytes} bytes.`);
    }
    if (stat?.contentType && stat.contentType.split(";")[0] !== ASSET_CONTENT_TYPE) {
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
      (pid, baseUrl) => {
        const viewer = state.players.get(pid);
        if (!viewer) return null;
        if (secretOnly && viewer.role !== "hider" && state.status !== "finished") return null;
        const payload = this.wireObject(state, viewer, o, baseUrl);
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
      if (me.role !== "seeker") throw new ApiError(403, "FORBIDDEN_ROLE", "Only seekers can capture.");
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
