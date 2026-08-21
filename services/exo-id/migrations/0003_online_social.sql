-- Public-profile privacy and the account-level social graph.
-- Missing privacy rows intentionally resolve to privacy-safe defaults in code.

CREATE TABLE "profile_privacy" (
  "user_id" TEXT PRIMARY KEY NOT NULL,
  "profile_visibility" TEXT NOT NULL DEFAULT 'friends'
    CHECK ("profile_visibility" IN ('public', 'friends', 'private')),
  "searchable" INTEGER NOT NULL DEFAULT 0
    CHECK ("searchable" IN (0, 1)),
  "request_policy" TEXT NOT NULL DEFAULT 'anyone'
    CHECK ("request_policy" IN ('anyone', 'none')),
  "activity_visibility" TEXT NOT NULL DEFAULT 'friends'
    CHECK ("activity_visibility" IN ('friends', 'private')),
  "updated_at" TEXT NOT NULL,
  FOREIGN KEY ("user_id") REFERENCES "user" ("id") ON DELETE CASCADE
);

CREATE INDEX "profile_privacy_search_idx"
  ON "profile_privacy" ("searchable", "profile_visibility", "user_id");

-- One current request state per unordered pair. Keeping accepted/declined state
-- makes repeated transition calls safe without creating duplicate friendships.
CREATE TABLE "friend_request" (
  "id" TEXT PRIMARY KEY NOT NULL,
  "user_low" TEXT NOT NULL,
  "user_high" TEXT NOT NULL,
  "sender_id" TEXT NOT NULL,
  "recipient_id" TEXT NOT NULL,
  "status" TEXT NOT NULL DEFAULT 'pending'
    CHECK ("status" IN ('pending', 'accepted', 'declined')),
  "created_at" TEXT NOT NULL,
  "updated_at" TEXT NOT NULL,
  CHECK ("user_low" < "user_high"),
  CHECK ("sender_id" <> "recipient_id"),
  CHECK (
    ("sender_id" = "user_low" AND "recipient_id" = "user_high") OR
    ("sender_id" = "user_high" AND "recipient_id" = "user_low")
  ),
  UNIQUE ("user_low", "user_high"),
  FOREIGN KEY ("user_low") REFERENCES "user" ("id") ON DELETE CASCADE,
  FOREIGN KEY ("user_high") REFERENCES "user" ("id") ON DELETE CASCADE,
  FOREIGN KEY ("sender_id") REFERENCES "user" ("id") ON DELETE CASCADE,
  FOREIGN KEY ("recipient_id") REFERENCES "user" ("id") ON DELETE CASCADE
);

CREATE INDEX "friend_request_recipient_idx"
  ON "friend_request" ("recipient_id", "status", "created_at", "id");
CREATE INDEX "friend_request_sender_idx"
  ON "friend_request" ("sender_id", "status", "created_at", "id");

CREATE TABLE "direct_friendship" (
  "user_low" TEXT NOT NULL,
  "user_high" TEXT NOT NULL,
  "created_at" TEXT NOT NULL,
  PRIMARY KEY ("user_low", "user_high"),
  CHECK ("user_low" < "user_high"),
  FOREIGN KEY ("user_low") REFERENCES "user" ("id") ON DELETE CASCADE,
  FOREIGN KEY ("user_high") REFERENCES "user" ("id") ON DELETE CASCADE
);

CREATE INDEX "direct_friendship_high_idx" ON "direct_friendship" ("user_high", "user_low");

-- Removing or blocking a discovered connection must not let the next store
-- match silently recreate it. A later accepted direct request clears this row.
CREATE TABLE "friend_suppression" (
  "user_low" TEXT NOT NULL,
  "user_high" TEXT NOT NULL,
  "created_by" TEXT NOT NULL,
  "reason" TEXT NOT NULL CHECK ("reason" IN ('removed', 'blocked')),
  "created_at" TEXT NOT NULL,
  PRIMARY KEY ("user_low", "user_high"),
  CHECK ("user_low" < "user_high"),
  CHECK ("created_by" = "user_low" OR "created_by" = "user_high"),
  FOREIGN KEY ("user_low") REFERENCES "user" ("id") ON DELETE CASCADE,
  FOREIGN KEY ("user_high") REFERENCES "user" ("id") ON DELETE CASCADE,
  FOREIGN KEY ("created_by") REFERENCES "user" ("id") ON DELETE CASCADE
);

CREATE INDEX "friend_suppression_high_idx" ON "friend_suppression" ("user_high", "user_low");

-- Blocks are directional, while authorization treats either direction as a
-- complete deny. Only a user's outgoing block list is exposed.
CREATE TABLE "user_block" (
  "blocker_id" TEXT NOT NULL,
  "blocked_id" TEXT NOT NULL,
  "created_at" TEXT NOT NULL,
  PRIMARY KEY ("blocker_id", "blocked_id"),
  CHECK ("blocker_id" <> "blocked_id"),
  FOREIGN KEY ("blocker_id") REFERENCES "user" ("id") ON DELETE CASCADE,
  FOREIGN KEY ("blocked_id") REFERENCES "user" ("id") ON DELETE CASCADE
);

CREATE INDEX "user_block_blocked_idx" ON "user_block" ("blocked_id", "blocker_id");
