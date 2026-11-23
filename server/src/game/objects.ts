import type { Transform } from "../contracts/transform.ts";
import type { ServerMessage } from "../contracts/ws.ts";
import type { AssetStorage } from "../storage.ts";
import type { Connections } from "./connections.ts";
import type { GameState, PlayerState } from "./registry.ts";

export type ObjectState = {
  objectId: string;
  /** 所有者（game_players.id）。ケイには送らない */
  playerId: string;
  assetKey: string;
  transform: Transform;
  /** 擬態中か。ケイには送らない（仕様書 6.2-6） */
  isDisguise: boolean;
  /** 最後に受け付けた object.transform の clientSeq */
  lastClientSeq: number;
};

/** 署名付きダウンロードURLの有効期間。切れたら再接続（snapshot）か object.updated で新しい URL が届く */
const ASSET_URL_TTL_SECONDS = 3600;

/** クライアントに見せる形。所有者・擬態フラグなど正解に直結する属性は入れない */
export function toWireObject(o: ObjectState, storage?: AssetStorage) {
  return {
    objectId: o.objectId,
    assetKey: o.assetKey,
    ...(storage && { assetUrl: storage.presignDownload(o.assetKey, ASSET_URL_TTL_SECONDS) }),
    transform: o.transform,
  };
}

/** 仕様書 6.2-5: 準備中、ケイにはオブジェクト配置・擬態情報を送らない */
export function canSeeObjects(state: GameState, viewer: PlayerState): boolean {
  return !(state.status === "preparing" && viewer.role === "seeker");
}

export function visibleObjects(state: GameState, viewer: PlayerState): ObjectState[] {
  return canSeeObjects(state, viewer) ? [...state.objects.values()] : [];
}

type ObjectEvent =
  | { type: "object.created" | "object.updated"; payload: ReturnType<typeof toWireObject> }
  | { type: "object.deleted"; payload: { objectId: string } }
  | { type: "object.transform"; payload: { objectId: string; transform: Transform } };

/**
 * オブジェクトの変更に seq を振って、見せてよい接続へ配る。
 * seq を振るのと送るのを同じ関数に閉じ込めて、配信順と seq の順がずれないようにする。
 */
export function publishObjectEvent(
  state: GameState,
  connections: Connections,
  event: ObjectEvent,
  exceptPlayerId?: string,
): void {
  state.seq += 1;
  const text = JSON.stringify({
    ...event,
    round: state.round,
    seq: state.seq,
    timestamp: Date.now(),
  } satisfies ServerMessage);
  for (const [playerId, peer] of connections.entries(state.sessionId)) {
    const viewer = state.players.get(playerId);
    if (playerId !== exceptPlayerId && viewer && canSeeObjects(state, viewer)) peer.send(text);
  }
}
