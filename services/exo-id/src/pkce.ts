import { ApiError, ErrorCode } from "./errors.ts";

const VERIFIER_RE = /^[A-Za-z0-9\-._~]{43,128}$/;
const CHALLENGE_RE = /^[A-Za-z0-9_-]{43,128}$/;

export function assertPkceStart(input: { codeChallenge: unknown; codeChallengeMethod: unknown }): {
  codeChallenge: string;
} {
  const method = typeof input.codeChallengeMethod === "string" ? input.codeChallengeMethod : "";
  if (method !== "S256") {
    throw new ApiError(
      400,
      ErrorCode.INVALID_PKCE,
      "codeChallengeMethod must be S256.",
    );
  }
  const challenge = typeof input.codeChallenge === "string" ? input.codeChallenge : "";
  if (!CHALLENGE_RE.test(challenge)) {
    throw new ApiError(400, ErrorCode.INVALID_PKCE, "codeChallenge is not a valid S256 challenge.");
  }
  return { codeChallenge: challenge };
}

export function assertPkceVerifier(verifier: unknown): string {
  if (typeof verifier !== "string" || !VERIFIER_RE.test(verifier)) {
    throw new ApiError(400, ErrorCode.INVALID_PKCE, "codeVerifier is not a valid PKCE verifier.");
  }
  return verifier;
}
