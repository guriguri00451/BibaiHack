import { loadConfig } from "./config.ts";
import { openDb } from "./db/client.ts";
import { createServer } from "./server.ts";
import { createS3Storage } from "./storage/assets.ts";
import { createStorage } from "./storage/storage.ts";

const config = loadConfig();
const { db } = await openDb(config);
const srv = createServer({
  db,
  jwtSecret: config.jwtSecret,
  basePath: config.basePath,
  publicUrl: config.publicUrl,
  assetBaseUrl: config.assetBaseUrl,
  storage: createStorage(config.storageDir),
  // S3 を設定していなければ、GLB もサーバー自身が storage に受ける
  assetStorage: config.s3 && createS3Storage(config.s3),
});
if (!config.storageDir) console.warn("STORAGE_DIR が未設定のため、ログ（S3 が無ければ GLB も）はメモリに置きます（再起動で消えます）");

const resumed = await srv.registry.resumeActive();
const server = Bun.serve({ port: config.port, fetch: srv.fetch, websocket: srv.websocket });
console.log(
  `bibai-server listening on :${server.port}${config.basePath} (${config.publicUrl})` +
    `、GLB の置き場: ${config.s3 ? "S3" : "サーバー自身"}` +
    (resumed ? `、進行中のセッション ${resumed} 件のタイマーを再開` : ""),
);

for (const sig of ["SIGTERM", "SIGINT"] as const) {
  process.on(sig, async () => {
    // docker stop など。溜まっているログを書いてから終わる
    await srv.dispose();
    server.stop();
    process.exit(0);
  });
}
