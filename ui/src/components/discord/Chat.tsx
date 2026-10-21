import { Fragment, useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { host, onHostEvent } from '../../lib/host'
import { report } from '../../lib/report'
import { shouldReduceMotion } from '../../lib/motionPreference'
import { applyPollVote, applyReaction, avatarHashOf, hasJump, idBefore, snowflakeTime, takeJump, type DiscordChannel, type DiscordHome, type DiscordMessage, type DiscordUser, type PendingUpload } from '../../lib/discord'
import { Message, type MessageActions } from './Message'
import { Composer } from './Composer'
import { ChatHeader } from './ChatHeader'
import { MemberList } from './MemberList'
import { DmProfile } from './DmProfile'
import { MessageConfirm, type ConfirmKind } from './MessageConfirm'
import { ForwardDialog, NewThreadDialog } from './MessageTools'
import { Icons } from './icons'
import type { MarkdownContext } from './markdown'
import { useAccountSettings, useDiscordPrefs } from '../../lib/discordPrefs'
import { applyMessage, byId, forgetMessages, FRESH_MS, isAfter, isAsking, keepMessages, keptMessages, mergeNewest, newestMessages, PAGE } from './messageCache'

const GROUP_MS = 7 * 60 * 1000
const UPLOAD_LIMIT = 25 * 1024 * 1024

type Load = { state: 'loading' | 'ready' | 'error'; message?: string | null }
type Edge = { where: 'older' | 'newer'; message: string }

/** A channel or DM: its messages (older ones load on scrolling up), live changes, typing and the composer. */
export function Chat({ channel, guildId, meId, me, hidden, channelName, roles, active, onCall, inCall, serverName, onOpenThread, parent, onBack, onHide }: {
  channel: DiscordChannel
  guildId: string | null
  meId: string | null
  /** You, as the side panel shows you: a message you send shows under your name and picture at once. */
  me?: DiscordHome['me']
  /** Who you blocked or ignored: their messages fold away, as in Discord. */
  hidden?: Pick<DiscordHome, 'blocked' | 'ignored'> | null
  channelName: (id: string) => string | null
  roles?: Record<string, { name: string; color: string | null }>
  active: boolean
  /** A DM: start (or join) its call. */
  onCall?: () => void
  inCall?: boolean
  serverName?: string | null
  onOpenThread?: (thread: DiscordChannel) => void
  /** A thread's channel (or forum), named above it to go back to. */
  parent?: DiscordChannel | null
  onBack?: () => void
  /** Beside a voice call: no member list of its own. */
  /** Beside a voice call, as Discord's chat panel: its own short header, and Close Chat hides it. */
  onHide?: () => void
}) {
  const channelId = channel.id
  const scroller = useRef<HTMLDivElement>(null)
  const history = useHistory(channelId, meId, scroller)
  const { messages, setMessages, load, newer, edge, lastRead, pinned } = history
  const [typing, setTyping] = useTyping(channelId)
  const [replyTo, setReplyTo] = useState<DiscordMessage | null>(null)
  // Discord's @ ON/OFF on a reply: on, and no choice (null) when replying to yourself.
  const [replyPing, setReplyPing] = useState<boolean | null>(true)
  const [editing, setEditing] = useState<DiscordMessage | null>(null)
  const [error, setError] = useState<string | null>(null)
  const acked = useRef<string | null>(null)
  // After Mark Unread the conversation stays unread until you write in it or open it again, as in Discord.
  const holdUnread = useRef(false)

  // A new message from someone ends their typing.
  useEffect(() => onHostEvent('discord.message', event => {
    const author = event.kind === 'create' && event.channelId === channelId ? event.message?.author.id : undefined
    if (author) setTyping(map => { if (!map.has(author)) return map; const next = new Map(map); next.delete(author); return next })
  }), [channelId, setTyping])

  // Notifications skip the conversation on screen; a hidden room shows none.
  useEffect(() => {
    if (!active) return
    void host.discordView(channelId).catch(() => { })
    return () => { void host.discordView(null).catch(() => { }) }
  }, [active, channelId])

  // Marks read what the user has seen, as Discord's automatic ack: while the room shows, the newest page is loaded and
  // the list sits at its bottom, measured (a jump that lands at the bottom, or a channel too short to scroll, sends no
  // scroll). The host reads to the channel's newest message, as Discord acks its last_message_id.
  const last = messages.findLast(m => !m.pending)?.id ?? null
  const ackIfSeen = useCallback(() => {
    const el = scroller.current
    if (!el || !active || !last || load.state !== 'ready' || newer || acked.current === last || holdUnread.current) return
    if (el.scrollHeight - el.scrollTop - el.clientHeight >= 48) return
    pinned.current = true
    acked.current = last
    void host.discordAck(channelId, last).catch(() => { })
  }, [active, last, load.state, newer, channelId, pinned])
  useEffect(ackIfSeen, [ackIfSeen])

  const { setLastRead } = history
  const newBar = useNewBar(scroller, messages, lastRead, meId, history.older)
  const markAllRead = () => {
    holdUnread.current = false
    if (last) { acked.current = last; void host.discordAck(channelId, last).catch(() => { }) }
    setLastRead(last)
  }

  function onScroll() {
    const el = scroller.current
    if (!el) return
    newBar.check()
    const bottom = el.scrollHeight - el.scrollTop - el.clientHeight < 48
    pinned.current = bottom && !newer
    if (el.scrollTop < 400) void history.loadOlder()
    if (newer && el.scrollHeight - el.scrollTop - el.clientHeight < 400) void history.loadNewer()
    ackIfSeen()
  }

  const users = useUsers(messages)
  const colors = useAuthorColors(guildId, messages)
  const dmName = dmPartner(channel)
  const manages = channel.canManageMessages
  const ctx = useMemo<MarkdownContext>(() => ({ users, channelName, meId, guildId, colors, roles, moderator: !!guildId && manages === true, dmName }),
    [users, channelName, meId, guildId, colors, roles, manages, dmName])
  const [membersPreferred, setMembersShown] = useState(readMembersShown)
  const membersShown = membersPreferred && !onHide
  const canSend = channel.canSend !== false
  const onReply = useCallback((message: DiscordMessage) => { setEditing(null); setReplyTo(message); setReplyPing(message.author.id !== meId ? true : null) }, [meId])
  const { actions, dialogs } = useMessageTools({
    channel, guildId, ctx, threadable: !!guildId && channel.kind === 'text' && channel.type !== 11 && channel.type !== 12 && !parent, onOpenThread,
    setMessages, setError, setEditing, setLastRead, holdUnread, onReply,
  })

  const sendNow = useSend({ channelId, me: me ?? null, meId, replyTo, replyPing, setReplyTo, setError, setMessages, pinned, newer, toPresent: history.toPresent })
  const send = (content: string, files: File[]) => { holdUnread.current = false; return sendNow(content, files) }
  const withRetry = useRetry(actions, sendNow, setMessages)

  function editLast() {
    const mine = messages.findLast(m => m.author.id === meId && !m.pending && (m.type === 0 || m.type === 19))
    if (mine) setEditing(mine)
  }

  const typers = [...typing.values()].map(t => t.name)
  return (
    <section className="exo-dc-chat" aria-label={channel.name}>
      {dialogs}
      <ChatHeader channel={channel} guildId={guildId} serverName={serverName} onOpenThread={onOpenThread} ctx={ctx} membersShown={membersShown} onCall={onCall} inCall={inCall}
        parent={parent} onBack={onBack} onHide={onHide}
        onToggleMembers={onHide ? undefined : () => setMembersShown(shown => { writeMembersShown(!shown); return !shown })} />
      <div className="exo-dc-chat-body">
      <div className="exo-dc-chat-main">
      <NewBar bar={newBar} lastRead={lastRead} messages={messages} meId={meId} onRead={markAllRead} />
      <div className="exo-dc-scroll" ref={scroller} onScroll={onScroll}>
        <HistoryTop history={history} start={guildId ? `#${channel.name}` : 'your conversation'} />
        <MessageRows messages={messages} lastRead={lastRead} meId={meId} hidden={hidden} ctx={ctx} canSend={canSend} editing={editing?.id ?? null} actions={withRetry} />
        {edge?.where === 'newer' ? <Retry message={edge.message} onRetry={() => void history.loadNewer(true)} /> : null}
      </div>
      {newer ? (
        <div className="exo-dc-present">
          <span>You're viewing older messages</span>
          <button type="button" onClick={history.toPresent}>Jump to Present</button>
        </div>
      ) : null}
      <Composer
        key={channelId}
        channelId={channelId}
        guildId={guildId}
        users={users}
        placeholder={canSend ? `Message ${target(channel, guildId)}` : "You can't send messages here"}
        disabled={!canSend || load.state === 'error'}
        replyTo={replyTo}
        replyPing={replyPing}
        onReplyPing={() => setReplyPing(on => on === null ? null : !on)}
        slowmode={channel.slowmode}
        error={error}
        typers={typers}
        onSend={send}
        onCancel={() => { setReplyTo(null); setError(null) }}
        onEditLast={editLast}
        onMarkRead={markAllRead}
      />
      </div>
      {/* A DM's profile stays read and laid out while hidden, so showing it again is instant, as Discord's is. */}
      {membersShown || !guildId ? <SidePane channel={channel} guildId={guildId} hidden={!membersShown} /> : null}
      </div>
    </section>
  )
}

type ToolsSetup = {
  channel: DiscordChannel
  guildId: string | null
  ctx: MarkdownContext
  threadable: boolean
  onOpenThread?: (thread: DiscordChannel) => void
  setMessages: React.Dispatch<React.SetStateAction<DiscordMessage[]>>
  setError: (error: string | null) => void
  setEditing: (message: DiscordMessage | null) => void
  setLastRead: (id: string | null) => void
  holdUnread: React.RefObject<boolean>
  onReply: (message: DiscordMessage) => void
}

/**
 * What a message's hover bar and menu do here, and the dialogs they open (Delete, Pin, Unpin, Forward To, Create Thread).
 * Delete others' messages with Manage Messages, pin with Pin Messages (anyone in a DM); Discord asks before a delete, a pin
 * or an unpin unless Shift is held.
 */
function useMessageTools({ channel, guildId, ctx, threadable, onOpenThread, setMessages, setError, setEditing, setLastRead, holdUnread, onReply }: ToolsSetup) {
  const channelId = channel.id
  const [asking, setAsking] = useState<{ kind: ConfirmKind; message: DiscordMessage } | null>(null)
  const [forwarding, setForwarding] = useState<DiscordMessage | null>(null)
  const [threading, setThreading] = useState<DiscordMessage | null>(null)
  const shiftHeld = useShift()
  const fail = useCallback((fallback: string) => (result: { ok: boolean; message?: string | null }) => { if (!result.ok) setError(result.message ?? fallback) }, [setError])
  const removeMessage = useCallback((message: DiscordMessage) => {
    void host.discordDelete(channelId, message.id).then(fail('Discord did not delete that.')).catch(() => setError('Discord did not delete that.'))
  }, [channelId, fail, setError])
  const pin = useCallback((message: DiscordMessage) => {
    void host.discordPins(channelId, message.id, !message.pinned).then(result => {
      if (!result.ok) setError(result.message ?? 'Discord did not pin that.')
      else setMessages(list => list.map(m => m.id === message.id ? { ...m, pinned: !message.pinned } : m))
    }).catch(() => setError('Discord did not pin that.'))
  }, [channelId, setError, setMessages])
  const actions = useMemo<MessageActions>(() => ({
    canManageMessages: !!guildId && channel.canManageMessages === true,
    canPin: !guildId || channel.canPin === true,
    onReply,
    onForward: setForwarding,
    onThread: threadable ? setThreading : undefined,
    onOpenThread: onOpenThread ? thread => onOpenThread({ id: thread.id, name: thread.name, type: 11, kind: 'text', canRead: true, canSend: true }) : undefined,
    onMarkUnread: message => {
      const before = idBefore(message.id)
      holdUnread.current = true
      setLastRead(before)
      void host.discordAck(channelId, before, true).then(fail('Discord did not mark that unread.')).catch(() => setError('Discord did not mark that unread.'))
    },
    onEdit: message => setEditing(message),
    onEditDone: () => setEditing(null),
    onReact: (message, emoji, add, burst) => {
      void host.discordReact(channelId, message.id, emoji, add, { burst }).then(fail('Discord did not take that reaction.')).catch(() => setError('Discord did not take that reaction.'))
    },
    onPin: message => { if (shiftHeld.current) pin(message); else setAsking({ kind: message.pinned ? 'unpin' : 'pin', message }) },
    onDelete: message => { if (shiftHeld.current) removeMessage(message); else setAsking({ kind: 'delete', message }) },
  }), [channelId, guildId, channel.canManageMessages, channel.canPin, onReply, threadable, onOpenThread, holdUnread, setLastRead, fail, setError, setEditing, shiftHeld, removeMessage, pin])
  const dialogs = (
    <>
      {asking ? (
        <MessageConfirm kind={asking.kind} message={asking.message} channel={guildId ? channel.name : null} ctx={ctx} onCancel={() => setAsking(null)}
          onConfirm={() => { if (asking.kind === 'delete') removeMessage(asking.message); else pin(asking.message); setAsking(null) }} />
      ) : null}
      {forwarding ? <ForwardDialog message={forwarding} onClose={() => setForwarding(null)} /> : null}
      {threading ? <NewThreadDialog message={threading} onClose={() => setThreading(null)} onCreated={thread => { setThreading(null); actions.onOpenThread?.(thread) }} /> : null}
    </>
  )
  return { actions, dialogs }
}

// Whether the NEW line is above the view (or the first unread isn't loaded yet), and how many arrived since.
function useNewBar(scroller: React.RefObject<HTMLDivElement | null>, messages: DiscordMessage[], lastRead: string | null, meId: string | null, older: boolean) {
  const count = useMemo(() => lastRead ? messages.filter(m => !m.pending && m.author.id !== meId && isAfter(m.id, lastRead)).length : 0, [messages, lastRead, meId])
  const [above, setAbove] = useState(false)
  const check = useCallback(() => {
    const el = scroller.current
    if (!el || count === 0 || !lastRead) { setAbove(false); return }
    const line = el.querySelector('.exo-dc-new')
    setAbove(line ? line.getBoundingClientRect().bottom < el.getBoundingClientRect().top + 8 : older && !!messages[0] && isAfter(messages[0].id, lastRead))
  }, [scroller, count, lastRead, older, messages])
  useLayoutEffect(check, [check])
  return { count, above, check }
}

function NewBar({ bar, lastRead, messages, meId, onRead }: { bar: ReturnType<typeof useNewBar>; lastRead: string | null; messages: DiscordMessage[]; meId: string | null; onRead: () => void }) {
  if (!bar.above || !lastRead) return null
  const { count } = bar
  const jumpTo = firstUnread(messages, lastRead, meId) ?? lastRead
  return (
    <div className="exo-dc-new-bar" role="status">
      <button type="button" className="exo-dc-new-bar-jump" onClick={() => window.dispatchEvent(new CustomEvent('exo-dc-jump', { detail: jumpTo }))}>
        {count >= 50 ? '50+' : count} new {count === 1 ? 'message' : 'messages'} since {sinceLabel(lastRead)}
      </button>
      <button type="button" className="exo-dc-new-bar-read" onClick={onRead}>Mark As Read <Icons.Check size={14} /></button>
    </div>
  )
}

// The messages with Discord's day lines and NEW line, each grouped under the one before when it continues it.
function MessageRows({ messages, lastRead, meId, hidden, ctx, canSend, editing, actions }: {
  messages: DiscordMessage[]
  lastRead: string | null
  meId: string | null
  hidden?: Pick<DiscordHome, 'blocked' | 'ignored'> | null
  ctx: MarkdownContext
  canSend: boolean
  editing: string | null
  actions: MessageActions
}) {
  // Runs of blocked or ignored people's messages fold into one line, opened by its first message's id.
  const [shown, setShown] = useState<Set<string>>(() => new Set())
  const runs = foldRuns(messages, meId, hidden)
  const toggle = (id: string) => setShown(set => { const next = new Set(set); if (!next.delete(id)) next.add(id); return next })
  return messages.map((message, i) => {
    const fold = runs[i]
    const open = !fold || shown.has(fold.first)
    if (!open) return fold.start ? <Folded key={message.id} reason={fold.reason} count={fold.count} onShow={() => toggle(fold.first)} /> : null
    const before = messages[i - 1]
    const newDay = !before || new Date(before.timestamp).toDateString() !== new Date(message.timestamp).toDateString()
    const grouped = !newDay && !!before && before.author.id === message.author.id && !message.referenced_message && message.type === 0 &&
      new Date(message.timestamp).getTime() - new Date(before.timestamp).getTime() < GROUP_MS
    const firstNew = lastRead !== null && !message.pending && message.author.id !== meId && isAfter(message.id, lastRead) &&
      (!before || before.pending || !isAfter(before.id, lastRead))
    return (
      <Fragment key={message.id}>
        {newDay ? <DayDivider iso={message.timestamp} /> : null}
        {firstNew ? <div className="exo-dc-new" role="separator"><span>NEW</span></div> : null}
        {fold?.start ? <Folded reason={fold.reason} count={fold.count} open onShow={() => toggle(fold.first)} /> : null}
        <Message message={message} grouped={grouped} ctx={ctx} canSend={canSend} editing={editing === message.id}
          mine={message.author.id === meId} actions={actions} />
      </Fragment>
    )
  })
}

type Fold = { reason: 'blocked' | 'ignored'; first: string; count: number; start: boolean }

// Each message by someone you blocked or ignored, with the run of them it belongs to (as Discord groups them).
function foldRuns(messages: DiscordMessage[], meId: string | null, hidden?: Pick<DiscordHome, 'blocked' | 'ignored'> | null): (Fold | null)[] {
  const reasons = messages.map(m => m.author.id === meId ? null
    : hidden?.blocked?.includes(m.author.id) ? 'blocked' as const : hidden?.ignored?.includes(m.author.id) ? 'ignored' as const : null)
  const runs: (Fold | null)[] = []
  let head: Fold | null = null
  reasons.forEach((reason, i) => {
    if (reason && head?.reason === reason) head.count++
    else head = reason ? { reason, first: messages[i].id, count: 1, start: true } : null
    runs.push(head)
  })
  return runs.map((fold, i) => fold && { ...fold, start: runs[i - 1] !== fold })
}

// Discord's folded line: "3 blocked messages", then Show messages (or, opened, Collapse messages).
function Folded({ reason, count, open, onShow }: { reason: string; count: number; open?: boolean; onShow: () => void }) {
  const plural = count === 1 ? '' : 's'
  return (
    <p className="exo-dc-folded">
      <span>{count} {reason} message{plural}</span>
      <span aria-hidden="true">—</span>
      <button type="button" className="exo-dc-link" onClick={onShow}>{open ? 'Collapse' : 'Show'} message{plural}</button>
    </p>
  )
}

// Over the messages: loading, what failed with Retry, or Discord's start of the conversation.
function HistoryTop({ history, start }: { history: ReturnType<typeof useHistory>; start: string }) {
  const { load, edge, older } = history
  if (load.state === 'loading') return <p className="exo-dc-note" role="status">Loading messages</p>
  if (load.state === 'error') return <Retry message={load.message ?? "Discord didn't send this channel."} onRetry={history.toPresent} />
  if (edge?.where === 'older') return <Retry message={edge.message} onRetry={() => void history.loadOlder(true)} />
  return older ? null : <p className="exo-dc-start">This is the start of {start}.</p>
}

function Retry({ message, onRetry }: { message: string; onRetry: () => void }) {
  return (
    <p className="exo-dc-note exo-dc-retry" role="alert">
      {message}
      <button type="button" className="exo-dc-tab" onClick={onRetry}>Retry</button>
    </p>
  )
}

/**
 * A channel's messages: the newest page (or the one around a message jumped to), older pages scrolling up,
 * newer ones scrolling down after a jump, and live changes. A page that fails stops asking until Retry.
 * A conversation seen before (or hovered) shows at once as it was kept, and Discord's newest page is laid over it.
 */
function useHistory(channelId: string, meId: string | null, scroller: React.RefObject<HTMLDivElement | null>) {
  const [kept] = useState(() => hasJump(channelId) ? null : keptMessages(channelId))
  const [messages, setMessages] = useState<DiscordMessage[]>(() => kept?.messages ?? [])
  const [load, setLoad] = useState<Load>({ state: kept ? 'ready' : 'loading' })
  const [older, setOlder] = useState(kept?.older ?? true)
  // Newer messages than the ones shown (after a jump): live ones wait until Jump to Present.
  const [newer, setNewer] = useState(false)
  const [edge, setEdge] = useState<Edge | null>(null)
  // The newest message read when the channel opened: Discord's red NEW line sits after it.
  const [lastRead, setLastRead] = useState<string | null>(kept?.lastRead ?? null)
  const pinned = useRef(true)
  const keepFromBottom = useRef<number | null>(null)
  const scrollTo = useRef<string | null>(null)
  const busy = useRef(false)
  // Each first page replaces what any earlier request would still bring.
  const generation = useRef(0)
  const firstRead = useRef(kept?.lastRead === undefined)
  // The message the conversation opened to show (kept for the mount, so a remount in development shows it too).
  const opening = useRef<string | null | undefined>(undefined)
  const newerNow = useRef(false)
  const back = useRef(false)
  newerNow.current = newer
  const shown = useRef({ messages, older, newer, ready: load.state === 'ready' })
  shown.current = { messages, older, newer, ready: load.state === 'ready' }

  const loadPage = useCallback(async (around: string | null) => {
    const generationNow = ++generation.current
    setLoad({ state: 'loading' }); setEdge(null)
    try {
      const result = around ? await host.discordMessages(channelId, { around }) : await newestMessages(channelId, false)
      if (generationNow !== generation.current) return
      if (!result.ok || !result.messages) { setLoad({ state: 'error', message: result.message }); return }
      if (firstRead.current) { firstRead.current = false; setLastRead(result.lastRead ?? null) }
      const page = byId(result.messages)
      const after = around ? page.filter(m => isAfter(m.id, around)).length : 0
      setMessages(page)
      setOlder(around ? page.length - after > PAGE / 2 - 1 : page.length >= PAGE)
      setNewer(around ? after >= PAGE / 2 - 1 : false)
      pinned.current = !around
      scrollTo.current = around
      setLoad({ state: 'ready' })
    } catch (err) {
      report(err, 'DiscordChat.messages')
      if (generationNow === generation.current) setLoad({ state: 'error' })
    }
  }, [channelId])

  // Discord's newest page over what shows (a conversation kept from before, or after a reconnect).
  const refresh = useCallback(async () => {
    if (newerNow.current) return
    const generationNow = generation.current
    try {
      const result = await newestMessages(channelId, false)
      if (generationNow !== generation.current || newerNow.current || !result.ok || !result.messages) return
      if (firstRead.current) { firstRead.current = false; setLastRead(result.lastRead ?? null) }
      const page = byId(result.messages)
      if (mergeNewest(shown.current.messages, page).gap) setOlder(true)
      setMessages(list => mergeNewest(list, page).messages)
    } catch (err) {
      report(err, 'DiscordChat.refresh')
    }
  }, [channelId])

  // First page: the newest, or around the message a search, pin or the Inbox jumps to; a kept one shows as it was.
  // Back on screen (the room was hidden, which pauses its effects): what shows stays, and only the newest is laid over it.
  useEffect(() => {
    if (opening.current === undefined) opening.current = takeJump(channelId)
    if (back.current && shown.current.ready && shown.current.messages.length > 0) void refresh()
    else if (kept && !opening.current) {
      if (Date.now() - kept.at > FRESH_MS || isAsking(channelId)) void refresh()
    } else {
      setMessages([]); setOlder(true); setNewer(false)
      firstRead.current = true
      void loadPage(opening.current)
    }
    back.current = true
    const requests = generation
    return () => { requests.current++ }
  }, [channelId, loadPage, kept, refresh])

  // Kept for coming back, unless it shows older history after a jump.
  useEffect(() => () => {
    const { messages, older, newer, ready } = shown.current
    if (newer || !ready) forgetMessages(channelId)
    else keepMessages(channelId, messages, older)
  }, [channelId])

  // A reconnect may have missed messages: Discord's newest page again.
  useEffect(() => onHostEvent('discord.state', event => { if (event.state === 'ready') void refresh() }), [refresh])

  // A jump to a message here: to it when it's loaded, else to the page around it.
  useEffect(() => {
    const jump = (id: string) => {
      const el = scroller.current?.querySelector(`[data-msg-id="${id}"]`)
      if (el) flash(el, true)
      else void loadPage(id)
    }
    const fromQuote = (event: Event) => jump((event as CustomEvent<string>).detail)
    const fromOpen = () => { const id = takeJump(channelId); if (id) jump(id) }
    window.addEventListener('exo-dc-jump', fromQuote)
    window.addEventListener('exo-discord-open', fromOpen)
    return () => { window.removeEventListener('exo-dc-jump', fromQuote); window.removeEventListener('exo-discord-open', fromOpen) }
  }, [channelId, loadPage, scroller])

  useLiveMessages(channelId, meId, setMessages, newerNow)

  // Stays at the bottom when it was there; keeps the reading position when older messages arrive above; shows a message jumped to.
  useLayoutEffect(() => {
    const el = scroller.current
    if (!el) return
    if (scrollTo.current) {
      const target = el.querySelector(`[data-msg-id="${scrollTo.current}"]`)
      scrollTo.current = null
      if (target) flash(target, false)
    } else if (keepFromBottom.current !== null) {
      el.scrollTop = el.scrollHeight - keepFromBottom.current
      keepFromBottom.current = null
    } else if (pinned.current) el.scrollTop = el.scrollHeight
  }, [messages, scroller])

  // The room hidden and shown again (display: none drops an element's scroll position) or the messages growing under
  // it (a picture or embed loading): back at the bottom when it was there, else as far from the bottom as it was left.
  useLayoutEffect(() => {
    const el = scroller.current
    if (!el) return
    let fromBottom = 0
    const remember = () => { if (el.clientHeight > 0) fromBottom = el.scrollHeight - el.scrollTop - el.clientHeight }
    const settle = () => {
      if (el.clientHeight === 0) return
      el.scrollTop = pinned.current ? el.scrollHeight : el.scrollHeight - el.clientHeight - fromBottom
    }
    const watch = new ResizeObserver(settle)
    watch.observe(el)
    // Each message is its own row in the scroller: new ones are watched as they come.
    for (const child of el.children) watch.observe(child)
    const rows = new MutationObserver(changes => { for (const change of changes) for (const node of change.addedNodes) if (node instanceof Element) watch.observe(node) })
    rows.observe(el, { childList: true })
    el.addEventListener('scroll', remember, { passive: true })
    return () => { watch.disconnect(); rows.disconnect(); el.removeEventListener('scroll', remember) }
  }, [scroller])

  // A new look (text size, spacing, compact) changes every message's height: stay at the bottom if there.
  const { fontSize, groupSpacing } = useDiscordPrefs()
  const compact = useAccountSettings().messageDisplayCompact
  useLayoutEffect(() => {
    const el = scroller.current
    if (el && pinned.current) el.scrollTop = el.scrollHeight
  }, [fontSize, groupSpacing, compact, scroller])

  const loadEdge = useCallback(async (where: 'older' | 'newer', retry: boolean) => {
    const real = messages.filter(m => !m.pending)
    const from = where === 'older' ? real[0] : real.at(-1)
    if ((where === 'older' ? !older : !newer) || busy.current || !from || (edge && !retry) || load.state !== 'ready') return
    busy.current = true
    const generationNow = generation.current
    setEdge(null)
    const failed = where === 'older' ? "Discord didn't send older messages." : "Discord didn't send newer messages."
    try {
      const result = await host.discordMessages(channelId, where === 'older' ? { before: from.id } : { after: from.id })
      if (generationNow !== generation.current) return
      if (!result.ok || !result.messages) { setEdge({ where, message: result.message ?? failed }); return }
      const page = byId(result.messages)
      const el = scroller.current
      if (where === 'older') {
        if (el) keepFromBottom.current = el.scrollHeight - el.scrollTop
        setOlder(page.length >= PAGE)
        setMessages(list => [...page.filter(m => !list.some(x => x.id === m.id)), ...list])
      } else {
        setNewer(page.length >= PAGE)
        setMessages(list => [...list, ...page.filter(m => !list.some(x => x.id === m.id))])
      }
    } catch (err) {
      report(err, 'DiscordChat.' + where)
      if (generationNow === generation.current) setEdge({ where, message: failed })
    } finally {
      busy.current = false
    }
  }, [channelId, messages, older, newer, edge, load.state, scroller])

  const toPresent = useCallback(() => loadPage(null), [loadPage])
  return {
    messages, setMessages, load, older, newer, edge, lastRead, setLastRead, pinned, toPresent,
    loadOlder: (retry = false) => loadEdge('older', retry),
    loadNewer: (retry = false) => loadEdge('newer', retry),
  }
}

// Live: new, edited and deleted messages, reactions and poll votes. New ones wait while older messages show.
function useLiveMessages(channelId: string, meId: string | null, setMessages: React.Dispatch<React.SetStateAction<DiscordMessage[]>>, newer: React.RefObject<boolean>) {
  useEffect(() => {
    const offMessage = onHostEvent('discord.message', event => {
      if (event.channelId === channelId) setMessages(list => applyMessage(list, event, meId, newer.current))
    })
    const offReaction = onHostEvent('discord.reaction', event => {
      if (event.channelId !== channelId) return
      setMessages(list => list.map(m => m.id === event.messageId ? { ...m, reactions: applyReaction(m.reactions, event) } : m))
    })
    const offPoll = onHostEvent('discord.pollVote', event => {
      if (event.channelId !== channelId) return
      setMessages(list => list.map(m => m.id === event.messageId && m.poll ? { ...m, poll: applyPollVote(m.poll, event.answerId, event.add, event.me) } : m))
    })
    return () => { offMessage(); offReaction(); offPoll() }
  }, [channelId, meId, setMessages, newer])
}

// Who is typing; names fade after ten seconds without another ping.
function useTyping(channelId: string) {
  const [typing, setTyping] = useState<Map<string, { name: string; until: number }>>(new Map())
  useEffect(() => {
    setTyping(new Map())
    return onHostEvent('discord.typing', event => {
      if (event.channelId !== channelId) return
      setTyping(map => new Map(map).set(event.userId, { name: event.name, until: Date.now() + 10_000 }))
    })
  }, [channelId])
  useEffect(() => {
    if (typing.size === 0) return
    const timer = window.setInterval(() => {
      setTyping(map => {
        const now = Date.now()
        const next = new Map([...map].filter(([, value]) => value.until > now))
        return next.size === map.size ? map : next
      })
    }, 1000)
    return () => window.clearInterval(timer)
  }, [typing.size])
  return [typing, setTyping] as const
}

type SendSetup = {
  channelId: string
  me: DiscordHome['me']
  meId: string | null
  replyTo: DiscordMessage | null
  replyPing: boolean | null
  setReplyTo: (message: DiscordMessage | null) => void
  setError: (error: string | null) => void
  setMessages: React.Dispatch<React.SetStateAction<DiscordMessage[]>>
  pinned: React.RefObject<boolean>
  newer: boolean
  toPresent: () => Promise<void>
}

// Retry sends a failed message again (with the reply it had); Delete drops it, as Discord's failed message offers.
function useRetry(actions: MessageActions, send: ReturnType<typeof useSend>, setMessages: React.Dispatch<React.SetStateAction<DiscordMessage[]>>) {
  const latest = useRef(send)
  useEffect(() => { latest.current = send })
  return useMemo<MessageActions>(() => ({
    ...actions,
    onRetry: message => void latest.current(message.content, [], message),
    onDiscard: message => setMessages(list => list.filter(m => m.id !== message.id)),
  }), [actions, setMessages])
}

/**
 * Sending, Discord's way: the message shows at once under your name (its files with their upload's
 * progress), then as Discord has it. A file that fails takes back the ones already sent to the host, and its
 * message goes back to the box; a text Discord didn't take stays in the list with why, to Retry (retry) or Delete.
 */
function useSend({ channelId, me, meId, replyTo, replyPing, setReplyTo, setError, setMessages, pinned, newer, toPresent }: SendSetup) {
  return async function send(content: string, files: File[] = [], retry?: DiscordMessage): Promise<boolean> {
    setError(null)
    const total = files.reduce((sum, file) => sum + file.size, 0)
    if (total > UPLOAD_LIMIT) { setError('Files can be 25 MB at most.'); return false }
    if (newer) await toPresent()
    const pendingId = `pending-${Date.now()}-${Math.random().toString(36).slice(2, 6)}`
    const reply = retry ? retry.referenced_message ?? null : replyTo
    if (retry) setMessages(list => list.filter(m => m.id !== retry.id))
    const uploading: PendingUpload | undefined = files.length ? { files: files.map(f => ({ name: f.name, size: f.size })), progress: 0 } : undefined
    pinned.current = true
    setMessages(list => [...list, {
      id: pendingId, channel_id: channelId, type: 0, content, timestamp: new Date().toISOString(),
      author: meAsAuthor(me, meId), attachments: [], embeds: [], mentions: [], pending: true, uploading,
      referenced_message: reply,
    }])
    if (!retry) setReplyTo(null)
    const drop = (message: string) => {
      if (!files.length) {
        setMessages(list => list.map(m => m.id === pendingId ? { ...m, failed: message } : m))
        return true
      }
      setMessages(list => list.filter(m => m.id !== pendingId))
      setReplyTo(reply)
      setError(message)
      return false
    }
    const uploads: string[] = []
    let done = 0
    for (const file of files) {
      const uploaded = await uploadFile(file, sent => {
        const progress = total ? (done + sent) / total : 1
        setMessages(list => list.map(m => m.id === pendingId && m.uploading ? { ...m, uploading: { ...m.uploading, progress } } : m))
      })
      if (typeof uploaded !== 'string') {
        for (const id of uploads) void host.discordUpload({ uploadId: id, cancel: true }).catch(() => { })
        return drop(uploaded.message)
      }
      uploads.push(uploaded)
      done += file.size
    }
    try {
      const result = await host.discordSend(channelId, content, reply?.id, uploads, reply ? { ping: replyPing !== false } : {})
      if (!result.ok) return drop(result.message ?? 'Discord did not send that.')
      const sent = result.sent
      setMessages(list => {
        const without = list.filter(m => m.id !== pendingId)
        return sent && !without.some(m => m.id === sent.id) ? [...without, sent] : without
      })
      return true
    } catch (err) {
      report(err, 'DiscordChat.send')
      return drop('Discord did not send that.')
    }
  }
}

// You as a message's author, with your picture (the side panel has it as a link; messages want the hash).
function meAsAuthor(me: DiscordHome['me'], meId: string | null): DiscordUser {
  return { id: me?.id ?? meId ?? '0', username: me?.name ?? 'You', global_name: me?.name ?? null, avatar: avatarHashOf(me?.avatar) }
}

const PIECE = 600 * 1024

/** Sends a file to the host in base64 pieces, telling how many bytes are in; the upload id goes with the message. */
export async function uploadFile(file: File, onProgress?: (sent: number) => void): Promise<string | { message: string }> {
  if (file.size > UPLOAD_LIMIT) return { message: `${file.name} is over 25 MB.` }
  let uploadId: string | undefined
  for (let at = 0; at < Math.max(file.size, 1); at += PIECE) {
    const bytes = new Uint8Array(await file.slice(at, at + PIECE).arrayBuffer())
    let binary = ''
    for (let i = 0; i < bytes.length; i += 0x8000) binary += String.fromCharCode(...bytes.subarray(i, i + 0x8000))
    const result = await host.discordUpload({ uploadId, name: file.name, type: file.type || 'application/octet-stream', data: btoa(binary) })
      .catch(() => ({ ok: false, uploadId: undefined, message: undefined }))
    if (!result.ok || !result.uploadId) {
      if (uploadId) void host.discordUpload({ uploadId, cancel: true }).catch(() => { })
      return { message: result.message ?? `${file.name} couldn't be added.` }
    }
    uploadId = result.uploadId
    onProgress?.(Math.min(file.size, at + PIECE))
  }
  return uploadId!
}

/**
 * The people in this channel's messages (authors and mentions), the same map while nobody new
 * appears, so one more message doesn't redraw every other one.
 */
function useUsers(messages: DiscordMessage[]) {
  const kept = useRef<Map<string, DiscordUser>>(new Map())
  return useMemo(() => {
    const next = new Map<string, DiscordUser>()
    for (const m of messages) {
      if (!m.pending) next.set(m.author.id, m.author)
      for (const u of m.mentions) next.set(u.id, u)
    }
    const before = kept.current
    const same = next.size === before.size && [...next].every(([id, u]) => {
      const was = before.get(id)
      return was && was.username === u.username && was.global_name === u.global_name && was.avatar === u.avatar
    })
    if (!same) kept.current = next
    return kept.current
  }, [messages])
}

// Name colors for this channel's authors: what Exo knows now, then whatever Discord sends for the rest.
function useAuthorColors(guildId: string | null, messages: DiscordMessage[]) {
  const [colors, setColors] = useState<Map<string, string | null>>(new Map())
  const authors = useMemo(() => [...new Set(messages.filter(m => !m.pending).map(m => m.author.id))].sort().join(','), [messages])
  useEffect(() => {
    if (!guildId || !authors) { setColors(new Map()); return }
    let live = true
    const load = () => host.discordColors(guildId, authors.split(',')).then(result => {
      if (live) setColors(new Map(Object.entries(result.colors)))
    }).catch(() => { })
    load()
    const off = onHostEvent('discord.colors', event => { if (event.guildId === guildId) load() })
    return () => { live = false; off() }
  }, [guildId, authors])
  return colors
}

/** Scrolls a message into the middle and lights it up a moment, as Discord does for a jump. */
function flash(el: Element, smooth: boolean) {
  el.scrollIntoView({ block: 'center', behavior: smooth && !shouldReduceMotion() ? 'smooth' : 'auto' })
  el.classList.add('is-flash')
  window.setTimeout(() => el.classList.remove('is-flash'), 1600)
}

function DayDivider({ iso }: { iso: string }) {
  return (
    <div className="exo-dc-day" role="separator">
      <span>{new Date(iso).toLocaleDateString(undefined, { dateStyle: 'long' })}</span>
    </div>
  )
}

function readMembersShown() {
  try { return localStorage.getItem('exo.discord.members') !== 'off' } catch { return true }
}

function writeMembersShown(shown: boolean) {
  try { localStorage.setItem('exo.discord.members', shown ? 'on' : 'off') } catch { /* a hidden list just isn't remembered */ }
}

// The first message after what was read that isn't yours, loaded or not (null: load around the read mark).
function firstUnread(messages: DiscordMessage[], lastRead: string, meId: string | null) {
  return messages.find(m => !m.pending && m.author.id !== meId && isAfter(m.id, lastRead))?.id ?? null
}

// "3:45 PM" today, else "Oct 12 at 3:45 PM", as Discord's bar words it.
function sinceLabel(id: string) {
  const date = new Date(snowflakeTime(id))
  const clock = date.toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' })
  return date.toDateString() === new Date().toDateString() ? clock : `${date.toLocaleDateString(undefined, { month: 'short', day: 'numeric' })} at ${clock}`
}

// "#chat", "@lazy" or a group's name, as Discord's composer names its conversation.
function target(channel: DiscordChannel, guildId: string | null) {
  if (guildId) return `#${channel.name}`
  return channel.dm?.group ? channel.name : `@${channel.name}`
}

// Beside the chat: the member list in a server, the person's profile in a 1:1 DM.
function SidePane({ channel, guildId, hidden }: { channel: DiscordChannel; guildId: string | null; hidden: boolean }) {
  if (guildId) return <MemberList guildId={guildId} />
  const dm = channel.dm
  if (!dm?.userId || dm.group) return null
  const fallback = { id: dm.userId, username: dm.username ?? channel.name, global_name: channel.name, avatar: avatarHashOf(dm.avatar) }
  return <DmProfile userId={dm.userId} status={dm.status} fallback={fallback} hidden={hidden} />
}

/** Whether Shift is down right now (Delete Message with Shift skips Discord's question). */
function useShift() {
  const held = useRef(false)
  useEffect(() => {
    const key = (event: KeyboardEvent) => { held.current = event.shiftKey }
    window.addEventListener('keydown', key)
    window.addEventListener('keyup', key)
    return () => { window.removeEventListener('keydown', key); window.removeEventListener('keyup', key) }
  }, [])
  return held
}

// The other person's name in a 1:1 DM.
function dmPartner(channel: { name: string; dm?: { group?: boolean } | null }) {
  return channel.dm && !channel.dm.group ? channel.name : null
}
