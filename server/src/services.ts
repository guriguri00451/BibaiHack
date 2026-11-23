import type { Db } from "./db/client.ts";
import { GameRegistry } from "./game/registry.ts";
import { Hub } from "./game/hub.ts";
import { GameService } from "./game/service.ts";
import { EventLog } from "./log/event-log.ts";
import { MapCatalog } from "./maps/maps.ts";
import { UrlSigner } from "./storage/signed.ts";
import { MemoryStorage, type ObjectStorage } from "./storage/storage.ts";

export type Services = {
  game: GameService;
  storage: ObjectStorage;
  signer: UrlSigner;
  maps: MapCatalog;
};

/** REST と WS が共有するもの（メモリのゲーム状態・接続・保存先）を組み立てる */
export function buildServices(opts: {
  db: Db;
  jwtSecret: string;
  storage?: ObjectStorage;
  maps?: MapCatalog;
  logFlushIntervalMs?: number;
}): Services {
  const storage = opts.storage ?? new MemoryStorage();
  const log = new EventLog(storage, { flushIntervalMs: opts.logFlushIntervalMs });
  const game = new GameService(opts.db, new GameRegistry(opts.db), new Hub(), storage, log);
  return { game, storage, signer: new UrlSigner(opts.jwtSecret), maps: opts.maps ?? new MapCatalog() };
}
