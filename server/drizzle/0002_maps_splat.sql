ALTER TABLE "maps" ALTER COLUMN "model_key" DROP NOT NULL;--> statement-breakpoint
ALTER TABLE "maps" ADD COLUMN "splat_key" text;--> statement-breakpoint
ALTER TABLE "maps" ADD COLUMN "splat_size" integer;