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
