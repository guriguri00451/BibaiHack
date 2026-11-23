import type { S3Config } from "./storage/assets.ts";

export type Config = {
  port: number;
  /** 指定があれば PostgreSQL に繋ぐ。なければ PGlite（プロセス内の Postgres）を使う */
  databaseUrl?: string;
  /** PGlite の保存先。未指定ならメモリ上（再起動で消える） */
  pgliteDir?: string;
  jwtSecret: string;
  /** この環境の URL の前置き（例 "/a-kun-01"）。本番は "" */
  basePath: string;
  /** 外から見たこの環境の URL（OpenAPI の servers に載せる） */
  publicUrl: string;
  /**
   * PUBLIC_URL を明示したときだけ入る。署名付き URL やマップの modelUrl をこの URL で作る。
   * 無ければリクエストの Host から作る（手元の LAN で実機から繋ぐとき localhost にならないように）
   */
  assetBaseUrl?: string;
  /** GLB・ログ・Snapshot の保存先ディレクトリ。未指定ならメモリ上（再起動で消える） */
  storageDir?: string;
  /** S3 互換ストレージ（GLB の置き場）。S3_* がそろっていなければ undefined（サーバー自身が受ける） */
  s3?: S3Config;
  /** 管理画面（/admin、/v1/admin/*）の入り方 */
  admin: AdminAccess;
};

/**
 * token: このトークンを Bearer で送れば入れる。
 * token が無いとき、開発時（NODE_ENV が production でない）は誰でも入れ、本番では管理画面を閉じる
 */
export type AdminAccess = { token?: string; open: boolean };

export function loadConfig(env: Record<string, string | undefined> = process.env): Config {
  const port = Number(env.PORT ?? 8080);
  if (!Number.isInteger(port) || port < 0 || port > 65535) {
    throw new Error(`PORT が不正です: ${env.PORT}`);
  }

  let jwtSecret = env.JWT_SECRET;
  if (!jwtSecret) {
    if (env.NODE_ENV === "production") {
      throw new Error("JWT_SECRET を設定してください");
    }
    // 開発時だけ起動ごとに作る。再起動すると発行済みトークンは使えなくなる
    jwtSecret = crypto.randomUUID() + crypto.randomUUID();
    console.warn("JWT_SECRET が未設定のため、一時的な値を使います（開発用）");
  }

  const basePath = (env.BASE_PATH ?? "").replace(/\/+$/, "");
  if (basePath && !/^\/[a-z0-9-]+$/.test(basePath)) {
    throw new Error(`BASE_PATH は "/a-kun-01" のような形にしてください: ${env.BASE_PATH}`);
  }
  const publicUrl = (env.PUBLIC_URL ?? `http://localhost:${port}`).replace(/\/+$/, "") + basePath;

  return {
    port,
    basePath,
    publicUrl,
    assetBaseUrl: env.PUBLIC_URL ? publicUrl : undefined,
    databaseUrl: env.DATABASE_URL || undefined,
    pgliteDir: env.PGLITE_DIR || undefined,
    storageDir: env.STORAGE_DIR || undefined,
    jwtSecret,
    s3: loadS3Config(env),
    admin: loadAdminAccess(env),
  };
}

function loadAdminAccess(env: Record<string, string | undefined>): AdminAccess {
  const token = env.ADMIN_TOKEN || undefined;
  if (token) {
    if (token.length < 16) throw new Error("ADMIN_TOKEN は16文字以上にしてください");
    return { token, open: false };
  }
  if (env.NODE_ENV === "production") {
    console.warn("ADMIN_TOKEN が未設定のため、管理画面は使えません");
    return { open: false };
  }
  console.warn("ADMIN_TOKEN が未設定のため、管理画面は誰でも使えます（開発用）");
  return { open: true };
}

function loadS3Config(env: Record<string, string | undefined>): S3Config | undefined {
  const { S3_ENDPOINT: endpoint, S3_BUCKET: bucket, S3_ACCESS_KEY_ID: accessKeyId, S3_SECRET_ACCESS_KEY: secretAccessKey } = env;
  const given = [endpoint, bucket, accessKeyId, secretAccessKey];
  if (given.every((v) => !v)) return undefined;
  // 一部だけ設定されているのは設定ミス。黙って無効にせず起動時に気づけるようにする
  if (!endpoint || !bucket || !accessKeyId || !secretAccessKey) {
    throw new Error("S3_ENDPOINT / S3_BUCKET / S3_ACCESS_KEY_ID / S3_SECRET_ACCESS_KEY はすべて設定してください");
  }
  return { endpoint, bucket, accessKeyId, secretAccessKey };
}
