import type { D1Migration } from "@cloudflare/vitest-pool-workers";
declare global {
  namespace Cloudflare {
    interface Env {
      BETTER_AUTH_SECRET: string;
      GOOGLE_CLIENT_SECRET?: string;
      RESEND_API_KEY?: string;
      TEST_MIGRATIONS?: D1Migration[];
    }
  }
}

export {};
