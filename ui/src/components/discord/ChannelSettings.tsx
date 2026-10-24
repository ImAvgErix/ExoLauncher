import { useEffect, useRef, useState } from 'react'
import { Close, Plus } from '../../brand/icons'
import { host, onHostEvent } from '../../lib/host'
import { useGlide } from '../../lib/glide'
import { avatarUrl, type DiscordUser } from '../../lib/discord'
import { channelGroups, triOf, withTri, type Tri } from '../../lib/discordPermissions'
import { Switch } from '../Switch'
import { Choice } from '../settings/Choice'
import { Slider } from '../ui/Slider'
import { admin, Confirm, Field, personName, ReadError, SaveBar, UnsavedProvider, useAdmin, useLayer, useUnsaved } from './admin'
import { Icons } from './icons'
import { SettingsLayer } from './SettingsLayer'
import { Avatar } from './presence'
import { pushPage } from '../../lib/viewTransition'
import { useDismiss } from '../../lib/dismiss'

type Overwrite = { id: string; type: number; allow: string; deny: string }
type Settings = {
  id: string; guildId: string; name: string; type: number; topic: string | null; nsfw: boolean; rateLimit: number; userLimit: number; bitrate: number
  canManage: boolean; canPermissions: boolean; maxBitrate: number; overwrites: Overwrite[]
  roles: { id: string; name: string; color: string | null }[]; members: { id: string; name: string; avatar: string | null }[]
}
type Section = 'overview' | 'permissions'

const SLOWMODE = [[0, 'Off'], [5, '5s'], [10, '10s'], [15, '15s'], [30, '30s'], [60, '1m'], [120, '2m'], [300, '5m'], [600, '10m'], [900, '15m'],
  [1800, '30m'], [3600, '1h'], [7200, '2h'], [21600, '6h']].map(([value, label]) => ({ value: String(value), label: String(label) }))
const isVoice = (type: number) => type === 2 || type === 13

/**
 * Discord's Edit Channel: Overview (name, topic, slowmode, age-restricted, a voice channel's bitrate and user limit), Permissions, Delete Channel.
 * It opens on the first section the user may change (Permissions with Manage Permissions alone); unsaved changes hold the page.
 */
export function ChannelSettings({ channelId, onClose }: { channelId: string; onClose: () => void }) {
  const settings = useAdmin<Settings>('channelSettings', { channelId })
  const [picked, setPicked] = useState<Section | null>(null)
  const [deleting, setDeleting] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const glide = useGlide<HTMLUListElement>()
  const unsaved = useUnsaved()
  const close = () => unsaved.guard(onClose)
  const box = useRef<HTMLDivElement>(null)
  useLayer(box, close)
  const data = settings.data
  const sections = (['overview', 'permissions'] as const).filter(s => s === 'overview' ? data?.canManage !== false : data?.canPermissions !== false)
  const section = picked && sections.includes(picked) ? picked : sections[0] ?? 'overview'
  // Discord confirms a save with the channel's update on the gateway: read the settings again then.
  const guildId = data?.guildId
  const reload = settings.reload
  useEffect(() => onHostEvent('discord.changed', event => { if (guildId && event.guildId === guildId) reload() }), [guildId, reload])
  const remove = () => void host.discordChannel(channelId, 'delete').then(result => {
    if (result.ok) onClose(); else setError(result.message ?? 'Discord did not delete that channel.')
  }).catch(() => setError('Discord did not delete that channel.'))
  return (
    <UnsavedProvider value={unsaved}>
    <SettingsLayer onClose={close}>
    <div ref={box} className="exo-dc-settings" role="dialog" aria-modal="true" aria-label="Edit channel" tabIndex={-1}>
      <span className="exo-glass is-panel exo-sheet-glass" aria-hidden="true" />
      <nav className="exo-dc-settings-nav" aria-label="Channel settings sections">
        <h2>{data ? `${isVoice(data.type) ? '' : '#'}${data.name}` : 'Channel'}</h2>
        <ul ref={glide} className="exo-glide">
          {sections.map(s => (
            <li key={s}>
              <button type="button" aria-current={section === s ? 'true' : undefined} className={section === s ? 'is-current' : undefined} onClick={() => unsaved.guard(() => void pushPage(() => setPicked(s), '.exo-dc-settings-page', '.exo-dc-settings-nav [aria-current="true"]'))}>
                {s === 'overview' ? 'Overview' : 'Permissions'}
              </button>
            </li>
          ))}
          {data?.canManage ? <li><button type="button" className="is-danger" onClick={() => setDeleting(true)}>Delete channel</button></li> : null}
        </ul>
      </nav>
      <div className="exo-dc-settings-body">
        <button type="button" className="exo-game-close exo-dc-settings-close" aria-label="Close channel settings" onClick={close}><Close size={16} /></button>
        <div key={section} className="exo-dc-settings-page exo-motion-switch">
          <ReadError error={settings.error} onRetry={settings.reload} />
          {!data && !settings.error ? <p className="exo-dc-note">Loading</p> : null}
          {data && section === 'overview' ? <Overview settings={data} onSaved={settings.reload} /> : null}
          {data && section === 'permissions' ? <Permissions settings={data} onSaved={settings.reload} /> : null}
        </div>
      </div>
      {deleting && data ? (
        <Confirm title="Delete channel" action="Delete channel" error={error} onConfirm={remove} onClose={() => setDeleting(false)}>
          <p>Are you sure you want to delete {isVoice(data.type) ? data.name : `#${data.name}`}? This cannot be undone.</p>
        </Confirm>
      ) : null}
    </div>
    </SettingsLayer>
    </UnsavedProvider>
  )
}

type Draft = Partial<{ name: string; topic: string | null; rate_limit_per_user: number; nsfw: boolean; bitrate: number; user_limit: number }>

function Overview({ settings, onSaved }: { settings: Settings; onSaved: () => void }) {
  const [draft, setDraft] = useState<Draft>({})
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const voice = isVoice(settings.type)
  // A category has only its name here (its permissions are their own page), as Discord's Edit Category.
  const category = settings.type === 4
  const value = {
    name: draft.name ?? settings.name, topic: 'topic' in draft ? draft.topic : settings.topic, slow: draft.rate_limit_per_user ?? settings.rateLimit,
    nsfw: draft.nsfw ?? settings.nsfw, bitrate: draft.bitrate ?? settings.bitrate, limit: draft.user_limit ?? settings.userLimit,
  }
  const set = (change: Draft) => { setError(null); setDraft(d => ({ ...d, ...change })) }
  async function save() {
    setBusy(true)
    const result = await admin('channelSave', { channelId: settings.id, changes: draft })
    setBusy(false)
    if (!result.ok) { setError(result.message ?? 'Discord did not save that.'); return }
    setDraft({}); onSaved()
  }
  return (
    <>
      <h1>Overview</h1>
      <Field label={category ? 'Category name' : 'Channel name'}>
        <input className="exo-dc-modal-input exo-dc-field" value={value.name} maxLength={100} aria-label={category ? 'Category name' : 'Channel name'} onChange={event => set({ name: event.target.value })} />
      </Field>
      {category ? null : <ChannelFields value={value} voice={voice} maxBitrate={settings.maxBitrate} set={set} />}
      <SaveBar shown={Object.keys(draft).length > 0} busy={busy} error={error} onReset={() => { setDraft({}); setError(null) }} onSave={() => void save()} />
    </>
  )
}

// A channel's own settings below its name: topic (not a voice channel's), slowmode, age-restricted, a voice channel's bitrate and user limit.
function ChannelFields({ value, voice, maxBitrate, set }: {
  value: { topic?: string | null; slow: number; nsfw: boolean; bitrate: number; limit: number }
  voice: boolean
  maxBitrate: number
  set: (change: Draft) => void
}) {
  return (
    <>
      {voice ? null : (
        <Field label="Channel topic" detail="Let everyone know how to use this channel." stacked>
          <textarea className="exo-dc-modal-input exo-dc-field is-wide" rows={3} maxLength={1024} value={value.topic ?? ''} aria-label="Channel topic"
            onChange={event => set({ topic: event.target.value || null })} />
        </Field>
      )}
      <Field label="Slowmode" detail="Members will be restricted to sending one message and creating one thread per this interval, unless they have bypass slowmode, manage channel or manage messages permissions.">
        <Choice label="Slowmode" value={String(value.slow)} choices={SLOWMODE} onChange={seconds => set({ rate_limit_per_user: Number(seconds) })} />
      </Field>
      <Field label="Age-restricted channel" detail="Users will need to confirm they are of over legal age to view the content in this channel. Age-restricted channels are exempt from the explicit content filter.">
        <Switch checked={value.nsfw} label="Age-restricted channel" onChange={nsfw => set({ nsfw })} />
      </Field>
      {voice ? (
        <>
          <Field label="Bitrate" detail={`${Math.round(value.bitrate / 1000)} kbps. Going above 64 kbps may adversely affect people on poor connections.`}>
            <Slider className="exo-dc-range" min={8000} max={maxBitrate} step={1000} value={value.bitrate} aria-label="Bitrate"
              onChange={event => set({ bitrate: Number(event.target.value) })} />
          </Field>
          <Field label="User limit" detail={value.limit ? `${value.limit} users. Limits the number of users that can connect to this voice channel.` : 'No limit. Limits the number of users that can connect to this voice channel.'}>
            <Slider className="exo-dc-range" min={0} max={99} value={value.limit} aria-label="User limit"
              onChange={event => set({ user_limit: Number(event.target.value) })} />
          </Field>
        </>
      ) : null}
    </>
  )
}

type Target = { id: string; type: number; name: string; color?: string | null; avatar?: string | null }

// Discord's advanced permissions: a role or member on the left, each permission allowed, left to roles, or denied.
function Permissions({ settings, onSaved }: { settings: Settings; onSaved: () => void }) {
  const everyone = settings.guildId
  const [edits, setEdits] = useState<Map<string, Overwrite | null>>(new Map())
  const [picked, setPicked] = useState(everyone)
  const [adding, setAdding] = useState(false)
  // Members added here, by name and picture until Discord confirms the save.
  const [added, setAdded] = useState<Map<string, Target>>(new Map())
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const current = new Map(settings.overwrites.map(o => [o.id, o]))
  for (const [id, o] of edits) { if (o) current.set(id, o); else current.delete(id) }
  const targets: Target[] = [
    ...settings.roles.filter(r => r.id === everyone || current.has(r.id)).map(r => ({ id: r.id, type: 0, name: r.name, color: r.color })),
    ...[...current.values()].filter(o => o.type === 1).map(o => {
      const known = settings.members.find(m => m.id === o.id) ?? added.get(o.id)
      return { id: o.id, type: 1, name: known?.name ?? 'Member', avatar: known?.avatar }
    }),
  ]
  const target = targets.find(t => t.id === picked) ?? targets[0]
  const overwrite = current.get(target.id) ?? { id: target.id, type: target.type, allow: '0', deny: '0' }
  const setState = (n: number, state: Tri) => setEdits(map => new Map(map).set(target.id, { ...overwrite, ...withTri(overwrite.allow, overwrite.deny, n, state) }))
  async function save() {
    setBusy(true)
    for (const [id, o] of edits) {
      const result = o
        ? await admin('channelOverwrite', { channelId: settings.id, targetId: id, type: o.type, allow: o.allow, deny: o.deny })
        : await admin('channelClearOverwrite', { channelId: settings.id, targetId: id })
      if (!result.ok) { setError(result.message ?? 'Discord did not save those permissions.'); setBusy(false); return }
    }
    setBusy(false); setEdits(new Map()); onSaved()
  }
  const add = (next: Target) => {
    setAdding(false)
    if (next.type === 1) setAdded(map => new Map(map).set(next.id, next))
    setEdits(map => new Map(map).set(next.id, { id: next.id, type: next.type, allow: '0', deny: '0' }))
    setPicked(next.id)
  }
  return (
    <div className="exo-dc-role-editor">
      <aside className="exo-dc-role-side" aria-label="Roles and members">
        <p className="exo-dc-side-label is-row">Roles/Members
          <button type="button" className="exo-dc-label-btn" aria-label="Add members or roles" aria-expanded={adding} onClick={() => setAdding(a => !a)}><Plus size={14} /></button>
        </p>
        {adding ? <AddTarget settings={settings} taken={current} onAdd={add} onClose={() => setAdding(false)} /> : null}
        {targets.map(t => (
          <button key={t.id} type="button" aria-current={t.id === target.id} className={`exo-dc-role-pick${t.id === target.id ? ' is-current' : ''}`} onClick={() => setPicked(t.id)}>
            {t.type === 1 ? <Avatar src={t.avatar} name={t.name} status={null} size={20} /> : <span className="exo-dc-role-dot" style={{ background: t.color ?? 'var(--dc-muted)' }} aria-hidden="true" />}
            <span>{t.name}</span>
          </button>
        ))}
      </aside>
      <section className="exo-dc-role-main" aria-label={`Permissions for ${target.name}`}>
        <h1>Permissions — {target.name}</h1>
        {target.id !== everyone ? (
          <button type="button" className="exo-dc-tab is-danger" onClick={() => { setEdits(map => new Map(map).set(target.id, null)); setPicked(everyone) }}>Remove {target.type === 1 ? 'member' : 'role'}</button>
        ) : null}
        {channelGroups(isVoice(settings.type)).map(group => (
          <section key={group.title} className="exo-dc-perm-group" aria-label={group.title}>
            <h3>{group.title}</h3>
            {group.items.map(item => (
              <Field key={item.bit} label={item.title} detail={item.description}>
                <TriState label={item.title} state={triOf(overwrite.allow, overwrite.deny, item.bit)} onChange={state => setState(item.bit, state)} />
              </Field>
            ))}
          </section>
        ))}
      </section>
      <SaveBar shown={edits.size > 0} busy={busy} error={error} onReset={() => { setEdits(new Map()); setError(null) }} onSave={() => void save()} />
    </div>
  )
}

/** Deny, neutral (the roles decide) or allow, as Discord's red cross, gray slash and green check. */
function TriState({ label, state, onChange }: { label: string; state: Tri; onChange: (state: Tri) => void }) {
  const options: { value: Tri; name: string; icon: React.ReactNode }[] = [
    { value: 'deny', name: 'Deny', icon: <Close size={14} /> },
    { value: 'neutral', name: 'Neutral', icon: <span aria-hidden="true">/</span> },
    { value: 'allow', name: 'Allow', icon: <Icons.Check size={14} /> },
  ]
  return (
    <span className="exo-dc-tri" role="radiogroup" aria-label={label}>
      {options.map(option => (
        <button key={option.value} type="button" role="radio" aria-checked={state === option.value} aria-label={`${option.name} ${label}`}
          className={`is-${option.value}${state === option.value ? ' is-on' : ''}`} onClick={() => onChange(option.value)}>{option.icon}</button>
      ))}
    </span>
  )
}

type Member = { member: { user: DiscordUser; nick?: string | null } }

function AddTarget({ settings, taken, onAdd, onClose }: { settings: Settings; taken: Map<string, Overwrite>; onAdd: (target: Target) => void; onClose: () => void }) {
  const [query, setQuery] = useState('')
  const [members, setMembers] = useState<Member[]>([])
  const box = useRef<HTMLDivElement>(null)
  useLayer(box, onClose)
  useDismiss(box, onClose)
  const q = query.trim().toLowerCase()
  const roles = settings.roles.filter(r => r.id !== settings.guildId && !taken.has(r.id) && (!q || r.name.toLowerCase().includes(q)))
  // Member search with this channel's Manage Permissions, as Discord allows it.
  const search = (text: string) => {
    setQuery(text)
    if (text.trim().length < 2) { setMembers([]); return }
    void admin<{ members: Member[] }>('searchMembers', { guildId: settings.guildId, channelId: settings.id, query: text.trim() }).then(result => setMembers(result.data?.members ?? []))
  }
  return (
    <div ref={box} className="exo-dc-menu is-start" role="dialog" aria-label="Add members or roles">
      <input className="exo-dc-find" placeholder="Add members or roles" value={query} autoFocus aria-label="Add members or roles" onChange={event => search(event.target.value)} />
      <div className="exo-dc-start-list">
        {roles.slice(0, 30).map(role => (
          <button key={role.id} type="button" onClick={() => onAdd({ id: role.id, type: 0, name: role.name, color: role.color })}>
            <span className="exo-dc-role-dot" style={{ background: role.color ?? 'var(--dc-muted)' }} aria-hidden="true" /><span>{role.name}</span>
          </button>
        ))}
        {members.filter(m => !taken.has(m.member.user.id)).map(({ member }) => (
          <button key={member.user.id} type="button" onClick={() => onAdd({ id: member.user.id, type: 1, name: personName(member.user, member.nick), avatar: avatarUrl(member.user, 32) })}>
            <Avatar src={avatarUrl(member.user, 24)} name={member.user.username} status={null} size={20} /><span>{personName(member.user, member.nick)}</span>
          </button>
        ))}
      </div>
    </div>
  )
}
