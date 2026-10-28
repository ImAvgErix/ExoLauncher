import { useMemo } from 'react'
import type { Game, InstallProgress, LauncherSettings } from '../../lib/host'
import { isPlayableLibraryGame } from '../../lib/catalogHygiene'
import { isInstallingGame, librarySortMode } from '../../lib/library'
import { sortGames } from '../../lib/utils'

export type Shelves = {
  /** The one grid: every card the library shows, sorted the user's way. Installed or not is a mark, not a place. Empty means the empty state. */
  library: Game[]
  /** What you are playing: running, then downloading or updating, then the most recently played; four at most (three across shows three, two across all four). */
  playing: Game[]
  /** The games you hid: out of the grid, Continue and folders; Library's Hidden view lists them, sorted the same way. */
  hidden: Game[]
}

const NONE: Game[] = []

/** A card the library shows: playable, and installed, owned, downloading or a local folder. */
function isShelfGame(game: Game, transferId: string | null, showTools: boolean): boolean {
  if (!isPlayableLibraryGame(game, showTools)) return false
  if (isInstallingGame(game, transferId)) return true
  if (game.installed || game.owned) return true
  return game.store === 'local'
}

const CONTINUE_MAX = 4

function continueOrder(games: Game[], transferId: string | null): Game[] {
  const rank = (game: Game) => game.isRunning ? 0 : isInstallingGame(game, transferId) ? 1 : 2
  const played = (game: Game) => (game.lastPlayedUtc ? Date.parse(game.lastPlayedUtc) : 0)
  return games
    .filter(game => game.isRunning || isInstallingGame(game, transferId) || (game.installed && played(game) > 0))
    .sort((a, b) => rank(a) - rank(b) || played(b) - played(a))
    .slice(0, CONTINUE_MAX)
}

/**
 * The library for its three places (DESIGN.md › Library): Continue, Friends
 * are playing, and one grid, less the games you hid. Pure derivation over
 * `games`. `recent` is the host's launch order from the latest library read.
 */
export function useShelves(games: Game[], progress: InstallProgress | null, settings: LauncherSettings | null, recent: string[] | undefined): Shelves {
  const sortMode = settings?.sortMode
  const showTools = settings?.showLibraryTools === true
  const transferId = progress?.isActive ? progress.gameId : null

  const hiddenIds = settings?.hiddenGames
  const all = useMemo(
    () => sortGames(games.filter((game) => isShelfGame(game, transferId, showTools)), librarySortMode(sortMode), recent ?? []),
    [games, sortMode, recent, transferId, showTools],
  )
  const [library, hidden] = useMemo(() => {
    if (!hiddenIds?.length) return [all, NONE]
    const out = new Set(hiddenIds)
    return [all.filter(game => !out.has(game.id)), all.filter(game => out.has(game.id))]
  }, [all, hiddenIds])
  const playing = useMemo(() => continueOrder(library, transferId), [library, transferId])

  return useMemo(() => ({ library, playing, hidden }), [library, playing, hidden])
}
