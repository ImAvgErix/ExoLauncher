import { applyD1Migrations, env } from "cloudflare:test";

if (!env.TEST_MIGRATIONS) throw new Error("TEST_MIGRATIONS binding is required.");
await applyD1Migrations(env.DB, env.TEST_MIGRATIONS);
