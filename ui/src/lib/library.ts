/**
 * Pure library-shape helpers behind the shell: grouped cards, exact store
 * variants, and the merges that keep them stable across host refreshes.
 * No React, no host calls.
 */
import type { Game, GameVariant, InstallProgress, SortMode } from './host'
import { titleIdentity } from './utils'

function steamAppIdFromText(value: string | null | undefined): string | null {
  if (!value) return null
  const fromId = value.match(/^steam:(\d+)/i)
  if (fromId) return fromId[1]
  const fromApps = value.match(/\/(?:steam\/)?apps\/(\d+)\//)
  if (fromApps) return fromApps[1]
  const fromProtocol = value.match(/steam:\/\/(?:rungameid|launch|install)\/(\d+)/i)
  if (fromProtocol) return fromProtocol[1]
  return null
}

/** Steam app id from the card id, cover CDN path, launch target, or a variant. */
export function steamAppId(
  game: Pick<Game, 'id' | 'coverUrl' | 'launchTarget' | 'variants'>,
): string | null {
  return (
    steamAppIdFromText(game.id) ||
    steamAppIdFromText(game.coverUrl) ||
    steamAppIdFromText(game.launchTarget) ||
    game.variants?.reduce<string | null>((found, variant) => {
      if (found) return found
      return steamAppIdFromText(variant.id) || steamAppIdFromText(variant.launchTarget)
    }, null) ||
    null
  )
}

export function mergedCoverUrl(incoming: Game, previous?: Game): string | null {
  const incomingRevision = incoming.artRevision ?? 0
  const previousRevision = previous?.artRevision ?? 0
  if (incomingRevision > previousRevision) return incoming.coverUrl ?? null
  if (incoming.coverUrl) return incoming.coverUrl
  if (previous?.coverUrl) return previous.coverUrl
  return null
}

export function findLibraryGame(games: Game[], id: string): Game | undefined {
  const needle = id.toLowerCase()
  return (
    games.find(
      (game) =>
        game.id.toLowerCase() === needle ||
        game.variants?.some((variant) => variant.id.toLowerCase() === needle),
    ) ?? games.find((game) => sameSteamApp(game, id))
  )
}

export function findLibraryGameByTitle(games: Game[], title: string): Game | undefined {
  const key = titleIdentity(title)
  if (!key) return undefined
  return games.find((game) => isSearchableLibraryGame(game) && titleIdentity(game.title) === key)
}

function sameSteamApp(game: Game, id: string): boolean {
  const app = id.match(/^steam:(\d+)/i)?.[1]
  if (!app) return false
  return steamAppId(game) === app
}

export function isSearchableLibraryGame(game: Game): boolean {
  if (game.isAddPortable || game.id === 'local:add') return false
  return !!(game.installed || game.owned || game.canInstall)
}

export function isInstallingGame(game: Game, installingId: string | null): boolean {
  if (!installingId) return false
  return (
    game.id === installingId ||
    !!game.variants?.some((variant) => variant.id === installingId) ||
    sameSteamApp(game, installingId)
  )
}

export function transferForGame(
  progress: InstallProgress | null,
  game: Game,
): InstallProgress | null {
  if (!progress?.isActive || !progress.gameId) return null
  if (!isInstallingGame(game, progress.gameId)) return null
  return progress
}

/** Structural equality for host JSON: plain objects, arrays and primitives. Undefined keys count as absent. */
function sameJson(left: unknown, right: unknown): boolean {
  if (left === right) return true
  if (typeof left !== 'object' || typeof right !== 'object' || !left || !right) return false
  if (Array.isArray(left) || Array.isArray(right)) {
    return Array.isArray(left) && Array.isArray(right) && left.length === right.length &&
      left.every((item, index) => sameJson(item, right[index]))
  }
  const a = left as Record<string, unknown>, b = right as Record<string, unknown>
  const keys = Object.keys(a).filter(key => a[key] !== undefined)
  return keys.length === Object.keys(b).filter(key => b[key] !== undefined).length &&
    keys.every(key => sameJson(a[key], b[key]))
}

/** The previous card object when a refresh carries identical data, so memoized cards skip rendering. */
export function reuseGame(previous: Game | undefined, next: Game): Game {
  return previous && sameJson(previous, next) ? previous : next
}

/** The previous list itself when every entry is the same object in the same order. */
export function reuseList<T>(previous: T[], next: T[]): T[] {
  return previous.length === next.length && next.every((item, index) => item === previous[index]) ? previous : next
}

const issuedGameReads = new Map<string, number>()
const appliedGameReads = new Map<string, number>()

/**
 * Tag one `game.get` for an exact id. The returned check is true when the
 * response may be applied: no newer read (or local run-state change) for the
 * same id has landed first. Responses arriving out of order are dropped.
 */
export function beginGameRead(id: string): () => boolean {
  const sequence = (issuedGameReads.get(id) ?? 0) + 1
  issuedGameReads.set(id, sequence)
  return () => {
    if (sequence < (appliedGameReads.get(id) ?? 0)) return false
    appliedGameReads.set(id, sequence)
    return true
  }
}

/**
 * A `library.updated` push during cache warm. Never wipes a good cover with
 * null.
 */
export function mergeLibraryUpdate(prev: Game[], pushed: Game[], selectedId: string | null): Game[] {
  const prevById = new Map(prev.map((g) => [g.id, g]))
  const incoming = pushed.map((g) => {
    const old = prevById.get(g.id)
    return reuseGame(old, {
      ...old,
      ...g,
      coverUrl: mergedCoverUrl(g, old),
      playtimeMinutes: keepPlaytime(g.playtimeMinutes, old?.playtimeMinutes),
      lastPlayedUtc: keepLastPlayed(g.lastPlayedUtc, old?.lastPlayedUtc),
      isRunning: typeof g.isRunning === 'boolean' ? g.isRunning : !!old?.isRunning,
      canStop: typeof g.canStop === 'boolean' ? g.canStop : !!old?.canStop,
    })
  })
  return reuseList(prev, mergeHostGames(prev, incoming, selectedId))
}

export function mergeHostGames(prev: Game[], incoming: Game[], selectedId: string | null): Game[] {
  const nextIds = new Set(incoming.map((game) => game.id.toLowerCase()))
  const retained = prev.filter((game) => {
    if (nextIds.has(game.id.toLowerCase())) return false
    if (!selectedId) return false
    if (game.id === selectedId || game.variants?.some((variant) => variant.id === selectedId)) return true
    return sameSteamApp(game, selectedId)
  })
  return retained.length ? [...incoming, ...retained] : incoming
}


export function cardForExactId(games: Game[], id: string | null): Game | null {
  if (!id) return null
  return games.find((game) =>
    game.id === id || game.variants?.some((variant) => variant.id === id),
  ) ?? null
}

export function preferredVariantId(card: Game, preferences?: Record<string, string>): string | null {
  const preferred = card.canonicalTitleKey ? preferences?.[card.canonicalTitleKey] : undefined
  return preferred && card.variants?.some(variant => variant.id === preferred)
    ? preferred
    : null
}

export function materializeVariant(card: Game, variantId: string | null): Game {
  // An explicit source can be installed or available for download. With no
  // explicit choice, favor a running or installed copy as before.
  const installed = card.variants?.filter(item => item.installed) ?? []
  const variant = card.variants?.find(item => item.id === variantId) ??
    installed.find(item => item.canStop || item.isRunning) ??
    installed.find(item => item.id === card.selectedVariantId || item.id === card.id) ??
    installed[0]
  if (!variant) return card
  return {
    ...card,
    id: variant.id,
    store: variant.store,
    installed: variant.installed,
    owned: variant.owned,
    entitlementState: variant.entitlementState,
    updateAvailable: variant.updateAvailable,
    canInstall: variant.canInstall,
    primaryAction: variant.primaryAction,
    path: variant.path,
    launchTarget: variant.launchTarget,
    playtimeMinutes: variant.playtimeMinutes,
    lastPlayedUtc: variant.lastPlayedUtc,
    status: variant.status,
    isRunning: variant.isRunning,
    canStop: variant.canStop,
    selectedVariantId: variant.id,
  }
}

/**
 * The host's hours are the truth whenever it sends them (0 is none): they can go
 * down when an account is switched or a source stops counting. Only a payload
 * that leaves them out keeps the hours already shown.
 */
function keepPlaytime(next?: number | null, prev?: number | null): number | null | undefined {
  if (typeof next === 'number') return next > 0 ? next : null
  return prev ?? next
}

function keepLastPlayed(next?: string | null, prev?: string | null): string | null | undefined {
  if (!next) return prev ?? next
  if (!prev) return next
  const nextTime = Date.parse(next)
  const prevTime = Date.parse(prev)
  if (Number.isNaN(nextTime)) return prev
  if (Number.isNaN(prevTime)) return next
  return nextTime >= prevTime ? next : prev
}

function variantFromGame(game: Game, previous?: GameVariant): GameVariant {
  return {
    id: game.id,
    store: game.store,
    installed: game.installed,
    owned: game.owned,
    entitlementState: game.entitlementState,
    updateAvailable: game.updateAvailable,
    canInstall: game.canInstall,
    primaryAction: game.primaryAction,
    path: game.path,
    launchTarget: game.launchTarget,
    playtimeMinutes: keepPlaytime(game.playtimeMinutes, previous?.playtimeMinutes),
    lastPlayedUtc: keepLastPlayed(game.lastPlayedUtc, previous?.lastPlayedUtc),
    status: game.status,
    isRunning: game.isRunning,
    canStop: game.canStop,
  }
}

/** Keep a grouped card intact when `game.get` refreshes one of its exact sources. */
export function mergeExactGame(items: Game[], refreshed: Game): Game[] {
  return items.map((item) => {
    if (item.id === refreshed.id) {
      // Preserve the cover when a single-source refresh omits it.
      return {
        ...refreshed,
        coverUrl: refreshed.coverUrl || item.coverUrl || null,
        playtimeMinutes: keepPlaytime(refreshed.playtimeMinutes, item.playtimeMinutes),
        lastPlayedUtc: keepLastPlayed(refreshed.lastPlayedUtc, item.lastPlayedUtc),
        variants: refreshed.variants?.length ? refreshed.variants : item.variants,
      }
    }
    if (!item.variants?.some((variant) => variant.id === refreshed.id)) return item
    const variants = item.variants.map((variant) =>
      variant.id === refreshed.id ? variantFromGame(refreshed, variant) : variant,
    )
    const anyRunning = variants.some((variant) => variant.isRunning)
    const anyStop = variants.some((variant) => variant.canStop)
    return {
      ...item,
      variants,
      isRunning: anyRunning,
      canStop: anyStop,
      playtimeMinutes: item.id === refreshed.id
        ? keepPlaytime(refreshed.playtimeMinutes, item.playtimeMinutes)
        : item.playtimeMinutes,
    }
  })
}

/** A `game.get` overtaken by a newer run-state push: take its other fields
 * (playtime, last played) but keep the run state the push already set. */
export function mergeExactGameKeepingRunState(items: Game[], refreshed: Game): Game[] {
  let known: { isRunning?: boolean; canStop?: boolean } | undefined
  for (const item of items) {
    known = item.id === refreshed.id ? item : item.variants?.find((variant) => variant.id === refreshed.id)
    if (known) break
  }
  const merged = mergeExactGame(items, refreshed)
  return known ? setExactRunState(merged, refreshed.id, !!known.isRunning, !!known.canStop) : merged
}

export function statusBelongsToSelection(statusGameId: string | null, selected: Game | null): boolean {
  if (!statusGameId || !selected) return false
  if (statusGameId === selected.id) return true
  if (selected.selectedVariantId && statusGameId === selected.selectedVariantId) return true
  if (selected.variants?.some((v) => v.id === statusGameId)) return true
  return sameSteamApp(selected, statusGameId)
}

/** Update one exact source's transient run state without collapsing its card.
 * Returns the previous array when nothing changed so the 2s run-state poll does
 * not invalidate every useMemo downstream of `games`. */
export function setExactRunState(items: Game[], id: string, isRunning: boolean, canStop: boolean): Game[] {
  let changed = false
  const next = items.map((item): Game => {
    let variantsChanged = false
    const variants = item.variants?.map((variant) => {
      if (variant.id !== id) return variant
      if (variant.isRunning === isRunning && variant.canStop === canStop) return variant
      variantsChanged = true
      return { ...variant, isRunning, canStop }
    })
    if (variantsChanged) changed = true
    if (item.id === id) {
      const flagsChanged = item.isRunning !== isRunning || item.canStop !== canStop
      if (!flagsChanged && !variantsChanged) return item
      changed = true
      return { ...item, isRunning, canStop, variants: variants ?? item.variants }
    }
    if (variantsChanged && variants) {
      const anyRunning = variants.some((variant) => !!variant.isRunning)
      const anyStop = variants.some((variant) => !!variant.canStop)
      return { ...item, variants, isRunning: anyRunning, canStop: anyStop }
    }
    return item
  })
  return changed ? next : items
}

export const SORT_OPTIONS: SortMode[] = ['name', 'installed', 'recent', 'played', 'size', 'store']
export const SORT_LABELS: Record<SortMode, string> = {
  name: 'Name',
  installed: 'Installed first',
  recent: 'Last launched',
  played: 'Time played',
  size: 'Size',
  store: 'Store',
}

export function librarySortMode(value: string | undefined): SortMode {
  return SORT_OPTIONS.includes(value as SortMode) ? (value as SortMode) : 'name'
}
