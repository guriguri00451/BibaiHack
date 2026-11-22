import { sql } from "drizzle-orm";
import {
  boolean,
  check,
  index,
  integer,
  jsonb,
  pgTable,
  text,
  timestamp,
  unique,
  uniqueIndex,
  uuid,
} from "drizzle-orm/pg-core";
import type { Transform } from "../contracts/transform.ts";

/** 仕様書 3章の4テーブル */

const createdAt = () => timestamp("created_at", { withTimezone: true }).notNull().defaultNow();

export const users = pgTable("users", {
  id: uuid("id").primaryKey().defaultRandom(),
  name: text("name").notNull(),
  createdAt: createdAt(),
});

export const gameSessions = pgTable(
  "game_sessions",
  {
    id: uuid("id").primaryKey().defaultRandom(),
    hostUserId: uuid("host_user_id")
      .notNull()
      .references(() => users.id),
    status: text("status", { enum: ["waiting", "preparing", "playing", "finished"] })
      .notNull()
      .default("waiting"),
    round: integer("round").notNull().default(1),
    mapId: text("map_id").notNull(),
    phaseEndsAt: timestamp("phase_ends_at", { withTimezone: true }),
    createdAt: createdAt(),
  },
  (t) => [
    check(
      "game_sessions_status_check",
      sql`${t.status} IN ('waiting', 'preparing', 'playing', 'finished')`,
    ),
    check("game_sessions_round_check", sql`${t.round} >= 1`),
  ],
);

export const gamePlayers = pgTable(
  "game_players",
  {
    id: uuid("id").primaryKey().defaultRandom(),
    sessionId: uuid("session_id")
      .notNull()
      .references(() => gameSessions.id, { onDelete: "cascade" }),
    userId: uuid("user_id")
      .notNull()
      .references(() => users.id),
    role: text("role", { enum: ["hider", "seeker"] }),
    score: integer("score").notNull().default(0),
    isCaught: boolean("is_caught").notNull().default(false),
    createdAt: createdAt(),
  },
  (t) => [
    unique("game_players_session_id_user_id_key").on(t.sessionId, t.userId),
    check("game_players_role_check", sql`${t.role} IN ('hider', 'seeker')`),
    index("idx_game_players_session").on(t.sessionId),
  ],
);

export const gameObjects = pgTable(
  "game_objects",
  {
    id: uuid("id").primaryKey().defaultRandom(),
    playerId: uuid("player_id")
      .notNull()
      .references(() => gamePlayers.id, { onDelete: "cascade" }),
    round: integer("round").notNull(),
    assetUrl: text("asset_url").notNull(),
    transform: jsonb("transform").$type<Transform>().notNull(),
    isDisguise: boolean("is_disguise").notNull().default(false),
    createdAt: createdAt(),
    updatedAt: timestamp("updated_at", { withTimezone: true }).notNull().defaultNow(),
  },
  (t) => [
    check("game_objects_round_check", sql`${t.round} >= 1`),
    uniqueIndex("one_disguise_per_player_round")
      .on(t.playerId, t.round)
      .where(sql`${t.isDisguise} = true`),
    index("idx_game_objects_player_round").on(t.playerId, t.round),
  ],
);
