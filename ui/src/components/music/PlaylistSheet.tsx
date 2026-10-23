import { useEffect, useRef, useState } from 'react'
import { ImageUpload } from '../../brand/icons'
import { Art } from './Art'
import { runActionFor, usePlayer, type PlaylistEdit } from './playerContext'
import { useDismiss } from '../../lib/dismiss'

/** A playlist's privacy, where the service has one (YouTube Music). */
const PRIVACY = [['PUBLIC', 'Public'], ['UNLISTED', 'Unlisted'], ['PRIVATE', 'Private']] as const

/** A picture as Spotify takes a cover: square, at least 300 px, drawn at up to 640 px as a JPEG. */
async function coverFrom(file: File): Promise<string | 'small' | 'bad'> {
  let bitmap: ImageBitmap
  try { bitmap = await createImageBitmap(file) } catch { return 'bad' }
  const side = Math.min(bitmap.width, bitmap.height)
  if (side < 300) { bitmap.close(); return 'small' }
  const size = Math.min(640, side)
  const canvas = document.createElement('canvas')
  canvas.width = size
  canvas.height = size
  canvas.getContext('2d')?.drawImage(bitmap, (bitmap.width - side) / 2, (bitmap.height - side) / 2, side, side, 0, 0, size, size)
  bitmap.close()
  return canvas.toDataURL('image/jpeg', 0.9)
}

/**
 * Every service's playlist sheet, as their own Edit details and New playlist dialogs have it: the
 * cover where the service takes one (Spotify), the name and description, and the privacy where
 * the service has one (YouTube Music). A new playlist made from a song's menu starts with that song.
 * The sheet is a native modal dialog (Escape closes it); Delete is on the playlist's own page.
 */
export function PlaylistSheet({ edit, onClose }: { edit: PlaylistEdit; onClose: () => void }) {
  const { adapter, has } = usePlayer()
  const dialog = useRef<HTMLDialogElement>(null)
  const picker = useRef<HTMLInputElement>(null)
  const [name, setName] = useState(edit.title ?? '')
  const [description, setDescription] = useState(edit.description ?? '')
  const [privacy, setPrivacy] = useState(edit.privacy)
  const [cover, setCover] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const creating = !edit.uri
  useEffect(() => {
    const box = dialog.current
    if (box && !box.open) box.showModal()
  }, [])
  // A press on the dimmed backdrop (the dialog element, outside its box) closes it.
  useDismiss(dialog, () => dialog.current?.close(), { modal: true })

  async function pick(file: File | undefined) {
    if (!file) return
    setError(null)
    const image = await coverFrom(file)
    if (image === 'small') setError('Choose a picture at least 300 by 300 pixels.')
    else if (image === 'bad') setError('Choose a JPEG or PNG picture.')
    else setCover(image)
  }
  async function save() {
    if (!name.trim()) return
    setBusy(true)
    setError(null)
    const details = { name: name.trim(), description: description.trim(), privacy }
    const changed = creating || details.name !== edit.title || details.description !== (edit.description ?? '') || privacy !== edit.privacy
    const made = changed ? await runActionFor(adapter, creating ? 'createPlaylist' : 'editPlaylist',
      creating ? { ...details, uri: edit.track, context: edit.folder } : { ...details, uri: edit.uri }) : { ok: true, message: undefined, uri: edit.uri }
    const uri = creating ? made.uri : edit.uri
    const picture = made.ok && cover && uri ? await runActionFor(adapter, 'playlistImage', { uri, image: cover }) : { ok: true, message: undefined }
    setBusy(false)
    if (made.ok && picture.ok) dialog.current?.close()
    else setError(made.message ?? picture.message ?? `${adapter.name} did not take that. Try again.`)
  }

  return (
    <dialog ref={dialog} className="exo-music-sheet exo-glass is-panel" aria-label={creating ? 'New playlist' : 'Edit details'}
      onClose={onClose}>
      <form className="exo-music-sheet-body" onSubmit={event => { event.preventDefault(); void save() }}>
        <h2>{creating ? 'New playlist' : 'Edit details'}</h2>
        {has('cover') ? (
          <>
            <button type="button" className="exo-music-edit-cover" aria-label="Choose a cover" onClick={() => picker.current?.click()}>
              <Art src={cover ?? edit.image} size={176} />
              <span className="exo-music-edit-cover-hint"><ImageUpload size={20} /> Choose picture</span>
            </button>
            <input ref={picker} type="file" accept="image/jpeg,image/png" hidden onChange={event => { void pick(event.target.files?.[0]); event.target.value = '' }} />
          </>
        ) : null}
        <label className="exo-music-sheet-field">
          <span>Name</span>
          <input value={name} maxLength={100} required autoFocus onChange={event => setName(event.target.value)} />
        </label>
        <label className="exo-music-sheet-field">
          <span>Description</span>
          <textarea value={description} maxLength={300} rows={3} placeholder="Add an optional description" onChange={event => setDescription(event.target.value)} />
        </label>
        {privacy ? (
          <div className="exo-music-sheet-field" role="group" aria-label="Privacy">
            <span>Privacy</span>
            <div className="exo-shelf-chips">
              {PRIVACY.map(([id, label]) => (
                <button key={id} type="button" className="exo-shelf-chip" aria-pressed={privacy === id} onClick={() => setPrivacy(id)}>{label}</button>
              ))}
            </div>
          </div>
        ) : null}
        {error ? <p className="exo-music-error" role="alert">{error}</p> : null}
        <div className="exo-music-sheet-actions">
          <button type="button" className="button button--secondary" onClick={() => dialog.current?.close()}>Cancel</button>
          <button type="submit" className="button button--primary" disabled={busy || !name.trim()}>{busy ? 'Saving' : creating ? 'Create' : 'Save'}</button>
        </div>
      </form>
    </dialog>
  )
}
