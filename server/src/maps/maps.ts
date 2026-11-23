import { readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { z } from "@hono/zod-openapi";
import { asc, eq } from "drizzle-orm";
import { type Transform, TransformSchema } from "../contracts/transform.ts";
import type { Db } from "../db/client.ts";
import { maps as mapsTable } from "../db/schema.ts";
import { ApiError } from "../http/errors.ts";
import type { ObjectStorage } from "../storage/storage.ts";

/**
 * マップ（GET /v1/maps、GET /v1/maps/{mapId}）。2種類ある:
 * - 組み込み: server/maps/<mapId>.json と、その中の model が指す GLB。起動時に一度だけ読む。変えるにはデプロイ
 * - 登録: 管理画面（/v1/admin/maps）から上げたもの。定義は DB の maps、GLB は ObjectStorage の maps/<mapId>/<uuid>.glb
 * 同じ mapId は持てない（組み込みの mapId では登録できない）
 */
const MapFileSchema = z.object({
  mapId: z.string().regex(/^[a-z0-9-]{1,64}$/),
  name: z.string(),
  model: z.string().regex(/^[A-Za-z0-9._-]+\.glb$/),
  transform: TransformSchema,
});
export type MapFile = z.infer<typeof MapFileSchema>;

export const MAP_ID_PATTERN = /^[a-z0-9-]{1,64}$/;

export const DEFAULT_MAPS_DIR = fileURLToPath(new URL("../../maps", import.meta.url));

export type MapInfo = {
  mapId: string;
  name: string;
  transform: Transform;
  /** builtin: server/maps/ にあるもの（管理画面から消せない）、registered: 管理画面から登録したもの */
  source: "builtin" | "registered";
  modelSize: number;
  /** GLB が変わるたびに変わる。modelUrl に付けてキャッシュを外す */
  version: string;
  updatedAt: string | null;
};

type Registered = MapInfo & { modelKey: string };

export class MapCatalog {
  private readonly builtin = new Map<string, { info: MapInfo; glb: Uint8Array }>();
  private readonly registered = new Map<string, Registered>();
  private loading: Promise<void> | null = null;
  private db: Db | null = null;
  private storage: ObjectStorage | null = null;

  constructor(dir = DEFAULT_MAPS_DIR) {
    let files: string[] = [];
    try {
      files = readdirSync(dir).filter((f) => f.endsWith(".json"));
    } catch {
      console.warn(`マップのディレクトリがありません: ${dir}`);
    }
    for (const f of files) {
      const def = MapFileSchema.parse(JSON.parse(readFileSync(join(dir, f), "utf8")));
      const glb = new Uint8Array(readFileSync(join(dir, def.model)));
      this.builtin.set(def.mapId, {
        info: {
          mapId: def.mapId,
          name: def.name,
          transform: def.transform,
          source: "builtin",
          modelSize: glb.byteLength,
          version: "builtin",
          updatedAt: null,
        },
        glb,
      });
    }
  }

  /** 登録したマップを DB・ストレージから読めるようにする（services.ts で1度だけ呼ぶ） */
  attach(db: Db, storage: ObjectStorage): this {
    this.db = db;
    this.storage = storage;
    return this;
  }

  /** 初めて使うときに DB から登録したマップを読む（以後はメモリの写しを使う。サーバーは1台の前提） */
  private ensureLoaded(): Promise<void> {
    if (!this.db) return Promise.resolve();
    this.loading ??= this.loadRegistered().catch((e) => {
      this.loading = null;
      throw e;
    });
    return this.loading;
  }

  private async loadRegistered() {
    const rows = await this.db!.select().from(mapsTable).orderBy(asc(mapsTable.createdAt));
    this.registered.clear();
    for (const r of rows) this.registered.set(r.mapId, toRegistered(r));
  }

  async list(): Promise<MapInfo[]> {
    await this.ensureLoaded();
    return [...[...this.builtin.values()].map((b) => b.info), ...[...this.registered.values()].map(publicInfo)];
  }

  async get(mapId: string): Promise<MapInfo | null> {
    await this.ensureLoaded();
    const r = this.registered.get(mapId);
    return this.builtin.get(mapId)?.info ?? (r ? publicInfo(r) : null);
  }

  async has(mapId: string): Promise<boolean> {
    return (await this.get(mapId)) !== null;
  }

  /** GLB の中身。無ければ null */
  async glb(mapId: string): Promise<Uint8Array | null> {
    await this.ensureLoaded();
    const b = this.builtin.get(mapId);
    if (b) return b.glb;
    const r = this.registered.get(mapId);
    if (!r || !this.storage) return null;
    return (await this.storage.get(r.modelKey))?.body ?? null;
  }

  // ================= 登録（管理画面） =================

  /** 新しく登録するか、GLB・名前・Transform を差し替える。glb を省くと定義だけ変える */
  async save(input: { mapId: string; name?: string; transform?: Transform; glb?: Uint8Array }): Promise<MapInfo> {
    const { db, storage } = this.requireWritable();
    await this.ensureLoaded();
    if (!MAP_ID_PATTERN.test(input.mapId)) {
      throw new ApiError(400, "INVALID_MAP_ID", "mapId は英小文字・数字・ハイフン（64文字まで）にしてください。");
    }
    if (this.builtin.has(input.mapId)) {
      throw new ApiError(409, "BUILTIN_MAP", "組み込みのマップ（server/maps/）は管理画面から変えられません。");
    }
    const existing = this.registered.get(input.mapId);
    if (!existing && !input.glb) throw new ApiError(400, "MODEL_REQUIRED", "新しく登録するときは GLB が要ります。");

    let modelKey = existing?.modelKey;
    let modelSize = existing?.modelSize;
    if (input.glb) {
      // 古い GLB は消さずに新しいキーに置く（読み込み中の端末が途中で壊れたものを掴まないように）
      modelKey = `maps/${input.mapId}/${crypto.randomUUID()}.glb`;
      modelSize = input.glb.byteLength;
      await storage.putNew(modelKey, input.glb, { contentType: "model/gltf-binary" });
    }
    const values = {
      mapId: input.mapId,
      name: input.name ?? existing?.name ?? input.mapId,
      transform: input.transform ?? existing?.transform ?? IDENTITY,
      modelKey: modelKey!,
      modelSize: modelSize!,
      updatedAt: new Date(),
    };
    const [row] = await db
      .insert(mapsTable)
      .values(values)
      .onConflictDoUpdate({ target: mapsTable.mapId, set: values })
      .returning();
    const saved = toRegistered(row!);
    this.registered.set(saved.mapId, saved);
    return publicInfo(saved);
  }

  async remove(mapId: string): Promise<void> {
    const { db } = this.requireWritable();
    await this.ensureLoaded();
    if (this.builtin.has(mapId)) {
      throw new ApiError(409, "BUILTIN_MAP", "組み込みのマップ（server/maps/）は管理画面から消せません。");
    }
    if (!this.registered.has(mapId)) throw new ApiError(404, "MAP_NOT_FOUND", "Map not found.");
    // GLB は残す（このマップで遊んだセッションのリプレイ用。ストレージに削除が無いこともある）
    await db.delete(mapsTable).where(eq(mapsTable.mapId, mapId));
    this.registered.delete(mapId);
  }

  private requireWritable() {
    if (!this.db || !this.storage) throw new Error("MapCatalog is not attached to a database");
    return { db: this.db, storage: this.storage };
  }
}

const IDENTITY: Transform = { position: [0, 0, 0], rotation: [0, 0, 0, 1], scale: [1, 1, 1] };

function toRegistered(r: typeof mapsTable.$inferSelect): Registered {
  return {
    mapId: r.mapId,
    name: r.name,
    transform: r.transform,
    source: "registered",
    modelKey: r.modelKey,
    modelSize: r.modelSize,
    version: String(r.updatedAt.getTime()),
    updatedAt: r.updatedAt.toISOString(),
  };
}

function publicInfo({ modelKey: _, ...info }: Registered): MapInfo {
  return info;
}
