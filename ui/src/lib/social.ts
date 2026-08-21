/**
 * The Exo profile and the two people lists behind the Friends room.
 *
 * The profile is the user's own: they author it, the host keeps it in
 * settings.json, and nothing here is read from a store persona. Store friends
 * remain a separate source, while accepted Exo friends and their privacy-aware
 * presence come from the optional online service.
 */

export type Presence = 'ingame' | 'online' | 'away' | 'dnd' | 'offline' | 'unknown'

export const PRESENCE_LABEL: Record<Presence, string> = {
  ingame: 'In game',
  online: 'Online',
  away: 'Idle',
  dnd: 'Do not disturb',
  offline: 'Offline',
  unknown: 'Unavailable',
}

/**
 * Who counts as around. Offline is not one of them, and neither is unknown — a
 * name a store client left on disk is not a person Exo can see.
 */
const ACTIVE_PRESENCE: readonly Presence[] = ['ingame', 'online', 'away', 'dnd']

export function isActivePresence(status: string | null | undefined): boolean {
  return ACTIVE_PRESENCE.includes(presenceOf(status))
}

/** Shown when the host has store names but no live session behind them. */
export const CACHE_PRESENCE_NOTE =
  'Names come from the store client cache on this PC. Live presence is unavailable until that store reports a session.'

/** Minimum a room needs from a store friend row; the host may send more. */
export type FriendLike = { id: string; name: string; status?: string | null; live?: boolean }

export function presenceOf(status: string | null | undefined): Presence {
  return status && status in PRESENCE_LABEL ? (status as Presence) : 'unknown'
}

/** A status is presence only while the row says its store reading is live. */
export function friendPresence(friend: FriendLike): Presence {
  return friend.live === true ? presenceOf(friend.status) : 'unknown'
}

const PRESENCE_RANK: Record<Presence, number> = {
  ingame: 0,
  online: 1,
  away: 2,
  dnd: 3,
  unknown: 4,
  offline: 5,
}

const PRESENCE_GROUPS: ReadonlyArray<{
  key: Presence
  label: string
  statuses: readonly Presence[]
}> = [
  { key: 'ingame', label: PRESENCE_LABEL.ingame, statuses: ['ingame'] },
  { key: 'online', label: PRESENCE_LABEL.online, statuses: ['online'] },
  { key: 'away', label: PRESENCE_LABEL.away, statuses: ['away'] },
  { key: 'dnd', label: PRESENCE_LABEL.dnd, statuses: ['dnd'] },
  { key: 'unknown', label: 'Presence unavailable', statuses: ['unknown'] },
  { key: 'offline', label: PRESENCE_LABEL.offline, statuses: ['offline'] },
]

export function sortFriends<T extends FriendLike>(friends: readonly T[]): T[] {
  return [...friends].sort(
    (a, b) =>
      PRESENCE_RANK[friendPresence(a)] - PRESENCE_RANK[friendPresence(b)] ||
      a.name.localeCompare(b.name, undefined, { sensitivity: 'base' }),
  )
}

export type PresenceGroup<T extends FriendLike> = {
  key: Presence
  label: string
  friends: T[]
}

/** Empty groups are dropped. Missing presence never masquerades as offline. */
export function groupFriends<T extends FriendLike>(friends: readonly T[]): PresenceGroup<T>[] {
  const sorted = sortFriends(friends)
  return PRESENCE_GROUPS.map((group) => ({
    key: group.key,
    label: group.label,
    friends: sorted.filter((friend) => group.statuses.includes(friendPresence(friend))),
  })).filter((group) => group.friends.length > 0)
}

/**
 * Titles the rest of Exo keeps its hands off, mirroring the host's anti-cheat
 * markers. Nothing here is ever launched from a friend's row.
 */
const ANTI_CHEAT_TITLES: readonly string[] = [
  'fortnite',
  'valorant',
  'league of legends',
  'teamfight tactics',
  'legends of runeterra',
  '2xko',
]

export type GameVariantLike = {
  id: string
  store?: string
  installed?: boolean
  owned?: boolean
  canInstall?: boolean
  updateAvailable?: boolean
  canStop?: boolean
  primaryAction?: string
}

export type GameLike = {
  id: string
  title: string
  store?: string
  stores?: readonly string[]
  variants?: readonly GameVariantLike[]
  installed?: boolean
  owned?: boolean
  canInstall?: boolean
  updateAvailable?: boolean
  /** True only for a revalidated game process Exo may safely stop. */
  canStop?: boolean
  primaryAction?: string
  /** Host storefront URL. Absent when Buy must not appear. */
  buyUrl?: string | null
}

export function isAntiCheatTitle(game: GameLike): boolean {
  const stores = [game.store, ...(game.stores ?? [])]
  if (stores.some((store) => store === 'riot')) return true
  const title = game.title.toLowerCase()
  return ANTI_CHEAT_TITLES.some((marker) => title.includes(marker))
}

/** Resolve an exact presence id through a grouped card without losing its art/title. */
function gameForPlayingId<T extends GameLike>(
  playingId: string | null | undefined,
  games: readonly T[],
): T | null {
  if (!playingId) return null
  for (const card of games) {
    if (card.id === playingId) return card
    const variant = card.variants?.find((variant) => variant.id === playingId)
    if (variant) return Object.assign({}, card, variant)
  }
  return null
}

/**
 * The library entry a friend's presence names, but only when Exo could really
 * play it: installed, and never an anti-cheat title.
 */
export function openableGame<T extends GameLike>(
  playingId: string | null | undefined,
  games: readonly T[],
): T | null {
  const game = gameForPlayingId(playingId, games)
  if (!game || !game.installed || isAntiCheatTitle(game)) return null
  // Install/none means this row cannot play, even if a file happens to exist.
  if (game.primaryAction === 'none' || game.primaryAction === 'install') return null
  return game
}

export type FriendPlayingKind = 'play' | 'stop' | 'install' | 'update' | 'buy' | 'none'

export type FriendPlayingAction<T extends GameLike = GameLike> = {
  title: string
  game: T | null
  kind: FriendPlayingKind
  /** Button copy. Null means there is nothing honest to press. */
  label: string | null
  url: string | null
  /** Exact Steam app id, present only for an authoritative unmatched Steam buy. */
  steamAppId: string | null
  reason: string | null
}

/**
 * Presence named a Steam app that is not in this library. The app id is
 * enough to open the store page; it is not enough to Install.
 */
function steamStoreUrlFromPlayingId(playingId: string | null | undefined): string | null {
  const id = steamAppIdFromPlayingId(playingId)
  return id ? `steam://store/${id}` : null
}

function steamAppIdFromPlayingId(playingId: string | null | undefined): string | null {
  if (!playingId?.startsWith('steam:')) return null
  const id = playingId.slice('steam:'.length)
  return /^\d+$/.test(id) ? id : null
}

/**
 * What the Friends room may offer for a title a store said someone is in.
 * Stop and Update mirror the matched library entry before Play is considered.
 * A Steam app id that is not in the library is Buy, not Install. A name Exo
 * cannot match says so — no button.
 */
export function friendPlayingAction<T extends GameLike>(
  playingId: string | null | undefined,
  playingTitle: string | null | undefined,
  games: readonly T[],
): FriendPlayingAction<T> | null {
  const named = playingTitle?.trim() || ''
  const game = gameForPlayingId(playingId, games)
  const title = game?.title || named
  if (!title) return null

  if (!game) {
    const id = steamAppIdFromPlayingId(playingId)
    const url = steamStoreUrlFromPlayingId(playingId)
    if (url && id) {
      return {
        title,
        game: null,
        kind: 'buy',
        label: 'Buy on Steam',
        url,
        steamAppId: id,
        reason: null,
      }
    }
    return {
      title,
      game: null,
      kind: 'none',
      label: null,
      url: null,
      steamAppId: null,
      reason: 'Exo cannot match this title, so it cannot install it.',
    }
  }

  if (isAntiCheatTitle(game)) {
    return {
      title: game.title,
      game,
      kind: 'none',
      label: null,
      url: null,
      steamAppId: null,
      reason: 'Exo does not open anti-cheat titles from here.',
    }
  }

  if (game.canStop) {
    return {
      title: game.title,
      game,
      kind: 'stop',
      label: 'Stop',
      url: null,
      steamAppId: null,
      reason: null,
    }
  }

  if (game.installed && (game.updateAvailable === true || game.primaryAction === 'update')) {
    return {
      title: game.title,
      game,
      kind: 'update',
      label: 'Update',
      url: null,
      steamAppId: null,
      reason: null,
    }
  }

  const playable = openableGame(game.id, games)
  if (playable) {
    return {
      title: playable.title,
      game: playable,
      kind: 'play',
      label: 'Play',
      url: null,
      steamAppId: null,
      reason: null,
    }
  }

  if (!game.installed && (game.canInstall === true || game.primaryAction === 'install')) {
    return {
      title: game.title,
      game,
      kind: 'install',
      label: 'Install',
      url: null,
      steamAppId: null,
      reason: null,
    }
  }

  if (!game.installed && game.owned === true) {
    return {
      title: game.title,
      game,
      kind: 'none',
      label: null,
      url: null,
      steamAppId: null,
      reason: 'Owned, not installed on this PC.',
    }
  }

  const url = game.buyUrl?.trim() || null
  if (url) {
    return {
      title: game.title,
      game,
      kind: 'buy',
      label: buyLabel(game.store),
      url,
      steamAppId: null,
      reason: null,
    }
  }

  return {
    title: game.title,
    game,
    kind: 'none',
    label: null,
    url: null,
    steamAppId: null,
    reason: 'Exo cannot open a store page for this title.',
  }
}

export function buyLabel(store: string | null | undefined): string {
  if (store === 'steam') return 'Buy on Steam'
  if (store === 'gog') return 'Buy on GOG'
  return 'Buy in browser'
}

/** Prefer Steam's full-size portrait and never intentionally downgrade it. */
export function highestQualityAvatarUrl(url: string | null | undefined): string | null {
  const raw = url?.trim()
  if (!raw) return null
  if (/_full\.(?:jpe?g|png|gif|webp)(?:$|\?)/i.test(raw)) return raw
  if (/_medium\./i.test(raw)) return raw.replace(/_medium\./i, '_full.')
  const hashOnly = raw.match(/^(https:\/\/avatars(?:\.[a-z0-9-]+)*\.steamstatic\.com\/[0-9a-f]{40})\.(jpe?g|png|gif|webp)(\?.*)?$/i)
  return hashOnly ? `${hashOnly[1]}_full.${hashOnly[2]}${hashOnly[3] ?? ''}` : raw
}

export function steamPlayingCoverUrl(appId: string): string {
  return `https://cdn.cloudflare.steamstatic.com/steam/apps/${appId}/library_600x900_2x.jpg`
}

/** "Last seen" from a store timestamp. Null when there is nothing to say. */
export function lastSeenLabel(iso: string | null | undefined): string | null {
  if (!iso) return null
  const at = Date.parse(iso)
  if (Number.isNaN(at)) return null
  const minutes = Math.floor((Date.now() - at) / 60000)
  if (minutes < 0) return null
  if (minutes < 60) return 'Last seen under an hour ago'
  const hours = Math.floor(minutes / 60)
  if (hours < 24) return `Last seen ${hours}h ago`
  const days = Math.floor(hours / 24)
  if (days < 30) return `Last seen ${days}d ago`
  return `Last seen ${new Date(at).toLocaleDateString(undefined, {
    month: 'short',
    year: 'numeric',
  })}`
}

/** Roster row shape the rooms need. The host owns the persisted record. */
export type PersonLike = { handle: string; name?: string | null }

export function personLabel(person: PersonLike): string {
  return person.name?.trim() || `@${person.handle}`
}

export function sortPeople<T extends PersonLike>(people: readonly T[]): T[] {
  return [...people].sort((a, b) =>
    personLabel(a).localeCompare(personLabel(b), undefined, { sensitivity: 'base' }),
  )
}

/** Lengths the host enforces too. Kept here so the editor stops you first. */
export const PROFILE_LIMITS = {
  name: 40,
  handle: 24,
  pronouns: 24,
  status: 80,
  bio: 400,
  note: 120,
  roster: 100,
  showcase: 10,
} as const

/** Handles are typed, not issued: lowercase letters, digits, underscore. */
export function normalizeHandle(raw: string): string {
  return raw
    .toLowerCase()
    .replace(/[^a-z0-9_]/g, '')
    .slice(0, PROFILE_LIMITS.handle)
}

/** Null when the handle is usable. Otherwise the one thing to fix. */
export function handleProblem(raw: string): string | null {
  const trimmed = raw.trim()
  if (trimmed.length === 0) return 'Enter a handle.'
  if (normalizeHandle(trimmed) !== trimmed) return 'Lowercase letters, numbers, and underscore only.'
  if (trimmed.length < 2) return 'At least two characters.'
  return null
}

/**
 * Accents that still read on AMOLED black. Deliberately muted, and none of them
 * is the positive-state green, which stays reserved for state.
 */
export type AccentKey = 'ash' | 'steel' | 'sand' | 'clay' | 'sage' | 'rose'

export const ACCENTS: ReadonlyArray<{ key: AccentKey; label: string; hex: string }> = [
  { key: 'ash', label: 'Ash', hex: '#8a8a8a' },
  { key: 'steel', label: 'Steel', hex: '#6f8fb0' },
  { key: 'sand', label: 'Sand', hex: '#c2a878' },
  { key: 'clay', label: 'Clay', hex: '#b8776a' },
  { key: 'sage', label: 'Sage', hex: '#7d9c86' },
  { key: 'rose', label: 'Rose', hex: '#b57f92' },
]

export const DEFAULT_ACCENT: AccentKey = 'ash'

export function accentHex(key: string | null | undefined): string {
  return ACCENTS.find((accent) => accent.key === key)?.hex ?? ACCENTS[0].hex
}
