-- Better Auth 1.7.1 core tables (camelCase, Kysely/D1) plus Exo identity.

CREATE TABLE "user" (
  "id" TEXT PRIMARY KEY NOT NULL,
  "name" TEXT NOT NULL,
  "email" TEXT NOT NULL UNIQUE,
  "emailVerified" INTEGER NOT NULL,
  "image" TEXT,
  "createdAt" DATE NOT NULL,
  "updatedAt" DATE NOT NULL
);

CREATE TABLE "session" (
  "id" TEXT PRIMARY KEY NOT NULL,
  "expiresAt" DATE NOT NULL,
  "token" TEXT NOT NULL UNIQUE,
  "createdAt" DATE NOT NULL,
  "updatedAt" DATE NOT NULL,
  "ipAddress" TEXT,
  "userAgent" TEXT,
  "userId" TEXT NOT NULL,
  FOREIGN KEY ("userId") REFERENCES "user" ("id") ON DELETE CASCADE
);

CREATE INDEX "session_userId_idx" ON "session" ("userId");

CREATE TABLE "account" (
  "id" TEXT PRIMARY KEY NOT NULL,
  "issuer" TEXT NOT NULL,
  "accountId" TEXT NOT NULL,
  "providerId" TEXT NOT NULL,
  "userId" TEXT NOT NULL,
  "accessToken" TEXT,
  "refreshToken" TEXT,
  "idToken" TEXT,
  "accessTokenExpiresAt" DATE,
  "refreshTokenExpiresAt" DATE,
  "scope" TEXT,
  "password" TEXT,
  "createdAt" DATE NOT NULL,
  "updatedAt" DATE NOT NULL,
  FOREIGN KEY ("userId") REFERENCES "user" ("id") ON DELETE CASCADE
);

CREATE UNIQUE INDEX "account_issuer_accountId_uidx" ON "account" ("issuer", "accountId");
CREATE INDEX "account_userId_idx" ON "account" ("userId");

CREATE TABLE "verification" (
  "id" TEXT PRIMARY KEY NOT NULL,
  "identifier" TEXT NOT NULL,
  "value" TEXT NOT NULL,
  "expiresAt" DATE NOT NULL,
  "createdAt" DATE NOT NULL,
  "updatedAt" DATE NOT NULL
);

CREATE INDEX "verification_identifier_idx" ON "verification" ("identifier");

CREATE TABLE "rateLimit" (
  "id" TEXT PRIMARY KEY NOT NULL,
  "key" TEXT NOT NULL UNIQUE,
  "count" INTEGER NOT NULL,
  "lastRequest" INTEGER NOT NULL
);

-- One live handle per user. Uniqueness is the database's job.
CREATE TABLE "handle" (
  "user_id" TEXT PRIMARY KEY NOT NULL,
  "display" TEXT NOT NULL,
  "normalized" TEXT NOT NULL,
  "skeleton" TEXT NOT NULL,
  "claimed_at" TEXT NOT NULL,
  "changed_at" TEXT NOT NULL,
  FOREIGN KEY ("user_id") REFERENCES "user" ("id") ON DELETE CASCADE
);

CREATE UNIQUE INDEX "handle_normalized_uidx" ON "handle" ("normalized");
CREATE UNIQUE INDEX "handle_skeleton_uidx" ON "handle" ("skeleton");

-- Deleted handles stay parked. never_release=1 is forever (abuse).
CREATE TABLE "handle_tombstone" (
  "normalized" TEXT PRIMARY KEY NOT NULL,
  "skeleton" TEXT NOT NULL,
  "user_id" TEXT NOT NULL,
  "deleted_at" TEXT NOT NULL,
  "release_at" TEXT,
  "never_release" INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX "handle_tombstone_skeleton_idx" ON "handle_tombstone" ("skeleton");

CREATE TABLE "profile_field" (
  "user_id" TEXT NOT NULL,
  "key" TEXT NOT NULL,
  "value" TEXT NOT NULL,
  "updated_at" TEXT NOT NULL,
  "device_id" TEXT NOT NULL,
  PRIMARY KEY ("user_id", "key"),
  FOREIGN KEY ("user_id") REFERENCES "user" ("id") ON DELETE CASCADE
);

CREATE TABLE "pref_field" (
  "user_id" TEXT NOT NULL,
  "key" TEXT NOT NULL,
  "value" TEXT NOT NULL,
  "updated_at" TEXT NOT NULL,
  "device_id" TEXT NOT NULL,
  PRIMARY KEY ("user_id", "key"),
  FOREIGN KEY ("user_id") REFERENCES "user" ("id") ON DELETE CASCADE
);

CREATE TABLE "pending_login" (
  "id" TEXT PRIMARY KEY NOT NULL,
  "provider" TEXT NOT NULL,
  "redirect_uri" TEXT NOT NULL,
  "code_challenge" TEXT NOT NULL,
  "client_state" TEXT NOT NULL,
  "expires_at" TEXT NOT NULL,
  "created_at" TEXT NOT NULL,
  "consumed_at" TEXT
);

CREATE TABLE "auth_code" (
  "code_hash" TEXT PRIMARY KEY NOT NULL,
  "login_id" TEXT NOT NULL,
  "user_id" TEXT NOT NULL,
  "session_id" TEXT NOT NULL,
  "expires_at" TEXT NOT NULL,
  "consumed_at" TEXT
);

CREATE TABLE "app_rate_limit" (
  "key" TEXT PRIMARY KEY NOT NULL,
  "count" INTEGER NOT NULL,
  "window_start" INTEGER NOT NULL
);

-- Test/local only. Production never writes this.
CREATE TABLE "email_outbox" (
  "id" INTEGER PRIMARY KEY AUTOINCREMENT,
  "sent_at" TEXT NOT NULL,
  "kind" TEXT NOT NULL,
  "url" TEXT NOT NULL
);
