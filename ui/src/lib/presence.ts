import type { OnlinePresenceEntry, OnlinePresenceEvent } from './host'

export type PresenceByUser = Record<string, OnlinePresenceEntry>

function unavailableEntry(entry: OnlinePresenceEntry): OnlinePresenceEntry {
  return {
    ...entry,
    status: 'unknown',
    gameId: null,
    gameTitle: null,
    available: false,
  }
}

/**
 * Failure is not offline. Project the last roster into an unavailable snapshot
 * in one state transition and remove activity that is no longer authoritative.
 */
export function downgradePresenceRoster(current: PresenceByUser): PresenceByUser {
  return Object.fromEntries(
    Object.entries(current).map(([userId, entry]) => [userId, unavailableEntry(entry)]),
  )
}

/** Build one REST snapshot while keeping per-user unavailability scoped. */
export function projectPresenceRoster(entries: OnlinePresenceEntry[]): PresenceByUser {
  return Object.fromEntries(
    entries.map((entry) => [entry.userId, entry.available ? entry : unavailableEntry(entry)]),
  )
}

/** A transport failure is roster-wide; ordinary presence messages are per-user. */
export function applyPresenceEvent(
  current: PresenceByUser,
  event: OnlinePresenceEvent,
): PresenceByUser {
  if (event.scope === 'roster' || event.kind === 'transportError') {
    return downgradePresenceRoster(current)
  }

  const next = event.presence
  if (!next?.userId) return current
  return {
    ...current,
    [next.userId]: next.available ? next : unavailableEntry(next),
  }
}
