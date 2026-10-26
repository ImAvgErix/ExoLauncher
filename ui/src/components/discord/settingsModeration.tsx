import { useRef, useState } from 'react'
import { Close } from '../../brand/icons'
import { avatarUrl, snowflakeTime, type DiscordChannel, type DiscordModeration, type DiscordUser } from '../../lib/discord'
import { Choice } from '../settings/Choice'
import { Switch } from '../Switch'
import { notify } from '../../lib/notify'
import { admin, Confirm, dropUpload, personName, pickFile, ReadError, untilLabel, useAdmin } from './admin'
import { uploadFile } from './Chat'
import { Icons } from './icons'
import { Avatar } from './presence'
import { Dialog } from '../ui/Dialog'
import { Spinner } from '../ui/Spinner'
import { Skeleton } from '../ui/Skeleton'

type Invite = { code: string; uses: number; max_uses: number; expires_at?: string | null; inviter?: DiscordUser | null; channel?: { id: string; name: string } | null }

export const EXPIRES = [{ value: '1800', label: '30 minutes' }, { value: '3600', label: '1 hour' }, { value: '21600', label: '6 hours' }, { value: '43200', label: '12 hours' },
  { value: '86400', label: '1 day' }, { value: '604800', label: '7 days' }, { value: '0', label: 'Never' }]
export const USES = [{ value: '0', label: 'No limit' }, ...[1, 5, 10, 25, 50, 100].map(n => ({ value: String(n), label: n === 1 ? '1 use' : `${n} uses` }))]
const HOURS = [1, 2, 4, 6, 12, 24].map(h => ({ value: String(h), label: h === 1 ? '1 hour' : `${h} hours` }))

/** A server's security pause (Discord's incidents_data): until when invites and DMs are paused. */
export type Incidents = { invites_disabled_until?: string | null; dms_disabled_until?: string | null } | null

const pausedNow = (until?: string | null) => !!until && Date.parse(until) > Date.now()

/**
 * Discord's Invites: Pause Invites (Discord's Security Actions) and Create Invite Link over every active invite with
 * who made it, its uses and when it runs out; revoke one.
 */
export function Invites({ guildId, channels, incidents, onChanged }: { guildId: string; channels: DiscordChannel[]; incidents: Incidents; onChanged: () => void }) {
  const invites = useAdmin<Invite[]>('invites', { guildId })
  const [dialog, setDialog] = useState<'create' | 'pause' | null>(null)
  const paused = pausedNow(incidents?.invites_disabled_until)
  // A failed revoke, said under its invite.
  const [failed, setFailed] = useState<{ code: string; message: string } | null>(null)
  const revoke = (code: string) => { setFailed(null); void admin('revokeInvite', { guildId, code }).then(result => {
    if (result.ok) invites.set(list => list && list.filter(i => i.code !== code)); else setFailed({ code, message: result.message ?? 'Discord did not revoke that.' })
  }) }
  return (
    <>
      <h1>Invites</h1>
      <p className="exo-dc-note">Here's a list of all active invite links. You can revoke any one.</p>
      <div className="exo-dc-page-bar">
        <strong className="exo-dc-grow">{invites.data?.length ? 'Active invite links' : 'No active invite links'}</strong>
        <button type="button" className={paused ? 'exo-set-btn is-primary' : 'exo-dc-tab is-raised is-danger'} onClick={() => setDialog('pause')}>{paused ? 'Enable invites' : 'Pause invites'}</button>
        <button type="button" className="exo-set-btn is-primary" onClick={() => setDialog('create')}>Create invite link</button>
      </div>
      {paused ? <p className="exo-dc-note">Invites are paused until {new Date(incidents?.invites_disabled_until ?? '').toLocaleString()}.</p> : null}
      <ReadError error={invites.error} onRetry={invites.reload} />
      {!invites.data && !invites.error ? <Skeleton label="Loading invites" /> : null}
      {invites.data?.length ? (
        <div className="exo-dc-table is-invites" role="table" aria-label="Invites">
          <div className="exo-dc-table-row is-head" role="row">
            <span role="columnheader">Inviter</span><span role="columnheader">Invite code</span><span role="columnheader">Uses</span>
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
      {dialog === 'create' ? <CreateInvite guildId={guildId} channels={channels} onClose={() => setDialog(null)} onCreated={() => { setDialog(null); invites.reload() }} /> : null}
      {dialog === 'pause' ? <SecurityActions guildId={guildId} incidents={incidents} onClose={() => setDialog(null)} onSaved={() => { setDialog(null); onChanged() }} /> : null}
    </>
  )
}

// Discord's Invite Link Settings: the channel, how long it lasts, how many uses, and temporary membership.
function CreateInvite({ guildId, channels, onClose, onCreated }: { guildId: string; channels: DiscordChannel[]; onClose: () => void; onCreated: () => void }) {
  const open = channels.filter(c => c.kind === 'text' || c.kind === 'voice')
  const [channelId, setChannelId] = useState(open[0]?.id ?? '')
  const [maxAge, setMaxAge] = useState('604800')
  const [maxUses, setMaxUses] = useState('0')
  const [temporary, setTemporary] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  async function create() {
    setBusy(true); setError(null)
    const result = await admin('createInvite', { guildId, channelId, maxAge: Number(maxAge), maxUses: Number(maxUses), temporary })
    setBusy(false)
    if (result.ok) onCreated(); else setError(result.message ?? 'Discord did not make that invite.')
  }
  return (
    <Confirm title="Invite link settings" action="Generate a new link" danger={false} form busy={busy} disabled={!channelId} error={error} onConfirm={() => void create()} onClose={onClose}>
      <div className="exo-dc-form-rows">
        <label><span>Channel</span><Choice label="Channel" value={channelId} choices={open.map(c => ({ value: c.id, label: c.kind === 'text' ? `#${c.name}` : c.name }))} onChange={setChannelId} /></label>
        <label><span>Expire after</span><Choice label="Expire after" value={maxAge} choices={EXPIRES} onChange={setMaxAge} /></label>
        <label><span>Max number of uses</span><Choice label="Max number of uses" value={maxUses} choices={USES} onChange={setMaxUses} /></label>
        <span className="exo-dc-form-switch">
          <span><strong>Grant temporary membership</strong><span>Temporary members are automatically kicked when they disconnect unless a role has been assigned</span></span>
          <Switch checked={temporary} label="Grant temporary membership" onChange={setTemporary} />
        </span>
      </div>
    </Confirm>
  )
}

// Discord's Security Actions: pause invites and DMs for a while (Discord's 1 to 24 hours), or resume them.
function SecurityActions({ guildId, incidents, onClose, onSaved }: { guildId: string; incidents: Incidents; onClose: () => void; onSaved: () => void }) {
  const [invites, setInvites] = useState(pausedNow(incidents?.invites_disabled_until) || !pausedNow(incidents?.dms_disabled_until))
  const [dms, setDms] = useState(pausedNow(incidents?.dms_disabled_until))
  const [hours, setHours] = useState('2')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  async function save() {
    setBusy(true); setError(null)
    const result = await admin('incidents', { guildId, invites, dms, hours: Number(hours) })
    setBusy(false)
    if (result.ok) onSaved(); else setError(result.message ?? 'Discord did not save that.')
  }
  return (
    <Confirm title="Security actions" action="Save" danger={false} form busy={busy} error={error} onConfirm={() => void save()} onClose={onClose}>
      <div className="exo-dc-form-rows">
        <label><span>Duration</span><Choice label="Select a time" value={hours} choices={HOURS} onChange={setHours} /></label>
        <span className="exo-dc-form-switch">
          <span><strong>Pause invites</strong><span>Temporarily stop new members from joining this server via invite or vanity links.</span></span>
          <Switch checked={invites} label="Pause invites" onChange={setInvites} />
        </span>
        <span className="exo-dc-form-switch">
          <span><strong>Pause DMs</strong><span>Temporarily stop new direct messages from being sent between members in your server. Friends can still DM each other, moderators can still DM members, and your apps can still DM members.</span></span>
          <Switch checked={dms} label="Pause DMs" onChange={setDms} />
        </span>
      </div>
    </Confirm>
  )
}

type Webhook = { id: string; type?: number; name: string; channel_id?: string | null; avatar?: string | null; user?: DiscordUser | null
  source_guild?: { name: string } | null; source_channel?: { name: string } | null }
type Integration = { id: string; name: string; type: string; enabled?: boolean; application?: { id: string; name: string; icon?: string | null; bot?: DiscordUser | null } | null; user?: DiscordUser | null }
type Linked = { webhooks?: Webhook[] | null; integrations?: Integration[] | null }
type Removing = { kind: 'webhook' | 'integration'; id: string; name: string }

const hookAvatar = (hook: Webhook) => hook.avatar ? `https://cdn.discordapp.com/avatars/${hook.id}/${hook.avatar}.webp?size=64` : null

/** Discord's Webhooks: New Webhook, and each one opening to its name, picture and channel, Copy Webhook URL and Delete. */
function Webhooks({ guildId, hooks, channels, onRemove, onChanged }: { guildId: string; hooks: Webhook[]; channels: DiscordChannel[]; onRemove: (item: Removing) => void; onChanged: () => void }) {
  const [open, setOpen] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const text = channels.filter(c => c.kind === 'text')
  const create = () => {
    if (!text[0]) return
    setError(null)
    void admin<Webhook>('createWebhook', { guildId, channelId: text[0].id }).then(result => {
      if (!result.ok) { setError(result.message ?? 'Error creating webhook'); return }
      onChanged(); if (result.data) setOpen(result.data.id)
    })
  }
  return (
    <section className="exo-dc-perm-group" aria-label="Webhooks">
      <div className="exo-dc-page-bar">
        <h3 className="exo-dc-grow">Webhooks</h3>
        <button type="button" className="exo-set-btn is-primary" disabled={!text.length} onClick={create}>New webhook</button>
      </div>
      {error ? <p className="exo-dc-note is-bad" role="alert">{error}</p> : null}
      {hooks.length ? null : <p className="exo-dc-note">No webhooks yet.</p>}
      <ul className="exo-dc-mod-list exo-dc-hook-list">
        {hooks.map(hook => (
          <li key={hook.id} className={open === hook.id ? 'is-open' : undefined}>
            <button type="button" className="exo-dc-hook-head" aria-expanded={open === hook.id} onClick={() => setOpen(o => o === hook.id ? null : hook.id)}>
              <Avatar src={hookAvatar(hook)} name={hook.name} status={null} size={32} />
              <span className="exo-dc-mod-name"><span>{hook.name}</span><small>{text.find(c => c.id === hook.channel_id) ? `#${text.find(c => c.id === hook.channel_id)?.name}` : ''}{hook.user ? ` · Created by ${personName(hook.user)}` : ''}</small></span>
            </button>
            {open === hook.id ? <WebhookEditor guildId={guildId} hook={hook} channels={text} onRemove={onRemove} onChanged={onChanged} /> : null}
          </li>
        ))}
      </ul>
    </section>
  )
}

function WebhookEditor({ guildId, hook, channels, onRemove, onChanged }: { guildId: string; hook: Webhook; channels: DiscordChannel[]; onRemove: (item: Removing) => void; onChanged: () => void }) {
  const [name, setName] = useState(hook.name)
  const [channelId, setChannelId] = useState(hook.channel_id ?? '')
  const [picture, setPicture] = useState<File | null | undefined>(undefined)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const dirty = name.trim() !== hook.name || channelId !== (hook.channel_id ?? '') || picture !== undefined
  async function save() {
    setBusy(true); setError(null)
    let uploadId: string | undefined
    if (picture) {
      const uploaded = await uploadFile(picture)
      if (typeof uploaded !== 'string') { setBusy(false); setError(uploaded.message); return }
      uploadId = uploaded
    }
    const result = await admin('editWebhook', { guildId, webhookId: hook.id, name: name.trim(), channelId, ...(uploadId ? { uploadId } : {}), removeAvatar: picture === null })
    setBusy(false)
    if (!result.ok) { dropUpload(uploadId); setError(result.message ?? 'Discord did not save that webhook.'); return }
    setPicture(undefined); onChanged()
  }
  const copy = () => void admin('copyWebhook', { guildId, webhookId: hook.id }).then(result => {
    if (result.ok) notify({ id: 'dc-copied', text: 'Webhook URL copied', tone: 'good' }); else setError(result.message ?? 'Discord didn\u2019t send the link.')
  })
  return (
    <div className="exo-dc-hook-editor">
      <label><span>Name</span><input className="exo-dc-modal-input" value={name} maxLength={80} aria-label="Webhook name" onChange={event => setName(event.target.value)} /></label>
      <label><span>Channel</span><Choice label="Channel" value={channelId} choices={channels.map(c => ({ value: c.id, label: `#${c.name}` }))} onChange={setChannelId} /></label>
      <span className="exo-dc-settings-buttons">
        <button type="button" className="exo-dc-tab is-raised" onClick={() => void pickFile('image/png,image/jpeg,image/gif,image/webp').then(file => { if (file) setPicture(file) })}>{picture ? 'Picture chosen' : 'Change avatar'}</button>
        {hook.avatar || picture ? <button type="button" className="exo-dc-tab" onClick={() => setPicture(null)}>Remove avatar</button> : null}
        <button type="button" className="exo-dc-tab is-raised" onClick={copy}>Copy webhook URL</button>
        <button type="button" className="exo-dc-tab is-raised is-danger" onClick={() => onRemove({ kind: 'webhook', id: hook.id, name: hook.name })}>Delete webhook</button>
        {dirty ? <button type="button" className="exo-set-btn is-primary" disabled={busy || !name.trim()} onClick={() => void save()}>Save changes</button> : null}
      </span>
      {error ? <p className="exo-dc-note is-bad" role="alert">{error}</p> : null}
    </div>
  )
}

// Channels Followed: announcement channels from other servers posting here (Discord's follower webhooks), each unfollowed by removing it.
function Followed({ hooks, onRemove }: { hooks: Webhook[]; onRemove: (item: Removing) => void }) {
  if (!hooks.length) return null
  return (
    <section className="exo-dc-perm-group" aria-label="Channels followed">
      <h3>Channels followed</h3>
      <ul className="exo-dc-mod-list">
        {hooks.map(hook => (
          <li key={hook.id}>
            <Avatar src={hookAvatar(hook)} name={hook.name} status={null} size={32} />
            <span className="exo-dc-mod-name"><span>{hook.source_guild?.name ?? hook.name}</span><small>{hook.source_channel ? `#${hook.source_channel.name}` : ''}</small></span>
            <button type="button" className="exo-dc-tab is-raised is-danger" onClick={() => onRemove({ kind: 'webhook', id: hook.id, name: hook.source_guild?.name ?? hook.name })}>Unfollow</button>
          </li>
        ))}
      </ul>
    </section>
  )
}

function Apps({ items, onRemove }: { items: Integration[]; onRemove: (item: Removing) => void }) {
  return (
    <section className="exo-dc-perm-group" aria-label="Apps">
      <h3>Bots and apps</h3>
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
      {!linked.data && !linked.error ? <Skeleton label="Loading integrations" /> : null}
      {moderation.manageWebhooks && linked.data ? (
        <>
          <Webhooks guildId={guildId} hooks={(linked.data.webhooks ?? []).filter(h => h.type !== 2)} channels={channels} onRemove={ask} onChanged={linked.reload} />
          <Followed hooks={(linked.data.webhooks ?? []).filter(h => h.type === 2)} onRemove={ask} />
        </>
      ) : null}
      {moderation.manageGuild && linked.data ? <Apps items={[...apps, ...others]} onRemove={ask} /> : null}
      {removing ? (
        <Confirm title={`${removing.kind === 'webhook' ? 'Delete' : 'Remove'} ${removing.name}?`} action={removing.kind === 'webhook' ? 'Delete' : 'Remove'} error={error} busy={busy}
          onConfirm={remove} onClose={() => { setRemoving(null); setError(null) }}>
          <p>{removing.kind === 'webhook' ? 'This action cannot be undone.' : `${removing.name} will be removed from this server, with its role and commands.`}</p>
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
  1: ['updated the server', 'Update server'], 10: ['created a channel', 'Create channel'], 11: ['updated a channel', 'Update channel'], 12: ['deleted a channel', 'Delete channel'],
  13: ['created a permission override', 'Create channel permissions'], 14: ['updated a permission override', 'Update channel permissions'], 15: ['deleted a permission override', 'Delete channel permissions'],
  20: ['kicked', 'Kick member'], 21: ['pruned members', 'Prune members'], 22: ['banned', 'Ban member'], 23: ['unbanned', 'Unban member'], 24: ['updated', 'Update member'],
  25: ['updated roles for', 'Update member roles'], 26: ['moved a member', 'Move member'], 27: ['disconnected a member', 'Disconnect member'], 28: ['added a bot', 'Add bot'],
  30: ['created a role', 'Create role'], 31: ['updated a role', 'Update role'], 32: ['deleted a role', 'Delete role'],
  40: ['created an invite', 'Create invite'], 41: ['updated an invite', 'Update invite'], 42: ['deleted an invite', 'Delete invite'],
  50: ['created a webhook', 'Create webhook'], 51: ['updated a webhook', 'Update webhook'], 52: ['deleted a webhook', 'Delete webhook'],
  60: ['created an emoji', 'Create emoji'], 61: ['updated an emoji', 'Update emoji'], 62: ['deleted an emoji', 'Delete emoji'],
  72: ['deleted a message by', 'Delete message'], 73: ['deleted messages', 'Bulk delete messages'], 74: ['pinned a message by', 'Pin message'], 75: ['unpinned a message by', 'Unpin message'],
  80: ['added an integration', 'Create integration'], 81: ['updated an integration', 'Update integration'], 82: ['removed an integration', 'Delete integration'],
  90: ['created a sticker', 'Create sticker'], 91: ['updated a sticker', 'Update sticker'], 92: ['deleted a sticker', 'Delete sticker'],
  100: ['created an event', 'Create event'], 101: ['updated an event', 'Update event'], 102: ['deleted an event', 'Delete event'],
  110: ['created a thread', 'Create thread'], 111: ['updated a thread', 'Update thread'], 112: ['deleted a thread', 'Delete thread'],
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
      <h1>Audit log</h1>
      <div className="exo-dc-page-bar">
        <Choice label="Filter by user" value={userId || 'all'} choices={[{ value: 'all', label: 'All users' }, ...[...users.values()].map(u => ({ value: u.id, label: personName(u) }))]}
          onChange={id => setUserId(id === 'all' ? '' : id)} />
        <Choice label="Filter by action" value={action} choices={[{ value: '0', label: 'All actions' }, ...Object.entries(ACTIONS).map(([code, [, label]]) => ({ value: code, label }))]}
          onChange={setAction} />
      </div>
      <ReadError error={audit.error} onRetry={audit.reload} />
      {!audit.data && !audit.error ? <Skeleton label="Loading the audit log" rows={6} avatar /> : null}
      {audit.data && entries.length === 0 ? <p className="exo-dc-note">Nothing here yet.</p> : null}
      <ul className="exo-dc-mod-list exo-dc-audit">
        {entries.map(entry => <AuditRow key={entry.id} entry={entry} users={users} />)}
      </ul>
      {audit.data && entries.length >= 50 && !more.done ? <button type="button" className="exo-dc-tab is-raised" disabled={loading} onClick={loadMore}>
        {loading ? <Spinner label="Loading more entries" /> : null}Load more
      </button> : null}
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
      {!bans.data && !bans.error ? <Skeleton label="Loading bans" avatar /> : null}
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
  return (
    <Dialog title={personName(ban.user)} form portal={false} onClose={onClose} actions={<>
      <button type="button" className="exo-set-btn is-danger" disabled={busy} onClick={onRevoke}>Revoke ban</button>
      <button type="button" className="exo-set-btn is-primary" data-autofocus onClick={onClose}>Done</button>
    </>}>
      <p className="exo-dc-side-label">Ban reason</p>
      <p>{ban.reason || 'No reason given.'}</p>
      {error ? <p className="exo-dialog-error" role="alert">{error}</p> : null}
    </Dialog>
  )
}
