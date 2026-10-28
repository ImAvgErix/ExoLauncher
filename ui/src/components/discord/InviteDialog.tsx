import { useEffect, useRef, useState } from 'react'
import { host } from '../../lib/host'
import type { DiscordFriend } from '../../lib/discord'
import { useStreamerHides } from '../../lib/discordPrefs'
import { notify } from '../../lib/notify'
import { Choice } from '../settings/Choice'
import { Switch } from '../Switch'
import { Confirm, useLayer } from './admin'
import { Avatar } from './presence'
import { ModalBack } from './SettingsLayer'
import { EXPIRES, USES } from './settingsModeration'
import { ErrorNote } from '../ui/ErrorNote'

type Link = { maxAge: number; maxUses: number; temporary: boolean }
const FIRST: Link = { maxAge: 604800, maxUses: 0, temporary: false }

// Discord's line under the link: how long it lasts and how many uses it has.
function lasts({ maxAge, maxUses }: Link) {
  const uses = maxUses === 1 ? '1 use' : `${maxUses} uses`
  if (!maxAge) return maxUses ? `Your invite link expires after ${uses}.` : 'Your invite link will never expire.'
  const time = EXPIRES.find(e => Number(e.value) === maxAge)?.label ?? '7 days'
  return `Your invite link expires in ${time}${maxUses ? `, or after ${uses}` : ''}.`
}

/**
 * Discord's Invite friends to a server: your friends, each with Invite (the link sent in your DM with them), the link
 * to copy, and Edit invite link (how long it lasts, how many uses, temporary membership) for a new one.
 */
export function InviteDialog({ name, channelId, channelName, onClose }: { name: string; channelId: string; channelName?: string | null; onClose: () => void }) {
  const hide = useStreamerHides().invites
  const [link, setLink] = useState<Link>(FIRST)
  const [url, setUrl] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [editing, setEditing] = useState(false)
  // Escape closes it and focus starts in it, as in Discord's other dialogs (while Edit invite link is open, that goes first).
  const box = useRef<HTMLDivElement>(null)
  useLayer(box, onClose, !editing)
  const make = (next: Link) => host.discordInvite(channelId, next).then(result => {
    if (!result.url) return result.message ?? 'Discord did not make an invite.'
    setUrl(result.url); setLink(next)
    return null
  }, () => 'Discord did not make an invite.')
  useEffect(() => { void make(FIRST).then(setError) }, [channelId]) // eslint-disable-line react-hooks/exhaustive-deps -- one link per opening
  if (editing) return <EditLink link={link} onClose={() => setEditing(false)} onMake={next => make(next).then(why => { if (!why) setEditing(false); return why })} />
  const copy = () => { if (url) void navigator.clipboard.writeText(url).then(() => notify({ id: 'dc-copied', text: 'Invite link copied', tone: 'good' })).catch(() => { }) }
  return (
    <ModalBack onClose={onClose}>
      <div ref={box} className="exo-dc-modal exo-dc-invite-dialog" role="dialog" aria-modal="true" aria-label={`Invite friends to ${name}`} tabIndex={-1} onClick={event => event.stopPropagation()}>
        <strong>Invite friends to {name}</strong>
        {channelName ? <p className="exo-dc-note">Recipients will land in #{channelName}</p> : null}
        <InviteFriends url={url} />
        <label className="exo-dc-modal-field"><span>Or, send a server invite link to a friend</span>
          <span className="exo-dc-invite-link">
            <input className="exo-dc-modal-input" readOnly value={url ? (hide ? 'Hidden by Streamer Mode' : url) : 'Making a link'} aria-label="Invite link" />
            <button type="button" className="exo-set-btn is-primary" disabled={!url} onClick={copy}>Copy</button>
          </span>
        </label>
        {error ? <ErrorNote className="is-bad" message={error} /> : null}
        <p className="exo-dc-note">{lasts(link)} <button type="button" className="exo-dc-link" onClick={() => setEditing(true)}>Edit invite link.</button></p>
      </div>
    </ModalBack>
  )
}

// Your friends to invite, found as you type; Invite sends the link in your DM with them, then reads Sent.
function InviteFriends({ url }: { url: string | null }) {
  const [friends, setFriends] = useState<DiscordFriend[]>([])
  const [query, setQuery] = useState('')
  const [sent, setSent] = useState<Set<string>>(new Set())
  const [failed, setFailed] = useState<string | null>(null)
  useEffect(() => { void host.discordFriends().then(result => setFriends(result.friends.filter(f => f.kind === 'friend'))).catch(() => { }) }, [])
  const q = query.trim().toLowerCase()
  const shown = friends.filter(f => !q || f.name.toLowerCase().includes(q) || f.username?.toLowerCase().includes(q)).slice(0, 50)
  const invite = async (friend: DiscordFriend) => {
    if (!url) return
    setFailed(null)
    const dm = friend.dmId ?? (await host.discordFriend('dm', { userId: friend.id }).catch(() => null))?.channelId
    const result = dm ? await host.discordSend(dm, url).catch(() => null) : null
    if (result?.ok) setSent(set => new Set(set).add(friend.id)); else setFailed(friend.id)
  }
  return (
    <>
      <input className="exo-dc-modal-input" placeholder="Search for friends" aria-label="Search for friends" value={query} onChange={event => setQuery(event.target.value)} />
      <ul className="exo-dc-invite-friends" aria-label="Friends">
        {shown.map(friend => (
          <li key={friend.id}>
            <Avatar src={friend.avatar} name={friend.name} status={null} size={32} look={friend.look} />
            <span className="exo-dc-invite-who"><strong>{friend.name}</strong>{friend.username ? <span>{friend.username}</span> : null}</span>
            {failed === friend.id ? <ErrorNote as="span" className="is-bad" message="Not sent" /> : null}
            <button type="button" className="exo-set-btn" disabled={!url || sent.has(friend.id)} onClick={() => void invite(friend)}>{sent.has(friend.id) ? 'Sent' : 'Invite'}</button>
          </li>
        ))}
        {!shown.length ? <li className="exo-dc-note">{friends.length ? 'No friends found' : 'No friends to invite yet'}</li> : null}
      </ul>
    </>
  )
}

// Discord's Server invite link settings: how long, how many uses, temporary membership; Generate a new link.
function EditLink({ link, onMake, onClose }: { link: Link; onMake: (next: Link) => Promise<string | null>; onClose: () => void }) {
  const [maxAge, setMaxAge] = useState(String(link.maxAge))
  const [maxUses, setMaxUses] = useState(String(link.maxUses))
  const [temporary, setTemporary] = useState(link.temporary)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const make = () => {
    setBusy(true); setError(null)
    void onMake({ maxAge: Number(maxAge), maxUses: Number(maxUses), temporary }).then(why => { setBusy(false); setError(why) })
  }
  return (
    <Confirm title="Server invite link settings" action="Generate a new link" danger={false} form busy={busy} error={error} onConfirm={make} onClose={onClose}>
      <div className="exo-dc-form-rows">
        <label><span>Expire after</span><Choice label="Expire after" value={maxAge} choices={EXPIRES} onChange={setMaxAge} /></label>
        <label><span>Max number of uses</span><Choice label="Max number of uses" value={maxUses} choices={USES} onChange={setMaxUses} /></label>
        <span className="exo-dc-form-switch">
          <span><strong>Grant temporary membership</strong><span>Members are automatically kicked when they disconnect unless a role is assigned.</span></span>
          <Switch checked={temporary} label="Grant temporary membership" onChange={setTemporary} />
        </span>
      </div>
    </Confirm>
  )
}
