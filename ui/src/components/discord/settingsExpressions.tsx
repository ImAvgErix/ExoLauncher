import { useState } from 'react'
import { Close } from '../../brand/icons'
import { avatarUrl, type DiscordUser } from '../../lib/discord'
import { admin, Confirm, dropUpload, personName, pickFile, previewOf, ReadError, useAdmin } from './admin'
import { uploadFile } from './Chat'
import { Icons } from './icons'
import { Avatar } from './presence'
import { Skeleton } from '../ui/Skeleton'
import { ErrorNote } from '../ui/ErrorNote'

type Emoji = { id: string; name: string; animated?: boolean; roles?: string[]; user?: DiscordUser | null }
type Sticker = { id: string; name: string; tags?: string; description?: string | null; format_type: number; user?: DiscordUser | null }

// Discord's slots per boost level.
const EMOJI_SLOTS = [50, 100, 150, 250]
const STICKER_SLOTS = [5, 15, 30, 60]

// Emoji names: letters, numbers and underscores, at least two (Discord's rule), from a file name when uploading.
const emojiName = (text: string) => text.replace(/\.[^.]+$/, '').replace(/[^\w]/g, '_').slice(0, 32).padEnd(2, '_')

async function upload(file: File, limit: number, what: string): Promise<{ id?: string; error?: string }> {
  if (file.size > limit) return { error: `${what} must be under ${Math.round(limit / 1024)} KB.` }
  const uploaded = await uploadFile(file)
  return typeof uploaded === 'string' ? { id: uploaded } : { error: uploaded.message }
}

/** Discord's Emoji: upload, rename in place, delete; who uploaded each, and the slots left. */
export function Emoji({ guildId, tier }: { guildId: string; tier: number }) {
  const emojis = useAdmin<Emoji[]>('emojis', { guildId })
  const [error, setError] = useState<string | null>(null)
  // A rename or delete Discord refused, said under that emoji.
  const [failed, setFailed] = useState<{ id: string; message: string } | null>(null)
  const [busy, setBusy] = useState(false)
  const list = emojis.data ?? []
  const slots = EMOJI_SLOTS[Math.min(3, tier)]
  // A slot is free for a still or an animated emoji (Discord counts them apart); a file picks which.
  const full = list.filter(e => !e.animated).length >= slots && list.filter(e => e.animated).length >= slots
  async function add() {
    const file = await pickFile('image/png,image/jpeg,image/gif,image/webp')
    if (!file) return
    setBusy(true); setError(null)
    const sent = await upload(file, 256 * 1024, 'Emoji')
    const result = sent.id ? await admin<Emoji>('uploadEmoji', { guildId, name: emojiName(file.name), uploadId: sent.id }) : null
    setBusy(false)
    if (!result?.ok) { dropUpload(sent.id); setError(sent.error ?? result?.message ?? 'Discord did not take that emoji.'); return }
    emojis.reload()
  }
  const rename = (emoji: Emoji, name: string) => {
    if (name === emoji.name) return
    setFailed(null)
    void admin('renameEmoji', { guildId, emojiId: emoji.id, name, roles: emoji.roles ?? [] }).then(result => {
      if (result.ok) emojis.set(all => all && all.map(e => e.id === emoji.id ? { ...e, name } : e)); else setFailed({ id: emoji.id, message: result.message ?? 'Discord did not rename that.' })
    })
  }
  const remove = (emoji: Emoji) => { setFailed(null); void admin('deleteEmoji', { guildId, emojiId: emoji.id }).then(result => {
    if (result.ok) emojis.set(all => all && all.filter(e => e.id !== emoji.id)); else setFailed({ id: emoji.id, message: result.message ?? 'Discord did not delete that.' })
  }) }
  return (
    <>
      <h1>Emoji</h1>
      <p className="exo-dc-note">Add up to {slots} custom emoji that anyone can use in this server. Animated GIF emoji may be used by members with Discord Nitro.
        Names need at least 2 letters, numbers or underscores; files must be under 256 KB.</p>
      <div className="exo-dc-page-bar">
        <button type="button" className="exo-set-btn is-primary" disabled={busy || full} onClick={() => void add()}>Upload emoji</button>
        <span className="exo-dc-mod-meta">{list.filter(e => !e.animated).length} of {slots} emoji, {list.filter(e => e.animated).length} of {slots} animated</span>
      </div>
      {error ? <ErrorNote className="exo-dc-note is-bad" message={error} /> : null}
      <ReadError error={emojis.error} onRetry={emojis.reload} />
      {!emojis.data && !emojis.error ? <Skeleton label="Loading emoji" avatar /> : null}
      {emojis.data && list.length === 0 ? <p className="exo-dc-note">No emoji yet.</p> : null}
      <ul className="exo-dc-mod-list">
        {list.map(emoji => (
          <li key={emoji.id} className={failed?.id === emoji.id ? 'has-error' : undefined}>
            <img className="exo-dc-emoji-cell" src={`https://cdn.discordapp.com/emojis/${emoji.id}.${emoji.animated ? 'gif' : 'webp'}?size=96&quality=lossless`} alt="" draggable={false} />
            <input className="exo-dc-inline-input" defaultValue={emoji.name} aria-label={`Rename ${emoji.name}`} maxLength={32}
              onBlur={event => rename(emoji, event.target.value.trim())} onKeyDown={event => { if (event.key === 'Enter') event.currentTarget.blur() }} />
            <Uploader user={emoji.user} />
            <button type="button" className="exo-dc-icon-btn is-danger" aria-label={`Delete ${emoji.name}`} onClick={() => remove(emoji)}><Close size={14} /></button>
            {failed?.id === emoji.id ? <ErrorNote as="span" className="exo-dc-field-error exo-dc-row-error" message={failed.message} /> : null}
          </li>
        ))}
      </ul>
    </>
  )
}

function Uploader({ user }: { user?: DiscordUser | null }) {
  if (!user) return <span className="exo-dc-mod-meta" />
  return (
    <span className="exo-dc-mod-meta exo-dc-uploader" data-dc-user={user.id}>
      <Avatar src={avatarUrl(user, 24)} name={user.username} status={null} size={20} />{personName(user)}
    </span>
  )
}

const stickerUrl = (sticker: Sticker) => `https://media.discordapp.net/stickers/${sticker.id}.${sticker.format_type === 4 ? 'gif' : 'png'}?size=320`

/** Discord's Stickers: upload (name, related emoji, description, file), edit, delete, and the slots left. */
export function Stickers({ guildId, tier }: { guildId: string; tier: number }) {
  const stickers = useAdmin<Sticker[]>('stickers', { guildId })
  const [editing, setEditing] = useState<Sticker | 'new' | null>(null)
  const [deleting, setDeleting] = useState<Sticker | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const list = stickers.data ?? []
  const slots = STICKER_SLOTS[Math.min(3, tier)]
  const remove = (sticker: Sticker) => {
    setBusy(true)
    void admin('deleteSticker', { guildId, stickerId: sticker.id }).then(result => {
      setBusy(false)
      if (!result.ok) { setError(result.message ?? 'Discord did not delete that.'); return }
      setDeleting(null); stickers.set(all => all && all.filter(s => s.id !== sticker.id))
    })
  }
  return (
    <>
      <h1>Stickers</h1>
      <p className="exo-dc-note">Upload stickers for everyone in this server: PNG, APNG or GIF, 320x320, under 512 KB.</p>
      <div className="exo-dc-page-bar">
        <button type="button" className="exo-set-btn is-primary" disabled={list.length >= slots} onClick={() => setEditing('new')}>Upload sticker</button>
        <span className="exo-dc-mod-meta">{list.length} of {slots} slots</span>
      </div>
      <ReadError error={stickers.error} onRetry={stickers.reload} />
      {!stickers.data && !stickers.error ? <Skeleton label="Loading stickers" tiles={8} /> : null}
      {stickers.data && list.length === 0 ? <p className="exo-dc-note">No stickers yet.</p> : null}
      <div className="exo-dc-sticker-grid is-settings">
        {list.map(sticker => (
          <article key={sticker.id} className="exo-dc-sticker-tile">
            <img src={stickerUrl(sticker)} alt="" loading="lazy" draggable={false} />
            <span>{sticker.name}</span>
            <span className="exo-dc-tile-tools">
              <button type="button" className="exo-dc-icon-btn" aria-label={`Edit ${sticker.name}`} onClick={() => setEditing(sticker)}><Icons.Edit size={14} /></button>
              <button type="button" className="exo-dc-icon-btn is-danger" aria-label={`Delete ${sticker.name}`} onClick={() => setDeleting(sticker)}><Icons.Delete size={14} /></button>
            </span>
          </article>
        ))}
      </div>
      {editing ? <StickerDialog guildId={guildId} sticker={editing === 'new' ? null : editing} onClose={() => setEditing(null)} onSaved={() => { setEditing(null); stickers.reload() }} /> : null}
      {deleting ? (
        <Confirm title={`Delete ${deleting.name}?`} action="Delete" error={error} busy={busy} onConfirm={() => remove(deleting)} onClose={() => { setDeleting(null); setError(null) }}>
          <p>It can't be brought back.</p>
        </Confirm>
      ) : null}
    </>
  )
}

function StickerDialog({ guildId, sticker, onClose, onSaved }: { guildId: string; sticker: Sticker | null; onClose: () => void; onSaved: () => void }) {
  const [name, setName] = useState(sticker?.name ?? '')
  const [tags, setTags] = useState(sticker?.tags ?? '')
  const [description, setDescription] = useState(sticker?.description ?? '')
  const [file, setFile] = useState<{ file: File; preview: string } | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  async function choose() {
    const picked = await pickFile('image/png,image/apng,image/gif')
    if (picked) { setFile({ file: picked, preview: await previewOf(picked) }); if (!name) setName(picked.name.replace(/\.[^.]+$/, '').slice(0, 30)) }
  }
  async function save() {
    setBusy(true); setError(null)
    const fields = { guildId, name: name.trim(), tags: tags.trim(), description: description.trim() }
    let result
    if (sticker) result = await admin('editSticker', { ...fields, stickerId: sticker.id })
    else {
      const sent = file ? await upload(file.file, 512 * 1024, 'Stickers') : { error: 'Pick a file first.' }
      result = sent.id ? await admin('uploadSticker', { ...fields, uploadId: sent.id }) : { ok: false, message: sent.error }
      if (!result.ok) dropUpload(sent.id)
    }
    setBusy(false)
    if (result.ok) onSaved(); else setError(result.message ?? 'Discord did not take that sticker.')
  }
  const ready = name.trim().length >= 2 && tags.trim().length > 0 && (sticker !== null || file !== null)
  return (
    <Confirm title={sticker ? 'Edit sticker' : 'Upload sticker'} action={sticker ? 'Save changes' : 'Upload'} danger={false} form busy={busy} disabled={!ready} error={error}
      onConfirm={() => void save()} onClose={onClose}>
      <div className="exo-dc-sticker-preview">
        {file ? <img src={file.preview} alt="" draggable={false} /> : sticker ? <img src={stickerUrl(sticker)} alt="" draggable={false} /> : null}
        {sticker ? null : <button type="button" className="exo-dc-tab is-raised" onClick={() => void choose()}>{file ? 'Change file' : 'Browse'}</button>}
      </div>
      <label className="exo-dc-side-label" htmlFor="exo-dc-sticker-name">Sticker name</label>
      <input id="exo-dc-sticker-name" className="exo-dc-modal-input" value={name} maxLength={30} onChange={event => setName(event.target.value)} />
      <label className="exo-dc-side-label" htmlFor="exo-dc-sticker-tags">Related emoji</label>
      <input id="exo-dc-sticker-tags" className="exo-dc-modal-input" value={tags} maxLength={200} placeholder="An emoji name, like smile" onChange={event => setTags(event.target.value)} />
      <label className="exo-dc-side-label" htmlFor="exo-dc-sticker-description">Description</label>
      <input id="exo-dc-sticker-description" className="exo-dc-modal-input" value={description} maxLength={100} onChange={event => setDescription(event.target.value)} />
    </Confirm>
  )
}
