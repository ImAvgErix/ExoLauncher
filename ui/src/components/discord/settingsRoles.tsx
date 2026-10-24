import { useEffect, useRef, useState } from 'react'
import { ChevronLeft, Close } from '../../brand/icons'
import { avatarUrl, type DiscordUser } from '../../lib/discord'
import { hasPermission, ROLE_GROUPS, withPermission } from '../../lib/discordPermissions'
import { Switch } from '../Switch'
import { admin, colorOf, Confirm, Field, personName, ReadError, SaveBar, useAdmin, useGuard } from './admin'
import { Icons } from './icons'
import { Avatar } from './presence'
import { RoleLinks, type RoleLink } from './settingsRoleLinks'

type Role = { id: string; name: string; color: number; position: number; hoist: boolean; mentionable: boolean; managed: boolean; permissions: string }
type RolesData = { roles: Role[]; counts: Record<string, number> | null; top: number }
type Draft = Partial<Pick<Role, 'name' | 'color' | 'hoist' | 'mentionable' | 'permissions'>>
type Member = { member: { user: DiscordUser; nick?: string | null; roles: string[] } }
type MemberSearch = { members: Member[]; total_result_count?: number }

// Discord's role color swatches.
const SWATCHES = [0x1abc9c, 0x2ecc71, 0x3498db, 0x9b59b6, 0xe91e63, 0xf1c40f, 0xe67e22, 0xe74c3c, 0x95a5a6, 0x607d8b,
  0x11806a, 0x1f8b4c, 0x206694, 0x71368a, 0xad1457, 0xc27c0e, 0xa84300, 0x992d22, 0x979c9f, 0x546e7a]

/** Discord's Roles: every role in order (drag to reorder), @everyone's default permissions, and the role editor. */
export function Roles({ guildId }: { guildId: string }) {
  const roles = useAdmin<RolesData>('roles', { guildId })
  const [open, setOpen] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [creating, setCreating] = useState(false)
  if (!roles.data) return <><h1>Roles</h1>{roles.error ? <ReadError error={roles.error} onRetry={roles.reload} /> : <p className="exo-dc-note">Loading</p>}</>
  const data = roles.data
  const sorted = [...data.roles].sort((a, b) => b.position - a.position)
  const role = open ? sorted.find(r => r.id === open) : undefined
  async function create() {
    if (creating) return
    setError(null); setCreating(true)
    const result = await admin<Role>('createRole', { guildId })
    setCreating(false)
    if (!result.ok || !result.data) { setError(result.message ?? 'Discord did not make that role.'); return }
    roles.reload()
    setOpen(result.data.id)
  }
  if (role) {
    return <RoleEditor key={role.id} guildId={guildId} role={role} roles={sorted} data={data} onPick={setOpen} onBack={() => setOpen(null)}
      onSaved={roles.reload} onDeleted={() => { setOpen(null); roles.reload() }} />
  }
  return (
    <>
      <RoleList guildId={guildId} sorted={sorted} data={data} error={error} creating={creating} onCreate={() => void create()} onOpen={setOpen}
        onMoved={next => roles.set({ ...data, roles: next })} onFailed={roles.reload} />
      <ReadError error={roles.error} onRetry={roles.reload} />
    </>
  )
}

function RoleList({ guildId, sorted, data, error, creating, onCreate, onOpen, onMoved, onFailed }: {
  guildId: string
  sorted: Role[]
  data: RolesData
  error: string | null
  creating: boolean
  onCreate: () => void
  onOpen: (id: string) => void
  onMoved: (roles: Role[]) => void
  onFailed: () => void
}) {
  const [query, setQuery] = useState('')
  const [dragging, setDragging] = useState<string | null>(null)
  const [over, setOver] = useState<string | null>(null)
  // A move Discord refused, said under the role that moved.
  const [failed, setFailed] = useState<{ id: string; message: string } | null>(null)
  const everyone = sorted.find(r => r.id === guildId)
  const others = sorted.filter(r => r.id !== guildId)
  const q = query.trim().toLowerCase()
  const movable = (role: Role) => role.position < data.top
  // The moved role takes the place of the one at "to"; every role below the top gets its new position, as Discord saves a drag.
  function move(id: string, to: number) {
    const from = others.findIndex(r => r.id === id)
    if (from < 0 || to < 0 || to >= others.length || from === to || !movable(others[to])) return
    const order = [...others]
    order.splice(to, 0, ...order.splice(from, 1))
    const positions = order.map((r, i) => ({ id: r.id, position: order.length - i }))
    setFailed(null)
    onMoved([...order.map((r, i) => ({ ...r, position: positions[i].position })), ...(everyone ? [everyone] : [])])
    void admin('moveRoles', { guildId, positions }).then(result => {
      if (!result.ok) { setFailed({ id, message: result.message ?? 'Discord did not move that role.' }); onFailed() }
    })
  }
  function drop(target: Role) {
    const id = dragging
    setDragging(null); setOver(null)
    if (id) move(id, others.findIndex(r => r.id === target.id))
  }
  return (
    <>
      <h1>Roles</h1>
      <p className="exo-dc-note">Use roles to group your server members and assign permissions.</p>
      {everyone ? (
        <button type="button" className="exo-dc-card exo-dc-everyone" onClick={() => onOpen(everyone.id)}>
          <Icons.Members size={20} />
          <span className="exo-dc-friend-names"><strong>Default permissions</strong><span>@everyone · applies to all server members</span></span>
          <ChevronLeft size={16} className="exo-dc-flip" />
        </button>
      ) : null}
      <div className="exo-dc-page-bar">
        <label className="exo-dc-friend-search exo-dc-grow">
          <Icons.Search size={16} />
          <input placeholder="Search roles" value={query} onChange={event => setQuery(event.target.value)} aria-label="Search roles" />
        </label>
        <button type="button" className="exo-set-btn is-primary" disabled={creating} onClick={onCreate}>Create role</button>
      </div>
      {error ? <p className="exo-dc-note is-bad" role="alert">{error}</p> : null}
      <p className="exo-dc-note">Members use the color of the highest role they have on this list. Drag roles to reorder them, or focus a handle and use the arrow keys.</p>
      {/* Discord's table: Roles with their count, and Members; a row opens its role. */}
      <div className="exo-dc-role-head" aria-hidden="true"><span>Roles — {others.length}</span><span>Members</span></div>
      <ul className="exo-dc-role-list">
        {others.filter(r => !q || r.name.toLowerCase().includes(q)).map(role => (
          <li key={role.id} draggable={!q && movable(role)} className={[over === role.id ? 'is-over' : '', failed?.id === role.id ? 'has-error' : ''].join(' ').trim() || undefined}
            onDragStart={() => setDragging(role.id)} onDragEnd={() => { setDragging(null); setOver(null) }}
            onDragOver={event => { if (dragging && movable(role)) { event.preventDefault(); setOver(role.id) } }}
            onDrop={event => { event.preventDefault(); drop(role) }}>
            {!q && movable(role) ? (
              <button type="button" className="exo-dc-grip" aria-label={`Move ${role.name}, up or down arrow`}
                onKeyDown={event => {
                  const step = event.key === 'ArrowUp' ? -1 : event.key === 'ArrowDown' ? 1 : 0
                  if (!step) return
                  event.preventDefault()
                  move(role.id, others.findIndex(r => r.id === role.id) + step)
                }}><Icons.Grip size={14} /></button>
            ) : <span className="exo-dc-grip" aria-hidden="true" />}
            <button type="button" className="exo-dc-role-open" onClick={() => onOpen(role.id)}>
              <span className="exo-dc-role-dot" style={{ background: colorOf(role.color) }} aria-hidden="true" />
              <span className="exo-dc-role-name">{role.name}</span>
              <span className="exo-dc-role-count"><Icons.Members size={14} /> {data.counts?.[role.id] ?? 0}</span>
            </button>
            <button type="button" className="exo-dc-icon-btn exo-dc-role-edit" aria-label={`Edit ${role.name}`} onClick={() => onOpen(role.id)}><Icons.Edit size={16} /></button>
            {failed?.id === role.id ? <span className="exo-dc-field-error exo-dc-row-error" role="alert">{failed.message}</span> : null}
          </li>
        ))}
      </ul>
    </>
  )
}

type Tab = 'display' | 'permissions' | 'links' | 'members'

function RoleEditor({ guildId, role, roles, data, onPick, onBack, onSaved, onDeleted }: {
  guildId: string
  role: Role
  roles: Role[]
  data: RolesData
  onPick: (id: string) => void
  onBack: () => void
  onSaved: () => void
  onDeleted: () => void
}) {
  const everyone = role.id === guildId
  const [tab, setTab] = useState<Tab>(everyone ? 'permissions' : 'display')
  const [draft, setDraft] = useState<Draft>({})
  // The Links tab's unsaved requirements (null while they're Discord's); saved brings the tab back to Discord's.
  const [links, setLinks] = useState<RoleLink[][] | null>(null)
  const [linksSaved, setLinksSaved] = useState(0)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const guard = useGuard()
  // An app's or Server Booster's role edits too (color, display, permissions), as in Discord; only its name and members are Discord's.
  const editable = everyone || role.position < data.top
  const value = { ...role, ...draft }
  const dirty = Object.keys(draft).length > 0 || links !== null
  const change = (next: Draft) => { setError(null); setDraft(d => ({ ...d, ...next })) }
  async function save() {
    setBusy(true)
    if (Object.keys(draft).length) {
      const result = await admin('saveRole', { guildId, roleId: role.id, changes: draft })
      if (!result.ok) { setBusy(false); setError(result.message ?? 'Discord did not save that role.'); return }
      setDraft({}); onSaved()
    }
    if (links) {
      const result = await admin('saveRoleLinks', { guildId, roleId: role.id, groups: links })
      if (!result.ok) { setBusy(false); setError(result.message ?? 'Discord did not save those requirements.'); return }
      setLinks(null); setLinksSaved(n => n + 1)
    }
    setBusy(false)
  }
  const tabs: { id: Tab; label: string }[] = everyone ? [{ id: 'permissions', label: 'Permissions' }]
    : [{ id: 'display', label: 'Display' }, { id: 'permissions', label: 'Permissions' }, { id: 'links', label: 'Links' }, { id: 'members', label: `Manage members (${data.counts?.[role.id] ?? 0})` }]
  return (
    <div className="exo-dc-role-editor">
      <aside className="exo-dc-role-side" aria-label="Roles">
        <button type="button" className="exo-dc-tab" onClick={() => guard(onBack)}><ChevronLeft size={14} />Back</button>
        {roles.map(r => (
          <button key={r.id} type="button" aria-current={r.id === role.id} className={`exo-dc-role-pick${r.id === role.id ? ' is-current' : ''}`}
            onClick={() => guard(() => onPick(r.id))}>
            <span className="exo-dc-role-dot" style={{ background: colorOf(r.color) }} aria-hidden="true" />
            <span>{r.id === guildId ? '@everyone' : r.name}</span>
          </button>
        ))}
      </aside>
      <section className="exo-dc-role-main" aria-label={everyone ? 'Default permissions' : `Edit role ${role.name}`}>
        <h1>{everyone ? 'Default permissions' : `Edit role — ${value.name}`}</h1>
        {everyone ? <p className="exo-dc-note">This will edit the default permissions for all members of this server.</p> : null}
        {!editable ? <p className="exo-dc-note">This role is above your highest role, so you can't change it.</p> : null}
        <div className="exo-dc-full-tabs" role="tablist">
          {tabs.map(t => <button key={t.id} type="button" role="tab" aria-selected={tab === t.id} onClick={() => setTab(t.id)}>{t.label}</button>)}
        </div>
        {tab === 'display' ? <RoleDisplay role={role} value={value} editable={editable} change={change} guildId={guildId} onDeleted={onDeleted} /> : null}
        {tab === 'permissions' ? <RolePermissions permissions={value.permissions} editable={editable} onChange={permissions => change({ permissions })} /> : null}
        {tab === 'links' ? <RoleLinks key={linksSaved} guildId={guildId} roleId={role.id} members={data.counts?.[role.id] ?? 0} editable={editable} draft={links}
          onChange={groups => { setError(null); setLinks(groups) }} /> : null}
        {tab === 'members' ? <RoleMembers guildId={guildId} role={role} editable={editable && !role.managed} onChanged={onSaved} /> : null}
      </section>
      <SaveBar shown={dirty} busy={busy} error={error} onReset={() => { setDraft({}); setLinks(null); setError(null) }} onSave={() => void save()} />
    </div>
  )
}

function RoleDisplay({ role, value, editable, change, guildId, onDeleted }: {
  role: Role
  value: Role
  editable: boolean
  change: (next: Draft) => void
  guildId: string
  onDeleted: () => void
}) {
  const [confirm, setConfirm] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const remove = () => {
    setBusy(true)
    void admin('deleteRole', { guildId, roleId: role.id }).then(result => {
      setBusy(false)
      if (result.ok) onDeleted(); else setError(result.message ?? 'Discord did not delete that role.')
    })
  }
  return (
    <>
      <Field label="Role name">
        <input className="exo-dc-modal-input exo-dc-field" value={value.name} maxLength={100} disabled={!editable || role.managed} aria-label="Role name"
          onChange={event => change({ name: event.target.value })} />
      </Field>
      <Field label="Role color" detail="Members use the color of the highest role they have on the roles list." stacked>
        <div className="exo-dc-swatches" role="radiogroup" aria-label="Role color">
          <button type="button" role="radio" aria-checked={value.color === 0} aria-label="Default" className="exo-dc-swatch is-default" disabled={!editable}
            style={{ background: colorOf(0) }} onClick={() => change({ color: 0 })} />
          <label className="exo-dc-swatch is-custom" style={{ background: value.color && !SWATCHES.includes(value.color) ? colorOf(value.color) : undefined }}>
            <input type="color" aria-label="Custom color" disabled={!editable} value={colorOf(value.color || 0x99aab5)}
              onChange={event => change({ color: parseInt(event.target.value.slice(1), 16) })} />
          </label>
          {SWATCHES.map(color => (
            <button key={color} type="button" role="radio" aria-checked={value.color === color} aria-label={colorOf(color)} className="exo-dc-swatch" disabled={!editable}
              style={{ background: colorOf(color) }} onClick={() => change({ color })} />
          ))}
        </div>
      </Field>
      <Field label="Display role members separately from online members">
        <Switch checked={value.hoist} disabled={!editable} label="Display role members separately from online members" onChange={hoist => change({ hoist })} />
      </Field>
      <Field label="Allow anyone to @mention this role" detail="Note: Members with the 'Mention @everyone, @here and All Roles' permission will always be able to ping this role.">
        <Switch checked={value.mentionable} disabled={!editable} label="Allow anyone to @mention this role" onChange={mentionable => change({ mentionable })} />
      </Field>
      {editable && !role.managed ? (
        <Field label="Delete role">
          <button type="button" className="exo-dc-tab is-raised is-danger" onClick={() => setConfirm(true)}>Delete role</button>
        </Field>
      ) : null}
      {confirm ? (
        <Confirm title="Delete role" action="Delete" error={error} busy={busy} onConfirm={remove} onClose={() => setConfirm(false)}>
          <p>Are you sure you want to delete the <strong>{role.name}</strong> role? This action cannot be undone.</p>
        </Confirm>
      ) : null}
    </>
  )
}

function RolePermissions({ permissions, editable, onChange }: { permissions: string; editable: boolean; onChange: (permissions: string) => void }) {
  const [query, setQuery] = useState('')
  const q = query.trim().toLowerCase()
  return (
    <>
      <div className="exo-dc-page-bar">
        <label className="exo-dc-friend-search exo-dc-grow">
          <Icons.Search size={16} />
          <input placeholder="Search permissions" value={query} onChange={event => setQuery(event.target.value)} aria-label="Search permissions" />
        </label>
        <button type="button" className="exo-dc-tab" disabled={!editable} onClick={() => onChange('0')}>Clear permissions</button>
      </div>
      {ROLE_GROUPS.map(group => {
        const items = group.items.filter(item => !q || item.title.toLowerCase().includes(q) || item.description.toLowerCase().includes(q))
        if (!items.length) return null
        return (
          <section key={group.title} className="exo-dc-perm-group" aria-label={group.title}>
            <h3>{group.title}</h3>
            {items.map(item => (
              <Field key={item.bit} label={item.title} detail={item.description}>
                <Switch checked={hasPermission(permissions, item.bit)} disabled={!editable} label={item.title}
                  onChange={on => onChange(withPermission(permissions, item.bit, on))} />
              </Field>
            ))}
          </section>
        )
      })}
    </>
  )
}

// Manage Members: who has the role (Discord's member search by role), remove one, or Add Members.
function RoleMembers({ guildId, role, editable, onChanged }: { guildId: string; role: Role; editable: boolean; onChanged: () => void }) {
  const members = useAdmin<MemberSearch>('roleMembers', { guildId, roleId: role.id })
  const [query, setQuery] = useState('')
  const [adding, setAdding] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const q = query.trim().toLowerCase()
  const remove = (row: Member) => void admin('removeRole', { guildId, userId: row.member.user.id, roleId: role.id }).then(result => {
    if (!result.ok) { setError(result.message ?? 'Discord did not take that.'); return }
    members.set(list => list && { ...list, members: list.members.filter(m => m.member.user.id !== row.member.user.id) })
    onChanged()
  })
  const shown = (members.data?.members ?? []).filter(({ member }) => !q || [member.nick, member.user.global_name, member.user.username].some(n => n?.toLowerCase().includes(q)))
  return (
    <>
      <div className="exo-dc-page-bar">
        <label className="exo-dc-friend-search exo-dc-grow">
          <Icons.Search size={16} />
          <input placeholder="Search members" value={query} onChange={event => setQuery(event.target.value)} aria-label="Search members" />
        </label>
        {editable ? <button type="button" className="exo-set-btn is-primary" onClick={() => setAdding(true)}>Add members</button> : null}
      </div>
      {error ? <p className="exo-dc-note is-bad" role="alert">{error}</p> : null}
      <ReadError error={members.error} onRetry={members.reload} />
      {members.data && shown.length === 0 ? <p className="exo-dc-note">No members were found.</p> : null}
      {!members.data && !members.error ? <p className="exo-dc-note">Loading</p> : null}
      <ul className="exo-dc-mod-list">
        {shown.map(row => (
          <li key={row.member.user.id} data-dc-user={row.member.user.id}>
            <Avatar src={avatarUrl(row.member.user, 32)} name={row.member.user.username} status={null} size={32} />
            <span className="exo-dc-mod-name"><span>{personName(row.member.user, row.member.nick)}</span><small>{row.member.user.username}</small></span>
            {editable ? <button type="button" className="exo-dc-icon-btn" aria-label={`Remove ${personName(row.member.user, row.member.nick)}`} onClick={() => remove(row)}><Close size={14} /></button> : null}
          </li>
        ))}
      </ul>
      {adding ? <AddMembers guildId={guildId} role={role} onClose={() => setAdding(false)} onAdded={() => { setAdding(false); members.reload(); onChanged() }} /> : null}
    </>
  )
}

// The host adds at most this many in one call.
const MAX_ADD = 30

function AddMembers({ guildId, role, onClose, onAdded }: { guildId: string; role: Role; onClose: () => void; onAdded: () => void }) {
  const [query, setQuery] = useState('')
  const [found, setFound] = useState<Member[]>([])
  const [picked, setPicked] = useState<Set<string>>(new Set())
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  // Discord's member search is rate-limited: ask once typing pauses, and only the newest answer counts.
  const latest = useRef('')
  useEffect(() => {
    const text = query.trim()
    latest.current = text
    if (text.length < 2) { setFound([]); return }
    const wait = window.setTimeout(() => void admin<MemberSearch>('searchMembers', { guildId, query: text }).then(result => {
      if (latest.current !== text) return
      if (!result.ok) { setError(result.message ?? 'Discord did not search.'); return }
      setError(null)
      setFound((result.data?.members ?? []).filter(m => !m.member.roles.includes(role.id)))
    }), 300)
    return () => window.clearTimeout(wait)
  }, [query, guildId, role.id])
  const add = () => {
    setBusy(true)
    void admin('addRoleMembers', { guildId, roleId: role.id, userIds: [...picked] }).then(result => {
      setBusy(false)
      if (result.ok) onAdded(); else setError(result.message ?? 'Discord did not add them.')
    })
  }
  return (
    <Confirm title="Add members" action="Add" danger={false} error={error} busy={busy || picked.size === 0} onConfirm={add} onClose={onClose}>
      <p>{role.name}</p>
      <input className="exo-dc-modal-input" placeholder="Search members" value={query} autoFocus aria-label="Search members" onChange={event => setQuery(event.target.value)} />
      {picked.size >= MAX_ADD ? <p className="exo-dc-note">You can add {MAX_ADD} members at a time.</p> : null}
      <div className="exo-dc-pick-list">
        {found.map(({ member }) => (
          <button key={member.user.id} type="button" className="exo-dc-pick-row" aria-pressed={picked.has(member.user.id)}
            disabled={picked.size >= MAX_ADD && !picked.has(member.user.id)} onClick={() => setPicked(set => {
              const next = new Set(set)
              if (!next.delete(member.user.id)) next.add(member.user.id)
              return next
            })}>
            <span className="exo-dc-pick-mark" aria-hidden="true">{picked.has(member.user.id) ? <Icons.Check size={14} /> : null}</span>
            <Avatar src={avatarUrl(member.user, 24)} name={member.user.username} status={null} size={24} />
            <span>{personName(member.user, member.nick)}</span>
            <small>{member.user.username}</small>
          </button>
        ))}
      </div>
    </Confirm>
  )
}
