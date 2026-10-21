import { host } from '../../lib/host'
import { useAccountSettings } from '../../lib/discordPrefs'
import { MUTE_FOR } from './ChatHeader'
import { Icons } from './icons'
import { Submenu } from './UserMenu'

/** A DM row as its right-click menu needs it (read from the row: data-dc-dm and its state). */
export type DmInfo = { id: string; unread: boolean; muted: boolean; mutedUntil: number | null }

/** The DM row's attributes that carry DmInfo to the person menu. */
export function dmData(dm: { id: string; unread: boolean; mentions: number; muted?: boolean; mutedUntil?: number | null }) {
  return {
    'data-dc-dm': dm.id,
    'data-dc-dm-unread': dm.unread || dm.mentions > 0 ? '1' : undefined,
    'data-dc-dm-muted': dm.muted ? String(dm.mutedUntil ?? 0) : undefined,
  }
}

export function readDm(el: Element): DmInfo | undefined {
  const id = el.getAttribute('data-dc-dm')
  if (!id) return undefined
  const muted = el.getAttribute('data-dc-dm-muted')
  return { id, unread: el.hasAttribute('data-dc-dm-unread'), muted: muted !== null, mutedUntil: muted && muted !== '0' ? Number(muted) : null }
}

type Done = (result: { ok: boolean; message?: string | null }) => void

/** Mark As Read, off when nothing is unread, as Discord's DM menu opens with it. */
export function DmReadItem({ dm, onDone }: { dm: DmInfo; onDone: Done }) {
  return (
    <button type="button" role="menuitem" disabled={!dm.unread}
      onClick={() => void host.discordChannel(dm.id, 'read').then(onDone, () => onDone({ ok: false }))}>Mark As Read</button>
  )
}

/** Mute @user (or Mute Conversation for a group) for a while, or Unmute with when it ends. */
export function DmMuteItem({ dm, label, onDone }: { dm: DmInfo; label: string; onDone: Done }) {
  const mute = (change: { muted: boolean; minutes?: number }) => void host.discordChannelNotify(dm.id, change).then(onDone, () => onDone({ ok: false }))
  if (dm.muted) {
    return (
      <button type="button" role="menuitem" onClick={() => mute({ muted: false })}>
        <span className="exo-dc-menu-text">Un{label.charAt(0).toLowerCase()}{label.slice(1)}
          {dm.mutedUntil ? <small>Muted until {new Date(dm.mutedUntil).toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' })}</small> : null}
        </span>
      </button>
    )
  }
  return (
    <Submenu label={label}>
      {MUTE_FOR.map(([minutes, text]) => <button key={minutes} type="button" role="menuitem" onClick={() => mute(minutes ? { muted: true, minutes } : { muted: true })}>{text}</button>)}
    </Submenu>
  )
}

/** Copy Channel ID, in Developer Mode. */
export function DmCopyId({ dm, onClose }: { dm?: DmInfo; onClose: () => void }) {
  const developer = useAccountSettings().developerMode
  if (!developer || !dm) return null
  return <button type="button" role="menuitem" onClick={() => { void navigator.clipboard.writeText(dm.id).catch(() => { }); onClose() }}><Icons.CopyId size={16} />Copy Channel ID</button>
}

/** The person menu on a DM row: Mark As Read first, Close DM after the person's rows, Mute @user before Copy. */
export function DmLead({ dm, onDone }: { dm?: DmInfo; onDone: Done }) {
  return dm ? <><DmReadItem dm={dm} onDone={onDone} /><span className="exo-dc-menu-line" role="separator" /></> : null
}

export function DmClose({ dm, onDone }: { dm?: DmInfo; onDone: Done }) {
  return dm ? <button type="button" role="menuitem" onClick={() => void host.discordChannel(dm.id, 'close').then(onDone, () => onDone({ ok: false }))}>Close DM</button> : null
}

export function DmMuteRow({ dm, username, name, onDone }: { dm?: DmInfo; username: string | null; name: string; onDone: Done }) {
  return dm ? <><span className="exo-dc-menu-line" role="separator" /><DmMuteItem dm={dm} label={`Mute @${username ?? name}`} onDone={onDone} /></> : null
}
