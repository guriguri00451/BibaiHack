import { afterAll, expect, test } from "bun:test";
import { openDb } from "../src/db/client.ts";
import { gameObjects, gamePlayers, gameSessions, users } from "../src/db/schema.ts";

const { db, close } = await openDb({});
afterAll(close);

const t = { position: [0, 0, 0], rotation: [0, 0, 0, 1], scale: [1, 1, 1] } as const;

test("マイグレーション後に4テーブルへ書ける", async () => {
  const [u] = await db.insert(users).values({ name: "a" }).returning();
  const [s] = await db.insert(gameSessions).values({ hostUserId: u!.id, mapId: "m" }).returning();
  expect(s!.status).toBe("waiting");
  expect(s!.round).toBe(1);
  const [p] = await db.insert(gamePlayers).values({ sessionId: s!.id, userId: u!.id }).returning();
  expect(p!.role).toBeNull();
});

test("1ラウンドで擬態できるのは1つだけ（部分ユニーク索引）", async () => {
  const [u] = await db.insert(users).values({ name: "b" }).returning();
  const [s] = await db.insert(gameSessions).values({ hostUserId: u!.id, mapId: "m" }).returning();
  const [p] = await db.insert(gamePlayers).values({ sessionId: s!.id, userId: u!.id }).returning();
  const obj = { playerId: p!.id, round: 1, assetUrl: "k", transform: t as never };
  await db.insert(gameObjects).values({ ...obj, isDisguise: true });
  await db.insert(gameObjects).values({ ...obj, isDisguise: false });
  const second = db.insert(gameObjects).values({ ...obj, isDisguise: true }).then(() => "inserted");
  expect(await second.catch(() => "rejected")).toBe("rejected");
});
