import { useMemo, useRef, useState } from 'react'
import { CoverArt } from '../CoverArt'
import { CHECK_PATH, Search } from '../../brand/icons'
import { FOLDER_COLORS, FOLDER_COLOR_NAMES, folderColor, type FolderColor } from '../../lib/folders'
import type { Game, LibraryFolder } from '../../lib/host'

/**
 * Library > New folder (the + menu, or a game's menu with that game ticked) and a folder's Edit: its name, its color
 * and its games, ticked from the whole Library. A sheet of large glass over the dimmed Library; Create (or Save)
 * folds it into the folder's card at the front of the grid (LibraryPane's morph). Escape or Cancel leaves it.
 */
export function FolderSheet({ folder, library, folders, onSave, onCancel }: {
  /** The folder edited, or a new one (its id unused yet; `games` ticked to start with). */
  folder: LibraryFolder & { isNew: boolean }
  /** Every game the Library shows, in its order. */
  library: Game[]
  folders: LibraryFolder[]
  onSave: (folder: LibraryFolder) => void
  onCancel: () => void
}) {
  const [name, setName] = useState(folder.name)
  const [color, setColor] = useState<FolderColor>(folderColor(folder.color))
  const [picked, setPicked] = useState<string[]>(folder.games)
  const [query, setQuery] = useState('')
  const nameField = useRef<HTMLInputElement>(null)
  const elsewhere = useMemo(() => new Map(folders.filter(item => item.id !== folder.id).flatMap(item => item.games.map(id => [id, item.name] as const))), [folders, folder.id])
  const shown = useMemo(() => {
    const words = query.trim().toLowerCase()
    return words ? library.filter(game => game.title.toLowerCase().includes(words)) : library
  }, [library, query])
  const ready = name.trim().length > 0 && picked.length > 0
  const save = () => { if (ready) onSave({ id: folder.id, name: name.trim(), color, games: picked }) }
  const toggle = (id: string) => setPicked(current => current.includes(id) ? current.filter(item => item !== id) : [...current, id])

  return (
    <div
      className="exo-folder-sheet-back"
      onPointerDown={event => { if (event.target === event.currentTarget) onCancel() }}
      onKeyDown={event => {
        if (event.key !== 'Escape' || event.defaultPrevented) return
        event.preventDefault()
        event.stopPropagation()
        onCancel()
      }}
    >
      <div className="exo-folder-sheet" role="dialog" aria-modal="true" aria-labelledby="exo-folder-sheet-title" data-color={color}>
        <span className="exo-glass is-panel exo-sheet-glass" aria-hidden="true" />
        <h2 id="exo-folder-sheet-title">{folder.isNew ? 'New folder' : 'Edit folder'}</h2>
        <div className="exo-folder-sheet-top">
          <input
            ref={nameField}
            className="exo-field exo-folder-sheet-name"
            aria-label="Folder name"
            placeholder="Folder name"
            value={name}
            maxLength={40}
            autoFocus
            onFocus={event => event.currentTarget.select()}
            onChange={event => setName(event.currentTarget.value)}
            onKeyDown={event => { if (event.key === 'Enter') { event.preventDefault(); save() } }}
          />
          <div className="exo-folder-swatches" role="radiogroup" aria-label="Color">
            {FOLDER_COLORS.map(option => (
              <button
                key={option}
                type="button"
                role="radio"
                aria-checked={color === option}
                aria-label={FOLDER_COLOR_NAMES[option]}
                className="exo-folder-swatch"
                data-color={option}
                onClick={() => setColor(option)}
              />
            ))}
          </div>
        </div>
        <label className="exo-folder-sheet-search">
          <Search size={16} />
          <input className="exo-field" placeholder="Search your games" aria-label="Search your games" value={query} onChange={event => setQuery(event.currentTarget.value)} />
        </label>
        <div className="exo-folder-pick" role="listbox" aria-label="Games" aria-multiselectable="true">
          {shown.map(game => {
            const on = picked.includes(game.id)
            const from = elsewhere.get(game.id)
            return (
              <button
                key={game.id}
                type="button"
                role="option"
                aria-selected={on}
                className="exo-folder-pick-item"
                onClick={() => toggle(game.id)}
              >
                <span className="exo-folder-pick-art">
                  <CoverArt game={game} className="absolute inset-0 h-full w-full" />
                  {on && <svg className="exo-folder-pick-check" viewBox="0 0 24 24" aria-hidden="true"><path d={CHECK_PATH} /></svg>}
                </span>
                <span className="exo-folder-pick-title">{game.title}</span>
                {from && !on ? <span className="exo-folder-pick-from">In {from}</span> : null}
              </button>
            )
          })}
          {shown.length === 0 ? <p className="exo-folder-pick-none">No games match.</p> : null}
        </div>
        <footer className="exo-folder-sheet-foot">
          <span className="exo-folder-sheet-count">{picked.length === 1 ? '1 game' : `${picked.length} games`}</span>
          <button type="button" className="exo-ghost-btn" onClick={onCancel}>Cancel</button>
          <button type="button" className="exo-ghost-btn is-primary" disabled={!ready} onClick={save}>{folder.isNew ? 'Create' : 'Save'}</button>
        </footer>
      </div>
    </div>
  )
}
