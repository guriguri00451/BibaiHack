import { loadConfig } from "./config.ts";
import { openDb } from "./db/client.ts";
import { createServer } from "./server.ts";

const config = loadConfig();
const { db } = await openDb(config);
const { fetch, websocket } = createServer({
  db,
  jwtSecret: config.jwtSecret,
  basePath: config.basePath,
  publicUrl: config.publicUrl,
});

const server = Bun.serve({ port: config.port, fetch, websocket });
console.log(`bibai-server listening on :${server.port}${config.basePath} (${config.publicUrl})`);
