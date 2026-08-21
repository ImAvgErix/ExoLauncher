import path from "node:path";
import { fileURLToPath } from "node:url";
import { defineConfig } from "vitest/config";
import { cloudflareTest, readD1Migrations } from "@cloudflare/vitest-pool-workers";

const root = path.dirname(fileURLToPath(import.meta.url));
const testBetterAuthSecret = "test-secret-at-least-32-characters-long!";
process.env.BETTER_AUTH_SECRET ??= testBetterAuthSecret;

const testBindings = {
  BETTER_AUTH_SECRET: testBetterAuthSecret,
  BETTER_AUTH_URL: "http://127.0.0.1:8787",
  GOOGLE_CLIENT_ID: "test-google-client-id.apps.googleusercontent.com",
  GOOGLE_CLIENT_SECRET: "test-google-client-secret",
  RESEND_API_KEY: "",
  RESEND_FROM: "Exo <noreply@localhost>",
  ENVIRONMENT: "test",
};

export default defineConfig(async () => {
  const migrations = await readD1Migrations(path.join(root, "migrations"));
  return {
    test: {
      projects: [
        {
          test: {
            name: "unit",
            environment: "node",
            include: ["test/unit/**/*.test.ts"],
          },
        },
        {
          plugins: [
            cloudflareTest({
              wrangler: { configPath: "./wrangler.jsonc" },
              miniflare: {
                bindings: { ...testBindings, TEST_MIGRATIONS: migrations },
              },
            }),
          ],
          test: {
            name: "worker",
            setupFiles: ["./test/apply-migrations.ts"],
            include: ["test/worker/**/*.test.ts"],
          },
        },
      ],
    },
  };
});
