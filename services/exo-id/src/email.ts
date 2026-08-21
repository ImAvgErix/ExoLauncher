import type { Env } from "./env.ts";
import { isLocalEnv } from "./env.ts";
import { nowIso } from "./crypto.ts";
import { logError } from "./log.ts";

export async function sendAuthEmail(env: Env, email: string, url: string): Promise<void> {
  if (isLocalEnv(env)) {
    await env.DB.prepare(`INSERT INTO email_outbox (sent_at, kind, url) VALUES (?, 'magic-link', ?)`)
      .bind(nowIso(), url)
      .run();
    return;
  }
  if (!env.RESEND_API_KEY) {
    logError("magic-link email unavailable; RESEND_API_KEY is not set");
    throw new Error("email send failed");
  }
  const from = env.RESEND_FROM;
  if (!from) {
    logError("magic-link email skipped; RESEND_FROM is not set");
    throw new Error("email send failed");
  }
  const digest = await crypto.subtle.digest(
    "SHA-256",
    new TextEncoder().encode(`${email}\n${url}`),
  );
  const idempotencyKey = Array.from(new Uint8Array(digest), (byte) => byte.toString(16).padStart(2, "0")).join("");
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 8_000);
  let res: Response;
  try {
    res = await fetch("https://api.resend.com/emails", {
      method: "POST",
      headers: {
        Authorization: `Bearer ${env.RESEND_API_KEY}`,
        "Content-Type": "application/json",
        "Idempotency-Key": idempotencyKey,
      },
      signal: controller.signal,
      body: JSON.stringify({
        from,
        to: [email],
        subject: "Sign in to Exo",
        text: `Sign in to Exo with this link. It expires in five minutes.\n\n${url}\n\nIf you did not ask for this, ignore it.`,
      }),
    });
  } finally {
    clearTimeout(timeout);
  }
  if (!res.ok) {
    logError("magic-link email send failed", { status: res.status });
    throw new Error("email send failed");
  }
}
