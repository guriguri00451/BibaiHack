/**
 * ゲームの数値ルール。仕様書 4章「点数・勝敗の細則は後から調整する」ので、ここに集めておく（仮の値）。
 */

/** ケイがドロの擬態を見つけたとき、ケイに入る点 */
export const CAPTURE_SCORE = 100;
/** 本番の終わりまで見つからなかったドロに入る点 */
export const SURVIVE_SCORE = 100;
/** 外れのときの点（0 = 減点しない） */
export const MISS_SCORE = 0;

/** rounds/start で playingSeconds を省いたとき、準備の期限が来て自動で本番に入る長さ */
export const DEFAULT_PLAYING_SECONDS = 180;
/** フェーズの長さの上限（秒） */
export const MAX_PHASE_SECONDS = 60 * 60;

/** 捕獲の連打防止。1人のケイが続けて捕獲を試せる間隔 */
export const CAPTURE_COOLDOWN_MS = 1_500;

/** 1人のドロが1ラウンドで置けるオブジェクトの数 */
export const MAX_OBJECTS_PER_PLAYER_ROUND = 50;

/** アップロードできるアセット（GLB だけ。テクスチャは GLB に埋め込む） */
export const ASSET_CONTENT_TYPE = "model/gltf-binary";
export const MAX_ASSET_BYTES = 20 * 1024 * 1024;
/** 仕様書 9章「署名付きアップロードURLには短い期限」 */
export const UPLOAD_URL_TTL_SECONDS = 5 * 60;
/** snapshot・object.* に載せるダウンロード URL の期限。切れたら再接続（snapshot）か object.updated で新しい URL が届く */
export const DOWNLOAD_URL_TTL_SECONDS = 60 * 60;
