import { useEffect, useRef, useState, type CSSProperties } from 'react'
import { GameCard } from '../GameCard'
import { Menu } from '../ui/Menu'
import { ChevronDown, Pencil } from '../../brand/icons'
import { isInstallingGame, SORT_LABELS, SORT_OPTIONS } from '../../lib/library'
import type { FolderView } from '../../lib/folders'
import type { FolderSort, Game, InstallProgress } from '../../lib/host'
import type { GameMenuAt } from './GameMenu'
import { useDismiss } from '../../lib/dismiss'
import { useDialogFocus } from '../../lib/focusTrap'

/** The folder's own order first (the default), then the Library's sorts. */
const SORT_ITEMS = [{ id: 'folder', label: 'Folder order' }, ...SORT_OPTIONS.map(mode => ({ id: mode, label: SORT_LABELS[mode] }))]
const sortLabel = (sort: FolderSort) => SORT_ITEMS.find(item => item.id === sort)?.label ?? 'Folder order'

/**
 * An open Library folder: large glass over the dimmed Library, its name on top and its games as the grid's cards.
 * It grows out of the folder's card and back into it (LibraryPane's morph, `exo-folder`). The name is a field once
 * pressed (or asked for by the folder's Rename); Enter or leaving it saves, Escape keeps the old one. Beside it, the
 * folder's own sort (the Library's menu, Folder order first) and Edit. Escape or a press outside closes the folder.
 */
export function FolderPanel({ folder, renaming, inert, cardWidth, selectedId, queuedIds, transferFor, onRename, onSort, onEdit, onClose, onOpen, onMenu }: {
  folder: FolderView
  renaming: boolean
  /** A game page is over it: it stays put and takes no input. */
  inert: boolean
  cardWidth: number | null
  selectedId: string | null
  queuedIds: string[]
  transferFor: (game: Game) => InstallProgress | null
  onRename: (name: string) => void
  onSort: (sort: FolderSort) => void
  /** Its name, color and games (FolderSheet, over the panel). */
  onEdit: () => void
  onClose: () => void
  onOpen: (game: Game) => void
  onMenu: (at: GameMenuAt) => void
}) {
  const panel = useRef<HTMLDivElement>(null)
  useDismiss(panel, onClose, { modal: true, on: !inert })
  // Tab stays in the folder, and the folder's card has the focus again when it closes.
  useDialogFocus(panel)
  // Rename remounts the panel (LibraryPane's key), so it opens with the field.
  const [editing, setEditing] = useState(renaming)
  useEffect(() => {
    if (!editing) panel.current?.querySelector<HTMLElement>('button[data-game-id]')?.focus({ preventScroll: true })
  }, [editing])
  // A game taken out from its menu leaves with the focus the menu gave back to it: the panel takes it again, or
  // Escape would reach nothing and the folder could not be closed from the keyboard.
  const count = folder.games.length
  useEffect(() => {
    if (editing || inert || panel.current?.contains(document.activeElement)) return
    panel.current?.querySelector<HTMLElement>('button[data-game-id]')?.focus({ preventScroll: true })
  }, [count, editing, inert])

  return (
    <div
      className="exo-folder-back"
      inert={inert ? true : undefined}
      onKeyDown={event => {
        if (event.key !== 'Escape' || event.defaultPrevented) return
        event.preventDefault()
        onClose()
      }}
    >
      <div
        ref={panel}
        className="exo-folder-panel"
        data-color={folder.color}
        role="dialog"
        aria-modal="true"
        aria-label={folder.name}
        style={{ '--exo-folder-cols': Math.min(5, Math.max(3, folder.games.length)), ...(cardWidth == null ? {} : { '--exo-card-w': `${cardWidth}px` }) } as CSSProperties}
      >
        <span className="exo-glass is-panel exo-sheet-glass" aria-hidden="true" />
        <header className="exo-folder-head">
          {editing ? (
            <input
              className="exo-field exo-folder-name"
              aria-label="Folder name"
              defaultValue={folder.name}
              maxLength={40}
              autoFocus
              onFocus={event => event.currentTarget.select()}
              onBlur={event => { onRename(event.currentTarget.value); setEditing(false) }}
              onKeyDown={event => {
                // Held back, or the card focused next would take the Enter as a press.
                if (event.key === 'Enter') { event.preventDefault(); event.currentTarget.blur() }
                else if (event.key === 'Escape') {
                  event.preventDefault()
                  event.currentTarget.value = folder.name
                  event.currentTarget.blur()
                }
              }}
            />
          ) : (
            <button type="button" className="exo-folder-title" onClick={() => setEditing(true)}>{folder.name}</button>
          )}
          <div className="exo-folder-tools">
            <Menu
              label="Sort folder"
              anchor="--exo-folder-sort-menu"
              offset={4}
              triggerClassName="exo-shelf-sort-trigger"
              trigger={<>{sortLabel(folder.sort)}<ChevronDown size={12} /></>}
              items={SORT_ITEMS}
              checked={folder.sort}
              onAction={id => onSort(id as FolderSort)}
            />
            <button type="button" className="exo-folder-edit" aria-label="Edit folder" aria-haspopup="dialog" onClick={onEdit}><Pencil size={16} /></button>
          </div>
        </header>
        <div className="exo-folder-grid" role="list" aria-label={folder.name}>
          {folder.games.map((game, index) => (
            <GameCard
              key={game.id}
              game={game}
              selected={game.id === selectedId}
              transfer={transferFor(game)}
              queued={queuedIds.some(id => isInstallingGame(game, id))}
              preload
              position={index + 1}
              setSize={folder.games.length}
              onSelect={onOpen}
              onMenu={onMenu}
            />
          ))}
        </div>
      </div>
    </div>
  )
}
