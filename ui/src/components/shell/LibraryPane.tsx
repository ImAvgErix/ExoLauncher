import { memo, useCallback, useLayoutEffect, useMemo, useState, type CSSProperties, type RefObject } from 'react'
import { ContinueRow } from './ContinueRow'
import { FolderPanel } from './FolderPanel'
import { FolderSheet } from './FolderSheet'
import { FriendsPlaying } from './FriendsPlaying'
import { GameMenu, type GameMenuAt } from './GameMenu'
import { type Game, type InstallProgress, type LibraryFolder, type SortMode } from '../../lib/host'
import { folderColor, folderTileId, freeFolderName, isFolderTile, newFolderId, renameFolder, saveFolder, withFolders, type FolderTile } from '../../lib/folders'
import { resolveGridCardLayout } from '../../lib/gridWindow'
import { pressedArt, transition, visible } from '../../lib/viewTransition'
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
  /** The Library's folders, and the change to them (shown at once, then saved). */
  folders: LibraryFolder[]
  onFolders: (next: LibraryFolder[]) => void
  onAddGame: () => void
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


/** The open folder takes the keyboard back once a sheet over it closes, so Escape closes the folder. */
const focusPanel = () => requestAnimationFrame(() =>
  document.querySelector<HTMLElement>('.exo-folder-panel button[data-game-id]')?.focus({ preventScroll: true }))

/** A folder's card art on screen: where its panel and its sheet grow from and fold back into. */
const tileArt = (id: string) => visible(`[data-game-id="${CSS.escape(folderTileId(id))}"] [data-vt-art]`)

export const LibraryPane = memo(function LibraryPane(props: LibraryPaneProps) {
  const { overlayOpen } = props
  const cardWidth = useCardWidth(props.mainRef)
  const { active, sort, onSort, onActivateKey, onAddGame, onAddFolder } = props
  const [refresh, runRefresh, refreshError] = useRefresh(props.onRefresh)
  const [menu, setMenu] = useState<GameMenuAt | null>(null)
  const { onOpen, folders, onFolders, overlayOpen: covered } = props
  // A game in Continue sits there only: All games lists the rest, its folders first as one card each.
  const { playing, games } = props
  const rest = useMemo(() => {
    const shown = new Set(playing.map(game => game.id))
    return shown.size ? games.filter(game => !shown.has(game.id)) : games
  }, [playing, games])
  const grid = useMemo(() => withFolders(rest, games, folders), [rest, games, folders])
  // The open folder; a new `rename` key opens it again with its name ready to type.
  const [open, setOpen] = useState<{ id: string; rename: number } | null>(null)
  const openTile = useMemo(() => open ? grid.find((item): item is FolderTile => isFolderTile(item) && item.folder.id === open.id) : undefined, [open, grid])
  // A folder grows out of its card into its panel and back into it (DESIGN.md › Motion › Move: art's flight).
  const openFolder = useCallback((id: string, rename = false) => {
    const from = pressedArt() ?? tileArt(id)
    void transition(() => setOpen({ id, rename: rename ? Date.now() : 0 }), {
      kind: 'morph',
      morph: { name: 'exo-folder', from, to: () => visible('.exo-folder-panel') },
    })
  }, [])
  const closeFolder = useCallback(() => {
    const id = open?.id
    void transition(() => setOpen(null), {
      kind: 'morph',
      back: true,
      morph: { name: 'exo-folder', from: visible('.exo-folder-panel'), to: () => id ? tileArt(id) : null },
    })
  }, [open])
  const select = useCallback((game: Game) => { if (isFolderTile(game)) openFolder(game.folder.id); else onOpen(game) }, [openFolder, onOpen])
  // New folder and Edit folder: a sheet over the Library (FolderSheet).
  const [sheet, setSheet] = useState<(LibraryFolder & { isNew: boolean }) | null>(null)
  const newFolder = useCallback((gameId?: string) => setSheet({
    id: newFolderId(folders), name: freeFolderName(folders), color: 'blue', games: gameId ? [gameId] : [], isNew: true,
  }), [folders])
  const editFolder = useCallback((id: string) => {
    const folder = folders.find(item => item.id === id)
    if (folder) setSheet({ ...folder, color: folderColor(folder.color), isNew: false })
  }, [folders])
  const { mainRef } = props
  // Saved, the sheet folds into the folder's card: a new one at the front of All games (scrolled into sight first),
  // which then glows once in its color. From inside the open folder the sheet just closes over it.
  const saveSheet = useCallback((folder: LibraryFolder) => {
    const next = saveFolder(folders, folder)
    const main = mainRef.current
    const front = main?.querySelector<HTMLElement>('.exo-windowed-game-grid')
    if (main && front && open?.id !== folder.id) {
      const top = front.getBoundingClientRect().top - main.getBoundingClientRect().top
      if (top < 0 || top > main.clientHeight - 160) main.scrollTop += top - 96
    }
    const land = () => {
      const art = tileArt(folder.id)
      if (!art) return
      art.classList.add('exo-folder-landing')
      art.addEventListener('animationend', () => art.classList.remove('exo-folder-landing'), { once: true })
    }
    void transition(() => { onFolders(next); setSheet(null) }, open?.id === folder.id ? { kind: 'none' } : {
      kind: 'morph',
      morph: { name: 'exo-folder', from: visible('.exo-folder-sheet'), to: () => tileArt(folder.id) },
    }).then(() => { if (open?.id === folder.id) focusPanel(); else window.setTimeout(land, 260) })
  }, [folders, onFolders, open, mainRef])
  const cancelSheet = useCallback(() => { setSheet(null); if (open) focusPanel() }, [open])
  // Every card has a menu now: a game can always go into a folder.
  const openMenu = useCallback((at: GameMenuAt) => setMenu(at), [])
  // Retained rooms keep their grid, but menus must leave with their route. A game page over the
  // Library keeps them (the pane is inert under it), so the shelf's header never changes height.
  const tools = useMemo(() => active ? (
    <LibraryTools sort={sort} onSort={onSort} onActivateKey={onActivateKey} onNewFolder={() => newFolder()} onAddGame={onAddGame} onAddFolder={onAddFolder} refresh={refresh} onRefresh={runRefresh} />
  ) : null, [active, sort, onSort, onActivateKey, newFolder, onAddGame, onAddFolder, refresh, runRefresh])
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
              count={rest.length}
              actions={tools}
              games={grid}
              selectedId={props.selectedId}
              activeGameId={props.activeGameId}
              onActiveGameChange={props.onActiveGameChange}
              onSelect={select}
              onMenu={openMenu}
              transferFor={props.transferFor}
              queuedIds={props.queuedIds}
              scrollRootRef={props.mainRef}
              cardWidth={cardWidth}
            />
          </div>
        )}
      </main>
      {openTile && active ? (
        <FolderPanel
          key={`${openTile.folder.id}:${open?.rename ?? 0}`}
          folder={openTile.folder}
          renaming={!!open?.rename}
          inert={covered}
          cardWidth={cardWidth}
          selectedId={props.selectedId}
          queuedIds={props.queuedIds}
          transferFor={props.transferFor}
          onRename={name => onFolders(renameFolder(folders, openTile.folder.id, name))}
          onEdit={() => editFolder(openTile.folder.id)}
          onClose={closeFolder}
          onOpen={onOpen}
          onMenu={openMenu}
        />
      ) : null}
      {sheet && active ? (
        <FolderSheet key={sheet.id} folder={sheet} library={games} folders={folders} onSave={saveSheet} onCancel={cancelSheet} />
      ) : null}
      {menu ? (
        <GameMenu
          key={`${menu.game.id}:${menu.x}:${menu.y}`}
          at={menu}
          folders={folders}
          onFolders={onFolders}
          onNewFolder={newFolder}
          onEditFolder={editFolder}
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
