import { readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { z } from "@hono/zod-openapi";
import { TransformSchema } from "../contracts/transform.ts";

/**
 * マップ定義（GET /v1/maps/{mapId}）。server/maps/<mapId>.json と、その中の model が指す GLB。
 * 起動時に一度だけ読む。マップを足すときはファイルを置いてデプロイする
 */
const MapFileSchema = z.object({
  mapId: z.string().regex(/^[a-z0-9-]{1,64}$/),
  name: z.string(),
  model: z.string().regex(/^[A-Za-z0-9._-]+\.glb$/),
  transform: TransformSchema,
});
export type MapFile = z.infer<typeof MapFileSchema>;

export const DEFAULT_MAPS_DIR = fileURLToPath(new URL("../../maps", import.meta.url));

export class MapCatalog {
  private readonly maps = new Map<string, { def: MapFile; glb: Uint8Array }>();

  constructor(dir = DEFAULT_MAPS_DIR) {
    let files: string[] = [];
    try {
      files = readdirSync(dir).filter((f) => f.endsWith(".json"));
    } catch {
      console.warn(`マップのディレクトリがありません: ${dir}`);
    }
    for (const f of files) {
      const def = MapFileSchema.parse(JSON.parse(readFileSync(join(dir, f), "utf8")));
      this.maps.set(def.mapId, { def, glb: new Uint8Array(readFileSync(join(dir, def.model))) });
    }
  }

  get(mapId: string) {
    return this.maps.get(mapId) ?? null;
  }
}
