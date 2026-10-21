import { useEffect, useRef, useState, type CSSProperties } from 'react'
import { GameCard } from '../GameCard'
import { Pencil } from '../../brand/icons'
import { isInstallingGame } from '../../lib/library'
import type { FolderView } from '../../lib/folders'
import type { Game, InstallProgress } from '../../lib/host'
import type { GameMenuAt } from './GameMenu'

/**
 * An open Library folder: large glass over the dimmed Library, its name on top and its games as the grid's cards.
 * It grows out of the folder's card and back into it (LibraryPane's morph, `exo-folder`). The name is a field once
 * pressed (or asked for by the folder's Rename); Enter or leaving it saves, Escape keeps the old one. Escape or a
 * press outside closes the folder.
 */
export function FolderPanel({ folder, renaming, inert, cardWidth, selectedId, queuedIds, transferFor, onRename, onEdit, onClose, onOpen, onMenu }: {
  folder: FolderView
  renaming: boolean
  /** A game page is over it: it stays put and takes no input. */
  inert: boolean
  cardWidth: number | null
  selectedId: string | null
  queuedIds: string[]
  transferFor: (game: Game) => InstallProgress | null
  onRename: (name: string) => void
  /** Its name, color and games (FolderSheet, over the panel). */
  onEdit: () => void
  onClose: () => void
  onOpen: (game: Game) => void
  onMenu: (at: GameMenuAt) => void
}) {
  const panel = useRef<HTMLDivElement>(null)
  // Rename remounts the panel (LibraryPane's key), so it opens with the field.
  const [editing, setEditing] = useState(renaming)
  useEffect(() => {
    if (!editing) panel.current?.querySelector<HTMLElement>('button[data-game-id]')?.focus({ preventScroll: true })
  }, [editing])

  return (
    <div
      className="exo-folder-back"
      inert={inert ? true : undefined}
      onPointerDown={event => { if (event.target === event.currentTarget) onClose() }}
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
          <button type="button" className="exo-folder-edit" aria-label="Edit folder" aria-haspopup="dialog" onClick={onEdit}><Pencil size={16} /></button>
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
