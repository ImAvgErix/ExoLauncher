import { resolvePrimaryAction, type Game, type InstallProgress } from './host'

export type NowKind = 'download' | 'playing' | 'update' | 'recent'

export type NowPick = { game: Game; kind: NowKind }

function matches(game: Game, id: string): boolean {
  const needle = id.toLowerCase()
  if (game.id.toLowerCase() === needle) return true
  if (game.variants?.some((variant) => variant.id.toLowerCase() === needle)) return true
  const app = id.match(/^steam:(\d+)/i)?.[1]
  const own = game.id.match(/^steam:(\d+)/i)?.[1]
  return !!app && app === own
}

function isPlaying(game: Game): boolean {
  return !!(
    game.canStop ||
    game.isRunning ||
    game.variants?.some((variant) => variant.canStop || variant.isRunning)
  )
}

function hasUpdate(game: Game): boolean {
  return (
    resolvePrimaryAction(game) === 'update' ||
    !!game.updateAvailable ||
    !!game.variants?.some((variant) => variant.updateAvailable)
  )
}

/**
 * One game that currently matters. Not a rotator.
 * Download / playing override a stale last-launched. Otherwise update, else
 * last launched. Nothing installed and nothing played → nothing.
 */
export function pickNow(
  games: Game[],
  progress: InstallProgress | null,
  recentIds: string[] = [],
): NowPick | null {
  const pool = games.filter((game) => !game.isAddPortable && game.id !== 'local:add')
  if (progress?.isActive && progress.gameId) {
    const downloading = pool.find((game) => matches(game, progress.gameId))
    if (downloading) return { game: downloading, kind: 'download' }
  }

  const playing = pool.find(isPlaying)
  if (playing) return { game: playing, kind: 'playing' }

  const update = pool.find((game) => game.installed && hasUpdate(game))
  if (update) return { game: update, kind: 'update' }

  const byClock = pool
    .filter((game) => game.installed && game.lastPlayedUtc)
    .sort((a, b) => Date.parse(b.lastPlayedUtc ?? '') - Date.parse(a.lastPlayedUtc ?? ''))
  if (byClock[0]) return { game: byClock[0], kind: 'recent' }

  for (const id of recentIds) {
    const hit = pool.find((game) => game.installed && matches(game, id))
    if (hit) return { game: hit, kind: 'recent' }
  }

  return null
}

/**
 * Tile click / library churn must not steal the banner. Download and Play still can.
 */
export function retainNow(
  games: Game[],
  picked: NowPick | null,
  holdId: string | null | undefined,
): NowPick | null {
  if (picked == null) return null
  if (!holdId) return picked
  if (matches(picked.game, holdId)) return picked
  if (picked.kind === 'download' || picked.kind === 'playing') return picked
  const held = games.find((game) => matches(game, holdId))
  if (!held || held.isAddPortable || held.id === 'local:add' || !held.installed) return picked
  if (hasUpdate(held)) return { game: held, kind: 'update' }
  return { game: held, kind: 'recent' }
}

export function nowKicker(kind: NowKind): string {
  switch (kind) {
    case 'download':
      return 'Downloading'
    case 'playing':
      return 'Playing'
    case 'update':
      return 'Update'
    case 'recent':
      return 'Last launched'
    default: {
      const exhaustive: never = kind
      return exhaustive
    }
  }
}
