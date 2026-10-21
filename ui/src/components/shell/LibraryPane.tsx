import { memo, useCallback, useLayoutEffect, useMemo, useState, type CSSProperties, type RefObject } from 'react'
import { ContinueRow } from './ContinueRow'
import { FriendsPlaying } from './FriendsPlaying'
import { GameMenu, type GameMenuAt } from './GameMenu'
import { shelfAction, type Game, type InstallProgress, type SortMode } from '../../lib/host'
import { resolveGridCardLayout } from '../../lib/gridWindow'
import { report } from '../../lib/report'
import { BrowseShelf } from '../BrowseShelf'
import { LibraryEmpty, LibraryTools, type RefreshState } from './LibraryShelf'

export type LibraryPaneProps = {
  mainRef: RefObject<HTMLElement | null>
  active: boolean
  /** The details sheet is up: the shelf stays put and takes no input. */
  overlayOpen: boolean
  /** Nothing to show and a reason why (or null when the PC simply has no installs). */
  empty: boolean
  error: string | null
  /** The one grid's games. */
  games: Game[]
  /** Continue: what you are playing. */
  playing: Game[]
  selectedId: string | null
  activeGameId: string | null
  queuedIds: string[]
  sort: SortMode
  reduceMotion: boolean
  transferFor: (game: Game) => InstallProgress | null
  onActiveGameChange: (id: string) => void
  onOpen: (game: Game) => void
  /** Play (or install) straight from Continue, Friends are playing and a card's menu. */
  onPlay: (game: Game) => void
  /** A Riot game's menu: switch to another saved Riot account, then Play. */
  onPlayAs: (game: Game, account: { key: string; name: string }) => void
  /** The card whose Play or Install is starting right now; its button says so. */
  startingId: string | null
  /** A card's menu: open its folder. */
  onOpenFolder: (game: Game) => void
  /** Opens the game's page with its Properties over it. */
  onProperties: (game: Game) => void
  onSort: (mode: SortMode) => void
  onAddFolder: () => void
  onActivateKey: () => void
  /** Library › Refresh: stores again, then every cover. */
  onRefresh: () => Promise<unknown>
  onRetry: () => void
  onStoreSettings: () => void
}

/**
 * The one card width for the grids, from the pane's own
 * width. It reaches CSS as --exo-card-w and the grids as a prop, so a width
 * change re-windows them even when their own boxes keep their size.
 */
function useCardWidth(mainRef: RefObject<HTMLElement | null>): number | null {
  const [width, setWidth] = useState<number | null>(null)
  useLayoutEffect(() => {
    const main = mainRef.current
    if (!main) return
    // Floor, not round: a rounded-up width can push the last column off the row.
    const measure = () => setWidth(Math.floor(resolveGridCardLayout(main.clientWidth).cardWidth * 100) / 100)
    measure()
    const observer = new ResizeObserver(measure)
    observer.observe(main)
    return () => observer.disconnect()
  }, [mainRef])
  return width
}

/**
 * Refresh: every store again (games that weren't showing appear), then the
 * store's art again for every game, installed or not (covers you chose stay).
 * While it runs the icon turns and a shine travels across the cards
 * (data-exo-refreshing on the page); a check shows for a moment when done.
 */
function useRefresh(onRefresh: () => Promise<unknown>): [RefreshState, () => void, string | null] {
  const [state, setState] = useState<RefreshState>('idle')
  const [error, setError] = useState<string | null>(null)
  const run = useCallback(() => {
    if (state === 'working') return
    setState('working')
    setError(null)
    document.documentElement.dataset.exoRefreshing = ''
    onRefresh()
      .then(() => {
        setState('done')
        window.setTimeout(() => setState(current => (current === 'done' ? 'idle' : current)), 1600)
      }, (cause: unknown) => {
        report(cause, 'LibraryTools.refresh')
        setError(cause instanceof Error && cause.message ? cause.message : 'Try again.')
        setState('idle')
      })
      .finally(() => { delete document.documentElement.dataset.exoRefreshing })
  }, [state, onRefresh])
  return [state, run, error]
}


export const LibraryPane = memo(function LibraryPane(props: LibraryPaneProps) {
  const { overlayOpen } = props
  const cardWidth = useCardWidth(props.mainRef)
  const { active, sort, onSort, onActivateKey, onAddFolder } = props
  const [refresh, runRefresh, refreshError] = useRefresh(props.onRefresh)
  const [menu, setMenu] = useState<GameMenuAt | null>(null)
  const { onOpen } = props
  // A card with nothing to Play, Install or open has no menu; its page has what there is.
  // A game in Continue sits there only: All games lists the rest.
  const { playing, games } = props
  const rest = useMemo(() => {
    const shown = new Set(playing.map(game => game.id))
    return shown.size ? games.filter(game => !shown.has(game.id)) : games
  }, [playing, games])
  const openMenu = useCallback((at: GameMenuAt) => {
    if (at.game.installed || shelfAction(at.game)) setMenu(at)
    else onOpen(at.game)
  }, [onOpen])
  // Retained rooms keep their grid, but menus must leave with their route. A game page over the
  // Library keeps them (the pane is inert under it), so the shelf's header never changes height.
  const tools = useMemo(() => active ? (
    <LibraryTools sort={sort} onSort={onSort} onActivateKey={onActivateKey} onAddFolder={onAddFolder} refresh={refresh} onRefresh={runRefresh} />
  ) : null, [active, sort, onSort, onActivateKey, onAddFolder, refresh, runRefresh])
  return (
    <div className="relative z-10 flex min-h-0 min-w-0 flex-1">
      <main
        ref={props.mainRef}
        className={`exo-library-pane min-w-0 flex-1${overlayOpen ? ' is-overlay-open' : ''}`}
        inert={overlayOpen ? true : undefined}
        style={cardWidth == null ? undefined : { '--exo-card-w': `${cardWidth}px` } as CSSProperties}
      >
        <h1 className="sr-only">Library</h1>
        {!props.empty && props.error ? (
          <div className="exo-library-refresh-error" role="status">
            <span>Showing saved games. {props.error}</span>
            <button type="button" className="exo-ghost-btn" onClick={props.onRetry}>Retry</button>
          </div>
        ) : !props.empty && refreshError && refresh !== 'working' ? (
          <div className="exo-library-refresh-error" role="alert">
            <span>Could not refresh the library. {refreshError}</span>
            <button type="button" className="exo-ghost-btn" onClick={runRefresh}>Retry</button>
          </div>
        ) : null}
        {props.empty ? (
          <LibraryEmpty
            error={props.error}
            onRetry={props.onRetry}
            onAddFolder={props.onAddFolder}
            onActivateKey={props.onActivateKey}
            onStoreSettings={props.onStoreSettings}
          />
        ) : (
          <div className="flex min-h-0 min-w-0 w-full flex-1 flex-col">
            <ContinueRow games={props.playing} transferFor={props.transferFor} startingId={props.startingId} onOpen={props.onOpen} onPlay={props.onPlay} onMenu={openMenu} />
            <FriendsPlaying games={props.games} startingId={props.startingId} onOpen={props.onOpen} onPlay={props.onPlay} />
            <BrowseShelf
              heading="All games"
              actions={tools}
              games={rest}
              selectedId={props.selectedId}
              activeGameId={props.activeGameId}
              onActiveGameChange={props.onActiveGameChange}
              onSelect={props.onOpen}
              onMenu={openMenu}
              transferFor={props.transferFor}
              queuedIds={props.queuedIds}
              scrollRootRef={props.mainRef}
              cardWidth={cardWidth}
            />
          </div>
        )}
      </main>
      {menu ? (
        <GameMenu
          key={`${menu.game.id}:${menu.x}:${menu.y}`}
          at={menu}
          onClose={() => setMenu(current => (current === menu ? null : current))}
          onPlay={props.onPlay}
          onPlayAs={props.onPlayAs}
          onOpenFolder={props.onOpenFolder}
          onProperties={props.onProperties}
        />
      ) : null}
    </div>
  )
})
