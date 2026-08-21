-- Verified store links, discovery opt-out, double-submit match claims,
-- and completed Exo connections. Unmatched friend ids are never stored.

CREATE TABLE "store_link" (
  "user_id" TEXT NOT NULL,
  "store" TEXT NOT NULL,
  -- Versioned AES-GCM ciphertext. Plaintext is returned only to its owner.
  "external_id" TEXT NOT NULL CHECK ("external_id" GLOB 'v1.*.*'),
  "id_hash" TEXT NOT NULL,
  "verified" INTEGER NOT NULL DEFAULT 1,
  "verified_at" TEXT NOT NULL,
  PRIMARY KEY ("user_id", "store"),
  FOREIGN KEY ("user_id") REFERENCES "user" ("id") ON DELETE CASCADE
);

-- A user can own at most one external account for each provider. The HMAC
-- fingerprint is provider-scoped and globally unique, so a verified external
-- account can belong to only one Exo user even when two link requests race.
CREATE UNIQUE INDEX "store_link_hash_uidx" ON "store_link" ("store", "id_hash");
CREATE UNIQUE INDEX "store_link_external_uidx" ON "store_link" ("store", "external_id");

CREATE TABLE "user_discovery" (
  "user_id" TEXT PRIMARY KEY NOT NULL,
  "enabled" INTEGER NOT NULL DEFAULT 1,
  "updated_at" TEXT NOT NULL,
  FOREIGN KEY ("user_id") REFERENCES "user" ("id") ON DELETE CASCADE
);

CREATE TABLE "match_claim" (
  "user_id" TEXT NOT NULL,
  "store" TEXT NOT NULL,
  "peer_user_id" TEXT NOT NULL,
  "created_at" TEXT NOT NULL,
  PRIMARY KEY ("user_id", "store", "peer_user_id"),
  FOREIGN KEY ("user_id") REFERENCES "user" ("id") ON DELETE CASCADE,
  FOREIGN KEY ("peer_user_id") REFERENCES "user" ("id") ON DELETE CASCADE
);

CREATE INDEX "match_claim_peer_idx" ON "match_claim" ("peer_user_id", "store");
CREATE INDEX "match_claim_created_idx" ON "match_claim" ("created_at");

CREATE TABLE "discovered_connection" (
  "user_low" TEXT NOT NULL,
  "user_high" TEXT NOT NULL,
  "store" TEXT NOT NULL,
  "created_at" TEXT NOT NULL,
  PRIMARY KEY ("user_low", "user_high", "store"),
  FOREIGN KEY ("user_low") REFERENCES "user" ("id") ON DELETE CASCADE,
  FOREIGN KEY ("user_high") REFERENCES "user" ("id") ON DELETE CASCADE
);

CREATE INDEX "discovered_connection_high_idx" ON "discovered_connection" ("user_high");

CREATE TABLE "pending_store_link" (
  "id" TEXT PRIMARY KEY NOT NULL,
  "user_id" TEXT NOT NULL,
  "store" TEXT NOT NULL,
  "redirect_uri" TEXT NOT NULL,
  "client_state" TEXT NOT NULL,
  "return_to" TEXT NOT NULL,
  "expires_at" TEXT NOT NULL,
  "created_at" TEXT NOT NULL,
  "consumed_at" TEXT,
  FOREIGN KEY ("user_id") REFERENCES "user" ("id") ON DELETE CASCADE
);

CREATE INDEX "pending_store_link_user_idx" ON "pending_store_link" ("user_id");
