export const PRESENCE_TTL_MS = 90_000;
export const PRESENCE_PEER_RETENTION_MS = 24 * 60 * 60 * 1000;
export const MAX_PRESENCE_MESSAGE_BYTES = 4 * 1024;
export const MAX_PRESENCE_ATTACHMENT_BYTES = 16 * 1024;
export const MAX_PRESENCE_CONNECTIONS_PER_USER = 8;
export const MAX_PRESENCE_GAME_ID_LENGTH = 128;
export const MAX_PRESENCE_GAME_TITLE_LENGTH = 160;

export const ACTIVE_PRESENCE_STATUSES = ["online", "away", "in_game"] as const;

export type ActivePresenceStatus = (typeof ACTIVE_PRESENCE_STATUSES)[number];
export type PresenceStatus = ActivePresenceStatus | "offline";
export type PresenceRosterStatus = PresenceStatus | "unknown";
export type PresenceAvailability = "available" | "unavailable";

export type ClientPresenceMessage =
  | { type: "heartbeat" }
  | {
      type: "status";
      status: ActivePresenceStatus;
      gameId: string | null;
      gameTitle: string | null;
    };

export type PresenceSnapshot = {
  userId: string;
  status: PresenceStatus;
  gameId: string | null;
  gameTitle: string | null;
  lastSeen: string | null;
  revision: number;
};

export type PublicPresenceSnapshot = Omit<PresenceSnapshot, "revision">;

export type PresenceRosterEntry = {
  userId: string;
  status: PresenceRosterStatus;
  gameId: string | null;
  gameTitle: string | null;
  lastSeen: string | null;
  availability: PresenceAvailability;
};

export type PresenceSocketAttachment = {
  version: 1;
  ownerId: string;
  sessionId: string;
  connectionId: string;
};

export type ServerPresenceMessage =
  | { type: "ready"; self: PublicPresenceSnapshot }
  | { type: "ack"; self: PublicPresenceSnapshot }
  | { type: "presence"; presence: PresenceRosterEntry }
  | { type: "error"; code: "INVALID_MESSAGE"; message: string };

export class PresenceMessageError extends Error {
  constructor(message = "Presence message is invalid.") {
    super(message);
    this.name = "PresenceMessageError";
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return Boolean(value) && typeof value === "object" && !Array.isArray(value);
}

function hasOnlyKeys(value: Record<string, unknown>, allowed: readonly string[]): boolean {
  const allowedSet = new Set(allowed);
  return Object.keys(value).every((key) => allowedSet.has(key));
}

function optionalBoundedString(value: unknown, maxLength: number): string | null {
  if (value === undefined || value === null || value === "") return null;
  if (typeof value !== "string") throw new PresenceMessageError();
  const trimmed = value.trim();
  if (!trimmed) return null;
  if (trimmed.length > maxLength || /[\u0000-\u001f\u007f]/u.test(trimmed)) {
    throw new PresenceMessageError();
  }
  return trimmed;
}

export function parseClientPresenceMessage(serialized: string): ClientPresenceMessage {
  if (new TextEncoder().encode(serialized).byteLength > MAX_PRESENCE_MESSAGE_BYTES) {
    throw new PresenceMessageError("Presence message exceeds 4096 bytes.");
  }

  let parsed: unknown;
  try {
    parsed = JSON.parse(serialized);
  } catch {
    throw new PresenceMessageError();
  }
  if (!isRecord(parsed) || typeof parsed.type !== "string") throw new PresenceMessageError();

  if (parsed.type === "heartbeat") {
    if (!hasOnlyKeys(parsed, ["type"])) throw new PresenceMessageError();
    return { type: "heartbeat" };
  }

  if (parsed.type !== "status" || !hasOnlyKeys(parsed, ["type", "status", "gameId", "gameTitle"])) {
    throw new PresenceMessageError();
  }
  if (!ACTIVE_PRESENCE_STATUSES.includes(parsed.status as ActivePresenceStatus)) {
    throw new PresenceMessageError();
  }
  const status = parsed.status as ActivePresenceStatus;

  const gameId = optionalBoundedString(parsed.gameId, MAX_PRESENCE_GAME_ID_LENGTH);
  const gameTitle = optionalBoundedString(parsed.gameTitle, MAX_PRESENCE_GAME_TITLE_LENGTH);
  if (status !== "in_game" && (gameId !== null || gameTitle !== null)) {
    throw new PresenceMessageError("Presence message game fields require in_game status.");
  }

  return {
    type: "status",
    status,
    gameId,
    gameTitle,
  };
}

export function createSocketAttachment(
  ownerId: string,
  sessionId: string,
  connectionId: string,
): PresenceSocketAttachment {
  const attachment: PresenceSocketAttachment = { version: 1, ownerId, sessionId, connectionId };
  if (new TextEncoder().encode(JSON.stringify(attachment)).byteLength > MAX_PRESENCE_ATTACHMENT_BYTES) {
    throw new Error("Presence socket attachment exceeds 16384 bytes.");
  }
  return attachment;
}

export function readSocketAttachment(value: unknown): PresenceSocketAttachment | null {
  if (!isRecord(value) || value.version !== 1) return null;
  if (
    typeof value.ownerId !== "string" ||
    typeof value.sessionId !== "string" ||
    typeof value.connectionId !== "string" ||
    !value.ownerId ||
    !value.sessionId ||
    !value.connectionId
  ) {
    return null;
  }
  return {
    version: 1,
    ownerId: value.ownerId,
    sessionId: value.sessionId,
    connectionId: value.connectionId,
  };
}

export function publicPresence(snapshot: PresenceSnapshot, activityAllowed: boolean): PublicPresenceSnapshot {
  return {
    userId: snapshot.userId,
    status: snapshot.status,
    gameId: activityAllowed ? snapshot.gameId : null,
    gameTitle: activityAllowed ? snapshot.gameTitle : null,
    lastSeen: snapshot.lastSeen,
  };
}

export function rosterEntry(
  snapshot: PublicPresenceSnapshot,
  availability: PresenceAvailability = "available",
): PresenceRosterEntry {
  return { ...snapshot, availability };
}

export function unavailablePresence(userId: string): PresenceRosterEntry {
  return {
    userId,
    status: "unknown",
    gameId: null,
    gameTitle: null,
    lastSeen: null,
    availability: "unavailable",
  };
}

export function authoritativeOfflinePresence(
  userId: string,
  lastSeen: string | null = null,
): PresenceRosterEntry {
  return {
    userId,
    status: "offline",
    gameId: null,
    gameTitle: null,
    lastSeen,
    availability: "available",
  };
}

function unixSecondsToIso(value: number | null | undefined): string | null {
  if (value == null || !Number.isSafeInteger(value) || value <= 0) return null;
  const millis = value * 1000;
  if (!Number.isSafeInteger(millis)) return null;
  const stamp = new Date(millis);
  if (Number.isNaN(stamp.getTime())) return null;
  return stamp.toISOString();
}

/**
 * Steam GetPlayerSummaries v2 mapping used by the presence contract.
 * Explicit `personastate=0` is Offline+available even on a private profile,
 * because friends still publish 0 when they are actually offline. A private
 * or omitted persona with no state stays Unknown+unavailable and must never
 * be dressed up as offline.
 */
export function rosterFromSteamSummary(input: {
  userId: string;
  personaState: number | null | undefined;
  lastLogoffUnix?: number | null;
  inGame?: boolean;
  gameId?: string | null;
  gameTitle?: string | null;
}): PresenceRosterEntry {
  const lastSeen = unixSecondsToIso(input.lastLogoffUnix);
  const state = input.personaState ?? (lastSeen === null ? null : 0);
  if (input.inGame) {
    return {
      userId: input.userId,
      status: "in_game",
      gameId: input.gameId ?? null,
      gameTitle: input.gameTitle ?? null,
      lastSeen,
      availability: "available",
    };
  }
  if (state === 0) return authoritativeOfflinePresence(input.userId, lastSeen);
  if (state === 1 || state === 5 || state === 6) {
    return {
      userId: input.userId,
      status: "online",
      gameId: null,
      gameTitle: null,
      lastSeen,
      availability: "available",
    };
  }
  if (state === 2 || state === 3 || state === 4) {
    return {
      userId: input.userId,
      status: "away",
      gameId: null,
      gameTitle: null,
      lastSeen,
      availability: "available",
    };
  }
  return unavailablePresence(input.userId);
}
