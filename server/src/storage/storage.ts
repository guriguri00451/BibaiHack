import { mkdir, readdir, readFile, rename, stat, writeFile } from "node:fs/promises";
import { dirname, join, relative, sep } from "node:path";

/**
 * Object Storage（仕様書 2章）。GLB・テクスチャ・ログ・Snapshot を置く。
 * MVP ではサーバー自身が置き場になる（メモリかディレクトリ）。S3 などに替えるときはこの interface を実装する。
 */
export type StoredMeta = {
  contentType: string;
  size: number;
  /** アセットを上げた playerId（ログ・Snapshot には無い） */
  ownerPlayerId?: string;
  createdAt: string;
};

export interface ObjectStorage {
  /** 既にあるキーには書かない（false を返す）。仕様書「過去のアセットは上書きせず新規キーを発行」 */
  putNew(key: string, body: Uint8Array, meta: Omit<StoredMeta, "size" | "createdAt">): Promise<boolean>;
  /** ログのように同じキーを書き直してよいもの */
  put(key: string, body: Uint8Array, contentType: string): Promise<void>;
  get(key: string): Promise<{ body: Uint8Array; meta: StoredMeta } | null>;
  head(key: string): Promise<StoredMeta | null>;
  /** prefix で始まるキーを辞書順で返す */
  list(prefix: string): Promise<string[]>;
}

/** sessions/<uuid>/assets/<uuid>.glb のようなキーだけを受け付ける（ディレクトリの外へ出させない） */
export function isValidKey(key: string): boolean {
  if (key.length === 0 || key.length > 512) return false;
  if (!/^[A-Za-z0-9][A-Za-z0-9._/-]*$/.test(key)) return false;
  return key.split("/").every((seg) => seg !== "" && seg !== "." && seg !== "..");
}

function assertKey(key: string) {
  if (!isValidKey(key)) throw new Error(`invalid storage key: ${key}`);
}

export class MemoryStorage implements ObjectStorage {
  private readonly items = new Map<string, { body: Uint8Array; meta: StoredMeta }>();

  async putNew(key: string, body: Uint8Array, meta: Omit<StoredMeta, "size" | "createdAt">) {
    assertKey(key);
    if (this.items.has(key)) return false;
    this.items.set(key, { body, meta: { ...meta, size: body.byteLength, createdAt: new Date().toISOString() } });
    return true;
  }

  async put(key: string, body: Uint8Array, contentType: string) {
    assertKey(key);
    this.items.set(key, { body, meta: { contentType, size: body.byteLength, createdAt: new Date().toISOString() } });
  }

  async get(key: string) {
    return isValidKey(key) ? (this.items.get(key) ?? null) : null;
  }

  async head(key: string) {
    return isValidKey(key) ? (this.items.get(key)?.meta ?? null) : null;
  }

  async list(prefix: string) {
    return [...this.items.keys()].filter((k) => k.startsWith(prefix)).sort();
  }
}

/** ディレクトリに置く。本体の横に <key>.meta.json を置いて形式・所有者を覚える */
export class FsStorage implements ObjectStorage {
  constructor(private readonly root: string) {}

  private path(key: string) {
    assertKey(key);
    return join(this.root, ...key.split("/"));
  }

  private async write(key: string, body: Uint8Array, meta: StoredMeta, flag: "wx" | "w") {
    const p = this.path(key);
    await mkdir(dirname(p), { recursive: true });
    try {
      await writeFile(p, body, { flag });
    } catch (e) {
      if ((e as NodeJS.ErrnoException).code === "EEXIST") return false;
      throw e;
    }
    // meta は一時ファイルに書いてから置き換える（途中で落ちても壊れた JSON を残さない）
    const tmp = `${p}.meta.json.${crypto.randomUUID()}.tmp`;
    await writeFile(tmp, JSON.stringify(meta));
    await rename(tmp, `${p}.meta.json`);
    return true;
  }

  putNew(key: string, body: Uint8Array, meta: Omit<StoredMeta, "size" | "createdAt">) {
    return this.write(key, body, { ...meta, size: body.byteLength, createdAt: new Date().toISOString() }, "wx");
  }

  async put(key: string, body: Uint8Array, contentType: string) {
    await this.write(key, body, { contentType, size: body.byteLength, createdAt: new Date().toISOString() }, "w");
  }

  async head(key: string): Promise<StoredMeta | null> {
    if (!isValidKey(key)) return null;
    try {
      return JSON.parse(await readFile(`${this.path(key)}.meta.json`, "utf8")) as StoredMeta;
    } catch {
      return null;
    }
  }

  async get(key: string) {
    const meta = await this.head(key);
    if (!meta) return null;
    try {
      return { body: new Uint8Array(await readFile(this.path(key))), meta };
    } catch {
      return null;
    }
  }

  async list(prefix: string) {
    // prefix の最後の "/" までをディレクトリとして辿る
    const dirPart = prefix.includes("/") ? prefix.slice(0, prefix.lastIndexOf("/")) : "";
    if (dirPart && !isValidKey(dirPart)) return [];
    const start = dirPart ? join(this.root, ...dirPart.split("/")) : this.root;
    const out: string[] = [];
    const walk = async (dir: string) => {
      let entries;
      try {
        entries = await readdir(dir, { withFileTypes: true });
      } catch {
        return;
      }
      for (const e of entries) {
        const full = join(dir, e.name);
        if (e.isDirectory()) await walk(full);
        else if (!e.name.endsWith(".meta.json") && !e.name.endsWith(".tmp")) {
          const key = relative(this.root, full).split(sep).join("/");
          if (key.startsWith(prefix)) out.push(key);
        }
      }
    };
    if (await stat(start).catch(() => null)) await walk(start);
    return out.sort();
  }
}

export function createStorage(dir?: string): ObjectStorage {
  return dir ? new FsStorage(dir) : new MemoryStorage();
}
