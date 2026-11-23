CREATE TABLE "maps" (
	"map_id" text PRIMARY KEY NOT NULL,
	"name" text NOT NULL,
	"model_key" text NOT NULL,
	"model_size" integer NOT NULL,
	"transform" jsonb NOT NULL,
	"created_at" timestamp with time zone DEFAULT now() NOT NULL,
	"updated_at" timestamp with time zone DEFAULT now() NOT NULL
);
--> statement-breakpoint
ALTER TABLE "game_sessions" ADD COLUMN "cycle" jsonb;