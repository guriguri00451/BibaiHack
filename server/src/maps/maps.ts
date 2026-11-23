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
import { EMPTY_GLB, isGlb, readSpzHeader } from "./formats.ts";

/**
 * マップ（GET /v1/maps、GET /v1/maps/{mapId}）。2種類ある:
 * - 組み込み: server/maps/<mapId>.json と、その中の model（GLB）・splat（SPZ）。起動時に一度だけ読む。変えるにはデプロイ
 * - 登録: 管理画面（/v1/admin/maps）から上げたもの。定義は DB の maps、
 *   GLB は ObjectStorage の maps/<mapId>/<uuid>.glb、SPZ は maps/<mapId>/<uuid>.spz
 * どちらも GLB と SPZ（Gaussian Splat）の少なくとも一方を持つ。SPZ だけのマップの model.glb は空のシーン
 * 同じ mapId は持てない（組み込みの mapId では登録できない）
 */
const MapFileSchema = z.object({
  mapId: z.string().regex(/^[a-z0-9-]{1,64}$/),
  name: z.string(),
  model: z.string().regex(/^[A-Za-z0-9._-]+\.glb$/).optional(),
  splat: z.string().regex(/^[A-Za-z0-9._-]+\.spz$/).optional(),
  transform: TransformSchema,
}).refine((d) => d.model || d.splat, { message: "model（GLB）か splat（SPZ）のどちらかが要ります" });
export type MapFile = z.infer<typeof MapFileSchema>;

export const MAP_ID_PATTERN = /^[a-z0-9-]{1,64}$/;

export const DEFAULT_MAPS_DIR = fileURLToPath(new URL("../../maps", import.meta.url));

export type MapInfo = {
  mapId: string;
  name: string;
  transform: Transform;
  /** builtin: server/maps/ にあるもの（管理画面から消せない）、registered: 管理画面から登録したもの */
  source: "builtin" | "registered";
  /** GLB の大きさ。SPZ だけのマップなら 0 */
  modelSize: number;
  /** SPZ（Gaussian Splat）の大きさ。無ければ null */
  splatSize: number | null;
  /** GLB・SPZ が変わるたびに変わる。modelUrl に付けてキャッシュを外す */
  version: string;
  updatedAt: string | null;
};

type Registered = MapInfo & { modelKey: string | null; splatKey: string | null };

export class MapCatalog {
  private readonly builtin = new Map<string, { info: MapInfo; glb: Uint8Array | null; splat: Uint8Array | null }>();
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
      const glb = def.model ? new Uint8Array(readFileSync(join(dir, def.model))) : null;
      const splat = def.splat ? new Uint8Array(readFileSync(join(dir, def.splat))) : null;
      if (glb && !isGlb(glb)) throw new Error(`${def.model} は GLB ではありません`);
      if (splat) {
        const h = readSpzHeader(splat);
        if ("error" in h) throw new Error(`${def.splat}: ${h.error}`);
      }
      this.builtin.set(def.mapId, {
        info: {
          mapId: def.mapId,
          name: def.name,
          transform: def.transform,
          source: "builtin",
          modelSize: glb?.byteLength ?? 0,
          splatSize: splat?.byteLength ?? null,
          version: "builtin",
          updatedAt: null,
        },
        glb,
        splat,
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

  /** GLB の中身。マップが無ければ null。SPZ だけのマップなら空のシーンの GLB */
  async glb(mapId: string): Promise<Uint8Array | null> {
    await this.ensureLoaded();
    const b = this.builtin.get(mapId);
    if (b) return b.glb ?? EMPTY_GLB;
    const r = this.registered.get(mapId);
    if (!r || !this.storage) return null;
    if (!r.modelKey) return EMPTY_GLB;
    return (await this.storage.get(r.modelKey))?.body ?? null;
  }

  /** SPZ（Gaussian Splat）の中身。マップが無いか、SPZ を持たないなら null */
  async splat(mapId: string): Promise<Uint8Array | null> {
    await this.ensureLoaded();
    const b = this.builtin.get(mapId);
    if (b) return b.splat;
    const r = this.registered.get(mapId);
    if (!r?.splatKey || !this.storage) return null;
    return (await this.storage.get(r.splatKey))?.body ?? null;
  }

  // ================= 登録（管理画面） =================

  /**
   * 新しく登録するか、GLB・SPZ・名前・Transform を差し替える。glb・splat を省くとそのまま残す。
   * removeSplat で SPZ を外す（GLB が無いマップからは外せない）
   */
  async save(input: {
    mapId: string;
    name?: string;
    transform?: Transform;
    glb?: Uint8Array;
    splat?: Uint8Array;
    removeSplat?: boolean;
  }): Promise<MapInfo> {
    const { db, storage } = this.requireWritable();
    await this.ensureLoaded();
    if (!MAP_ID_PATTERN.test(input.mapId)) {
      throw new ApiError(400, "INVALID_MAP_ID", "mapId は英小文字・数字・ハイフン（64文字まで）にしてください。");
    }
    if (this.builtin.has(input.mapId)) {
      throw new ApiError(409, "BUILTIN_MAP", "組み込みのマップ（server/maps/）は管理画面から変えられません。");
    }
    const existing = this.registered.get(input.mapId);
    if (!existing && !input.glb && !input.splat) {
      throw new ApiError(400, "MODEL_REQUIRED", "新しく登録するときは GLB か SPZ が要ります。");
    }
    if (input.splat && input.removeSplat) {
      throw new ApiError(400, "INVALID_REQUEST", "SPZ の差し替えと削除は同時にできません。");
    }
    const hasModel = Boolean(input.glb ?? existing?.modelKey);
    if (input.removeSplat && !hasModel) {
      throw new ApiError(400, "MODEL_REQUIRED", "GLB が無いマップから SPZ は外せません。");
    }

    let modelKey = existing?.modelKey ?? null;
    let modelSize = existing?.modelSize ?? 0;
    let splatKey = input.removeSplat ? null : (existing?.splatKey ?? null);
    let splatSize = input.removeSplat ? null : (existing?.splatSize ?? null);
    if (input.splat) {
      splatKey = `maps/${input.mapId}/${crypto.randomUUID()}.spz`;
      splatSize = input.splat.byteLength;
      await storage.putNew(splatKey, input.splat, { contentType: "application/octet-stream" });
    }
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
      modelKey,
      modelSize,
      splatKey,
      splatSize,
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
    // GLB・SPZ は残す（このマップで遊んだセッションのリプレイ用。ストレージに削除が無いこともある）
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
    splatKey: r.splatKey,
    splatSize: r.splatSize,
    version: String(r.updatedAt.getTime()),
    updatedAt: r.updatedAt.toISOString(),
  };
}

function publicInfo({ modelKey: _m, splatKey: _s, ...info }: Registered): MapInfo {
  return info;
}

/** マップの GLB・SPZ の URL。登録したマップは中身が変わるたびに ?v= が変わる（キャッシュを外す） */
export function mapAssetUrls(base: string, m: Pick<MapInfo, "mapId" | "version" | "splatSize">) {
  const v = m.version === "builtin" ? "" : `?v=${m.version}`;
  return {
    modelUrl: `${base}/v1/maps/${m.mapId}/model.glb${v}`,
    splatUrl: m.splatSize === null ? null : `${base}/v1/maps/${m.mapId}/splat.spz${v}`,
  };
}
