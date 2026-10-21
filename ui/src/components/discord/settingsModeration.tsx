import { useRef, useState } from 'react'
import { Close } from '../../brand/icons'
import { avatarUrl, snowflakeTime, type DiscordChannel, type DiscordModeration, type DiscordUser } from '../../lib/discord'
import { Choice } from '../settings/Choice'
import { admin, Confirm, personName, ReadError, untilLabel, useAdmin, useLayer } from './admin'
import { Icons } from './icons'
import { Avatar } from './presence'

type Invite = { code: string; uses: number; max_uses: number; expires_at?: string | null; inviter?: DiscordUser | null; channel?: { id: string; name: string } | null }

/** Discord's Invites: every active invite with who made it, its uses and when it runs out; revoke one. */
export function Invites({ guildId }: { guildId: string }) {
  const invites = useAdmin<Invite[]>('invites', { guildId })
  // A failed revoke, said under its invite.
  const [failed, setFailed] = useState<{ code: string; message: string } | null>(null)
  const revoke = (code: string) => { setFailed(null); void admin('revokeInvite', { guildId, code }).then(result => {
    if (result.ok) invites.set(list => list && list.filter(i => i.code !== code)); else setFailed({ code, message: result.message ?? 'Discord did not revoke that.' })
  }) }
  return (
    <>
      <h1>Invites</h1>
      <p className="exo-dc-note">Here's a list of all active invite links. You can revoke any one.</p>
      <ReadError error={invites.error} onRetry={invites.reload} />
      {!invites.data && !invites.error ? <p className="exo-dc-note">Loading</p> : null}
      {invites.data?.length === 0 ? <p className="exo-dc-note">No invites yet.</p> : null}
      {invites.data?.length ? (
        <div className="exo-dc-table is-invites" role="table" aria-label="Invites">
          <div className="exo-dc-table-row is-head" role="row">
            <span role="columnheader">Inviter</span><span role="columnheader">Invite Code</span><span role="columnheader">Uses</span>
            <span role="columnheader">Expires</span><span role="columnheader" aria-label="Revoke" />
          </div>
          {invites.data.map(invite => (
            <div key={invite.code} className="exo-dc-table-row" role="row">
              <span role="cell" className="exo-dc-table-name" data-dc-user={invite.inviter?.id}>
                {invite.inviter ? <Avatar src={avatarUrl(invite.inviter, 32)} name={invite.inviter.username} status={null} size={24} /> : null}
                <span className="exo-dc-friend-names"><strong>{personName(invite.inviter)}</strong>{invite.channel ? <span>#{invite.channel.name}</span> : null}</span>
              </span>
              <span role="cell">{invite.code}</span>
              <span role="cell">{invite.max_uses ? `${invite.uses}/${invite.max_uses}` : invite.uses}</span>
              <span role="cell">{untilLabel(invite.expires_at)}</span>
              <span role="cell" className="exo-dc-row-actions">
                <button type="button" className="exo-dc-icon-btn is-danger" aria-label={`Revoke ${invite.code}`} onClick={() => revoke(invite.code)}><Close size={14} /></button>
              </span>
              {failed?.code === invite.code ? <span className="exo-dc-field-error exo-dc-row-error" role="alert">{failed.message}</span> : null}
            </div>
          ))}
        </div>
      ) : null}
    </>
  )
}

type Webhook = { id: string; name: string; channel_id?: string | null; avatar?: string | null; user?: DiscordUser | null }
type Integration = { id: string; name: string; type: string; enabled?: boolean; application?: { id: string; name: string; icon?: string | null; bot?: DiscordUser | null } | null; user?: DiscordUser | null }
type Linked = { webhooks?: Webhook[] | null; integrations?: Integration[] | null }
type Removing = { kind: 'webhook' | 'integration'; id: string; name: string }

function Webhooks({ hooks, channelName, onRemove }: { hooks: Webhook[]; channelName: (id?: string | null) => string | undefined; onRemove: (item: Removing) => void }) {
  return (
    <section className="exo-dc-perm-group" aria-label="Webhooks">
      <h3>Webhooks</h3>
      {hooks.length ? null : <p className="exo-dc-note">No webhooks yet.</p>}
      <ul className="exo-dc-mod-list">
        {hooks.map(hook => (
          <li key={hook.id}>
            <Avatar src={hook.avatar ? `https://cdn.discordapp.com/avatars/${hook.id}/${hook.avatar}.webp?size=64` : null} name={hook.name} status={null} size={32} />
            <span className="exo-dc-mod-name"><span>{hook.name}</span><small>{channelName(hook.channel_id) ? `#${channelName(hook.channel_id)}` : ''}{hook.user ? ` · Created by ${personName(hook.user)}` : ''}</small></span>
            <button type="button" className="exo-dc-tab is-raised is-danger" onClick={() => onRemove({ kind: 'webhook', id: hook.id, name: hook.name })}>Delete Webhook</button>
          </li>
        ))}
      </ul>
    </section>
  )
}

function Apps({ items, onRemove }: { items: Integration[]; onRemove: (item: Removing) => void }) {
  return (
    <section className="exo-dc-perm-group" aria-label="Apps">
      <h3>Bots and Apps</h3>
      {items.length ? null : <p className="exo-dc-note">No apps in this server.</p>}
      <ul className="exo-dc-mod-list">
        {items.map(item => (
          <li key={item.id}>
            <Avatar src={item.application?.icon ? `https://cdn.discordapp.com/app-icons/${item.application.id}/${item.application.icon}.webp?size=64` : null} name={item.name} status={null} size={32} />
            <span className="exo-dc-mod-name"><span>{item.application?.name ?? item.name}</span><small>{item.type === 'discord' ? 'App' : item.type}{item.user ? ` · Added by ${personName(item.user)}` : ''}</small></span>
            <button type="button" className="exo-dc-tab is-raised is-danger" onClick={() => onRemove({ kind: 'integration', id: item.id, name: item.application?.name ?? item.name })}>Remove</button>
          </li>
        ))}
      </ul>
    </section>
  )
}

/** Discord's Integrations: the server's webhooks and its apps and bots, each removable. */
export function Integrations({ guildId, moderation, channels }: { guildId: string; moderation: DiscordModeration; channels: DiscordChannel[] }) {
  const linked = useAdmin<Linked>('integrations', { guildId })
  const [removing, setRemoving] = useState<Removing | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const channelName = (id?: string | null) => channels.find(c => c.id === id)?.name
  const ask = (item: Removing) => { setError(null); setRemoving(item) }
  const remove = () => {
    if (!removing) return
    const action = removing.kind === 'webhook' ? 'deleteWebhook' : 'deleteIntegration'
    setBusy(true)
    void admin(action, { guildId, [removing.kind === 'webhook' ? 'webhookId' : 'integrationId']: removing.id }).then(result => {
      setBusy(false)
      if (!result.ok) { setError(result.message ?? 'Discord did not remove that.'); return }
      setRemoving(null); setError(null); linked.reload()
    })
  }
  const apps = (linked.data?.integrations ?? []).filter(i => i.type === 'discord')
  const others = (linked.data?.integrations ?? []).filter(i => i.type !== 'discord')
  return (
    <>
      <h1>Integrations</h1>
      <p className="exo-dc-note">Customize your server with integrations. Manage webhooks, followed channels and apps.</p>
      <ReadError error={linked.error} onRetry={linked.reload} />
      {!linked.data && !linked.error ? <p className="exo-dc-note">Loading</p> : null}
      {moderation.manageWebhooks && linked.data ? <Webhooks hooks={linked.data.webhooks ?? []} channelName={channelName} onRemove={ask} /> : null}
      {moderation.manageGuild && linked.data ? <Apps items={[...apps, ...others]} onRemove={ask} /> : null}
      {removing ? (
        <Confirm title={removing.kind === 'webhook' ? 'Delete Webhook' : `Remove ${removing.name}`} action={removing.kind === 'webhook' ? 'Delete' : 'Remove'} error={error} busy={busy}
          onConfirm={remove} onClose={() => { setRemoving(null); setError(null) }}>
          <p>{removing.kind === 'webhook' ? `Are you sure you want to delete ${removing.name}? This action cannot be undone.` : `${removing.name} will be removed from this server, with its role and commands.`}</p>
        </Confirm>
      ) : null}
    </>
  )
}

type Change = { key: string; old_value?: unknown; new_value?: unknown }
type Entry = { id: string; action_type: number; user_id?: string | null; target_id?: string | null; reason?: string | null; changes?: Change[]; options?: { channel_id?: string; count?: string; delete_member_days?: string } }
type Audit = { audit_log_entries: Entry[]; users: DiscordUser[] }

// Discord's Audit Log actions, in its own words; the filter lists them by name.
const ACTIONS: Record<number, [verb: string, filter: string]> = {
  1: ['updated the server', 'Update Server'], 10: ['created a channel', 'Create Channel'], 11: ['updated a channel', 'Update Channel'], 12: ['deleted a channel', 'Delete Channel'],
  13: ['created a permission override', 'Create Channel Permissions'], 14: ['updated a permission override', 'Update Channel Permissions'], 15: ['deleted a permission override', 'Delete Channel Permissions'],
  20: ['kicked', 'Kick Member'], 21: ['pruned members', 'Prune Members'], 22: ['banned', 'Ban Member'], 23: ['unbanned', 'Unban Member'], 24: ['updated', 'Update Member'],
  25: ['updated roles for', 'Update Member Roles'], 26: ['moved a member', 'Move Member'], 27: ['disconnected a member', 'Disconnect Member'], 28: ['added a bot', 'Add Bot'],
  30: ['created a role', 'Create Role'], 31: ['updated a role', 'Update Role'], 32: ['deleted a role', 'Delete Role'],
  40: ['created an invite', 'Create Invite'], 41: ['updated an invite', 'Update Invite'], 42: ['deleted an invite', 'Delete Invite'],
  50: ['created a webhook', 'Create Webhook'], 51: ['updated a webhook', 'Update Webhook'], 52: ['deleted a webhook', 'Delete Webhook'],
  60: ['created an emoji', 'Create Emoji'], 61: ['updated an emoji', 'Update Emoji'], 62: ['deleted an emoji', 'Delete Emoji'],
  72: ['deleted a message by', 'Delete Message'], 73: ['deleted messages', 'Bulk Delete Messages'], 74: ['pinned a message by', 'Pin Message'], 75: ['unpinned a message by', 'Unpin Message'],
  80: ['added an integration', 'Create Integration'], 81: ['updated an integration', 'Update Integration'], 82: ['removed an integration', 'Delete Integration'],
  90: ['created a sticker', 'Create Sticker'], 91: ['updated a sticker', 'Update Sticker'], 92: ['deleted a sticker', 'Delete Sticker'],
  100: ['created an event', 'Create Event'], 101: ['updated an event', 'Update Event'], 102: ['deleted an event', 'Delete Event'],
  110: ['created a thread', 'Create Thread'], 111: ['updated a thread', 'Update Thread'], 112: ['deleted a thread', 'Delete Thread'],
  140: ['created an AutoMod rule', 'Create AutoMod Rule'], 141: ['updated an AutoMod rule', 'Update AutoMod Rule'], 142: ['deleted an AutoMod rule', 'Delete AutoMod Rule'],
  143: ['AutoMod blocked a message by', 'AutoMod Block Message'],
}

const KEYS: Record<string, string> = {
  name: 'name', topic: 'topic', nick: 'nickname', color: 'color', hoist: 'Display separately', mentionable: 'Allow anyone to @mention', permissions: 'permissions',
  rate_limit_per_user: 'slowmode', nsfw: 'Age-Restricted', bitrate: 'bitrate', user_limit: 'user limit', mute: 'server mute', deaf: 'server deafen',
  verification_level: 'verification level', explicit_content_filter: 'sensitive content filter', afk_timeout: 'inactive time-out', icon_hash: 'icon', max_uses: 'max uses', max_age: 'expiry',
}

const shown = (value: unknown) => typeof value === 'string' || typeof value === 'number' ? String(value) : typeof value === 'boolean' ? (value ? 'on' : 'off') : null

// One change, readable: roles added or taken away, a timeout, or "name: old → new".
function describe(change: Change): string {
  const roles = (v: unknown) => Array.isArray(v) ? v.map(r => (r as { name?: string }).name ?? 'a role').join(', ') : ''
  if (change.key === '$add') return `Added ${roles(change.new_value)}`
  if (change.key === '$remove') return `Removed ${roles(change.new_value)}`
  if (change.key === 'communication_disabled_until') {
    return change.new_value ? `Timed out until ${new Date(String(change.new_value)).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' })}` : 'Removed the timeout'
  }
  const label = KEYS[change.key] ?? change.key.replace(/_/g, ' ')
  const before = shown(change.old_value)
  const after = shown(change.new_value)
  if (change.key === 'permissions' || change.key === 'allow' || change.key === 'deny' || after === null) return `Changed ${label}`
  return before === null ? `Set ${label} to ${after}` : `Changed ${label} from ${before} to ${after}`
}

/** Discord's Audit Log: who did what to whom and what changed, with the reason; filter by person and action, load older. */
export function AuditLog({ guildId }: { guildId: string }) {
  const [userId, setUserId] = useState('')
  const [action, setAction] = useState('0')
  const params = { guildId, ...(userId ? { userId } : {}), ...(action !== '0' ? { actionType: Number(action) } : {}) }
  const audit = useAdmin<Audit>('audit', params)
  const [more, setMore] = useState<{ entries: Entry[]; users: DiscordUser[]; done: boolean }>({ entries: [], users: [], done: false })
  const [moreFor, setMoreFor] = useState(JSON.stringify(params))
  const [loading, setLoading] = useState(false)
  const [moreError, setMoreError] = useState<string | null>(null)
  if (moreFor !== JSON.stringify(params)) { setMoreFor(JSON.stringify(params)); setMore({ entries: [], users: [], done: false }); setMoreError(null) }
  // The filter now, read when a page arrives: a page asked for under an older filter is dropped.
  const current = useRef(moreFor)
  current.current = JSON.stringify(params)
  const entries = [...(audit.data?.audit_log_entries ?? []), ...more.entries]
  const users = new Map([...(audit.data?.users ?? []), ...more.users].map(u => [u.id, u]))
  // One page at a time; a page that failed says so under the button and can be asked for again.
  const loadMore = () => {
    if (loading) return
    const of = current.current
    setLoading(true); setMoreError(null)
    void admin<Audit>('audit', { ...params, before: entries.at(-1)?.id }).then(result => {
      setLoading(false)
      if (of !== current.current) return
      if (!result.ok) { setMoreError(result.message ?? 'Discord did not send more.'); return }
      const page = result.data?.audit_log_entries ?? []
      setMore(m => ({ entries: [...m.entries, ...page.filter(e => !m.entries.some(o => o.id === e.id))], users: [...m.users, ...(result.data?.users ?? [])], done: page.length < 50 }))
    })
  }
  return (
    <>
      <h1>Audit Log</h1>
      <div className="exo-dc-page-bar">
        <Choice label="Filter by User" value={userId || 'all'} choices={[{ value: 'all', label: 'All Users' }, ...[...users.values()].map(u => ({ value: u.id, label: personName(u) }))]}
          onChange={id => setUserId(id === 'all' ? '' : id)} />
        <Choice label="Filter by Action" value={action} choices={[{ value: '0', label: 'All Actions' }, ...Object.entries(ACTIONS).map(([code, [, label]]) => ({ value: code, label }))]}
          onChange={setAction} />
      </div>
      <ReadError error={audit.error} onRetry={audit.reload} />
      {!audit.data && !audit.error ? <p className="exo-dc-note">Loading</p> : null}
      {audit.data && entries.length === 0 ? <p className="exo-dc-note">Nothing here yet.</p> : null}
      <ul className="exo-dc-mod-list exo-dc-audit">
        {entries.map(entry => <AuditRow key={entry.id} entry={entry} users={users} />)}
      </ul>
      {audit.data && entries.length >= 50 && !more.done ? <button type="button" className="exo-dc-tab is-raised" disabled={loading} onClick={loadMore}>{loading ? 'Loading' : 'Load More'}</button> : null}
      {moreError ? <p className="exo-dc-note is-bad" role="alert">{moreError}</p> : null}
    </>
  )
}

// "X timed out Y", "X created a role mods": the verb, and who or what it was done to.
function entryWords(entry: Entry, target: DiscordUser | undefined) {
  const timedOut = entry.action_type === 24 && entry.changes?.some(c => c.key === 'communication_disabled_until')
  const verb = timedOut ? 'timed out' : ACTIONS[entry.action_type]?.[0] ?? 'made a change'
  const named = entry.changes?.find(c => c.key === 'name')
  const thing = target ? ` ${personName(target)}` : named ? ` ${String(named.new_value ?? named.old_value ?? '')}` : ''
  return { verb, thing }
}

function AuditRow({ entry, users }: { entry: Entry; users: Map<string, DiscordUser> }) {
  const [open, setOpen] = useState(false)
  const who = users.get(entry.user_id ?? '')
  const target = entry.target_id ? users.get(entry.target_id) : undefined
  const { verb, thing } = entryWords(entry, target)
  const when = new Date(snowflakeTime(entry.id)).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' })
  const changes = entry.changes?.filter(c => c.key !== 'id') ?? []
  return (
    <li className="exo-dc-audit-row">
      <button type="button" className="exo-dc-audit-head" aria-expanded={open} disabled={!changes.length && !entry.reason} onClick={() => setOpen(o => !o)}>
        {who ? <Avatar src={avatarUrl(who, 32)} name={who.username} status={null} size={32} /> : <Icons.Person size={20} />}
        <span className="exo-dc-mod-name"><span><strong data-dc-user={who?.id}>{personName(who)}</strong> {verb}<strong data-dc-user={target?.id}>{thing}</strong></span><small>{when}</small></span>
      </button>
      {open ? (
        <div className="exo-dc-audit-body">
          {changes.map((change, i) => <p key={i}>{describe(change)}</p>)}
          {entry.reason ? <p>Reason: {entry.reason}</p> : null}
        </div>
      ) : null}
    </li>
  )
}

type Ban = { user: DiscordUser; reason?: string | null }

/** Discord's Bans (with Ban Members): every ban, searched by name or ID; a ban opens with its reason and Revoke Ban. */
export function Bans({ guildId }: { guildId: string }) {
  const bans = useAdmin<Ban[]>('bans', { guildId })
  const [query, setQuery] = useState('')
  const [open, setOpen] = useState<Ban | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const q = query.trim().toLowerCase()
  const list = (bans.data ?? []).filter(b => !q || personName(b.user).toLowerCase().includes(q) || b.user.username.toLowerCase().includes(q) || b.user.id === q)
  const revoke = (ban: Ban) => {
    setBusy(true)
    void admin('unban', { guildId, userId: ban.user.id }).then(result => {
      setBusy(false)
      if (!result.ok) { setError(result.message ?? 'Discord did not revoke that ban.'); return }
      setOpen(null); bans.set(all => all && all.filter(b => b.user.id !== ban.user.id))
    })
  }
  return (
    <>
      <h1>Bans</h1>
      <p className="exo-dc-note">Bans by default are by account and IP. A user can circumvent an IP ban by using a proxy.</p>
      <label className="exo-dc-friend-search">
        <Icons.Search size={16} />
        <input placeholder="Search by user ID or username" value={query} onChange={event => setQuery(event.target.value)} aria-label="Search bans" />
      </label>
      <ReadError error={bans.error} onRetry={bans.reload} />
      {!bans.data && !bans.error ? <p className="exo-dc-note">Loading</p> : null}
      {bans.data?.length === 0 ? <p className="exo-dc-note">No bans. Whew.</p> : null}
      <ul className="exo-dc-mod-list">
        {list.map(ban => (
          <li key={ban.user.id}>
            <button type="button" className="exo-dc-ban" onClick={() => { setError(null); setOpen(ban) }}>
              <Avatar src={avatarUrl(ban.user, 32)} name={ban.user.username} status={null} size={32} />
              <span className="exo-dc-mod-name"><span>{personName(ban.user)}</span><small>{ban.user.username}</small></span>
            </button>
          </li>
        ))}
      </ul>
      {open ? <BanDetail ban={open} error={error} busy={busy} onRevoke={() => revoke(open)} onClose={() => setOpen(null)} /> : null}
    </>
  )
}

function BanDetail({ ban, error, busy, onRevoke, onClose }: { ban: Ban; error: string | null; busy: boolean; onRevoke: () => void; onClose: () => void }) {
  const box = useRef<HTMLDivElement>(null)
  useLayer(box, onClose)
  return (
    <div className="exo-dc-modal-back is-top" onClick={onClose}>
      <div ref={box} className="exo-dc-modal exo-dc-confirm" role="dialog" aria-modal="true" aria-label={personName(ban.user)} tabIndex={-1} onClick={event => event.stopPropagation()}>
        <strong>{personName(ban.user)}</strong>
        <p className="exo-dc-side-label">Ban Reason</p>
        <p>{ban.reason || 'No reason given.'}</p>
        {error ? <p className="is-bad" role="alert">{error}</p> : null}
        <div className="exo-dc-modal-row">
          <button type="button" className="exo-dc-tab is-danger" disabled={busy} onClick={onRevoke}>Revoke Ban</button>
          <button type="button" className="button button--primary" onClick={onClose}>Done</button>
        </div>
      </div>
    </div>
  )
}
