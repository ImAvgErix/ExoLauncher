-- One current immutable R2 object per profile media slot. Object bytes stay in
-- R2; D1 is the ownership/current-version authority used by every read.

CREATE TABLE "profile_media" (
  "user_id" TEXT NOT NULL,
  "kind" TEXT NOT NULL CHECK ("kind" IN ('avatar', 'banner')),
  "version" TEXT NOT NULL CHECK (length("version") = 64),
  "object_key" TEXT NOT NULL UNIQUE,
  "content_type" TEXT NOT NULL CHECK ("content_type" IN ('image/png', 'image/jpeg', 'image/webp')),
  "byte_size" INTEGER NOT NULL CHECK ("byte_size" > 0 AND "byte_size" <= 8388608),
  "width" INTEGER NOT NULL CHECK ("width" > 0 AND "width" <= 8192),
  "height" INTEGER NOT NULL CHECK ("height" > 0 AND "height" <= 4096),
  "sha256" TEXT NOT NULL CHECK (length("sha256") = 64),
  "created_at" TEXT NOT NULL,
  "updated_at" TEXT NOT NULL,
  PRIMARY KEY ("user_id", "kind"),
  FOREIGN KEY ("user_id") REFERENCES "user" ("id") ON DELETE CASCADE
);

CREATE INDEX "profile_media_user_idx" ON "profile_media" ("user_id");
CREATE UNIQUE INDEX "profile_media_version_uidx" ON "profile_media" ("user_id", "kind", "version");
