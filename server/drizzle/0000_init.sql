CREATE TABLE "game_objects" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"player_id" uuid NOT NULL,
	"round" integer NOT NULL,
	"asset_url" text NOT NULL,
	"transform" jsonb NOT NULL,
	"is_disguise" boolean DEFAULT false NOT NULL,
	"created_at" timestamp with time zone DEFAULT now() NOT NULL,
	"updated_at" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "game_objects_round_check" CHECK ("game_objects"."round" >= 1)
);
--> statement-breakpoint
CREATE TABLE "game_players" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"session_id" uuid NOT NULL,
	"user_id" uuid NOT NULL,
	"role" text,
	"score" integer DEFAULT 0 NOT NULL,
	"is_caught" boolean DEFAULT false NOT NULL,
	"created_at" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "game_players_session_id_user_id_key" UNIQUE("session_id","user_id"),
	CONSTRAINT "game_players_role_check" CHECK ("game_players"."role" IN ('hider', 'seeker'))
);
--> statement-breakpoint
CREATE TABLE "game_sessions" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"host_user_id" uuid NOT NULL,
	"status" text DEFAULT 'waiting' NOT NULL,
	"round" integer DEFAULT 1 NOT NULL,
	"map_id" text NOT NULL,
	"phase_ends_at" timestamp with time zone,
	"created_at" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "game_sessions_status_check" CHECK ("game_sessions"."status" IN ('waiting', 'preparing', 'playing', 'finished')),
	CONSTRAINT "game_sessions_round_check" CHECK ("game_sessions"."round" >= 1)
);
--> statement-breakpoint
CREATE TABLE "users" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"name" text NOT NULL,
	"created_at" timestamp with time zone DEFAULT now() NOT NULL
);
--> statement-breakpoint
ALTER TABLE "game_objects" ADD CONSTRAINT "game_objects_player_id_game_players_id_fk" FOREIGN KEY ("player_id") REFERENCES "public"."game_players"("id") ON DELETE cascade ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "game_players" ADD CONSTRAINT "game_players_session_id_game_sessions_id_fk" FOREIGN KEY ("session_id") REFERENCES "public"."game_sessions"("id") ON DELETE cascade ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "game_players" ADD CONSTRAINT "game_players_user_id_users_id_fk" FOREIGN KEY ("user_id") REFERENCES "public"."users"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "game_sessions" ADD CONSTRAINT "game_sessions_host_user_id_users_id_fk" FOREIGN KEY ("host_user_id") REFERENCES "public"."users"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
CREATE UNIQUE INDEX "one_disguise_per_player_round" ON "game_objects" USING btree ("player_id","round") WHERE "game_objects"."is_disguise" = true;--> statement-breakpoint
CREATE INDEX "idx_game_objects_player_round" ON "game_objects" USING btree ("player_id","round");--> statement-breakpoint
CREATE INDEX "idx_game_players_session" ON "game_players" USING btree ("session_id");