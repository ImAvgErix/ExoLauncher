import { useCallback, useEffect, useRef, useState } from 'react'
import { host } from '../../lib/host'
import { report } from '../../lib/report'
import { avatarUrl, displayName, jumpToMessage, openDiscord, snowflakeTime, type DiscordForYouItem, type DiscordMessage, type DiscordPlace, type DiscordSavedMessage, type DiscordUnreadChannel } from '../../lib/discord'
import { unsaveMessage } from '../../lib/discordSaved'
import { Icons } from './icons'
import { Markdown, MarkdownLine, type MarkdownContext } from './markdown'
import { Result, useOutside } from './results'
import { Skeleton } from '../ui/Skeleton'
import { ErrorNote } from '../ui/ErrorNote'

type Tab = 'forYou' | 'unreads' | 'mentions' | 'bookmarks' | 'reminders'
const TABS: [Tab, string][] = [['forYou', 'For you'], ['unreads', 'Unreads'], ['mentions', 'Mentions'], ['bookmarks', 'Bookmarks'], ['reminders', 'Reminders']]
const LAST_TAB = 'exo.discordInboxTab'

function savedTab(): Tab {
  try { const t = localStorage.getItem(LAST_TAB); return TABS.some(([value]) => value === t) ? t as Tab : 'mentions' } catch { return 'mentions' }
}

/** Discord's Inbox: For You (its notifications), Unreads (unread channels across your servers), Mentions, Bookmarks and Reminders. */
export function Inbox({ ctx, here, onClose }: { ctx: MarkdownContext; here: string; onClose: () => void }) {
  const [tab, setTab] = useState<Tab>(savedTab)
  const box = useOutside(onClose)
  const choose = (next: Tab) => { setTab(next); try { localStorage.setItem(LAST_TAB, next) } catch { /* the tab is only a convenience */ } }
  return (
    <div className="exo-dc-popover is-inbox" ref={box} role="dialog" aria-label="Inbox">
      <div className="exo-dc-popover-title">
        <span className="exo-dc-inbox-name"><Icons.Inbox size={18} /> Inbox</span>
        <span className="exo-dc-segments" role="tablist" aria-label="Inbox">
          {TABS.map(([value, label]) => (
            <button key={value} type="button" role="tab" className="exo-dc-tab" aria-selected={tab === value} aria-pressed={tab === value} onClick={() => choose(value)}>{label}</button>
          ))}
        </span>
      </div>
      <InboxTab tab={tab} ctx={ctx} here={here} onClose={onClose} />
    </div>
  )
}

function InboxTab({ tab, ctx, here, onClose }: { tab: Tab; ctx: MarkdownContext; here: string; onClose: () => void }) {
  if (tab === 'forYou') return <ForYou onClose={onClose} />
  if (tab === 'unreads') return <Unreads ctx={ctx} onClose={onClose} />
  if (tab === 'bookmarks' || tab === 'reminders') return <SavedList kind={tab} ctx={ctx} here={here} onClose={onClose} />
  return <Mentions ctx={ctx} here={here} onClose={onClose} />
}

// "Due in 3 hours", "Due tomorrow at 9:00 AM", or "Overdue", as Discord heads a reminder.
function dueLine(at: number, now = Date.now()) {
  if (at <= now) return 'Overdue'
  const minutes = Math.round((at - now) / 60_000)
  if (minutes < 60) return `Due in ${minutes} minute${minutes === 1 ? '' : 's'}`
  if (minutes < 24 * 60) return `Due in ${Math.round(minutes / 60)} hour${Math.round(minutes / 60) === 1 ? '' : 's'}`
  return `Due ${new Date(at).toLocaleString(undefined, { weekday: 'long', hour: 'numeric', minute: '2-digit' })}`
}

/** Bookmarks (25 at a time, latest first) or Reminders (soonest first), each with Jump and Remove (Mark complete). */
function SavedList({ kind, ctx, here, onClose }: { kind: 'bookmarks' | 'reminders'; ctx: MarkdownContext; here: string; onClose: () => void }) {
  const [items, setItems] = useState<DiscordSavedMessage[]>([])
  const [places, setPlaces] = useState<Record<string, DiscordPlace | null>>({})
  const [more, setMore] = useState(false)
  const [busy, setBusy] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const load = useCallback((before?: string) => {
    setBusy(true)
    const ask = kind === 'bookmarks' ? host.discordSaved('bookmarks', before ? { before } : {}) : host.discordSaved('state')
    ask.then(result => {
      const page = (kind === 'bookmarks' ? result.results : result.reminders) ?? []
      const sorted = kind === 'reminders' ? page.toSorted((a, b) => Date.parse(a.save_data.due_at ?? '') - Date.parse(b.save_data.due_at ?? '')) : page
      setItems(list => before ? [...list, ...sorted] : sorted)
      setPlaces(p => ({ ...(before ? p : {}), ...result.places }))
      setMore(kind === 'bookmarks' && result.hasMore === true)
      setError(result.ok ? null : result.message ?? 'Discord did not send what you saved.')
    }).catch(() => setError('Discord did not send what you saved.')).finally(() => setBusy(false))
  }, [kind])
  useEffect(() => { load() }, [load])
  const remove = (item: DiscordSavedMessage) => {
    setItems(list => list.filter(i => i !== item))
    void unsaveMessage(item.save_data.channel_id, item.save_data.message_id).then(why => { if (why) setError(why) })
  }
  return (
    <div className="exo-dc-popover-list" onScroll={event => { if (more && !busy && items.length && nearFoot(event)) load(items[items.length - 1].save_data.saved_at) }}>
      {error ? <ErrorNote className="exo-dc-note" message={error} /> : null}
      {!busy && !error && items.length === 0 ? <p className="exo-dc-note">{kind === 'bookmarks' ? 'Bookmark a message from its menu to find it here.' : 'Set a reminder from a message\'s menu, and it shows here.'}</p> : null}
      {items.map(item => item.message ? (
        <div key={item.save_data.message_id} className="exo-dc-saved">
          {item.save_data.due_at ? <p className="exo-dc-saved-due">{dueLine(Date.parse(item.save_data.due_at))}</p> : null}
          <Result message={item.message} place={places[item.message.channel_id] ?? null} here={here} ctx={ctx} onJumped={onClose}
            onRemove={() => remove(item)} removeLabel={kind === 'bookmarks' ? 'Remove bookmark' : 'Mark complete'} />
        </div>
      ) : null)}
      {busy ? <Skeleton label={kind === 'bookmarks' ? 'Loading saved messages' : 'Loading reminders'} avatar /> : null}
    </div>
  )
}

// Loads more as the list nears its foot.
function nearFoot(event: React.UIEvent<HTMLElement>) {
  const el = event.currentTarget
  return el.scrollTop + el.clientHeight > el.scrollHeight - 120
}

/** Mentions, newest first, 25 at a time: in every server or this one, with @everyone and @here and role mentions or not. */
function Mentions({ ctx, here, onClose }: { ctx: MarkdownContext; here: string; onClose: () => void }) {
  const [everyone, setEveryone] = useState(true)
  const [roles, setRoles] = useState(true)
  const [thisServer, setThisServer] = useState(false)
  const [messages, setMessages] = useState<DiscordMessage[]>([])
  const [places, setPlaces] = useState<Record<string, DiscordPlace | null>>({})
  const [more, setMore] = useState(true)
  const [busy, setBusy] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const asked = useRef(0)
  const guildId = thisServer ? ctx.guildId : null
  const load = useCallback((before?: string) => {
    const ask = ++asked.current
    setBusy(true)
    host.discordMentions({ before, guildId, roles, everyone }).then(result => {
      if (asked.current !== ask) return
      const page = result.messages ?? []
      setMessages(list => before ? [...list, ...page] : page)
      setPlaces(p => ({ ...(before ? p : {}), ...result.places }))
      setMore(page.length >= 25)
      setError(result.ok ? null : result.message ?? 'Discord did not send your mentions.')
    }).catch((err: unknown) => {
      report(err, 'Inbox.mentions')
      if (asked.current === ask) setError('Discord did not send your mentions.')
    }).finally(() => { if (asked.current === ask) setBusy(false) })
  }, [guildId, roles, everyone])
  useEffect(() => { load() }, [load])
  const remove = (message: DiscordMessage) => {
    setMessages(list => list.filter(m => m.id !== message.id))
    void host.discordMentions({ remove: message.id }).then(result => { if (!result.ok) setError(result.message ?? 'Discord did not take that off.') }).catch(() => setError('Discord did not take that off.'))
  }
  return (
    <>
      <div className="exo-dc-popover-tabs" role="group" aria-label="Show">
        {ctx.guildId ? <button type="button" className="exo-dc-tab" aria-pressed={thisServer} onClick={() => setThisServer(v => !v)}>This server only</button> : null}
        <button type="button" className="exo-dc-tab" aria-pressed={everyone} onClick={() => setEveryone(v => !v)}>@everyone and @here</button>
        <button type="button" className="exo-dc-tab" aria-pressed={roles} onClick={() => setRoles(v => !v)}>Role mentions</button>
      </div>
      <div className="exo-dc-popover-list" onScroll={event => { if (more && !busy && messages.length && nearFoot(event)) load(messages[messages.length - 1].id) }}>
        {error ? <ErrorNote className="exo-dc-note" message={error} /> : null}
        {!busy && !error && messages.length === 0 ? <p className="exo-dc-note">You're all caught up.</p> : null}
        {messages.map(message => <Result key={message.id} message={message} place={places[message.channel_id] ?? null} here={here} ctx={ctx} onJumped={onClose} onRemove={() => remove(message)} />)}
        {busy ? <Skeleton label="Loading mentions" avatar /> : null}
      </div>
    </>
  )
}

/** A server's unread channels in Unreads, each with its newest unread messages (read without opening it). */
type UnreadGroup = { guildId: string; guild: string; icon: string | null; channels: DiscordUnreadChannel[] }

function groupByServer(channels: DiscordUnreadChannel[]): UnreadGroup[] {
  const groups = new Map<string, UnreadGroup>()
  for (const c of channels) {
    const group = groups.get(c.guildId) ?? { guildId: c.guildId, guild: c.guild, icon: c.icon, channels: [] }
    group.channels.push(c)
    groups.set(c.guildId, group)
  }
  return [...groups.values()]
}

const UNREAD_PAGE = 10

/**
 * Unreads: every unread channel in servers you haven't muted, by server, newest first, 10 channels at a time;
 * each shows its newest unread messages, a click on one jumps there, and the check marks the channel (or the
 * whole server) read.
 */
function Unreads({ ctx, onClose }: { ctx: MarkdownContext; onClose: () => void }) {
  const [channels, setChannels] = useState<DiscordUnreadChannel[] | null>(null)
  const [shown, setShown] = useState(UNREAD_PAGE)
  const [error, setError] = useState<string | null>(null)
  useEffect(() => {
    let live = true
    host.discordUnreads().then(result => { if (live) setChannels(result.channels) }).catch((err: unknown) => { report(err, 'Inbox.unreads'); if (live) setChannels([]) })
    return () => { live = false }
  }, [])
  const read = (ids: string[]) => setChannels(list => list?.filter(c => !ids.includes(c.channelId)) ?? null)
  const markChannel = (c: DiscordUnreadChannel) => {
    read([c.channelId])
    void host.discordChannel(c.channelId, 'read').then(result => { if (!result.ok) setError(result.message ?? 'Discord did not mark that read.') }).catch(() => setError('Discord did not mark that read.'))
  }
  const markServer = (group: UnreadGroup) => {
    setChannels(list => list?.filter(c => c.guildId !== group.guildId) ?? null)
    void host.discordMarkRead(group.guildId).then(result => { if (!result.ok) setError('Discord did not mark that server read.') }).catch(() => setError('Discord did not mark that server read.'))
  }
  const visible = channels?.slice(0, shown) ?? []
  return (
    <div className="exo-dc-popover-list" onScroll={event => { if (channels && shown < channels.length && nearFoot(event)) setShown(n => n + UNREAD_PAGE) }}>
      {error ? <ErrorNote className="exo-dc-note" message={error} /> : null}
      {channels === null ? <Skeleton label="Loading unread channels" /> : null}
      {channels?.length === 0 ? <p className="exo-dc-note">You're all caught up.</p> : null}
      {groupByServer(visible).map(group => (
        <section key={group.guildId} className="exo-dc-unread-server" aria-label={group.guild}>
          <header className="exo-dc-unread-head">
            {group.icon ? <img src={group.icon} alt="" draggable={false} /> : <span className="exo-dc-unread-mark" aria-hidden="true">{group.guild.slice(0, 1)}</span>}
            <strong>{group.guild}</strong>
            <button type="button" className="exo-dc-icon-btn" aria-label={`Mark ${group.guild} as read`} onClick={() => markServer(group)}><Icons.Check size={16} /></button>
          </header>
          {group.channels.map(c => <UnreadChannel key={c.channelId} channel={c} ctx={ctx} onRead={() => markChannel(c)} onClose={onClose} />)}
        </section>
      ))}
    </div>
  )
}

function UnreadChannel({ channel, ctx, onRead, onClose }: { channel: DiscordUnreadChannel; ctx: MarkdownContext; onRead: () => void; onClose: () => void }) {
  const [messages, setMessages] = useState<DiscordMessage[] | null>(null)
  useEffect(() => {
    let live = true
    host.discordMessages(channel.channelId, channel.lastRead ? { after: channel.lastRead, peek: true } : { peek: true })
      .then(result => { if (live) setMessages(result.messages ?? []) }).catch(() => { if (live) setMessages([]) })
    return () => { live = false }
  }, [channel.channelId, channel.lastRead])
  // Discord sends newest first; the newest three unread show, oldest of them on top.
  const unread = (messages ?? []).filter(m => !channel.lastRead || BigInt(m.id) > BigInt(channel.lastRead))
  const newest = unread.slice(0, 3).reverse()
  const open = (messageId?: string) => {
    onClose()
    const target = { guildId: channel.guildId, channelId: channel.channelId, parentId: channel.parentId, name: channel.name }
    if (messageId) jumpToMessage({ ...target, messageId })
    else openDiscord({ outside: false, ...target })
  }
  return (
    <article className="exo-dc-unread-channel">
      <header>
        <button type="button" className="exo-dc-link" onClick={() => open(newest[0]?.id)}>
          {channel.parentId ? <Icons.Threads size={14} /> : <Icons.Hash size={14} />}{channel.name}
        </button>
        {channel.mentions ? <span className="exo-dc-badge">{channel.mentions}</span> : null}
        <button type="button" className="exo-dc-icon-btn" aria-label={`Mark #${channel.name} as read`} onClick={onRead}><Icons.Check size={16} /></button>
      </header>
      {messages === null ? <Skeleton label={`Loading #${channel.name}`} rows={2} avatar /> : null}
      {newest.map(m => (
        <button key={m.id} type="button" className="exo-dc-unread-msg" onClick={() => open(m.id)}>
          <img className="exo-dc-avatar" src={avatarUrl(m.author, 32)} alt="" draggable={false} loading="lazy" />
          <span>
            <strong>{displayName(m)}</strong>
            <span>{m.content ? <MarkdownLine text={m.content} ctx={ctx} /> : 'Attachment'}</span>
          </span>
        </button>
      ))}
      {unread.length > newest.length ? <p className="exo-dc-small">{unread.length - newest.length}{unread.length >= 50 ? '+' : ''} more</p> : null}
    </article>
  )
}

// A link in For You opens what it names in the room: a conversation or a message in it.
function linkTarget(deeplink: string | null | undefined) {
  const m = deeplink ? /\/channels\/(\d+|@me)\/(\d+)(?:\/(\d+))?/.exec(deeplink) : null
  return m ? { guildId: m[1] === '@me' ? null : m[1], channelId: m[2], messageId: m[3] ?? null } : null
}

/** For You: Discord's notifications (a friend request accepted, an event starting, someone going live), newest first. */
function ForYou({ onClose }: { onClose: () => void }) {
  const [items, setItems] = useState<DiscordForYouItem[]>([])
  const [more, setMore] = useState(true)
  const [busy, setBusy] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const asked = useRef(0)
  const load = useCallback((before?: string) => {
    const ask = ++asked.current
    setBusy(true)
    host.discordForYou(before).then(result => {
      if (asked.current !== ask) return
      setItems(list => before ? [...list, ...(result.items ?? [])] : result.items ?? [])
      setMore(result.hasMore === true)
      setError(result.ok ? null : result.message ?? 'Discord did not send your notifications.')
    }).catch((err: unknown) => {
      report(err, 'Inbox.forYou')
      if (asked.current === ask) setError('Discord did not send your notifications.')
    }).finally(() => { if (asked.current === ask) setBusy(false) })
  }, [])
  useEffect(() => { load() }, [load])
  const empty: MarkdownContext = { users: new Map(), channelName: () => null, meId: null, guildId: null, colors: new Map() }
  return (
    <div className="exo-dc-popover-list" onScroll={event => { if (more && !busy && items.length && nearFoot(event)) load(items[items.length - 1].id) }}>
      {error ? <ErrorNote className="exo-dc-note" message={error} /> : null}
      {!busy && !error && items.length === 0 ? <p className="exo-dc-note">Nothing new for you.</p> : null}
      {items.map(item => {
        const target = linkTarget(item.deeplink)
        const picture = item.icon_url ?? (item.other_user ? avatarUrl(item.other_user, 40) : null)
        const body = (
          <>
            {picture ? <img className="exo-dc-avatar" src={picture} alt="" draggable={false} loading="lazy" /> : <span className="exo-dc-foryou-mark" aria-hidden="true"><Icons.Bell size={18} /></span>}
            <span className="exo-dc-foryou-text">
              {item.body ? <Markdown text={item.body} ctx={empty} /> : null}
              <time className="exo-dc-msg-time">{new Date(snowflakeTime(item.id)).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' })}</time>
            </span>
            {item.acked === false ? <span className="exo-dc-foryou-new" aria-label="New" /> : null}
          </>
        )
        return target ? (
          <button key={item.id} type="button" className="exo-dc-foryou" onClick={() => {
            onClose()
            if (target.messageId) jumpToMessage({ guildId: target.guildId, channelId: target.channelId, messageId: target.messageId })
            else openDiscord({ outside: false, guildId: target.guildId, channelId: target.channelId })
          }}>{body}</button>
        ) : <div key={item.id} className="exo-dc-foryou">{body}</div>
      })}
      {busy ? <Skeleton label="Loading For You" avatar /> : null}
    </div>
  )
}
