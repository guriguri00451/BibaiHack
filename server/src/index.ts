import { loadConfig } from "./config.ts";
import { createApp } from "./http/app.ts";

const config = loadConfig();
const app = createApp();

const server = Bun.serve({ port: config.port, fetch: app.fetch });
console.log(`bibai-server listening on :${server.port}`);
