-- Better Auth 1.7.1 already unique-indexes (issuer, accountId). Add the
-- provider-scoped identity fingerprint so one credential/Google account
-- cannot attach to two Exo users even if issuer strings differ.
CREATE UNIQUE INDEX IF NOT EXISTS "account_providerId_accountId_uidx"
  ON "account" ("providerId", "accountId");
