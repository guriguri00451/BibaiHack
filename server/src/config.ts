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
};

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
    databaseUrl: env.DATABASE_URL || undefined,
    pgliteDir: env.PGLITE_DIR || undefined,
    jwtSecret,
  };
}
