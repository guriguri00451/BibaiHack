import { fileURLToPath } from "node:url";
import { PGlite } from "@electric-sql/pglite";
import { drizzle as drizzleBunSql } from "drizzle-orm/bun-sql";
import { migrate as migrateBunSql } from "drizzle-orm/bun-sql/migrator";
import type { PgDatabase, PgQueryResultHKT } from "drizzle-orm/pg-core";
import { drizzle as drizzlePglite } from "drizzle-orm/pglite";
import { migrate as migratePglite } from "drizzle-orm/pglite/migrator";
import * as schema from "./schema.ts";

export type Db = PgDatabase<PgQueryResultHKT, typeof schema>;

const migrationsFolder = fileURLToPath(new URL("../../drizzle", import.meta.url));

/**
 * databaseUrl があれば PostgreSQL（Bun.sql）、なければ PGlite を使う。
 * どちらも起動時にマイグレーションを当てる。
 */
export async function openDb(opts: { databaseUrl?: string; pgliteDir?: string }): Promise<{
  db: Db;
  close: () => Promise<void>;
}> {
  if (opts.databaseUrl) {
    const db = drizzleBunSql(opts.databaseUrl, { schema });
    await migrateBunSql(db, { migrationsFolder });
    return { db: db as unknown as Db, close: async () => db.$client.close() };
  }
  const client = new PGlite(opts.pgliteDir);
  const db = drizzlePglite(client, { schema });
  await migratePglite(db, { migrationsFolder });
  return { db: db as unknown as Db, close: () => client.close() };
}
