import { useEffect, useMemo, useRef, useState } from 'react'
import { host } from '../../lib/host'
import { report } from '../../lib/report'
import { avatarUrl, idAt, type DiscordChannel, type DiscordMessage, type DiscordPlace, type DiscordSearchFilters, type DiscordUser } from '../../lib/discord'
import { Icons } from './icons'
import type { MarkdownContext } from './markdown'
import { Result, useOutside } from './results'

/** Discord's search filters, as typed in its search bar (during: is also on:). */
type FilterKey = 'from' | 'mentions' | 'has' | 'before' | 'during' | 'after' | 'in' | 'pinned' | 'authorType'

const FILTERS: { key: FilterKey; label: string; example: string; dm?: boolean }[] = [
  { key: 'from', label: 'From a specific user', example: 'user', dm: true },
  { key: 'in', label: 'Sent in a specific channel', example: 'channel' },
  { key: 'has', label: 'Includes a specific type of data', example: 'link, embed or file', dm: true },
  { key: 'mentions', label: 'Mentions a specific user', example: 'user', dm: true },
  { key: 'before', label: 'Before a specific date', example: 'date', dm: true },
  { key: 'during', label: 'During a specific date', example: 'date', dm: true },
  { key: 'after', label: 'After a specific date', example: 'date', dm: true },
  { key: 'pinned', label: 'Message is pinned', example: 'true or false', dm: true },
  { key: 'authorType', label: 'Author type', example: 'user, bot or webhook', dm: true },
]

// has: answers as typed, and what Discord's search calls them (a forward is a snapshot).
const HAS: Record<string, string> = { link: 'link', embed: 'embed', poll: 'poll', forwarded: 'snapshot', file: 'file', video: 'video', image: 'image', sound: 'sound', sticker: 'sticker' }
const AUTHOR_TYPES = ['user', 'bot', 'webhook']
const DATE_WORDS = ['today', 'yesterday', 'this week', 'this month', 'this year']
const MONTHS = Array.from({ length: 12 }, (_, m) => new Date(2000, m, 1).toLocaleString('en-US', { month: 'long' }).toLowerCase())
const WEEKDAYS = Array.from({ length: 7 }, (_, d) => new Date(2000, 0, 2 + d).toLocaleString('en-US', { weekday: 'long' }).toLowerCase())

/** A filter in the bar: its key, what it shows, and what it sends (an id, a has: or authorType: value, a date's range). */
type Chip = { key: FilterKey; label: string; value: string; not?: boolean; range?: [number, number] }
type Suggestion = { text: string; label: string; detail?: string; avatar?: string; pick: () => void }

/** A day, month or year typed after before:, during: or after:, as Discord reads it: its start and end (ms). */
function dateRange(text: string, now = new Date()): [number, number] | null {
  const t = text.trim().toLowerCase()
  const day = (d: Date) => new Date(d.getFullYear(), d.getMonth(), d.getDate())
  const plusDays = (d: Date, n: number) => new Date(d.getFullYear(), d.getMonth(), d.getDate() + n)
  const today = day(now)
  if (t === 'today') return [+today, +plusDays(today, 1)]
  if (t === 'yesterday') return [+plusDays(today, -1), +today]
  if (t === 'this week') { const start = plusDays(today, -today.getDay()); return [+start, +plusDays(start, 7)] }
  if (t === 'this month') return [+new Date(now.getFullYear(), now.getMonth(), 1), +new Date(now.getFullYear(), now.getMonth() + 1, 1)]
  if (t === 'this year') return [+new Date(now.getFullYear(), 0, 1), +new Date(now.getFullYear() + 1, 0, 1)]
  if (MONTHS.includes(t)) { const m = MONTHS.indexOf(t); return [+new Date(now.getFullYear(), m, 1), +new Date(now.getFullYear(), m + 1, 1)] }
  if (WEEKDAYS.includes(t)) { const d = plusDays(today, WEEKDAYS.indexOf(t) - today.getDay()); return [+d, +plusDays(d, 1)] }
  let m = /^(\d{4})-(\d{1,2})-(\d{1,2})$/.exec(t)
  if (m) { const d = new Date(+m[1], +m[2] - 1, +m[3]); return d.getMonth() === +m[2] - 1 ? [+d, +plusDays(d, 1)] : null }
  m = /^(\d{4})-(\d{1,2})$/.exec(t)
  if (m && +m[2] >= 1 && +m[2] <= 12) return [+new Date(+m[1], +m[2] - 1, 1), +new Date(+m[1], +m[2], 1)]
  m = /^(\d{4})$/.exec(t)
  if (m && +m[1] >= 2015) return [+new Date(+m[1], 0, 1), +new Date(+m[1] + 1, 0, 1)]
  return null
}

/** What the bar sends for its chips and words: before: ends at the day's start, after: begins at its end, during: is the day. */
function filtersOf(chips: Chip[], text: string): DiscordSearchFilters {
  const f: DiscordSearchFilters = {}
  const add = (name: 'authorIds' | 'mentions' | 'has' | 'channelIds' | 'authorTypes', value: string) => { (f[name] ??= []).push(value) }
  for (const chip of chips) {
    switch (chip.key) {
      case 'from': add('authorIds', chip.value); break
      case 'mentions': add('mentions', chip.value); break
      case 'in': add('channelIds', chip.value); break
      case 'has': add('has', (chip.not ? '-' : '') + chip.value); break
      case 'authorType': add('authorTypes', (chip.not ? '-' : '') + chip.value); break
      case 'pinned': f.pinned = chip.value === 'true'; break
      case 'before': if (chip.range) f.maxId = idAt(chip.range[0]); break
      case 'after': if (chip.range) f.minId = idAt(chip.range[1]); break
      case 'during': if (chip.range) { f.minId = idAt(chip.range[0]); f.maxId = idAt(chip.range[1]) } break
    }
  }
  if (text.trim()) f.content = text.trim()
  return f
}

// The word being typed (after the last space, or a quoted channel name), and a filter key it starts with.
function typing(text: string) {
  const word = /(\S*)$/.exec(text)?.[1] ?? ''
  const m = /^(-?)(\w+):(.*)$/.exec(word)
  const key = m ? FILTERS.find(f => f.key.toLowerCase() === m[2].toLowerCase() || (m[2].toLowerCase() === 'on' && f.key === 'during'))?.key ?? null : null
  return { word, key, not: m?.[1] === '-', value: m?.[3] ?? '' }
}

/**
 * Discord's search bar: words and filters (from:, mentions:, has:, in:, before:, during:, after:, pinned:,
 * authorType:) as chips, with the filter list on focus and each filter's answers as you type its value
 * (people, channels, dates, kinds of content). Arrow keys move through the list, Enter or Tab picks,
 * Enter with nothing picked searches, Backspace in an empty bar takes the last chip back to edit it.
 */
export function SearchBox({ channel, guildId, placeholder, ctx, onSearch, onClear }: {
  channel: DiscordChannel; guildId: string | null; placeholder: string; ctx: MarkdownContext
  onSearch: (filters: DiscordSearchFilters) => void; onClear: () => void
}) {
  const [chips, setChips] = useState<Chip[]>([])
  const [text, setText] = useState('')
  const [focused, setFocused] = useState(false)
  const [at, setAt] = useState(0)
  const [people, setPeople] = useState<DiscordUser[]>([])
  const [channels, setChannels] = useState<DiscordChannel[]>([])
  const input = useRef<HTMLInputElement>(null)
  const now = typing(text)
  const dm = !guildId

  // A server's channels for in:, once per server.
  useEffect(() => {
    if (!guildId) return
    let live = true
    host.discordChannels(guildId).then(result => { if (live) setChannels(result.channels.filter(c => c.kind !== 'category' && c.canRead !== false)) }).catch(() => { })
    return () => { live = false }
  }, [guildId])
  // People for from: and mentions:, the server's members by name (asking Discord for more), or the DM's.
  const query = now.key === 'from' || now.key === 'mentions' ? now.value.replace(/^@/, '') : null
  useEffect(() => {
    if (query === null) return
    if (dm) {
      const them = channel.dm?.userId ? [{ id: channel.dm.userId, username: channel.dm.username ?? channel.name, global_name: channel.name, avatar: null }] : []
      const me = ctx.meId ? [{ id: ctx.meId, username: '@me', global_name: 'You', avatar: null }] : []
      setPeople([...me, ...them].filter(u => !query || `${u.username} ${u.global_name}`.toLowerCase().includes(query.toLowerCase())))
      return
    }
    if (!query) { setPeople([]); return }
    let live = true
    const timer = window.setTimeout(() => {
      host.discordMentionable(guildId!, query, true).then(result => { if (live) setPeople(result.users) }).catch(() => { })
    }, 120)
    return () => { live = false; window.clearTimeout(timer) }
  }, [query, dm, guildId, channel, ctx.meId])

  const commit = (chip: Chip) => {
    setChips(list => [...list.filter(c => !(c.key === chip.key && ['before', 'after', 'during', 'pinned'].includes(c.key))), chip])
    setText(t => t.slice(0, t.length - now.word.length).replace(/\s+$/, ''))
    setAt(0)
    input.current?.focus()
  }
  const startFilter = (key: FilterKey) => {
    setText(t => `${t.slice(0, t.length - now.word.length)}${key}:`)
    setAt(0)
    input.current?.focus()
  }

  const suggestions = useMemo<{ title: string; items: Suggestion[] }>(() => {
    const v = now.value.toLowerCase()
    const starts = (s: string) => s.toLowerCase().startsWith(v)
    switch (now.key) {
      case null: {
        const w = now.word.toLowerCase()
        return { title: 'Search options', items: FILTERS.filter(f => (!dm || f.dm) && f.key.toLowerCase().startsWith(w)).map(f => ({ text: `${f.key}:`, label: f.label, detail: `${f.key}: ${f.example}`, pick: () => startFilter(f.key) })) }
      }
      case 'from': case 'mentions':
        return { title: now.key === 'from' ? 'From user' : 'Mentions user', items: people.slice(0, 8).map(u => ({
          text: u.username, label: u.global_name || u.username, detail: u.username === '@me' ? undefined : u.username,
          avatar: u.username === '@me' ? undefined : avatarUrl(u, 32), pick: () => commit({ key: now.key!, label: u.username === '@me' ? 'You' : u.global_name || u.username, value: u.id }),
        })) }
      case 'in':
        return { title: 'In channel', items: channels.filter(c => starts(c.name)).slice(0, 8).map(c => ({ text: c.name, label: `#${c.name}`, pick: () => commit({ key: 'in', label: `#${c.name}`, value: c.id }) })) }
      case 'has':
        return { title: 'Message contains', items: Object.keys(HAS).filter(starts).map(h => ({ text: h, label: h, pick: () => commit({ key: 'has', label: h, value: HAS[h], not: now.not }) })) }
      case 'authorType':
        return { title: 'Author type', items: AUTHOR_TYPES.filter(starts).map(a => ({ text: a, label: a, pick: () => commit({ key: 'authorType', label: a, value: a, not: now.not }) })) }
      case 'pinned':
        return { title: 'Pinned', items: ['true', 'false'].filter(starts).map(b => ({ text: b, label: b, pick: () => commit({ key: 'pinned', label: b, value: b }) })) }
      default: {
        const key = now.key
        const words = [...DATE_WORDS, ...MONTHS, ...WEEKDAYS].filter(starts).slice(0, 6)
        const typed = dateRange(now.value) && !words.includes(v) ? [now.value] : []
        return { title: 'Dates', items: [...typed, ...words].map(d => ({ text: d, label: d, pick: () => commit({ key, label: d, value: d, range: dateRange(d)! }) })) }
      }
    }
  }, [now.key, now.value, now.word, now.not, people, channels, dm]) // eslint-disable-line react-hooks/exhaustive-deps -- commit and startFilter read the latest text

  const search = () => {
    // A typed filter Discord would read but nobody picked (has:link, pinned:true, a date): take it as a chip first.
    let rest = text
    const extra: Chip[] = []
    rest = rest.replace(/(^|\s)(-?)(has|authorType|pinned|before|during|on|after):(\S+)/gi, (whole, space: string, not: string, rawKey: string, value: string) => {
      const key = (rawKey.toLowerCase() === 'on' ? 'during' : rawKey.toLowerCase() === 'authortype' ? 'authorType' : rawKey.toLowerCase()) as FilterKey
      const v = value.toLowerCase()
      if (key === 'has' && HAS[v]) { extra.push({ key, label: v, value: HAS[v], not: not === '-' }); return space }
      if (key === 'authorType' && AUTHOR_TYPES.includes(v)) { extra.push({ key, label: v, value: v, not: not === '-' }); return space }
      if (key === 'pinned' && (v === 'true' || v === 'false')) { extra.push({ key, label: v, value: v }); return space }
      const range = ['before', 'during', 'after'].includes(key) ? dateRange(v) : null
      if (range) { extra.push({ key, label: v, value: v, range }); return space }
      return whole
    })
    const all = [...chips, ...extra]
    if (!all.length && !rest.trim()) return
    setChips(all); setText(rest.trim())
    setFocused(false)
    onSearch(filtersOf(all, rest))
  }

  const open = focused && suggestions.items.length > 0
  const keyDown = (event: React.KeyboardEvent<HTMLInputElement>) => {
    const picked = open ? suggestions.items[at] : undefined
    if (open && (event.key === 'ArrowDown' || event.key === 'ArrowUp')) {
      event.preventDefault()
      const n = suggestions.items.length
      setAt(i => (i + (event.key === 'ArrowDown' ? 1 : n - 1)) % n)
    } else if (picked && (event.key === 'Tab' ? now.word !== '' : event.key === 'Enter' && now.key !== null)) {
      event.preventDefault(); picked.pick()
    } else if (event.key === 'Enter') { event.preventDefault(); search() }
    else if (event.key === 'Backspace' && !text && chips.length) {
      event.preventDefault()
      const last = chips[chips.length - 1]
      setChips(chips.slice(0, -1))
      setText(`${last.not ? '-' : ''}${last.key}:${last.key === 'from' || last.key === 'mentions' || last.key === 'in' ? '' : last.label}`)
    } else if (event.key === 'Escape') { setChips([]); setText(''); onClear(); input.current?.blur() }
  }
  return (
    <div className="exo-dc-search-wrap">
      <label className="exo-dc-search">
        {chips.map((chip, i) => (
          <span key={i} className="exo-dc-search-chip">{chip.not ? '-' : ''}{chip.key}: <strong>{chip.label}</strong></span>
        ))}
        <input ref={input} placeholder={chips.length ? '' : placeholder} value={text} aria-label="Search messages"
          role="combobox" aria-expanded={open} aria-autocomplete="list"
          onFocus={() => setFocused(true)} onBlur={() => window.setTimeout(() => setFocused(false), 120)}
          onChange={event => { setText(event.target.value); setAt(0); setFocused(true) }}
          onKeyDown={keyDown} />
        {chips.length || text ? (
          <button type="button" className="exo-dc-search-clear" aria-label="Clear search" onMouseDown={event => event.preventDefault()}
            onClick={() => { setChips([]); setText(''); onClear() }}><Icons.Close size={14} /></button>
        ) : <Icons.Search size={16} />}
      </label>
      {open ? (
        <div className="exo-dc-menu exo-dc-search-menu" role="listbox" aria-label={suggestions.title} onMouseDown={event => event.preventDefault()}>
          <p className="exo-dc-menu-head">{suggestions.title}</p>
          {suggestions.items.map((item, i) => (
            <button key={item.text + i} type="button" role="option" aria-selected={i === at} className={i === at ? 'is-active' : undefined}
              onPointerEnter={() => setAt(i)} onClick={item.pick}>
              {item.avatar ? <img className="exo-dc-avatar" src={item.avatar} alt="" draggable={false} /> : null}
              <span className="exo-dc-menu-text">{item.label}{item.detail ? <small>{item.detail}</small> : null}</span>
            </button>
          ))}
        </div>
      ) : null}
    </div>
  )
}

type Page = { messages: DiscordMessage[]; places: Record<string, DiscordPlace | null>; total: number; error: string | null; loading: boolean }

/**
 * Discord's search results: how many there are, Newest, Oldest or Most Relevant, 25 to a page with page
 * numbers, and Jump on each; a whole server (or what in: picks) or a DM.
 */
export function SearchResults({ channel, filters, ctx, onClose }: { channel: DiscordChannel; filters: DiscordSearchFilters; ctx: MarkdownContext; onClose: () => void }) {
  const [sort, setSort] = useState<'newest' | 'oldest' | 'relevance'>('newest')
  const [page, setPage] = useState(0)
  const [found, setFound] = useState<Page>({ messages: [], places: {}, total: 0, error: null, loading: true })
  const box = useOutside(onClose)
  const list = useRef<HTMLDivElement>(null)
  const key = JSON.stringify(filters)
  useEffect(() => { setPage(0) }, [key, sort])
  useEffect(() => {
    let live = true
    setFound(f => ({ ...f, loading: true, error: null }))
    host.discordSearch(channel.id, { ...filters, sort, offset: page * 25 }).then(result => {
      if (!live) return
      setFound({
        messages: (result.result?.messages ?? []).map(group => group[0]).filter(Boolean), places: result.places ?? {},
        total: result.result?.total_results ?? 0, error: result.ok ? null : result.message ?? 'Discord did not search.', loading: false,
      })
      list.current?.scrollTo({ top: 0 })
    }).catch((err: unknown) => {
      report(err, 'SearchResults')
      if (live) setFound({ messages: [], places: {}, total: 0, error: 'Discord did not search.', loading: false })
    })
    return () => { live = false }
  }, [channel.id, key, sort, page]) // eslint-disable-line react-hooks/exhaustive-deps -- key names the filters
  const pages = Math.min(Math.ceil(found.total / 25), 400)
  return (
    <div className="exo-dc-popover is-search" ref={box} role="dialog" aria-label="Search results">
      <div className="exo-dc-popover-title">
        <span>{found.loading && !found.total ? 'Searching' : `${found.total.toLocaleString()} ${found.total === 1 ? 'Result' : 'Results'}`}</span>
        <span className="exo-dc-segments" role="group" aria-label="Sort">
          {([['newest', 'Newest'], ['oldest', 'Oldest'], ['relevance', 'Most Relevant']] as const).map(([value, label]) => (
            <button key={value} type="button" className="exo-dc-tab" aria-pressed={sort === value} onClick={() => setSort(value)}>{label}</button>
          ))}
        </span>
      </div>
      <div className="exo-dc-popover-list" ref={list} aria-busy={found.loading}>
        {found.error ? <p className="exo-dc-note" role="alert">{found.error}</p> : null}
        {!found.loading && !found.error && found.messages.length === 0 ? <p className="exo-dc-note">No results.</p> : null}
        {found.messages.map(message => <Result key={message.id} message={message} place={found.places[message.channel_id] ?? null} here={channel.id} ctx={ctx} onJumped={onClose} />)}
      </div>
      {pages > 1 ? <Pager page={page} pages={pages} onPage={setPage} /> : null}
    </div>
  )
}

// Discord's pager: back, the first page, the pages around this one, the last, next.
function Pager({ page, pages, onPage }: { page: number; pages: number; onPage: (page: number) => void }) {
  const shown = [...new Set([0, page - 1, page, page + 1, pages - 1])].filter(p => p >= 0 && p < pages).sort((a, b) => a - b)
  return (
    <nav className="exo-dc-pager" aria-label="Pages">
      <button type="button" className="exo-dc-tab" disabled={page === 0} onClick={() => onPage(page - 1)}>Back</button>
      {shown.map((p, i) => (
        <span key={p} className="exo-dc-pager-pages">
          {i > 0 && p - shown[i - 1] > 1 ? <span aria-hidden="true">…</span> : null}
          <button type="button" className="exo-dc-tab" aria-current={p === page ? 'page' : undefined} aria-pressed={p === page} onClick={() => onPage(p)}>{p + 1}</button>
        </span>
      ))}
      <button type="button" className="exo-dc-tab" disabled={page >= pages - 1} onClick={() => onPage(page + 1)}>Next</button>
    </nav>
  )
}
