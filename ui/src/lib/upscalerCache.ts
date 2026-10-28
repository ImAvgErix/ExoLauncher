import { host, type DlssStatus, type Game } from './host'

const statusByGame = new Map<string, DlssStatus>()
const cachedAt = new Map<string, number>()
const inflightByGame = new Map<string, Promise<DlssStatus>>()
function keyFor(id: string): string {
  return id.trim().toLowerCase()
}

export function peekUpscalerStatus(id: string): DlssStatus | null {
  return statusByGame.get(keyFor(id)) ?? null
}

export function invalidateUpscalerStatus(id?: string): void {
  if (id === undefined) { statusByGame.clear(); cachedAt.clear(); inflightByGame.clear(); return }
  const key = keyFor(id)
  statusByGame.delete(key)
  cachedAt.delete(key)
  inflightByGame.delete(key)
}

/** A game's upscaler files change when it updates or Exo updates them (which drops its entry): read again after 30 min. */
const KEEP_MS = 30 * 60_000

export function loadUpscalerStatus(id: string, refresh = false): Promise<DlssStatus> {
  const key = keyFor(id)
  if (!refresh) {
    const cached = statusByGame.get(key)
    if (cached && Date.now() - (cachedAt.get(key) ?? 0) < KEEP_MS) return Promise.resolve(cached)
    const inflight = inflightByGame.get(key)
    if (inflight) return inflight
  }

  const request = host.dlssStatus(id, refresh)
    .then((status) => {
      if (inflightByGame.get(key) === request) {
        statusByGame.set(key, status)
        cachedAt.set(key, Date.now())
        // Keep this session cache bounded even after browsing a very large library.
        if (statusByGame.size > 512) {
          const oldest = statusByGame.keys().next().value
          if (oldest !== undefined) { statusByGame.delete(oldest); cachedAt.delete(oldest) }
        }
      }
      return status
    })
    .finally(() => {
      if (inflightByGame.get(key) === request) inflightByGame.delete(key)
    })
  inflightByGame.set(key, request)
  return request
}

/**
 * Warm installed-game status with bounded concurrency. The ten-second ceiling
 * keeps a broken disk/provider probe from trapping startup; completed reads
 * remain cached and unfinished reads continue filling the cache quietly.
 */
export async function preloadUpscalerStatuses(
  games: readonly Pick<Game, 'id' | 'installed' | 'title' | 'store' | 'stores' | 'variants'>[],
  concurrency = 4,
  budgetMs = 10_000,
): Promise<void> {
  const ids = Array.from(new Set(
    games
      .flatMap((game) => [
        ...(game.installed ? [game.id] : []),
        ...(game.variants ?? []).filter(variant => variant.installed).map(variant => variant.id),
      ]),
  ))
  if (ids.length === 0) return

  let cursor = 0
  const workers = Array.from({ length: Math.min(Math.max(1, concurrency), ids.length) }, async () => {
    while (cursor < ids.length) {
      const id = ids[cursor++]
      try { await loadUpscalerStatus(id) } catch { /* details retain the honest unavailable state */ }
    }
  })
  await Promise.race([
    Promise.all(workers).then(() => undefined),
    new Promise<void>((resolve) => window.setTimeout(resolve, budgetMs)),
  ])
}
