import { useCallback, useEffect, useRef, useState } from 'react'
import { host } from '../../lib/host'
import { useDismiss } from '../../lib/dismiss'
import { useLayer } from '../../lib/layer'
import { useAccountSettings } from '../../lib/discordPrefs'
import type { DiscordForumTag, DiscordThreadState } from '../../lib/discord'
import { Confirm } from './admin'
import { LEVELS, MUTE_FOR } from './ChatHeader'
import { Icons } from './icons'
import { emojiUrl } from './markdown'
import { SubContext, Submenu } from './UserMenu'
import { ErrorNote } from '../ui/ErrorNote'

type At = { threadId: string; x: number; y: number }

/** Opens a thread's or forum post's menu at a point: its row in the sidebar, the Threads list, a forum's post. */
export function openThreadMenu(threadId: string, x: number, y: number) {
  window.dispatchEvent(new CustomEvent<At>('exo-dc-thread-menu', { detail: { threadId, x, y } }))
}

/** A forum tag as Discord draws it: its emoji (a server's or a Unicode one), then its name. */
export function TagLabel({ tag }: { tag: DiscordForumTag }) {
  return (
    <>
      {tag.emojiId ? <img src={emojiUrl(tag.emojiId, false, 32)} alt="" draggable={false} /> : tag.emojiName ? <span aria-hidden="true">{tag.emojiName}</span> : null}
      {tag.name}
    </>
  )
}

/** One thread menu at a time, wherever it was opened. */
export function ThreadMenuHost() {
  const [at, setAt] = useState<At | null>(null)
  useEffect(() => {
    const open = (event: Event) => setAt((event as CustomEvent<At>).detail)
    window.addEventListener('exo-dc-thread-menu', open)
    return () => window.removeEventListener('exo-dc-thread-menu', open)
  }, [])
  const close = useCallback(() => setAt(null), [])
  return at ? <ThreadMenu key={`${at.threadId}:${at.x}:${at.y}`} at={at} onClose={close} /> : null
}

type Action = Parameters<typeof host.discordThreadAction>[1]
type Params = Parameters<typeof host.discordThreadAction>[2]

/**
 * Discord's thread and forum post menu, read from Exo's copy of the thread as it opens: Mark As Read, Mute Thread for a
 * while (or Unmute), Notification Settings, Join or Leave (Follow or Unfollow a post), then for its owner or a moderator
 * Edit Tags (a forum's), Pin Post, Close and Lock, then Copy Link, Delete (Manage Threads) and, in Developer Mode, Copy
 * Thread ID.
 */
function ThreadMenu({ at, onClose }: { at: At; onClose: () => void }) {
  const [thread, setThread] = useState<DiscordThreadState | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [sub, setSub] = useState<string | null>(null)
  const [deleting, setDeleting] = useState(false)
  const developer = useAccountSettings().developerMode
  const box = useRef<HTMLDivElement>(null)
  useLayer(box, () => { if (sub) setSub(null); else onClose() }, !deleting)
  useDismiss(box, onClose, { on: !deleting })
  useEffect(() => {
    let live = true
    host.discordThreadAction(at.threadId, 'thread').then(result => {
      if (!live) return
      if (result.thread) setThread(result.thread); else setError("Exo doesn't know this thread yet. Open it first.")
    }, () => { if (live) setError("Exo doesn't know this thread yet. Open it first.") })
    return () => { live = false }
  }, [at.threadId])
  const why = "Discord didn't take that."
  const run = (action: Action, params?: Params, after?: () => void) => void host.discordThreadAction(at.threadId, action, params).then(
    result => { if (!result.ok) setError(result.message ?? why); else if (after) after(); else onClose() }, () => setError(why))
  const copy = (text: string) => { void navigator.clipboard.writeText(text).catch(() => { }); onClose() }
  const post = thread?.forum ? 'post' : 'thread'
  if (deleting && thread) {
    return (
      <Confirm title={`Delete ${post}?`} action={`Delete ${post}`} error={error} onClose={onClose} onConfirm={() => run('delete')}>
        <p>Are you sure you want to delete <strong>{thread.name}</strong>? This can't be undone.</p>
      </Confirm>
    )
  }
  return (
    <SubContext.Provider value={{ open: sub, show: setSub }}>
    <div ref={box} className="exo-dc-menu exo-dc-ctx exo-dc-channel-menu" role="menu" aria-label={thread?.name ?? 'Thread'} style={{ left: at.x, top: at.y }}>
      {thread ? <ThreadRows thread={thread} run={run} onChange={setThread} onCopy={copy} onDelete={() => setDeleting(true)} onClose={onClose} developer={developer} /> : null}
      {error ? <ErrorNote className="exo-dc-note is-bad" message={error} /> : null}
    </div>
    </SubContext.Provider>
  )
}

function ThreadRows({ thread, run, onChange, onCopy, onDelete, onClose, developer }: {
  thread: DiscordThreadState
  run: (action: Action, params?: Params, after?: () => void) => void
  onChange: (thread: DiscordThreadState) => void
  onCopy: (text: string) => void
  onDelete: () => void
  onClose: () => void
  developer: boolean
}) {
  const post = thread.forum ? 'post' : 'thread'
  return (
    <>
      <button type="button" role="menuitem" disabled={!thread.unread} onClick={() => { onClose(); void host.discordChannel(thread.id, 'read').catch(() => { }) }}>Mark as read</button>
      <span className="exo-dc-menu-line" role="separator" />
      {thread.muted ? (
        <button type="button" role="menuitem" onClick={() => run('threadMute', { muted: false })}>
          <span className="exo-dc-menu-text">Unmute {post}{thread.mutedUntil ? <small>Muted until {new Date(thread.mutedUntil).toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' })}</small> : null}</span>
        </button>
      ) : (
        <Submenu label={`Mute ${post}`}>
          {MUTE_FOR.map(([minutes, label]) => <button key={minutes} type="button" role="menuitem" onClick={() => run('threadMute', minutes ? { muted: true, minutes } : { muted: true })}>{label}</button>)}
        </Submenu>
      )}
      <Submenu label="Notification settings">
        {LEVELS.map(([value, label]) => (
          <button key={value} type="button" role="menuitemradio" aria-checked={thread.notify === value} onClick={() => run('threadNotify', { level: value })}>
            <span className="exo-dc-ctx-name">{label}</span>
            {thread.notify === value ? <Icons.Check size={14} className="exo-dc-ctx-check" /> : null}
          </button>
        ))}
      </Submenu>
      <span className="exo-dc-menu-line" role="separator" />
      {/* Discord follows a forum post and joins a thread. */}
      <button type="button" role="menuitem" onClick={() => run(thread.joined ? 'leave' : 'join')}>
        {thread.forum ? (thread.joined ? 'Unfollow post' : 'Follow post') : thread.joined ? 'Leave thread' : 'Join thread'}
      </button>
      <EditRows thread={thread} post={post} run={run} onChange={onChange} />
      <span className="exo-dc-menu-line" role="separator" />
      <button type="button" role="menuitem" onClick={() => onCopy(`https://discord.com/channels/${thread.guildId}/${thread.id}`)}><Icons.Link size={16} />Copy link</button>
      {thread.manage ? <button type="button" role="menuitem" className="is-danger" onClick={onDelete}><Icons.Delete size={16} />Delete {post}</button> : null}
      {developer ? <button type="button" role="menuitem" onClick={() => onCopy(thread.id)}><Icons.CopyId size={16} />Copy {post} ID</button> : null}
    </>
  )
}

// The owner's and a moderator's rows: Edit Tags and Pin Post (a forum's), Close or Open, and Lock (Manage Threads).
function EditRows({ thread, post, run, onChange }: { thread: DiscordThreadState; post: string; run: (action: Action, params?: Params, after?: () => void) => void; onChange: (thread: DiscordThreadState) => void }) {
  const edits = thread.mine || thread.manage
  return (
    <>
      {thread.forum && edits && thread.tags?.length ? <EditTags thread={thread} run={run} onChange={onChange} /> : null}
      {thread.forum && thread.manage ? <button type="button" role="menuitem" onClick={() => run('pin', { pinned: !thread.pinned })}><Icons.Pin size={16} />{thread.pinned ? 'Unpin post' : 'Pin post'}</button> : null}
      {edits ? <button type="button" role="menuitem" onClick={() => run('archive', { archived: !thread.archived })}>{thread.archived ? `Open ${post}` : `Close ${post}`}</button> : null}
      {thread.manage ? <button type="button" role="menuitem" onClick={() => run('lock', { locked: !thread.locked })}><Icons.Lock size={16} />{thread.locked ? `Unlock ${post}` : `Lock ${post}`}</button> : null}
    </>
  )
}

// Edit Tags: the forum's tags, up to five on a post; each pick is saved as it's made, and the menu stays open.
function EditTags({ thread, run, onChange }: { thread: DiscordThreadState; run: (action: Action, params?: Params, after?: () => void) => void; onChange: (thread: DiscordThreadState) => void }) {
  const applied = thread.applied
  return (
    <Submenu label="Edit tags">
      {(thread.tags ?? []).map(tag => {
        const on = applied.includes(tag.id)
        const next = on ? applied.filter(id => id !== tag.id) : [...applied, tag.id]
        return (
          <button key={tag.id} type="button" role="menuitemcheckbox" aria-checked={on} disabled={!on && applied.length >= 5}
            onClick={() => run('tags', { tags: next }, () => onChange({ ...thread, applied: next }))}>
            <span className="exo-dc-ctx-name exo-dc-tag-name"><TagLabel tag={tag} /></span>
            {on ? <Icons.Check size={14} className="exo-dc-ctx-check" /> : null}
          </button>
        )
      })}
    </Submenu>
  )
}
