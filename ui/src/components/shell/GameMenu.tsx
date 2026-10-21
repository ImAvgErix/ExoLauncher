import { useEffect, useLayoutEffect, useRef, useState, type KeyboardEvent } from 'react'
import { host, shelfAction, type Game } from '../../lib/host'
import { report } from '../../lib/report'
import { Download, FolderOpen, Play, Sliders } from '../../brand/icons'

/** The menu's fade as it closes (rooms.css › .exo-shelf-sort-menu, the quick time), and a frame to spare. */
const LEAVE_MS = 140

/** Where a card's menu opens: the pointer for a right-click, under the More button for a press. */
export type GameMenuAt = { game: Game; x: number; y: number; from: HTMLElement | null }

/**
 * A Library card's menu (a right-click on any card, Continue's More): Play or Install, Play as
 * another saved Riot account for an installed Riot game, and Open folder and Properties for an installed game. A native popover at the point it was asked for, kept inside the
 * window (lib/menuFit.ts); Escape, a click outside or a choice closes it.
 */
export function GameMenu({ at, onClose, onPlay, onPlayAs, onOpenFolder, onProperties }: {
  at: GameMenuAt
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
  const items = [
    ...(shortcut ? [{ id: 'play', label: shortcut.label, icon: shortcut.action === 'install' ? <Download size={16} /> : <Play size={16} />, run: () => onPlay(game) }] : []),
    ...others.map(account => ({ id: `as:${account.key}`, label: `Play as ${account.name}`, icon: <Play size={16} />, run: () => onPlayAs(game, account) })),
    ...(game.installed ? [{ id: 'folder', label: 'Open folder', icon: <FolderOpen size={16} />, run: () => onOpenFolder(game) }] : []),
    ...(game.installed && !game.isAddPortable ? [{ id: 'properties', label: 'Properties', icon: <Sliders size={16} />, run: () => onProperties(game) }] : []),
  ]

  useLayoutEffect(() => {
    const menu = ref.current
    if (!menu) return
    menu.showPopover()
    menu.style.left = `${at.x}px`
    menu.style.top = `${at.y}px`
    if (!menu.contains(document.activeElement)) menu.querySelector<HTMLElement>('[role="menuitem"]')?.focus()
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
    const list = Array.from(ref.current?.querySelectorAll<HTMLElement>('[role="menuitem"]') ?? [])
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
      {items.map(item => (
        <div
          key={item.id}
          role="menuitem"
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
        </div>
      ))}
    </div>
  )
}
