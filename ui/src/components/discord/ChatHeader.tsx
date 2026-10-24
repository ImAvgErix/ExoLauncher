import { useEffect, useRef, useState } from 'react'
import { host } from '../../lib/host'
import { report } from '../../lib/report'
import { type DiscordChannel, type DiscordMessage, type DiscordPlace, type DiscordSearchFilters, type DiscordThread } from '../../lib/discord'
import { Icons } from './icons'
import { useLayer } from './admin'
import { Markdown, MarkdownLine, type MarkdownContext } from './markdown'
import { ModalBack } from './SettingsLayer'
import { Avatar } from './presence'
import { FriendPicker } from './Sidebar'
import { Inbox } from './Inbox'
import { MessageConfirm } from './MessageConfirm'
import { Result, useOutside } from './results'
import { SearchBox, SearchResults } from './SearchBar'

/** The bar over a conversation, Discord's way: what it is, then calls, pins, the member list and search on the right. */
export function ChatHeader({ channel, guildId, serverName, ctx, membersShown, onToggleMembers, onCall, inCall, onOpenThread, parent, onBack, onHide }: {
  channel: DiscordChannel
  guildId: string | null
  serverName?: string | null
  onOpenThread?: (thread: DiscordChannel) => void
  /** A thread's channel: Discord's breadcrumb, the channel to go back to, then the thread. */
  parent?: DiscordChannel | null
  onBack?: () => void
  ctx: MarkdownContext
  membersShown: boolean
  /** Absent beside a voice call, which has no member list. */
  onToggleMembers?: () => void
  /** A DM's Start Voice Call; video: Start Video Call. */
  onCall?: (video: boolean) => void
  inCall?: boolean
  /** Discord's chat panel beside a call: the channel's name and Close Chat, nothing else. */
  onHide?: () => void
}) {
  const [panel, setPanel] = useState<'pins' | 'search' | 'inbox' | 'threads' | null>(null)
  // What was searched for: Enter searches, typing after it doesn't until the next Enter.
  const [searched, setSearched] = useState<DiscordSearchFilters | null>(null)
  if (onHide) return <PanelHead channel={channel} guildId={guildId} onHide={onHide} />
  // A 1:1 DM with its profile panel open: the panel is the person's header, so the bar doesn't repeat their face and name.
  const profileBeside = !guildId && !!channel.dm && !channel.dm.group && membersShown && !!onToggleMembers
  return (
    <header className="exo-dc-chat-head">
      {profileBeside ? null : <HeadTitle channel={channel} guildId={guildId} parent={parent} onBack={onBack} />}
      {channel.topic ? <Topic channel={channel} guildId={guildId} topic={channel.topic} ctx={ctx} /> : null}
      <span className="exo-dc-head-tools">
        {onCall ? (
          <>
            <button type="button" className={`exo-dc-icon-btn${inCall ? ' is-on' : ''}`} aria-label={inCall ? 'In call' : 'Start a voice call'} disabled={inCall} onClick={() => onCall(false)}>
              <Icons.Call size={20} />
            </button>
            <button type="button" className="exo-dc-icon-btn" aria-label="Start a video call" disabled={inCall} onClick={() => onCall(true)}>
              <Icons.Camera size={20} />
            </button>
          </>
        ) : null}
        {guildId && onOpenThread ? (
          <button type="button" className="exo-dc-icon-btn" aria-label="Threads" aria-pressed={panel === 'threads'} onClick={() => setPanel(p => p === 'threads' ? null : 'threads')}>
            <Icons.Threads size={20} />
          </button>
        ) : null}
        {/* Keys are named per control: a server's first channel can share the server's id, and two siblings with one key
            made React keep every old bell (Wooting's #general showed dozens). */}
        {guildId ? <Bell key={`bell:${channel.id}`} channel={channel} /> : null}
        <button type="button" className="exo-dc-icon-btn" aria-label="Pinned messages" aria-pressed={panel === 'pins'} onClick={() => setPanel(p => p === 'pins' ? null : 'pins')}>
          <Icons.Pin size={20} />
        </button>
        {onToggleMembers ? <SideToggle channel={channel} guildId={guildId} shown={membersShown} onToggle={onToggleMembers} /> : null}
        <SearchBox key={`search:${guildId ?? channel.id}`} channel={channel} guildId={guildId} placeholder={searchLabel(channel, serverName)} ctx={ctx}
          onSearch={filters => { setSearched(filters); setPanel('search') }} onClear={() => { setSearched(null); setPanel(p => p === 'search' ? null : p) }} />
        <button type="button" className="exo-dc-icon-btn" aria-label="Inbox" aria-pressed={panel === 'inbox'} onClick={() => setPanel(p => p === 'inbox' ? null : 'inbox')}>
          <Icons.Inbox size={20} />
        </button>
      </span>
      {panel ? <HeadPanel panel={panel} channel={channel} searched={searched} ctx={ctx} onOpenThread={onOpenThread} onClose={() => setPanel(null)} /> : null}
    </header>
  )
}

// The channel's topic, Discord's way: its first line in the bar, mentions and links working; a press anywhere else on it
// opens all of it in Channel topic.
export function Topic({ channel, guildId, topic, ctx }: { channel: DiscordChannel; guildId: string | null; topic: string; ctx: MarkdownContext }) {
  const [open, setOpen] = useState(false)
  return (
    <>
      <span className="exo-dc-head-line" aria-hidden="true" />
      <span className="exo-dc-chat-topic" onClick={event => { if (!(event.target as Element).closest('a, button, img, .exo-dc-mention')) setOpen(true) }}>
        <button type="button" className="sr-only" onClick={() => setOpen(true)}>Open channel topic</button>
        <MarkdownLine text={topic} ctx={ctx} />
      </span>
      {open ? <TopicDialog channel={channel} guildId={guildId} topic={topic} ctx={ctx} onClose={() => setOpen(false)} /> : null}
    </>
  )
}

function TopicDialog({ channel, guildId, topic, ctx, onClose }: { channel: DiscordChannel; guildId: string | null; topic: string; ctx: MarkdownContext; onClose: () => void }) {
  const box = useRef<HTMLDivElement>(null)
  useLayer(box, onClose)
  return (
    <ModalBack onClose={onClose}>
      <div ref={box} className="exo-dc-modal exo-dc-topic" role="dialog" aria-modal="true" aria-label="Channel topic" tabIndex={-1} onClick={event => event.stopPropagation()}>
        <h2>Channel topic</h2>
        <span className="exo-dc-topic-where"><HeadIcon channel={channel} guildId={guildId} />{channel.name}</span>
        <div className="exo-dc-topic-text"><Markdown text={topic} ctx={ctx} /></div>
      </div>
    </ModalBack>
  )
}

// The list a header button opened: pins, search results, the inbox or the channel's threads.
function HeadPanel({ panel, channel, searched, ctx, onOpenThread, onClose }: {
  panel: 'pins' | 'search' | 'inbox' | 'threads'
  channel: DiscordChannel
  searched: DiscordSearchFilters | null
  ctx: MarkdownContext
  onOpenThread?: (thread: DiscordChannel) => void
  onClose: () => void
}) {
  if (panel === 'pins') return <Pins channel={channel} ctx={ctx} onClose={onClose} />
  if (panel === 'search') return searched ? <SearchResults channel={channel} filters={searched} ctx={ctx} onClose={onClose} /> : null
  if (panel === 'inbox') return <Inbox ctx={ctx} here={channel.id} onClose={onClose} />
  return onOpenThread ? <Threads channelId={channel.id} onOpen={thread => { onClose(); onOpenThread(thread) }} onClose={onClose} /> : null
}

// What the bar is over: the channel (a thread: its channel to go back to, a chevron, then the thread), or the DM.
function HeadTitle({ channel, guildId, parent, onBack }: { channel: DiscordChannel; guildId: string | null; parent?: DiscordChannel | null; onBack?: () => void }) {
  return (
    <>
      {parent && onBack ? (
        <>
          <button type="button" className="exo-dc-crumb" onClick={onBack} aria-label={`Back to ${parent.name}`}>
            <HeadIcon channel={parent} guildId={guildId} />
            <span>{parent.name}</span>
          </button>
          <Icons.Crumb size={16} />
          <Icons.Threads size={20} />
        </>
      ) : <HeadIcon channel={channel} guildId={guildId} />}
      <strong>{channel.name}</strong>
    </>
  )
}

// A chat in a side panel (beside a call, or a Server Guide resource), as Discord's sidebar heads it: the channel, its
// notifications when it is a server's text channel, and Close, nothing else.
function PanelHead({ channel, guildId, onHide }: { channel: DiscordChannel; guildId: string | null; onHide: () => void }) {
  return (
    <header className="exo-dc-chat-head">
      <HeadIcon channel={channel} guildId={guildId} />
      <strong>{channel.name}</strong>
      <span className="exo-dc-head-tools">
        {guildId && channel.kind === 'text' ? <Bell key={`bell:${channel.id}`} channel={channel} /> : null}
        <button type="button" className="exo-dc-icon-btn" aria-label="Close chat" onClick={onHide}><Icons.Close size={18} /></button>
      </span>
    </header>
  )
}

type Found = { messages: DiscordMessage[]; places: Record<string, DiscordPlace | null>; error: string | null; loading: boolean }

/** One of the header's lists from Discord, asked again when what it asks changes; a late answer to an old question is dropped. */
function useFound(ask: () => Promise<{ ok: boolean; message?: string | null; messages: DiscordMessage[]; places?: Record<string, DiscordPlace | null> }>, failed: string, key: string) {
  const [found, setFound] = useState<Found>({ messages: [], places: {}, error: null, loading: true })
  useEffect(() => {
    let live = true
    setFound({ messages: [], places: {}, error: null, loading: true })
    ask().then(result => {
      if (live) setFound({ messages: result.messages, places: result.places ?? {}, error: result.ok ? null : result.message ?? failed, loading: false })
    }).catch((err: unknown) => {
      report(err, 'ChatHeader.' + key)
      if (live) setFound({ messages: [], places: {}, error: failed, loading: false })
    })
    return () => { live = false }
  }, [key]) // eslint-disable-line react-hooks/exhaustive-deps -- key names what is asked
  return found
}

function Results({ title, found, ctx, empty, box, tools, here, onClose, onRemove }: {
  title: string
  found: Found
  ctx: MarkdownContext
  empty: string
  box: React.RefObject<HTMLDivElement | null>
  tools?: React.ReactNode
  /** The conversation open now: its rows don't repeat where they are. */
  here: string
  onClose: () => void
  /** The pins' Unpin on each row (Shift held: no question). */
  onRemove?: (message: DiscordMessage, shift: boolean) => void
}) {
  const { messages, places, loading, error } = found
  return (
    <div className="exo-dc-popover" ref={box} role="dialog" aria-label={title}>
      <p className="exo-dc-popover-title">{title}</p>
      {tools}
      <div className="exo-dc-popover-list">
        {loading ? <p className="exo-dc-note">Loading</p> : null}
        {error ? <p className="exo-dc-note" role="alert">{error}</p> : null}
        {!loading && !error && messages.length === 0 ? <p className="exo-dc-note">{empty}</p> : null}
        {messages.map(message => <Result key={message.id} message={message} place={places[message.channel_id] ?? null} here={here} ctx={ctx} onJumped={onClose}
          onRemove={onRemove ? shift => onRemove(message, shift) : undefined} removeLabel="Unpin" />)}
      </div>
    </div>
  )
}

// The pins, each with Discord's Unpin where you may pin (asked first, unless Shift is held).
function Pins({ channel, ctx, onClose }: { channel: DiscordChannel; ctx: MarkdownContext; onClose: () => void }) {
  const box = useOutside(onClose)
  const found = useFound(() => host.discordPins(channel.id).then(result => ({ ...result, messages: result.messages ?? [] })), 'Discord did not send the pins.', `pins:${channel.id}`)
  const [gone, setGone] = useState<Set<string>>(new Set())
  const [asking, setAsking] = useState<DiscordMessage | null>(null)
  const [error, setError] = useState<string | null>(null)
  const unpin = (message: DiscordMessage) => {
    setAsking(null); setError(null)
    setGone(set => new Set(set).add(message.id))
    const failed = (why?: string | null) => { setGone(set => { const next = new Set(set); next.delete(message.id); return next }); setError(why ?? 'Discord did not unpin that.') }
    void host.discordPins(channel.id, message.id, false).then(result => { if (!result.ok) failed(result.message) }, () => failed())
  }
  const shown = { ...found, error: error ?? found.error, messages: found.messages.filter(m => !gone.has(m.id)) }
  const canPin = !ctx.guildId || channel.canPin === true
  return (
    <>
      <Results title="Pinned messages" found={shown} ctx={ctx} here={channel.id} empty="This channel doesn't have any pinned messages yet." box={box} onClose={onClose}
        onRemove={canPin ? (message, shift) => { if (shift) unpin(message); else setAsking(message) } : undefined} />
      {asking ? <MessageConfirm kind="unpin" message={asking} channel={null} ctx={ctx} onCancel={() => setAsking(null)} onConfirm={() => unpin(asking)} /> : null}
    </>
  )
}

// Discord's thread browser: the channel's open threads, or its archived ones.
function Threads({ channelId, onOpen, onClose }: { channelId: string; onOpen: (thread: DiscordChannel) => void; onClose: () => void }) {
  const [archived, setArchived] = useState(false)
  const [threads, setThreads] = useState<DiscordThread[] | null>(null)
  const box = useOutside(onClose)
  useEffect(() => {
    let live = true
    setThreads(null)
    host.discordThreads(channelId, { archived }).then(result => { if (live) setThreads(result.threads ?? []) }).catch(() => { if (live) setThreads([]) })
    return () => { live = false }
  }, [channelId, archived])
  return (
    <div className="exo-dc-popover" ref={box} role="dialog" aria-label="Threads">
      <p className="exo-dc-popover-title">Threads</p>
      <div className="exo-dc-popover-tabs" role="group" aria-label="Which threads">
        <button type="button" className="exo-dc-tab" aria-pressed={!archived} onClick={() => setArchived(false)}>Active</button>
        <button type="button" className="exo-dc-tab" aria-pressed={archived} onClick={() => setArchived(true)}>Archived</button>
      </div>
      <div className="exo-dc-popover-list">
        {threads === null ? <p className="exo-dc-note">Loading</p> : null}
        {threads?.length === 0 ? <p className="exo-dc-note">{archived ? 'There are no archived threads.' : 'There are no active threads.'}</p> : null}
        {threads?.map(thread => (
          <button key={thread.id} type="button" className="exo-dc-post"
            onClick={() => onOpen({ id: thread.id, name: thread.name, type: 11, kind: 'text', canRead: true, canSend: thread.thread_metadata?.locked !== true })}>
            <strong>{thread.name}</strong>
            <span><Icons.Chat size={14} /> {thread.message_count ?? 0}</span>
          </button>
        ))}
      </div>
    </div>
  )
}

export const MUTE_FOR: [number, string][] = [[15, 'For 15 minutes'], [60, 'For 1 hour'], [180, 'For 3 hours'], [480, 'For 8 hours'], [1440, 'For 24 hours'], [0, 'Until I turn it back on']]
export const LEVELS: [0 | 1 | 2, string][] = [[0, 'All messages'], [1, 'Only @mentions'], [2, 'Nothing']]

// Discord's bell: mute for a while or until turned back on (or unmute), and what this channel notifies you of.
function Bell({ channel }: { channel: DiscordChannel }) {
  const [open, setOpen] = useState(false)
  const [muted, setMuted] = useState(channel.muted === true)
  const [until, setUntil] = useState(channel.mutedUntil ?? null)
  const [level, setLevel] = useState(channel.notify ?? null)
  const [error, setError] = useState<string | null>(null)
  const change = (next: Parameters<typeof host.discordChannelNotify>[1], apply: () => void) => {
    setOpen(false); setError(null)
    void host.discordChannelNotify(channel.id, next).then(result => { if (result.ok) apply(); else setError(result.message ?? 'Discord did not change that.') })
      .catch(() => setError('Discord did not change that.'))
  }
  return (
    <span className="exo-dc-menu-anchor">
      <button type="button" className={`exo-dc-icon-btn${muted ? ' is-off' : ''}`} aria-label="Notification settings" aria-expanded={open} onClick={() => setOpen(o => !o)}>
        {muted ? <Icons.BellOff size={20} /> : <Icons.Bell size={20} />}
      </button>
      {open ? (
        <BellMenu onClose={() => setOpen(false)}>
          {muted ? (
            <button type="button" role="menuitem" onClick={() => change({ muted: false }, () => { setMuted(false); setUntil(null) })}>
              <span className="exo-dc-menu-text">Unmute channel{until ? <small>Muted until {new Date(until).toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' })}</small> : null}</span>
            </button>
          ) : MUTE_FOR.map(([minutes, label]) => (
            <button key={minutes} type="button" role="menuitem" onClick={() => change(minutes ? { muted: true, minutes } : { muted: true }, () => { setMuted(true); setUntil(minutes ? Date.now() + minutes * 60_000 : null) })}>
              <span className="exo-dc-menu-text">Mute {label.replace('For', 'for').replace('Until', 'until')}</span>
            </button>
          ))}
          <span className="exo-dc-menu-line" role="separator" />
          <button type="button" role="menuitemradio" aria-checked={level === null} onClick={() => change({ level: 3 }, () => setLevel(null))}>
            <span className="exo-dc-menu-text">Use default<small>{LEVELS.find(([value]) => value === (channel.notifyDefault ?? 0))?.[1]}</small></span>
            {level === null ? <Icons.Check size={16} /> : null}
          </button>
          {LEVELS.map(([value, label]) => (
            <button key={value} type="button" role="menuitemradio" aria-checked={level === value} onClick={() => change({ level: value }, () => setLevel(value))}>
              <span className="exo-dc-menu-text">{label}</span>
              {level === value ? <Icons.Check size={16} /> : null}
            </button>
          ))}
        </BellMenu>
      ) : null}
      {error ? <span className="exo-dc-head-error exo-glass" role="alert">{error}</span> : null}
    </span>
  )
}

function BellMenu({ onClose, children }: { onClose: () => void; children: React.ReactNode }) {
  const box = useOutside(onClose)
  return <div className="exo-dc-menu exo-dc-bell-menu" ref={box} role="menu" aria-label="Notification settings">{children}</div>
}

function HeadIcon({ channel, guildId }: { channel: DiscordChannel; guildId: string | null }) {
  if (guildId) return channel.type === 5 ? <Icons.Announcement size={22} /> : channel.kind === 'voice' ? (channel.locked ? <Icons.VoiceLock size={22} /> : <Icons.Voice size={22} />) : channel.locked ? <Icons.HashLock size={22} /> : <Icons.Hash size={22} />
  if (channel.dm) return <Avatar src={channel.dm.avatar} name={channel.name} status={channel.dm.group ? null : channel.dm.status} size={24} />
  return <Icons.At size={22} />
}

// "Search Uhh" in a server, "Search umlazy" in a DM, as Discord labels it.
function searchLabel(channel: DiscordChannel, serverName?: string | null) {
  if (serverName) return `Search ${serverName}`
  return channel.dm?.username ? `Search ${channel.dm.username}` : 'Search'
}

// The side pane's toggle: the member list in a server, a DM's tools and profile panel.
function SideToggle({ channel, guildId, shown, onToggle }: { channel: DiscordChannel; guildId: string | null; shown: boolean; onToggle: () => void }) {
  if (channel.dm) return <DmTools channel={channel} profileShown={shown} onToggleProfile={onToggle} />
  if (!guildId) return null
  return (
    <button type="button" className="exo-dc-icon-btn" aria-label={shown ? 'Hide member list' : 'Show member list'} aria-pressed={shown} onClick={onToggle}>
      <Icons.Members size={20} />
    </button>
  )
}

// A DM's header tools: Add Friends to DM, and the profile panel's toggle for a 1:1 DM (a group's member list's for a group).
function DmTools({ channel, profileShown, onToggleProfile }: { channel: DiscordChannel; profileShown: boolean; onToggleProfile: () => void }) {
  return (
    <>
      <AddToDm channel={channel} />
      {channel.dm?.group ? (
        <button type="button" className="exo-dc-icon-btn" aria-label={profileShown ? 'Hide member list' : 'Show member list'} aria-pressed={profileShown} onClick={onToggleProfile}>
          <Icons.Members size={20} />
        </button>
      ) : (
        <button type="button" className="exo-dc-icon-btn" aria-label={profileShown ? 'Hide user profile' : 'Show user profile'} aria-pressed={profileShown} onClick={onToggleProfile}>
          <Icons.Profile size={20} />
        </button>
      )}
    </>
  )
}

// Discord's Add Friends to DM: pick a friend; a 1:1 DM becomes a new group, a group DM gains them.
function AddToDm({ channel }: { channel: DiscordChannel }) {
  const [open, setOpen] = useState(false)
  return (
    <span className="exo-dc-menu-anchor">
      <button type="button" className="exo-dc-icon-btn" aria-label="Add friends to DM" aria-expanded={open} onClick={() => setOpen(o => !o)}><Icons.AddFriend size={20} /></button>
      {open ? (
        <FriendPicker title="Select friends" onClose={() => setOpen(false)} onPick={friend => {
          setOpen(false)
          void host.discordChannel(channel.id, 'add', undefined, friend.id).then(result => {
            if (result.ok && result.channelId && result.channelId !== channel.id) window.dispatchEvent(new CustomEvent('exo-dc-open-dm', { detail: result.channelId }))
          }).catch(() => { })
        }} />
      ) : null}
    </span>
  )
}
