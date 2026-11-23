import type { PublicObject } from "../contracts/ws.ts";
import type { GameState, ObjectState, PlayerState } from "./registry.ts";

/**
 * 誰に何を見せるか（仕様書 5.2・6.2）。REST の状態取得と WS の配信の両方でここを使う。
 */

/**
 * 受け手 viewer に、subject の位置を見せてよいか。
 * 仕様書 6.2-7「ドロの位置は原則ケイへ送らない」。ケイの位置はドロに見せる（未確定の仮ルール）。
 * 待機中・ラウンド終了後は全員に見せる。
 */
export function canSeePlayerPose(state: GameState, viewer: PlayerState, subject: PlayerState) {
  if (state.status === "waiting" || state.status === "finished") return true;
  if (subject.role !== "hider") return true;
  return viewer.role === "hider";
}

/** 作成者・擬態先まで見せてよいか（ドロどうし、またはラウンドが終わった後） */
export function canSeeObjectSecrets(state: GameState, viewer: PlayerState) {
  return state.status === "finished" || viewer.role === "hider";
}

/**
 * 受け手 viewer に、オブジェクトを見せてよいか。
 * 仕様書 6.2-5「preparing 中、ケイにはオブジェクト配置・擬態情報を送らない」。
 * 役割の無い人（途中参加など）はケイと同じ扱い。
 */
export function canSeeObjects(state: GameState, viewer: PlayerState) {
  switch (state.status) {
    case "waiting":
      return false;
    case "preparing":
      return viewer.role === "hider";
    case "playing":
    case "finished":
      return true;
  }
}

/** 受け手に合わせたオブジェクトの形。見せてはいけなければ null */
export function publicObject(state: GameState, viewer: PlayerState, o: ObjectState): PublicObject | null {
  if (!canSeeObjects(state, viewer)) return null;
  const base = { objectId: o.objectId, assetKey: o.assetKey, transform: o.transform };
  // 仕様書 6.2-6「is_disguise、作成者など正解に直結する属性を含めない」
  if (!canSeeObjectSecrets(state, viewer)) return base;
  return { ...base, ownerPlayerId: o.playerId, isDisguise: o.isDisguise };
}
