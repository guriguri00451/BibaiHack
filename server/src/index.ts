import { loadConfig } from "./config.ts";
import { openDb } from "./db/client.ts";
import { createApp } from "./http/app.ts";

const config = loadConfig();
const { db } = await openDb(config);
const app = createApp({ db, jwtSecret: config.jwtSecret });

const server = Bun.serve({ port: config.port, fetch: app.fetch });
console.log(`bibai-server listening on :${server.port}`);
