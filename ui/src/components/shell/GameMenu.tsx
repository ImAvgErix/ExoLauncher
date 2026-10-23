import { useEffect, useLayoutEffect, useRef, useState, type KeyboardEvent, type ReactNode } from 'react'
import { host, shelfAction, type Game, type LibraryFolder } from '../../lib/host'
import { report } from '../../lib/report'
import { addToFolder, deleteFolder, isFolderTile, leaveFolder } from '../../lib/folders'
import { Download, Eye, EyeOff, Folder, FolderOpen, FolderPlus, Pencil, Play, Sliders, Trash } from '../../brand/icons'

/** The menu's fade as it closes (rooms.css › .exo-shelf-sort-menu, the quick time), and a frame to spare. */
const LEAVE_MS = 140

/** Where a card's menu opens: the pointer for a right-click, under the More button for a press. */
export type GameMenuAt = { game: Game; x: number; y: number; from: HTMLElement | null }

type Item = { id: string; label: string; icon: ReactNode; run: () => void; section?: string; on?: boolean }

/**
 * A Library card's menu (a right-click on any card, Continue's More): Play or Install, Play as
 * another saved Riot account for an installed Riot game, and Open folder and Properties for an installed game, then
 * Hide (Unhide on a hidden game). A native popover at the point it was asked for, kept inside the window
 * (lib/menuFit.ts); Escape, a click outside or a choice closes it. Then its Folders: every folder with a check on
 * those the game is in (a game may be in several), a choice putting it in or taking it out, and New folder; a
 * folder's card is edited or deleted.
 */
export function GameMenu({ at, folders: liveFolders, hidden, onFolders, onHide, onNewFolder, onEditFolder, onClose, onPlay, onPlayAs, onOpenFolder, onProperties }: {
  at: GameMenuAt
  folders: LibraryFolder[]
  /** The game is hidden: Unhide, and no folders (it shows in none until it's back). */
  hidden: boolean
  /** Hide or Unhide. */
  onHide: (game: Game) => void
  /** The folders after a change. */
  onFolders: (next: LibraryFolder[]) => void
  /** New folder, this game ticked in it (FolderSheet). */
  onNewFolder: (gameId: string) => void
  /** A folder's name, color and games (FolderSheet). */
  onEditFolder: (folderId: string) => void
  onClose: () => void
  onPlay: (game: Game) => void
  onPlayAs: (game: Game, account: { key: string; name: string }) => void
  onOpenFolder: (game: Game) => void
  onProperties: (game: Game) => void
}) {
  const ref = useRef<HTMLDivElement>(null)
  const { game } = at
  const shortcut = shelfAction(game)
  const riot = game.store === 'riot' && game.installed && shortcut?.action === 'play'
  const [others, setOthers] = useState<Array<{ key: string; name: string }>>([])
  useEffect(() => {
    if (!riot) return
    let live = true
    host.accountsList()
      .then(accounts => { if (live) setOthers((accounts.riot?.accounts ?? []).filter(account => account.saved && !account.current)) })
      .catch((error: unknown) => report(error, 'GameMenu.riotAccounts'))
    return () => { live = false }
  }, [riot])
  // The folders as they were when the menu opened: a choice changes them while the menu fades out, and its rows then
  // read the change ("Add to" turning into "Remove from" on the way out).
  const [folders] = useState(liveFolders)
  const tile = isFolderTile(game) ? game.folder : null
  const items: Item[] = tile ? [
    { id: 'edit', label: 'Edit folder', icon: <Pencil size={16} />, run: () => onEditFolder(tile.id) },
    { id: 'delete', label: 'Delete folder', icon: <Trash size={16} />, run: () => onFolders(deleteFolder(folders, tile.id)) },
  ] : [
    ...(shortcut ? [{ id: 'play', label: shortcut.label, icon: shortcut.action === 'install' ? <Download size={16} /> : <Play size={16} />, run: () => onPlay(game) }] : []),
    ...others.map(account => ({ id: `as:${account.key}`, label: `Play as ${account.name}`, icon: <Play size={16} />, run: () => onPlayAs(game, account) })),
    ...(game.installed ? [{ id: 'folder', label: 'Open folder', icon: <FolderOpen size={16} />, run: () => onOpenFolder(game) }] : []),
    ...(game.installed && !game.isAddPortable ? [{ id: 'properties', label: 'Properties', icon: <Sliders size={16} />, run: () => onProperties(game) }] : []),
    { id: 'hide', label: hidden ? 'Unhide' : 'Hide', icon: hidden ? <Eye size={16} /> : <EyeOff size={16} />, run: () => onHide(game) },
    ...(hidden ? [] : [
      ...folders.map(folder => {
        const on = folder.games.includes(game.id)
        return {
          id: `in:${folder.id}`, label: folder.name, section: 'Folders', on, icon: <Folder size={16} />,
          run: () => onFolders(on ? leaveFolder(folders, game.id, folder.id) : addToFolder(folders, game.id, folder.id)),
        }
      }),
      { id: 'new-folder', label: 'New folder', section: 'Folders', icon: <FolderPlus size={16} />, run: () => onNewFolder(game.id) },
    ]),
  ]

  useLayoutEffect(() => {
    const menu = ref.current
    if (!menu) return
    menu.showPopover()
    menu.style.left = `${at.x}px`
    menu.style.top = `${at.y}px`
    if (!menu.contains(document.activeElement)) menu.querySelector<HTMLElement>('[role^="menuitem"]')?.focus()
  }, [at, others.length])

  const close = (returnFocus: boolean) => {
    if (ref.current?.matches(':popover-open')) ref.current.hidePopover()
    if (returnFocus && at.from?.isConnected) at.from.focus({ preventScroll: true })
  }
  const choose = (run: () => void) => {
    close(true)
    run()
  }
  function onKeyDown(event: KeyboardEvent<HTMLDivElement>) {
    const list = Array.from(ref.current?.querySelectorAll<HTMLElement>('[role^="menuitem"]') ?? [])
    const index = list.indexOf(document.activeElement as HTMLElement)
    const to = { ArrowDown: index + 1, ArrowUp: index - 1, Home: 0, End: list.length - 1 }[event.key]
    if (to !== undefined) {
      event.preventDefault()
      list[(to + list.length) % list.length]?.focus()
    } else if (event.key === 'Escape') {
      event.preventDefault()
      event.stopPropagation()
      close(true)
    } else if (event.key === 'Tab') close(false)
  }

  return (
    <div
      ref={ref}
      popover="auto"
      role="menu"
      aria-label={game.title}
      className="exo-shelf-sort-menu exo-game-menu"
      onToggle={(event) => {
        if (event.newState !== 'closed') return
        // Kept until its fade is done (rooms.css, the quick time), unless it opened again meanwhile.
        window.setTimeout(() => { if (!ref.current?.matches(':popover-open')) onClose() }, LEAVE_MS)
      }}
      onKeyDown={onKeyDown}
    >
      {items.map((item, index) => [
        item.section && item.section !== items[index - 1]?.section
          ? <div key={'section:' + item.section} className="exo-menu-section" role="presentation">{item.section}</div>
          : null,
        <div
          key={item.id}
          role={item.on === undefined ? 'menuitem' : 'menuitemcheckbox'}
          aria-checked={item.on}
          tabIndex={-1}
          className="exo-menu-item"
          onClick={() => choose(item.run)}
          onKeyDown={(event) => {
            if (event.key !== 'Enter' && event.key !== ' ') return
            event.preventDefault()
            choose(item.run)
          }}
        >
          {item.icon}
          {item.label}
        </div>,
      ])}
    </div>
  )
}
