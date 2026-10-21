import { useEffect, useRef, useState } from 'react'
import { Close } from '../../brand/icons'
import { host } from '../../lib/host'
import { report } from '../../lib/report'
import type { DiscordChannel, DiscordChannels, DiscordMemberRow, DiscordResource, DiscordRole } from '../../lib/discord'
import { avatarUrl, snowflakeTime } from '../../lib/discord'
import { Icons } from './icons'
import { Avatar } from './presence'
import { Chat } from './Chat'
import { showProfile } from './extras'
import { Markdown } from './markdown'
import { ChannelIcon } from './Sidebar'
import { openUserMenu } from './UserMenu'
import { useLayer } from './admin'

/** Discord's rows over a server's channels, opened in place of a channel. */
export const PAGES = { guide: 'page:guide', browse: 'page:browse', members: 'page:members' } as const
export const isPage = (id: string | null) => !!id && id.startsWith('page:')

const LABELS = { guide: 'Server Guide', browse: 'Browse Channels', members: 'Members' } as const
const ROW_ICONS = { guide: Icons.Guide, browse: Icons.Browse, members: Icons.Members }

// Server Guide, then Events (Discord's dialog, "N Events" when there are some), Browse Channels and Members.
export function ServerRows({ rows, events, open, onPick, onEvents }: {
  rows: DiscordChannels['rows']
  events: number
  open: string | null
  onPick: (id: string) => void
  onEvents: () => void
}) {
  if (!rows) return null
  const shown = (Object.keys(LABELS) as (keyof typeof LABELS)[]).filter(key => rows[key])
  if (shown.length === 0 && !rows.events) return null
  const eventsRow = rows.events ? (
    <button key="events" type="button" className="exo-dc-item" onClick={onEvents}>
      <span className="exo-dc-hash" aria-hidden="true"><Icons.Events size={18} /></span>
      <span className="exo-dc-item-name">{events ? `${events} ${events === 1 ? 'Event' : 'Events'}` : 'Events'}</span>
    </button>
  ) : null
  return (
    <div className="exo-dc-rows">
      {shown.slice(0, 1).map(key => <Row key={key} page={key} open={open} onPick={onPick} />)}
      {eventsRow}
      {shown.slice(1).map(key => <Row key={key} page={key} open={open} onPick={onPick} />)}
    </div>
  )
}

function Row({ page, open, onPick }: { page: keyof typeof LABELS; open: string | null; onPick: (id: string) => void }) {
  const Icon = ROW_ICONS[page]
  return (
    <button type="button" className="exo-dc-item" aria-current={open === PAGES[page]} onClick={() => onPick(PAGES[page])}>
      <span className="exo-dc-hash" aria-hidden="true"><Icon size={18} /></span>
      <span className="exo-dc-item-name">{LABELS[page]}</span>
    </button>
  )
}

export function ServerPage({ page, guildId, list, onPick, meId, channelName, active }: {
  page: string
  guildId: string
  list: DiscordChannels | null
  onPick: (id: string) => void
  meId: string | null
  channelName: (id: string) => string | null
  active: boolean
}) {
  const key = (Object.keys(PAGES) as (keyof typeof PAGES)[]).find(k => PAGES[k] === page) ?? 'browse'
  const Icon = ROW_ICONS[key]
  return (
    <section className="exo-dc-chat" aria-label={LABELS[key]}>
      <header className="exo-dc-chat-head">
        <Icon size={22} />
        <strong>{LABELS[key]}</strong>
      </header>
      <div className="exo-dc-page">
        {key === 'guide' ? <Guide guildId={guildId} list={list} onPick={onPick} side={{ meId, channelName, active }} /> : null}
        {key === 'browse' ? <Browse guildId={guildId} list={list} onPick={onPick} /> : null}
        {key === 'members' ? <Members guildId={guildId} canPrune={list?.moderation?.prune === true} /> : null}
      </div>
    </section>
  )
}

export type ServerReply = Awaited<ReturnType<typeof host.discordServer>>

// Loads one of the server's pages from Discord.
export function useServer(guildId: string, action: 'guide' | 'events', reload = 0) {
  const [data, setData] = useState<ServerReply | null>(null)
  const [error, setError] = useState<string | null>(null)
  useEffect(() => {
    let live = true
    setError(null)
    host.discordServer(action, { guildId }).then(result => {
      if (!live) return
      if (!result.ok) setError(result.message ?? 'Discord did not send this.')
      else setData(result)
    }).catch((err: unknown) => { report(err, 'ServerPage.' + action); if (live) setError('Discord did not send this.') })
    return () => { live = false }
  }, [guildId, action, reload])
  return { data, error }
}

type SideProps = { meId: string | null; channelName: (id: string) => string | null; active: boolean }

// Discord's Server Guide: the server's banner, its resources, and a card with who's around.
// A resource opens beside the guide, as Discord's does.
function Guide({ guildId, list, onPick, side }: { guildId: string; list: DiscordChannels | null; onPick: (id: string) => void; side: SideProps }) {
  const { data, error } = useServer(guildId, 'guide')
  const [opened, setOpened] = useState<string | null>(null)
  const guide = data?.guide
  if (error) return <p className="exo-dc-note" role="alert">{error}</p>
  if (!data) return <p className="exo-dc-note">Loading</p>
  const channel = opened ? list?.channels.find(c => c.id === opened) : undefined
  return (
    <div className={`exo-dc-guide${channel ? ' has-side' : ''}`}>
      <div className="exo-dc-guide-banner" aria-hidden="true" />
      <GuideHead list={list} />
      <div className="exo-dc-guide-body">
        <div className="exo-dc-guide-main">
          <GetStarted guide={guide} onPick={onPick} />
          {data.resources?.length ? <strong className="exo-dc-guide-label">Resources</strong> : null}
          {data.resources?.map(resource => <Resource key={resource.channelId} resource={resource} guildId={guildId} side={side} onOpen={setOpened} />)}
        </div>
        <GuideCard name={list?.name ?? ''} online={data.online ?? 0} total={data.total ?? 0} founded={new Date(snowflakeTime(guildId))} />
      </div>
      {channel ? (
        <aside className="exo-dc-guide-side" aria-label={channel.name}>
          <button type="button" className="exo-dc-icon-btn exo-dc-guide-close" aria-label="Close" onClick={() => setOpened(null)}><Close size={16} /></button>
          <Chat key={channel.id} channel={channel} guildId={guildId} serverName={list?.name ?? null} meId={side.meId} channelName={side.channelName} active={side.active} />
        </aside>
      ) : null}
    </div>
  )
}

// The welcome message and the new member to-dos.
function GetStarted({ guide, onPick }: { guide: ServerReply['guide']; onPick: (id: string) => void }) {
  return (
    <>
      {guide?.welcome_message?.message ? <p className="exo-dc-page-text">{guide.welcome_message.message}</p> : null}
      {guide?.new_member_actions?.length ? <strong className="exo-dc-guide-label">Get started</strong> : null}
      {guide?.new_member_actions?.map(action => (
        <button key={action.channel_id} type="button" className="exo-dc-card" onClick={() => onPick(action.channel_id)}>
          {action.emoji?.name ? <span className="exo-dc-card-emoji" aria-hidden="true">{action.emoji.name}</span> : null}
          <span className="exo-dc-friend-names"><strong>{action.title}</strong><span>{action.description}</span></span>
        </button>
      ))}
    </>
  )
}

// A resource: its icon, title, and its description or else the channel's first message.
function Resource({ resource, guildId, side, onOpen }: { resource: DiscordResource; guildId: string; side: SideProps; onOpen: (id: string) => void }) {
  const preview = resource.preview
  const ctx = preview ? { users: new Map(preview.mentions.map(u => [u.id, u])), channelName: side.channelName, meId: side.meId, guildId, colors: new Map<string, string | null>() } : null
  return (
    <button type="button" className="exo-dc-card exo-dc-resource" onClick={() => onOpen(resource.channelId)}>
      {resource.icon ? <img className="exo-dc-card-emoji" src={resource.icon} alt="" draggable={false} /> : null}
      <span className="exo-dc-friend-names">
        <strong>{resource.title}</strong>
        {resource.description ? <span>{resource.description}</span> : preview && ctx ? <Markdown text={preview.content} ctx={ctx} /> : null}
      </span>
    </button>
  )
}

function GuideHead({ list }: { list: DiscordChannels | null }) {
  return (
    <div className="exo-dc-guide-head">
      {list?.icon ? <img className="exo-dc-guide-icon" src={list.icon} alt="" draggable={false} /> : null}
      <h2 className="exo-dc-page-title">{list?.name}</h2>
      <GuideInvite channelId={list?.channels.find(c => c.kind === 'text' && c.canRead !== false)?.id ?? null} />
    </div>
  )
}

// Discord's Invite button: a 7-day invite, copied.
function GuideInvite({ channelId }: { channelId: string | null }) {
  const [copied, setCopied] = useState(false)
  if (!channelId) return null
  return (
    <button type="button" className="exo-dc-tab exo-dc-guide-invite" onClick={() => void host.discordChannel(channelId, 'invite').then(result => {
      if (!result.url) return
      void navigator.clipboard.writeText(result.url).catch(() => { })
      setCopied(true)
    }).catch(() => { })}>{copied ? 'Invite link copied' : 'Invite'}</button>
  )
}

function GuideCard({ name, online, total, founded }: { name: string; online: number; total: number; founded: Date }) {
  return (
    <aside className="exo-dc-card is-static" aria-label="About this server">
      <span className="exo-dc-friend-names">
        <strong>{name}</strong>
        <span><span className="exo-dc-dot is-online" aria-hidden="true" />{online} Online <span className="exo-dc-dot" aria-hidden="true" />{total} Members</span>
        <span>Est. {founded.toLocaleDateString(undefined, { month: 'short', year: 'numeric' })}</span>
      </span>
    </aside>
  )
}

// Discord's Browse Channels: search, Create, and each category with Follow Category and a checkbox per channel.
function Browse({ guildId, list, onPick }: { guildId: string; list: DiscordChannels | null; onPick: (id: string) => void }) {
  const [query, setQuery] = useState('')
  const [creating, setCreating] = useState(false)
  if (!list) return <p className="exo-dc-note">Loading</p>
  const q = query.trim().toLowerCase()
  const groups: { category: DiscordChannel | null; channels: DiscordChannel[] }[] = []
  for (const c of list.channels) {
    if (c.kind === 'category') groups.push({ category: c, channels: [] })
    else if (!c.private && (!c.resource || !list.rows?.guide) && (!q || c.name.toLowerCase().includes(q))) (groups.at(-1) ?? groups[groups.push({ category: null, channels: [] }) - 1]).channels.push(c)
  }
  const follow = (id: string, on: boolean) => void host.discordChannel(id, 'optin', on).catch(() => { })
  return (
    <>
      <div className="exo-dc-page-bar">
        <label className="exo-dc-friend-search exo-dc-grow">
          <Icons.Search size={16} />
          <input placeholder="Search Channels" value={query} onChange={event => setQuery(event.target.value)} aria-label="Search channels" />
        </label>
        {list.canManage ? <button type="button" className="button button--primary exo-dc-blurple" onClick={() => setCreating(true)}>+ Create</button> : null}
      </div>
      {groups.filter(g => g.channels.length).map(group => (
        <section key={group.category?.id ?? 'loose'} className="exo-dc-browse-group" aria-label={group.category?.name ?? 'Channels'}>
          {group.category ? (
            <div className="exo-dc-browse-head">
              <strong>{group.category.name}</strong>
              <label className="exo-dc-switch">
                Follow Category
                <input type="checkbox" role="switch" checked={!list.optIn || group.category.optedIn === true} onChange={event => follow(group.category!.id, event.target.checked)} />
              </label>
            </div>
          ) : null}
          {group.channels.map(c => (
            <div key={c.id} className="exo-dc-browse-row">
              <button type="button" className="exo-dc-browse-name" disabled={c.kind === 'voice' || c.canRead === false} onClick={() => onPick(c.id)}>
                <span className="exo-dc-hash" aria-hidden="true"><ChannelIcon channel={c} /></span>
                <span className="exo-dc-friend-names"><strong>{c.name}</strong>{c.active && c.kind !== 'voice' ? <span>Active {since(c.active)}</span> : null}</span>
              </button>
              {c.kind !== 'voice' && c.canRead !== false ? <button type="button" className="exo-dc-tab is-raised exo-dc-browse-view" onClick={() => onPick(c.id)}>View</button> : null}
              <input type="checkbox" aria-label={`Show ${c.name} in the channel list`} checked={!list.optIn || c.optedIn === true} onChange={event => follow(c.id, event.target.checked)} />
            </div>
          ))}
        </section>
      ))}
      {creating ? <CreateChannel guildId={guildId} parentId={null} category={list.name ?? ''} onClose={() => setCreating(false)} onCreated={id => { setCreating(false); onPick(id) }} /> : null}
    </>
  )
}

// "an hour ago", "a month ago", "2 days ago": Discord's wording for channel activity (moment's thresholds).
function since(ms: number) {
  const s = (Date.now() - ms) / 1000
  const steps: [number, number, string][] = [[45, 60, 'minute'], [45 * 60, 3600, 'hour'], [22 * 3600, 86_400, 'day'], [26 * 86_400, 30 * 86_400, 'month'], [320 * 86_400, 365 * 86_400, 'year']]
  let label = 'a few seconds'
  for (const [from, unit, name] of steps) {
    if (s < from) break
    const n = Math.max(1, Math.round(s / unit))
    label = n === 1 ? (name === 'hour' ? 'an hour' : 'a ' + name) : n + ' ' + name + 's'
  }
  return label + ' ago'
}

const relative = new Intl.RelativeTimeFormat(undefined, { numeric: 'always' })
// "3 days ago", "1 month ago", "9 years ago", as Discord writes them.
function ago(ms: number) {
  const minutes = Math.floor((Date.now() - ms) / 60_000)
  if (minutes < 60) return relative.format(-Math.max(1, minutes), 'minute')
  if (minutes < 24 * 60) return relative.format(-Math.floor(minutes / 60), 'hour')
  const days = Math.floor(minutes / (24 * 60))
  if (days < 30) return relative.format(-days, 'day')
  if (days < 365) return relative.format(-Math.floor(days / 30), 'month')
  return relative.format(-Math.floor(days / 365), 'year')
}

const PAGE_SIZES = [12, 25, 50, 100]
const SORTS = [
  { id: 1, label: 'Member Since (Newest first)' },
  { id: 2, label: 'Member Since (Oldest first)' },
  { id: 3, label: 'Joined Discord (Newest first)' },
  { id: 4, label: 'Joined Discord (Oldest first)' },
]
// Discord's join_source_type.
const JOIN_METHODS = ['Unknown', 'Bot', 'Integration', 'Server Discovery', 'Student Hub', 'Invite', 'Vanity URL', 'Manual Member Verification', 'Linked channel']

type Loaded = { rows: DiscordMemberRow[]; total: number; roles: Record<string, DiscordRole>; done: boolean }
// Discord's chunk: 250 members a request.
const CHUNK = 250

// The members Discord's member search finds for a sort and a name or id, a chunk at a time after the last one loaded.
function useMemberSearch(guildId: string, sort: number, search: string, reload: number) {
  const [loaded, setLoaded] = useState<Loaded | null>(null)
  const [error, setError] = useState<string | null>(null)
  const busy = useRef(false)
  // Which search the rows belong to: a chunk for an older one is dropped.
  const asked = `${guildId}|${sort}|${search}|${reload}`
  const current = useRef(asked)
  current.current = asked
  const ask = (after?: DiscordMemberRow) => host.discordServer('members', {
    guildId, sort, ...(search ? { query: search } : {}),
    ...(after ? { afterJoined: Date.parse(after.member.joined_at), afterUser: after.member.user.id } : {}),
  })
  useEffect(() => {
    let live = true
    setError(null)
    host.discordServer('members', { guildId, sort, ...(search ? { query: search } : {}) }).then(result => {
      if (!live) return
      if (!result.ok) { setError(result.message ?? 'Discord did not send this.'); return }
      const rows = result.members?.members ?? []
      setLoaded({ rows, total: result.members?.total_result_count ?? rows.length, roles: result.roles ?? {}, done: rows.length < CHUNK })
    }).catch((err: unknown) => { report(err, 'ServerPage.members'); if (live) setError('Discord did not send this.') })
    return () => { live = false }
  }, [guildId, sort, search, reload])
  const more = () => {
    const last = loaded?.rows.at(-1)
    if (busy.current || !loaded || loaded.done || !last) return
    busy.current = true
    const of = asked
    void ask(last).then(result => {
      if (of !== current.current) return
      if (!result.ok) { setError(result.message ?? 'Discord did not send more members.'); return }
      const page = result.members?.members ?? []
      setLoaded(l => l && { ...l, rows: [...l.rows, ...page.filter(r => !l.rows.some(o => o.member.user.id === r.member.user.id))], roles: { ...l.roles, ...result.roles }, done: page.length < CHUNK })
    }).catch((err: unknown) => { report(err, 'ServerPage.members'); setError('Discord did not send more members.') }).finally(() => { busy.current = false })
  }
  return { loaded, error, more }
}

// Page buttons as Discord shows them: the first, the last loaded, and those beside the current one.
function pageButtons(at: number, pages: number) {
  const shown = [...new Set([0, at - 1, at, at + 1, pages - 1])].filter(i => i >= 0 && i < pages).sort((a, b) => a - b)
  return shown.flatMap((i, n) => n && i - shown[n - 1] > 1 ? [-1 - i, i] : [i])
}

// Discord's Members page: Recent Members with when and how each joined, searched, sorted, paged and pruned as Discord does.
export function Members({ guildId, canPrune }: { guildId: string; canPrune: boolean }) {
  const [sort, setSort] = useState(1)
  const [reload, setReload] = useState(0)
  const [query, setQuery] = useState('')
  const [search, setSearch] = useState('')
  useEffect(() => {
    const wait = window.setTimeout(() => setSearch(query.trim()), 300)
    return () => window.clearTimeout(wait)
  }, [query])
  const { loaded, error, more } = useMemberSearch(guildId, sort, search.length > 1 ? search : '', reload)
  useEffect(() => {
    const changed = () => setReload(r => r + 1)
    window.addEventListener('exo-dc-members-changed', changed)
    return () => window.removeEventListener('exo-dc-members-changed', changed)
  }, [])
  const [page, setPage] = useState(0)
  const [size, setSize] = useState(PAGE_SIZES[0])
  const [panel, setPanel] = useState<'sort' | 'prune' | null>(null)
  const all = loaded?.rows ?? []
  const pages = Math.max(1, Math.ceil(all.length / size))
  const at = Math.min(page, pages - 1)
  // The next page past what's loaded asks Discord for the next chunk.
  const needMore = !!loaded && !loaded.done && !error && (at + 1) * size >= all.length
  useEffect(() => { if (needMore) more() })
  if (error && !loaded) return <p className="exo-dc-note" role="alert">{error}</p>
  if (!loaded) return <p className="exo-dc-note">Loading</p>
  const roles = loaded.roles
  return (
    <>
      <div className="exo-dc-page-bar">
        <strong>Recent Members</strong>
        <span className="exo-dc-pager">
          <label className="exo-dc-friend-search">
            <Icons.Search size={16} />
            <input placeholder="Search by username or id" value={query} onChange={event => { setQuery(event.target.value); setPage(0) }} aria-label="Search members" />
          </label>
          <span className="exo-dc-menu-anchor">
            <button type="button" className="exo-dc-tab is-raised" aria-expanded={panel === 'sort'} onClick={() => setPanel(p => p === 'sort' ? null : 'sort')}>Sort</button>
            {panel === 'sort' ? (
              <div className="exo-dc-menu is-right" role="menu">
                {SORTS.map(option => (
                  <button key={option.id} type="button" role="menuitemradio" aria-checked={sort === option.id}
                    onClick={() => { setSort(option.id); setPage(0); setPanel(null) }}>{option.label}</button>
                ))}
              </div>
            ) : null}
          </span>
          {canPrune ? <button type="button" className="exo-dc-tab is-raised is-danger" onClick={() => setPanel('prune')}>Prune</button> : null}
        </span>
      </div>
      {error ? <p className="exo-dc-note is-bad" role="alert">{error}</p> : null}
      {all.length === 0 ? <p className="exo-dc-note">No members were found.</p> : null}
      {panel === 'prune' ? <Prune guildId={guildId} onClose={() => setPanel(null)} onPruned={() => { setPanel(null); setReload(r => r + 1) }} /> : null}
      <div className="exo-dc-table" role="table" aria-label="Members">
        <div className="exo-dc-table-row is-head" role="row">
          <span role="columnheader">Name</span><span role="columnheader">Member Since</span><span role="columnheader">Joined Discord</span>
          <span role="columnheader">Join Method</span><span role="columnheader">Roles</span><span role="columnheader" aria-label="Actions" />
        </div>
        {all.slice(at * size, at * size + size).map(row => <MemberRow key={row.member.user.id} row={row} roles={roles} guildId={guildId} />)}
      </div>
      <div className="exo-dc-page-bar">
        <span>
          Showing{' '}
          <select className="exo-dc-select" value={size} aria-label="Members per page" onChange={event => { setSize(Number(event.target.value)); setPage(0) }}>
            {PAGE_SIZES.map(n => <option key={n} value={n}>{n}</option>)}
          </select>
          {' '}members of {Math.max(loaded.total, all.length)}
        </span>
        <span className="exo-dc-pager">
          <button type="button" className="exo-dc-tab" disabled={at === 0} onClick={() => setPage(at - 1)}>Previous</button>
          {pageButtons(at, pages).map(i => i < 0
            ? <span key={i} aria-hidden="true">…</span>
            : <button key={i} type="button" className="exo-dc-tab" aria-pressed={i === at} onClick={() => setPage(i)}>{i + 1}</button>)}
          <button type="button" className="exo-dc-tab" disabled={at >= pages - 1 && loaded.done} onClick={() => setPage(at + 1)}>Next</button>
        </span>
      </div>
    </>
  )
}

// Discord's Prune: how many members inactive for 7 or 30 days (without roles) would be removed, then remove them.
function Prune({ guildId, onClose, onPruned }: { guildId: string; onClose: () => void; onPruned: () => void }) {
  const [days, setDays] = useState(7)
  const [count, setCount] = useState<number | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  useEffect(() => {
    setCount(null)
    host.discordServer('prune', { guildId, days, countOnly: true })
      .then(result => result.ok ? setCount(result.pruned ?? 0) : setError(result.message ?? 'Discord did not answer.'))
      .catch(() => setError('Discord did not answer.'))
  }, [guildId, days])
  async function prune() {
    setBusy(true)
    try {
      const result = await host.discordServer('prune', { guildId, days, countOnly: false })
      if (result.ok) onPruned()
      else setError(result.message ?? 'Discord did not prune.')
    } catch { setError('Discord did not prune.') } finally { setBusy(false) }
  }
  const box = useRef<HTMLDivElement>(null)
  useLayer(box, onClose)
  return (
    <div className="exo-dc-modal-back is-top" onClick={onClose}>
      <div ref={box} className="exo-dc-modal" role="dialog" aria-modal="true" aria-label="Prune members" tabIndex={-1} onClick={event => event.stopPropagation()}>
        <strong>Prune members</strong>
        <p>Remove members who haven't been seen in a while and have no roles. They can rejoin with a new invite.</p>
        <p className="exo-dc-side-label">Last seen</p>
        {[7, 30].map(d => (
          <button key={d} type="button" className="exo-dc-card" aria-pressed={days === d} onClick={() => setDays(d)}>More than {d} days ago</button>
        ))}
        <p>{error ?? (count === null ? 'Counting' : `${count} ${count === 1 ? 'member' : 'members'} will be removed.`)}</p>
        <div className="exo-dc-modal-row">
          <button type="button" className="exo-dc-tab" onClick={onClose}>Cancel</button>
          <button type="button" className="button button--primary exo-dc-danger-btn" disabled={busy || !count} onClick={() => void prune()}>Prune</button>
        </div>
      </div>
    </div>
  )
}

function MemberRow({ row, roles, guildId }: { row: DiscordMemberRow; roles: Record<string, DiscordRole>; guildId: string }) {
  const { member } = row
  const own = member.roles.map(id => roles[id]).filter((r): r is DiscordRole => !!r).sort((x, y) => y.position - x.position)
  const top = own[0]
  const color = own.find(r => r.color)?.color ?? null
  const name = member.nick ?? member.user.global_name ?? member.user.username
  return (
    <div className="exo-dc-table-row" role="row">
      <span role="cell" className="exo-dc-table-name" data-dc-user={member.user.id} data-dc-name={name} data-dc-username={member.user.username}>
        <Avatar src={avatarUrl(member.user, 64)} name={name} status={null} size={32} />
        <span className="exo-dc-friend-names">
          <strong style={color ? { color } : undefined}>{name}</strong>
          <span>{member.user.username}</span>
        </span>
      </span>
      <span role="cell">{ago(Date.parse(member.joined_at))}</span>
      <span role="cell">{ago(snowflakeTime(member.user.id))}</span>
      <span role="cell">{row.source_invite_code ?? JOIN_METHODS[row.join_source_type ?? 0] ?? 'Unknown'}</span>
      <span role="cell" className="exo-dc-role-cell">
        {top ? <span className="exo-dc-role"><span className="exo-dc-role-dot" style={{ background: top.color ?? 'var(--dc-muted)' }} aria-hidden="true" />{top.name}</span> : null}
        {own.length > 1 ? <span>+{own.length - 1}</span> : null}
      </span>
      <MemberActions userId={member.user.id} name={name} username={member.user.username} guildId={guildId} />
    </div>
  )
}

// The row's profile button, and More: Discord's person menu (roles, nickname, timeout, kick, ban).
function MemberActions({ userId, name, username, guildId }: { userId: string; name: string; username: string; guildId: string }) {
  return (
    <span role="cell" className="exo-dc-row-actions">
      <button type="button" className="exo-dc-icon-btn" aria-label={`View ${name}'s profile`}
        onClick={event => showProfile(userId, guildId, event.clientX, event.clientY)}><Icons.Profile size={16} /></button>
      <button type="button" className="exo-dc-icon-btn" aria-label="More"
        onClick={event => { const box = event.currentTarget.getBoundingClientRect(); openUserMenu(userId, box.right - 220, box.bottom + 4, name, username) }}><Icons.More size={16} /></button>
    </span>
  )
}

/** Discord's Create Channel, from a category's +. */
export function CreateChannel({ guildId, parentId, category, onCreated, onClose }: {
  guildId: string
  parentId: string | null
  category: string
  onCreated: (channelId: string) => void
  onClose: () => void
}) {
  const [kind, setKind] = useState<'text' | 'voice'>('text')
  const [name, setName] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  async function create() {
    if (!name.trim() || busy) return
    setBusy(true)
    try {
      const result = await host.discordServer('createChannel', { guildId, ...(parentId ? { parentId } : {}), name: name.trim(), kind })
      if (result.ok && result.channelId) onCreated(result.channelId)
      else setError(result.message ?? 'Discord did not make that channel.')
    } catch (err) {
      report(err, 'CreateChannel')
      setError('Discord did not make that channel.')
    } finally { setBusy(false) }
  }
  return (
    <div className="exo-dc-modal-back" onClick={onClose} onKeyDown={event => { if (event.key === 'Escape') onClose() }}>
      <div className="exo-dc-modal" role="dialog" aria-label="Create Channel" onClick={event => event.stopPropagation()}>
        <strong>Create Channel</strong>
        <p>in {category}</p>
        <p className="exo-dc-side-label">Channel type</p>
        {(['text', 'voice'] as const).map(k => (
          <button key={k} type="button" className="exo-dc-card" aria-pressed={kind === k} onClick={() => setKind(k)}>
            <span className="exo-dc-hash" aria-hidden="true">{k === 'voice' ? <Icons.Voice size={18} /> : <Icons.Hash size={18} />}</span>
            <span className="exo-dc-friend-names"><strong>{k === 'text' ? 'Text' : 'Voice'}</strong><span>{k === 'text' ? 'Send messages, images, GIFs, emoji, opinions, and puns' : 'Hang out together with voice, video, and screen share'}</span></span>
          </button>
        ))}
        <label className="exo-dc-side-label" htmlFor="exo-dc-new-channel">Channel name</label>
        <input id="exo-dc-new-channel" className="exo-dc-modal-input" placeholder="new-channel" value={name} autoFocus
          onChange={event => setName(event.target.value)} onKeyDown={event => { if (event.key === 'Enter') void create() }} />
        {error ? <p className="is-bad" role="alert">{error}</p> : null}
        <div className="exo-dc-modal-row">
          <button type="button" className="exo-dc-tab" onClick={onClose}>Cancel</button>
          <button type="button" className="button button--primary" disabled={!name.trim() || busy} onClick={() => void create()}>Create Channel</button>
        </div>
      </div>
    </div>
  )
}
