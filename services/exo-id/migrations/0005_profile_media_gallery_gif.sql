-- Add six bounded gallery slots and sanitized GIF media without weakening the
-- one-current-version-per-owner/slot authority established in 0004.

DROP INDEX IF EXISTS "profile_media_version_uidx";
DROP INDEX IF EXISTS "profile_media_user_idx";

ALTER TABLE "profile_media" RENAME TO "profile_media_legacy";

CREATE TABLE "profile_media" (
  "user_id" TEXT NOT NULL,
  "kind" TEXT NOT NULL CHECK (
    "kind" IN ('avatar', 'banner', 'gallery0', 'gallery1', 'gallery2', 'gallery3', 'gallery4', 'gallery5')
  ),
  "version" TEXT NOT NULL CHECK (length("version") = 64),
  "object_key" TEXT NOT NULL UNIQUE,
  "content_type" TEXT NOT NULL CHECK (
    "content_type" IN ('image/png', 'image/jpeg', 'image/webp', 'image/gif')
  ),
  "byte_size" INTEGER NOT NULL CHECK ("byte_size" > 0 AND "byte_size" <= 8388608),
  "width" INTEGER NOT NULL CHECK ("width" > 0 AND "width" <= 8192),
  "height" INTEGER NOT NULL CHECK ("height" > 0 AND "height" <= 4096),
  "sha256" TEXT NOT NULL CHECK (length("sha256") = 64),
  "created_at" TEXT NOT NULL,
  "updated_at" TEXT NOT NULL,
  PRIMARY KEY ("user_id", "kind"),
  FOREIGN KEY ("user_id") REFERENCES "user" ("id") ON DELETE CASCADE
);

INSERT INTO "profile_media"
  ("user_id", "kind", "version", "object_key", "content_type", "byte_size", "width", "height", "sha256", "created_at", "updated_at")
SELECT
  "user_id", "kind", "version", "object_key", "content_type", "byte_size", "width", "height", "sha256", "created_at", "updated_at"
FROM "profile_media_legacy";

DROP TABLE "profile_media_legacy";

CREATE INDEX "profile_media_user_idx" ON "profile_media" ("user_id");
CREATE UNIQUE INDEX "profile_media_version_uidx" ON "profile_media" ("user_id", "kind", "version");
