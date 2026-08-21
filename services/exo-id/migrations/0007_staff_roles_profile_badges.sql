-- Staff authority is operationally assigned in D1. There is deliberately no
-- HTTP role-mutation route: profile badges must never become an escalation path.
CREATE TABLE "staff_role" (
  "user_id" TEXT NOT NULL,
  "role" TEXT NOT NULL
    CHECK ("role" IN ('owner', 'admin', 'developer')),
  "granted_by" TEXT,
  "granted_at" TEXT NOT NULL,
  PRIMARY KEY ("user_id", "role"),
  FOREIGN KEY ("user_id") REFERENCES "user" ("id") ON DELETE CASCADE,
  FOREIGN KEY ("granted_by") REFERENCES "user" ("id") ON DELETE SET NULL
);

CREATE INDEX "staff_role_role_idx" ON "staff_role" ("role", "user_id");

-- Only the key is stored. Labels, descriptions, and tones come from the
-- server's fixed projection so callers cannot inject markup or arbitrary CSS.
CREATE TABLE "profile_badge" (
  "user_id" TEXT NOT NULL,
  "badge_key" TEXT NOT NULL
    CHECK ("badge_key" IN (
      'founder',
      'ceo',
      'developer',
      'moderator',
      'contributor',
      'early_supporter'
    )),
  "granted_by" TEXT,
  "granted_at" TEXT NOT NULL,
  PRIMARY KEY ("user_id", "badge_key"),
  FOREIGN KEY ("user_id") REFERENCES "user" ("id") ON DELETE CASCADE,
  FOREIGN KEY ("granted_by") REFERENCES "user" ("id") ON DELETE SET NULL
);

-- Founder is a single reserved identity marker, not a generally assignable
-- community badge. The service also requires owner authority for mutations.
CREATE UNIQUE INDEX "profile_badge_founder_uidx"
  ON "profile_badge" ("badge_key")
  WHERE "badge_key" = 'founder';
