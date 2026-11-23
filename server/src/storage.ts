import { S3Client } from "bun";

/** 署名付きアップロードURLを発行する置き場。テストでは差し替える */
export type AssetStorage = {
  presignUpload(key: string, contentType: string, expiresInSeconds: number): string;
  presignDownload(key: string, expiresInSeconds: number): string;
};

export type S3Config = {
  endpoint: string;
  bucket: string;
  accessKeyId: string;
  secretAccessKey: string;
};

/** S3 互換（R2 など）。署名はローカル計算なので、ここでは通信しない */
export function createS3Storage(config: S3Config): AssetStorage {
  const client = new S3Client(config);
  return {
    presignUpload: (key, contentType, expiresIn) =>
      client.presign(key, { method: "PUT", type: contentType, expiresIn }),
    presignDownload: (key, expiresIn) => client.presign(key, { method: "GET", expiresIn }),
  };
}

/** サーバー自身のディスクに GLB を置く実装。署名付き URL は自分の /v1/assets/files/* を指す */
export type LocalAssetStorage = AssetStorage & {
  /** 署名（メソッド・キー・期限）が正しく、期限内か */
  verify(method: "PUT" | "GET", key: string, expires: number, signature: string): boolean;
  save(key: string, bytes: Uint8Array): Promise<void>;
  /** 無ければ null */
  load(key: string): ReturnType<typeof Bun.file> | null;
};

export type LocalStorageConfig = {
  dir: string;
  /** 外から見たこの環境の URL（basePath 込み） */
  publicUrl: string;
  /** 署名に使う鍵。JWT と同じものを流用する（別の用途と取り違えないよう用途名を署名対象に含める） */
  secret: string;
};

export const LOCAL_ASSET_PATH = "/v1/assets/files";

export function createLocalStorage(config: LocalStorageConfig, now: () => number = Date.now): LocalAssetStorage {
  const sign = (method: string, key: string, expires: number) =>
    new Bun.CryptoHasher("sha256", config.secret)
      .update(`asset-file\n${method}\n${key}\n${expires}`)
      .digest("hex");
  const presign = (method: "PUT" | "GET", key: string, expiresInSeconds: number) => {
    const expires = Math.floor(now() / 1000) + expiresInSeconds;
    const query = new URLSearchParams({ expires: String(expires), sig: sign(method, key, expires) });
    return `${config.publicUrl}${LOCAL_ASSET_PATH}/${key}?${query}`;
  };
  // キーは呼び出し側が ASSET_KEY_PATTERN で検証済みの前提。念のため `..` は二重に弾く
  const pathOf = (key: string) => {
    if (key.split("/").includes("..")) throw new Error(`不正なアセットキー: ${key}`);
    return `${config.dir}/${key}`;
  };

  return {
    presignUpload: (key, _contentType, expiresIn) => presign("PUT", key, expiresIn),
    presignDownload: (key, expiresIn) => presign("GET", key, expiresIn),
    verify(method, key, expires, signature) {
      if (!Number.isFinite(expires) || expires * 1000 < now()) return false;
      const expected = Buffer.from(sign(method, key, expires));
      const actual = Buffer.from(signature);
      return expected.length === actual.length && crypto.timingSafeEqual(expected, actual);
    },
    async save(key, bytes) {
      await Bun.write(pathOf(key), bytes); // 親ディレクトリも作られる
    },
    load(key) {
      const file = Bun.file(pathOf(key));
      return file.size > 0 ? file : null;
    },
  };
}
