import { S3Client } from "bun";
import type { UrlSigner } from "./signed.ts";
import type { ObjectStorage } from "./storage.ts";

/**
 * GLB の置き場と、そこへの署名付き URL（仕様書 5.1）。
 * S3 互換（R2 など）を設定していればそちら、無ければサーバー自身（createLocalAssetStorage）を使う。
 */
export type UploadGrant = {
  sizeBytes: number;
  ownerPlayerId: string;
  /** 外から見たこの環境の URL（サーバー自身が受けるときに使う） */
  baseUrl?: string;
};

export type AssetStat = { size: number; contentType?: string; ownerPlayerId?: string };

export type AssetStorage = {
  presignUpload(key: string, contentType: string, expiresInSeconds: number, grant?: UploadGrant): string;
  presignDownload(key: string, expiresInSeconds: number, baseUrl?: string): string;
  /** アップロード済みか・大きさを確かめる。無い実装（テストの差し替えなど）では確認を省く */
  stat?(key: string): Promise<AssetStat | null>;
};

export type S3Config = {
  endpoint: string;
  bucket: string;
  accessKeyId: string;
  secretAccessKey: string;
};

/** S3 互換（R2 など）。署名はローカル計算なので、presign では通信しない */
export function createS3Storage(config: S3Config): AssetStorage {
  const client = new S3Client(config);
  return {
    presignUpload: (key, contentType, expiresIn) => client.presign(key, { method: "PUT", type: contentType, expiresIn }),
    presignDownload: (key, expiresIn) => client.presign(key, { method: "GET", expiresIn }),
    async stat(key) {
      try {
        const s = await client.stat(key);
        return { size: s.size, contentType: s.type };
      } catch {
        return null;
      }
    },
  };
}

/**
 * サーバー自身が受ける。PUT/GET <baseUrl>/v1/storage/<key>?…（routes.ts）。
 * 署名に大きさ・所有者を入れるので、受け取るときにそれを確かめ、所有者は保存先の meta に残る
 */
export function createLocalAssetStorage(storage: ObjectStorage, signer: UrlSigner, defaultBaseUrl: string): AssetStorage {
  const exp = (ttl: number) => Math.floor(Date.now() / 1000) + ttl;
  return {
    presignUpload(key, contentType, ttl, grant) {
      if (!grant) throw new Error("local storage needs the upload size and owner");
      const q = signer.uploadQuery({
        key,
        exp: exp(ttl),
        contentType,
        size: grant.sizeBytes,
        ownerPlayerId: grant.ownerPlayerId,
      });
      return `${grant.baseUrl ?? defaultBaseUrl}/v1/storage/${key}?${q}`;
    },
    presignDownload(key, ttl, baseUrl) {
      return `${baseUrl ?? defaultBaseUrl}/v1/storage/${key}?${signer.downloadQuery({ key, exp: exp(ttl) })}`;
    },
    async stat(key) {
      const m = await storage.head(key);
      return m ? { size: m.size, contentType: m.contentType, ownerPlayerId: m.ownerPlayerId } : null;
    },
  };
}
