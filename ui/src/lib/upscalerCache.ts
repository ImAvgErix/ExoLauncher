import { host, type DlssStatus, type Game } from './host'
import { isAntiCheatTitle } from './social'

const statusByGame = new Map<string, DlssStatus>()
const inflightByGame = new Map<string, Promise<DlssStatus>>()

function keyFor(id: string): string {
  return id.trim().toLowerCase()
}

export function peekUpscalerStatus(id: string): DlssStatus | null {
  return statusByGame.get(keyFor(id)) ?? null
}

export function loadUpscalerStatus(id: string, refresh = false): Promise<DlssStatus> {
  const key = keyFor(id)
  if (!refresh) {
    const cached = statusByGame.get(key)
    if (cached) return Promise.resolve(cached)
    const inflight = inflightByGame.get(key)
    if (inflight) return inflight
  }

  const request = host.dlssStatus(id)
    .then((status) => {
      statusByGame.set(key, status)
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
  games: readonly Pick<Game, 'id' | 'installed' | 'title' | 'store' | 'stores'>[],
  concurrency = 4,
  budgetMs = 10_000,
): Promise<void> {
  const ids = Array.from(new Set(
    games
      .filter((game) => game.installed && !isAntiCheatTitle(game))
      .map((game) => game.id),
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
