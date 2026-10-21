import { useEffect, useRef, useState } from 'react'
import { report } from '../../lib/report'
import { loadView, runAction, runActionFor, type Page, type Player } from './playerContext'

/**
 * Your library and playlists, the way each service's own app manages them: create, rename, fold,
 * pin, links and radio. Every change goes through the hidden service's own APIs (Helpers/*Native.cs);
 * nothing here talks to a service itself.
 */

/** A song's or page's share link: the service's own (Apple Music sends it), else made from its id. */
export function shareLink(adapter: Player['adapter'], item: { uri: string; url?: string }) {
  return item.url ?? adapter.shareUrl?.(item.uri) ?? null
}

/** A share link on the clipboard; false when Windows refused it. */
export async function copyLink(url: string): Promise<boolean> {
  try {
    await navigator.clipboard.writeText(url)
    return true
  } catch (error) {
    report(error, 'MusicPlayer.copyLink')
    return false
  }
}

/** A word that answers an action for a moment ("Copied", "Didn't work"), then goes. */
export function useFlash(ms = 2400) {
  const [word, setWord] = useState<{ text: string; failed: boolean } | null>(null)
  useEffect(() => {
    if (!word) return
    const timer = window.setTimeout(() => setWord(null), ms)
    return () => window.clearTimeout(timer)
  }, [word, ms])
  return [word, (text: string, failed = false) => setWord({ text, failed })] as const
}

/** Song or artist radio: Spotify makes the playlist, the room opens it. */
export async function openRadio(adapter: Player['adapter'], open: (page: Page) => void, uri: string, title: string) {
  const result = await loadView(adapter, 'radio', { uri })
  if (!result.data?.uri) return false
  open({ view: 'playlist', uri: result.data.uri, title })
  return true
}

/** A name typed in place (a new folder's, a renamed one's): Enter keeps it, Escape or leaving drops it. */
export function NameField({ value, label, onDone }: { value: string; label: string; onDone: (name: string | null) => void }) {
  const [name, setName] = useState(value)
  const field = useRef<HTMLInputElement>(null)
  useEffect(() => { field.current?.select() }, [])
  return (
    <form className="exo-music-name" onSubmit={event => { event.preventDefault(); onDone(name.trim() || null) }}>
      <input ref={field} value={name} maxLength={100} aria-label={label} autoFocus
        onChange={event => setName(event.target.value)} onBlur={() => onDone(name.trim() && name.trim() !== value ? name.trim() : null)}
        onKeyDown={event => { if (event.key === 'Escape') { event.preventDefault(); onDone(null) } }} />
    </form>
  )
}

/** A delete asks once, in place, under what it deletes. */
export function Confirm({ text, action, onConfirm, onCancel }: { text: string; action: string; onConfirm: () => void; onCancel: () => void }) {
  return (
    <div className="exo-music-confirm" role="alertdialog" aria-label={text}>
      <span>{text}</span>
      <button type="button" className="exo-set-btn is-danger" onClick={onConfirm} autoFocus>{action}</button>
      <button type="button" className="exo-set-btn" onClick={onCancel}>Cancel</button>
    </div>
  )
}

/**
 * A new playlist, in the open folder or with a song in it. A service that names a new one itself
 * (Spotify's "My Playlist #n") makes it at once and opens it with its details to fill in (from a
 * song's menu the song stays where it is, as in Spotify); the others ask its name first, in the playlist sheet.
 */
export async function newPlaylist({ adapter, open, editPlaylist }: Player, count: number, folder?: string, song?: string) {
  if (!adapter.newPlaylistName) { editPlaylist({ folder, track: song }); return true }
  const name = adapter.newPlaylistName(count)
  const result = await runActionFor(adapter, 'createPlaylist', { name, context: folder, uri: song })
  if (result.ok && result.uri && !song) open({ view: 'playlist', uri: result.uri, title: name, edit: true })
  return result.ok
}

export const createFolder = (adapter: Player['adapter'], name: string, folder?: string) =>
  runAction(adapter, 'createFolder', { name, context: folder })
