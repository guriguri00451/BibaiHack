import type { Db } from "./db/client.ts";
import { GameRegistry } from "./game/registry.ts";
import { Hub } from "./game/hub.ts";
import { GameService } from "./game/service.ts";
import { EventLog } from "./log/event-log.ts";
import { MapCatalog } from "./maps/maps.ts";
import { type AssetStorage, createLocalAssetStorage } from "./storage/assets.ts";
import { UrlSigner } from "./storage/signed.ts";
import { MemoryStorage, type ObjectStorage } from "./storage/storage.ts";

export type Services = {
  game: GameService;
  storage: ObjectStorage;
  assets: AssetStorage;
  signer: UrlSigner;
  maps: MapCatalog;
};

/** REST と WS が共有するもの（メモリのゲーム状態・接続・保存先）を組み立てる */
export function buildServices(opts: {
  db: Db;
  jwtSecret: string;
  /** ログ・Snapshot の置き場。GLB も assetStorage が無ければここ */
  storage?: ObjectStorage;
  /** GLB の置き場（S3 など）。無ければ storage にサーバー自身が受ける */
  assetStorage?: AssetStorage;
  /** サーバー自身が受けるとき、リクエストから URL を作れない場面（WS の配信など）で使う URL */
  assetBaseUrl?: string;
  publicUrl?: string;
  maps?: MapCatalog;
  logFlushIntervalMs?: number;
}): Services {
  const storage = opts.storage ?? new MemoryStorage();
  const signer = new UrlSigner(opts.jwtSecret);
  const assets =
    opts.assetStorage ??
    createLocalAssetStorage(storage, signer, opts.assetBaseUrl ?? opts.publicUrl ?? "http://localhost");
  const log = new EventLog(storage, { flushIntervalMs: opts.logFlushIntervalMs });
  const game = new GameService(opts.db, new GameRegistry(opts.db), new Hub(), storage, assets, log);
  return { game, storage, assets, signer, maps: opts.maps ?? new MapCatalog() };
}
