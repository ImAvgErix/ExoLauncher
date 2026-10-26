import { useMemo, useRef, useState, type DragEvent } from 'react'
import { CoverArt } from '../CoverArt'
import { FolderCropper } from './FolderCropper'
import { CHECK_PATH, ImageUpload, Search } from '../../brand/icons'
import { FOLDER_COLORS, FOLDER_COLOR_NAMES, folderColor, type FolderColor } from '../../lib/folders'
import type { Game, LibraryFolder } from '../../lib/host'
import { useDismiss } from '../../lib/dismiss'
import { useDialogFocus } from '../../lib/focusTrap'
import { confirm } from '../ui/Dialog'
import { DictateButton } from '../ui/DictateButton'

/** What the folder's picture may be, as the host takes it (FolderArtStore). */
const PICTURE_TYPES = ['image/png', 'image/jpeg', 'image/webp']
const PICTURE_MAX_BYTES = 15 * 1024 * 1024

/**
 * Library > New folder (the + menu, or a game's menu with that game ticked) and a folder's Edit: its name, its color,
 * its picture and its games, ticked from the whole Library (a game may be in other folders too). A sheet of large
 * glass over the dimmed Library; Create (or Save) folds it into the folder's card at the front of the grid
 * (LibraryPane's morph). Escape or Cancel leaves it. Add picture (or a picture dropped on the sheet) opens the cropper
 * in the sheet's place; Remove picture goes back to the games' covers.
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
  const [art, setArt] = useState<string | null>(folder.art ?? null)
  const [cropping, setCropping] = useState<File | null>(null)
  const [pictureError, setPictureError] = useState<string | null>(null)
  const [dropping, setDropping] = useState(false)
  const [query, setQuery] = useState('')
  const nameField = useRef<HTMLInputElement>(null)
  const searchField = useRef<HTMLInputElement>(null)
  const picker = useRef<HTMLInputElement>(null)
  // Back from the cropper, the keyboard is on the picture's button rather than the name.
  const [cropped, setCropped] = useState(false)
  const sheet = useRef<HTMLDivElement>(null)
  const leave = useLeave(folder, { name, color, art, picked }, onCancel)
  useDismiss(sheet, () => void leave(), { modal: true })
  useDialogFocus(sheet)
  // The other folders each game is in, by name: shown under it, since ticking it here keeps it there too.
  const elsewhere = useMemo(() => {
    const names = new Map<string, string[]>()
    for (const item of folders) if (item.id !== folder.id) for (const id of item.games) names.set(id, [...names.get(id) ?? [], item.name])
    return names
  }, [folders, folder.id])
  const shown = useMemo(() => {
    const words = query.trim().toLowerCase()
    return words ? library.filter(game => game.title.toLowerCase().includes(words)) : library
  }, [library, query])
  const ready = name.trim().length > 0 && picked.length > 0
  const save = () => { if (ready) onSave({ id: folder.id, name: name.trim(), color, sort: folder.sort, art, games: picked }) }
  const pick = (file: File | undefined) => {
    if (!file) return
    const known = PICTURE_TYPES.includes(file.type) || (!file.type && /\.(png|jpe?g|webp)$/i.test(file.name))
    setPictureError(!known ? 'Choose a PNG, JPEG or WebP picture.' : file.size > PICTURE_MAX_BYTES ? 'Choose a picture under 15 MB.' : null)
    if (known && file.size <= PICTURE_MAX_BYTES) setCropping(file)
  }
  // A picture dragged in from File Explorer, anywhere on the sheet.
  const files = (event: DragEvent) => event.dataTransfer.types.includes('Files')
  const dragOver = (event: DragEvent) => {
    if (cropping || !files(event)) return
    event.preventDefault()
    event.dataTransfer.dropEffect = 'copy'
    setDropping(true)
  }
  const toggle = (id: string) => setPicked(current => current.includes(id) ? current.filter(item => item !== id) : [...current, id])
  // Every game not installed, in one press (a wishlist-like folder of what to download), and out again the same way.
  const notInstalled = useMemo(() => library.filter(game => !game.installed).map(game => game.id), [library])
  const allNotInstalled = notInstalled.length > 0 && notInstalled.every(id => picked.includes(id))
  const toggleNotInstalled = () => setPicked(current => allNotInstalled
    ? current.filter(id => !notInstalled.includes(id))
    : [...current, ...notInstalled.filter(id => !current.includes(id))])

  return (
    <div
      className="exo-folder-sheet-back"
      onKeyDown={event => {
        if (event.key !== 'Escape' || event.defaultPrevented) return
        event.preventDefault()
        event.stopPropagation()
        void leave()
      }}
    >
      <div
        ref={sheet}
        className={cropping ? 'exo-folder-sheet is-cropping' : 'exo-folder-sheet'}
        role="dialog"
        aria-modal="true"
        aria-labelledby="exo-folder-sheet-title"
        data-color={color}
        data-dropping={dropping ? '' : undefined}
        onDragOver={dragOver}
        onDragLeave={event => { if (!event.currentTarget.contains(event.relatedTarget as Node | null)) setDropping(false) }}
        onDrop={event => {
          if (cropping || !files(event)) return
          event.preventDefault()
          setDropping(false)
          pick(event.dataTransfer.files[0])
        }}
      >
        <span className="exo-glass is-panel exo-sheet-glass" aria-hidden="true" />
        {cropping ? (
          <FolderCropper
            file={cropping}
            folderId={folder.id}
            name={name}
            count={picked.length}
            onDone={next => { setArt(next); setCropping(null); setCropped(true) }}
            onCancel={() => { setCropping(null); setCropped(true) }}
          />
        ) : (
          <>
            <h2 id="exo-folder-sheet-title">{folder.isNew ? 'New folder' : 'Edit folder'}</h2>
            <div className="exo-folder-sheet-top">
              <input
                ref={nameField}
                className="exo-field exo-folder-sheet-name"
                aria-label="Folder name"
                placeholder="Folder name"
                value={name}
                maxLength={40}
                autoFocus={!cropped}
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
            <div className="exo-folder-sheet-picture">
              {art ? <img className="exo-folder-sheet-thumb" src={art} alt="" draggable={false} /> : null}
              <button type="button" className="exo-set-btn" autoFocus={cropped} onClick={() => picker.current?.click()}>
                <ImageUpload size={16} />
                {art ? 'Change picture' : 'Add picture'}
              </button>
              {art ? <button type="button" className="exo-set-btn" onClick={() => setArt(null)}>Remove picture</button> : null}
              <span className={pictureError ? 'exo-folder-sheet-error' : 'exo-folder-sheet-count'} role={pictureError ? 'alert' : undefined}>
                {pictureError ?? (dropping ? 'Drop the picture to crop it.' : 'PNG, JPEG or WebP, or drop one here.')}
              </span>
              <input ref={picker} type="file" accept={PICTURE_TYPES.join(',')} hidden
                onChange={event => { pick(event.currentTarget.files?.[0]); event.currentTarget.value = '' }} />
            </div>
            <div className="exo-folder-sheet-tools">
              <label className="exo-folder-sheet-search">
                <Search size={16} />
                <input ref={searchField} className="exo-field" placeholder="Search your games" aria-label="Search your games" value={query} onChange={event => setQuery(event.currentTarget.value)} />
                <DictateButton target={searchField} place="overlay" />
              </label>
              {notInstalled.length > 0 ? (
                <button type="button" className="exo-set-btn" aria-pressed={allNotInstalled} onClick={toggleNotInstalled}>
                  {allNotInstalled ? 'Remove not installed' : `Add all not installed (${notInstalled.length})`}
                </button>
              ) : null}
            </div>
            <div className="exo-folder-pick" role="listbox" aria-label="Games" aria-multiselectable="true">
              {shown.map(game => {
                const on = picked.includes(game.id)
                const inside = elsewhere.get(game.id)
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
                    {inside ? <span className="exo-folder-pick-from">{inside.length === 1 ? `In ${inside[0]}` : `In ${inside.length} folders`}</span> : null}
                  </button>
                )
              })}
              {shown.length === 0 ? <p className="exo-folder-pick-none">No games match.</p> : null}
            </div>
            <footer className="exo-folder-sheet-foot">
              <span className="exo-folder-sheet-count" data-picked="">{picked.length === 1 ? '1 game' : `${picked.length} games`}</span>
              <button type="button" className="exo-set-btn" onClick={() => void leave()}>Cancel</button>
              <button type="button" className="exo-set-btn is-primary" disabled={!ready} onClick={save}>{folder.isNew ? 'Create' : 'Save'}</button>
            </footer>
          </>
        )}
      </div>
    </div>
  )
}

/** Closing with changes made asks first (DESIGN.md › Confirm); closing as it opened just closes. */
function useLeave(folder: LibraryFolder & { isNew: boolean }, now: { name: string; color: FolderColor; art: string | null; picked: string[] }, onCancel: () => void) {
  const asking = useRef(false)
  const changed = now.name !== folder.name || now.color !== folderColor(folder.color) || now.art !== (folder.art ?? null) ||
    now.picked.length !== folder.games.length || now.picked.some(id => !folder.games.includes(id))
  return async () => {
    if (!changed) { onCancel(); return }
    if (asking.current) return
    asking.current = true
    const go = await confirm({
      title: folder.isNew ? 'Discard this folder?' : 'Discard your changes?',
      body: folder.isNew ? "It isn't made until you press Create." : `${folder.name} stays as it was.`,
      action: 'Discard', danger: true, cancel: 'Keep editing',
    })
    asking.current = false
    if (go === true) onCancel()
  }
}
