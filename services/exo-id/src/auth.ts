import { betterAuth } from "better-auth";
import { bearer, magicLink } from "better-auth/plugins";
import type { Env } from "./env.ts";
import { emailMagicLinkEnabled } from "./env.ts";
import { sendAuthEmail } from "./email.ts";
import { logError } from "./log.ts";

export function createAuth(env: Env) {
  const googleConfigured = Boolean(env.GOOGLE_CLIENT_ID && env.GOOGLE_CLIENT_SECRET);
  const magicLinkConfigured = emailMagicLinkEnabled(env);
  return betterAuth({
    appName: "Exo",
    baseURL: env.BETTER_AUTH_URL,
    secret: env.BETTER_AUTH_SECRET,
    database: env.DB,
    emailAndPassword: {
      enabled: true,
      autoSignIn: false,
      minPasswordLength: 12,
      maxPasswordLength: 128,
    },
    socialProviders: googleConfigured
      ? {
          google: {
            clientId: env.GOOGLE_CLIENT_ID,
            clientSecret: env.GOOGLE_CLIENT_SECRET,
            prompt: "select_account",
          },
        }
      : {},
    plugins: [
      bearer(),
      ...(magicLinkConfigured
        ? [
            magicLink({
              expiresIn: 300,
              disableSignUp: false,
              sendMagicLink: async ({ email, url }) => {
                await sendAuthEmail(env, email, url);
              },
            }),
          ]
        : []),
    ],
    session: {
      expiresIn: 60 * 60 * 24 * 7,
      updateAge: 60 * 60 * 24,
    },
    rateLimit: {
      enabled: true,
      window: 60,
      max: 30,
      storage: "database",
      customRules: {
        "/sign-up/email": { window: 60, max: 5 },
        "/sign-in/email": { window: 60, max: 5 },
        "/sign-in/magic-link": { window: 60, max: 5 },
        "/sign-in/social": { window: 60, max: 10 },
      },
    },
    advanced: {
      ipAddress: {
        ipAddressHeaders: ["cf-connecting-ip"],
      },
    },
    trustedOrigins: [env.BETTER_AUTH_URL, "http://127.0.0.1:8787"],
    logger: {
      disabled: false,
      level: "error",
      log: (level, message) => {
        if (level === "error") logError(String(message));
      },
    },
  });
}

export type Auth = ReturnType<typeof createAuth>;
