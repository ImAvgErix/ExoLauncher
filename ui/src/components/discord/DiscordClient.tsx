import { lazy, Suspense, useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { CompanyLogo } from '../../brand/CompanyLogo'
import { host, onHostEvent } from '../../lib/host'
import { report } from '../../lib/report'
import { onPerformanceMode, performanceMode, takePendingOpen, takePendingSettings, type DiscordChannel, type DiscordChannels, type DiscordDm, type DiscordHome, type OpenTarget } from '../../lib/discord'
import { Chat } from './Chat'
import { FriendsPage } from './FriendsPage'
import { DmSidebar, FRIENDS, Rail, ServerSidebar, SHOP, UserPanel } from './Sidebar'
import { ShopPage } from './ShopPage'
import { IncomingCall, useDiscordVoice, type VoiceControls, VoiceRoom } from './voice'
import { AddServer, ImageHost, ProfileHost, useProfileReadAhead } from './extras'
import { ForumView } from './Forum'
import { isPage, ServerPage } from './serverPages'
import { FullProfileHost } from './DmProfile'
import { ProfilePlaces } from './profileParts'
import { UserMenuHost } from './UserMenu'
import { ImageMenuHost } from './ImageMenu'
import { AppModalHost } from './AppModal'
import type { DiscordSettingsSection } from './DiscordSettings'
// Discord's settings load the first time they open, not with the room.
const DiscordSettings = lazy(() => import('./DiscordSettings').then(m => ({ default: m.DiscordSettings })))
import { startDiscordSettings, useRoomLook } from '../../lib/discordPrefs'
import { prefetchMessages } from './messageCache'
import { pane, pressedBefore, transition, visible } from '../../lib/viewTransition'

const HOME = '@me'
/** The server a place is (Direct messages is none). */
const guildOf = (place: string) => place === HOME ? null : place
const WHERE_KEY = 'exo.discord.where'
type SavedThread = { id: string; name: string; parentId: string }
type Where = { place: string; channels: Record<string, string>; threads: Record<string, SavedThread> }
function readWhere(): Where {
  try {
    const saved = JSON.parse(localStorage.getItem(WHERE_KEY) ?? 'null') as Partial<Where> | null
    if (saved && saved.channels && typeof saved.channels === 'object')
      return { place: HOME, channels: saved.channels, threads: saved.threads && typeof saved.threads === 'object' ? saved.threads : {} }
  } catch { /* a convenience */ }
  return { place: HOME, channels: {}, threads: {} }
}
// Discord opens at Friends each time Exo starts. The channel (and thread) last open in each server is kept across
// restarts, as Discord does, and where you are is kept across remounts (a reconnect, a room switch).
const where = readWhere()
const lastChannel = new Map<string, string>([...Object.entries(where.channels).filter(([place, id]) => place !== HOME && typeof id === 'string'), [HOME, FRIENDS]])
const lastThread = new Map<string, SavedThread>(Object.entries(where.threads).filter(([place, t]) => place !== HOME && typeof t?.id === 'string' && typeof t.parentId === 'string'))
function saveWhere(place: string) {
  where.place = place
  try { localStorage.setItem(WHERE_KEY, JSON.stringify({ place, channels: Object.fromEntries(lastChannel), threads: Object.fromEntries(lastThread) })) } catch { /* a convenience */ }
}
// The thread last open in a place, when it is still inside the channel the place reopens at.
function savedThread(place: string, channelId: string | null): DiscordChannel | null {
  const saved = lastThread.get(place)
  return saved && saved.parentId === channelId ? threadChannel(saved) : null
}

// Each server's channel list as last read: switching back shows it at once while it is read again.
const shownLists = new Map<string, DiscordChannels>()

// The conversation a place opens at, read ahead while the pointer rests on it in the rail.
function peekPlace(place: string | null) {
  const channelId = place ? lastChannel.get(place) : undefined
  const text = place === HOME ? channelId !== FRIENDS && channelId !== SHOP : shownLists.get(place ?? '')?.channels.some(c => c.id === channelId && c.kind === 'text')
  prefetchMessages(place && channelId && text ? savedThread(place, channelId)?.id ?? channelId : null)
}

function dmChannel(dm: DiscordDm): DiscordChannel {
  return {
    id: dm.id, name: dm.name, type: dm.group ? 3 : 1, kind: 'text', canSend: true, canRead: true,
    dm: { userId: dm.userId ?? null, avatar: dm.avatar, status: dm.status ?? null, username: dm.username ?? null, group: dm.group },
  }
}

/** A thread (or forum post) as the chat opens it. */
function threadChannel(thread: { id: string; name: string; locked?: boolean }): DiscordChannel {
  return { id: thread.id, name: thread.name, type: 11, kind: 'text', canRead: true, canSend: thread.locked !== true }
}

/** Keeps the server (or DMs) you're in and the channel open there across restarts; a server you've left falls back to your DMs. */
function useRememberWhere(home: DiscordHome | null, place: string, open: string | null, thread: DiscordChannel | null, leave: () => void) {
  useEffect(() => {
    if (open) {
      lastChannel.set(place, open)
      if (thread) lastThread.set(place, { id: thread.id, name: thread.name, parentId: open })
      else lastThread.delete(place)
    }
    saveWhere(place)
  }, [place, open, thread])
  const gone = !!home && place !== HOME && !home.guilds.some(g => g.id === place)
  useEffect(() => { if (gone) leave() }, [gone, leave])
}

/** Signed in and connected, laid out as Discord is: servers, then channels or DMs with you at the bottom, then the conversation. */
export function DiscordClient({ active }: { active: boolean }) {
  const [home, setHome] = useState<DiscordHome | null>(null)
  const [place, setPlace] = useState(where.place)
  const [list, setList] = useState<DiscordChannels | null>(() => shownLists.get(where.place) ?? null)
  const [open, setOpen] = useState<string | null>(() => lastChannel.get(where.place) ?? null)
  // A thread or forum post open in the channel `open`; picking any channel (its parent too) closes it.
  const [thread, setThread] = useState<DiscordChannel | null>(() => savedThread(where.place, lastChannel.get(where.place) ?? null))
  const now = useRef({ place, list })
  now.current = { place, list }
  // A channel opened that the list doesn't have yet (just made, a thread, a jump): the list asked for it decides.
  const awaiting = useRef<{ id: string; list: DiscordChannels | null } | null>(null)

  const refreshHome = useCallback(() => {
    host.discordHome().then(setHome).catch((error: unknown) => report(error, 'DiscordClient.home'))
  }, [])

  // A late answer for a server you've since left doesn't replace the one you're in.
  const refreshChannels = useCallback((guildId: string) => {
    host.discordChannels(guildId).then(next => { shownLists.set(guildId, next); if (now.current.place === guildId) setList(next) })
      .catch((error: unknown) => report(error, 'DiscordClient.channels'))
  }, [])

  /** Opens a place (a server or DMs) at a channel, or its last one; a thread opens inside its parent. */
  const navigate = useCallback((to: string, channelId: string | null, inThread: DiscordChannel | null = null) => {
    const { place: current, list: shown } = now.current
    const target = channelId ?? lastChannel.get(to) ?? null
    setThread(inThread ?? (channelId ? null : savedThread(to, target)))
    if (to !== current) { setPlace(to); if (to !== HOME) setList(shownLists.get(to) ?? null) }
    setOpen(target)
    // A channel the list to be shown doesn't have yet waits for the list asked for, not the server's first channel.
    const known = to === current ? shown : shownLists.get(to)
    if (to !== HOME && known && target && !isPage(target) && !known.channels.some(c => c.id === target)) {
      awaiting.current = { id: target, list: known }
      refreshChannels(to)
    }
  }, [refreshChannels])

  const toDms = useCallback(() => navigate(HOME, null), [navigate])
  useRememberWhere(home, place, open, thread, toDms)

  useEffect(() => { refreshHome() }, [refreshHome])

  // Each open from outside (Ctrl+K, a notification, a jump) closes what floats over the room: Discord's settings here,
  // and, by remounting their hosts, Server Settings, profiles, menus and the image viewer.
  const [opened, setOpened] = useState(0)
  // The sidebar (it holds Server Settings) remounts only for opens from outside the room, so a #channel click keeps its scroll.
  const [fromOutside, setFromOutside] = useState(0)
  const { settingsAt, setSettingsAt, lastSettings, closeSettings } = useSettingsAt()
  // Join a Server, open (true) or holding an invite link Discord refused, with its reason.
  const [adding, setAdding] = useState<boolean | { invite: string; message: string }>(false)

  // A clicked notification, Ctrl+K or a jump to a message opens its conversation, now or when the room first mounts.
  useEffect(() => {
    const show = (target: OpenTarget) => {
      setSettingsAt(null)
      setAdding(false)
      setOpened(n => n + 1)
      if (target.outside !== false) setFromOutside(n => n + 1)
      const inThread = target.parentId && target.channelId ? threadChannel({ id: target.channelId, name: target.name ?? '' }) : null
      navigate(target.guildId ?? HOME, inThread ? target.parentId! : target.channelId, inThread)
    }
    const waiting = takePendingOpen()
    if (waiting) show(waiting)
    const fromPalette = () => { const target = takePendingOpen(); if (target) show(target) }
    window.addEventListener('exo-discord-open', fromPalette)
    const offHost = onHostEvent('discord.open', target => { takePendingOpen(); show(target) })
    return () => { window.removeEventListener('exo-discord-open', fromPalette); offHost() }
  }, [navigate, setSettingsAt])

  useEffect(() => {
    if (place === HOME) { setList(null); return }
    refreshChannels(place)
  }, [place, refreshChannels])

  useLiveLists(place, home, refreshHome, refreshChannels, setHome, setList)
  useProfileReadAhead(guildOf(place))

  // A server opens on its last channel, or its first one with text; a thread's id opens the thread in its channel.
  useEffect(() => {
    if (place === HOME || !list) return
    if (open && (list.channels.some(c => c.id === open && c.kind !== 'category') || isPage(open))) { awaiting.current = null; return }
    const asThread = open ? list.threads?.find(t => t.id === open) : undefined
    if (asThread) { awaiting.current = null; setThread(threadChannel(asThread)); setOpen(asThread.parentId); return }
    if (awaiting.current && awaiting.current.id === open && awaiting.current.list === list) return
    awaiting.current = null
    const remembered = lastChannel.get(place)
    const texts = list.channels.filter(c => c.kind === 'text' && c.canRead !== false)
    setOpen(texts.find(c => c.id === remembered)?.id ?? texts[0]?.id ?? null)
  }, [place, list, open])

  const go = useCallback((next: string) => { if (next !== now.current.place) navigate(next, null) }, [navigate])
  // A server picked on the rail: the room beside the rail pushes from the side of the rail it was picked on. The rail
  // and your dock over its foot hold still (the dock would otherwise slide off under the rail and come back).
  const railGo = useCallback((next: string) => {
    if (next === now.current.place) return
    const rail = visible('.exo-dc-rail')
    const dock = visible('.exo-dc-dock')
    void transition(() => navigate(next, null), {
      kind: 'push',
      back: pressedBefore('.exo-dc-rail [aria-current="true"]'),
      morph: [pane(visible('main.exo-dc')), { name: 'exo-dc-rail', from: rail, to: () => rail }, { name: 'exo-dc-dock', from: dock, to: () => visible('.exo-dc-dock') }],
    })
  }, [navigate])
  const places = useProfilePlaces(home, go)
  const meId = home?.me?.id ?? null
  const pick = (id: string) => navigate(place, id)

  const voice = useDiscordVoice()
  const caller = voice.ringing ? home?.dms.find(d => d.id === voice.ringing) : undefined
  // The voice bar opens the call's own view: the voice channel in its server, or the DM.
  const openVoice = () => {
    const { guildId, channelId } = voice.voice
    if (channelId) navigate(guildId ?? HOME, channelId)
  }
  const panel = <UserPanel home={home} voice={voice} voicePlace={voicePlaceName(voice, list, home)} status={home?.me?.status ?? null} onOpenVoice={openVoice} />
  const channelName = useCallback((id: string) => list?.channels.find(c => c.id === id)?.name ?? null, [list])

  // An invite link clicked in a message joins the server (already in: opens it) at the invite's channel, as Discord's app
  // does; when Discord refuses (an expired invite, a captcha), Join a Server shows the link and why.
  useEffect(() => {
    const join = (event: Event) => {
      const invite = (event as CustomEvent<string>).detail
      const refused = (message?: string | null) => setAdding({ invite: `https://discord.gg/${invite}`, message: message ?? 'Discord did not take that invite.' })
      host.discordServer('join', { invite }).then(result => {
        if (!result.ok || !(result.guildId || result.channelId)) return refused(result.message)
        refreshHome()
        navigate(result.guildId ?? HOME, result.channelId ?? null)
      }).catch((error: unknown) => { report(error, 'DiscordClient.invite'); refused() })
    }
    window.addEventListener('exo-dc-invite', join)
    return () => window.removeEventListener('exo-dc-invite', join)
  }, [navigate, refreshHome])

  // A DM made elsewhere in the room (Add Friends to DM): open it in Direct messages.
  useEffect(() => {
    const show = (event: Event) => {
      navigate(HOME, (event as CustomEvent<string>).detail)
      refreshHome()
    }
    window.addEventListener('exo-dc-open-dm', show)
    return () => window.removeEventListener('exo-dc-open-dm', show)
  }, [navigate, refreshHome])

  // Start a Call from the person menu: their DM opens and rings, as its call button does.
  const call = useRef(voice.call)
  call.current = voice.call
  useEffect(() => {
    const ring = (event: Event) => {
      const channelId = (event as CustomEvent<string>).detail
      navigate(HOME, channelId)
      refreshHome()
      void call.current(channelId)
    }
    window.addEventListener('exo-dc-call-dm', ring)
    return () => window.removeEventListener('exo-dc-call-dm', ring)
  }, [navigate, refreshHome])

  // Message from a profile card: Discord's DM with that person, opened in Direct messages.
  const messageUser = useCallback((userId: string) => {
    void host.discordFriend('dm', { userId }).then(result => {
      if (!result.channelId) return
      navigate(HOME, result.channelId)
      refreshHome()
    }).catch(() => { })
  }, [navigate, refreshHome])

  // Discord's settings (the gear, Voice Settings): Discord's own, in the room, not Exo's.
  const [performance, setPerformance] = useState(performanceMode)
  useEffect(() => onPerformanceMode(() => setPerformance(performanceMode())), [])
  // Discord settings in effect: the account's, Streamer Mode, /tts, and the room's look.
  useEffect(startDiscordSettings, [])
  const look = useRoomLook()
  useEffect(() => {
    // The sheet has its own way in (system.css › exo-float-in): no page crossfade on top of it.
    const show = (event: Event) => void transition(() => { takePendingSettings(); setSettingsAt(((event as CustomEvent).detail as DiscordSettingsSection) ?? 'account') }, { kind: 'none' })
    window.addEventListener('exo-discord-settings', show)
    return () => window.removeEventListener('exo-discord-settings', show)
  }, [setSettingsAt])

  return (
    <ProfilePlaces.Provider value={places}>
    <main className={`exo-dc${performance ? ' is-performance' : ''}`} {...look} aria-label="Discord">
      {/* Mounted with the room and hidden while closed: they open at once, on the section last shown. */}
      <Suspense fallback={null}><DiscordSettings open={!!settingsAt} section={settingsAt ?? lastSettings.current} home={home} onSection={setSettingsAt} onClose={closeSettings} /></Suspense>
      <ProfileHost key={`p${opened}`} meId={meId} onMessage={messageUser} />
      <UserMenuHost key={`m${opened}`} place={place} list={list} home={home} />
      <FullProfileHost key={`f${opened}`} meId={meId} onMessage={messageUser} />
      <ImageHost key={`i${opened}`} />
      <ImageMenuHost />
      <AppModalHost guildId={place === HOME ? null : place} />
      {adding ? <AddServer refused={adding} onClose={() => setAdding(false)} onJoined={id => { setAdding(false); refreshHome(); go(id) }} /> : null}
      {caller ? <IncomingCall name={caller.name} onJoin={() => { navigate(HOME, caller.id); void voice.join(null, caller.id) }} onDecline={() => voice.decline(caller.id)} /> : null}
      <Rail home={home} place={place} onGo={railGo} onOpenDm={id => navigate(HOME, id)} onPeek={peekPlace} onAdd={() => setAdding(true)} homeIcon={<CompanyLogo company="discord" size={18} />} />
      <div className="exo-dc-side-col">
      {place === HOME
        ? <DmSidebar home={home} open={open} onPick={pick} />
        : (
          <ServerSidebar key={`${place}:${fromOutside}`} guildId={place} name={list?.name ?? home?.guilds.find(g => g.id === place)?.name ?? ''} list={list} open={open} voice={voice}
            onPick={pick} onJoin={id => { pick(id); if (voice.voice.channelId !== id || voice.voice.state === 'off') void voice.join(place, id) }}
            onMarkRead={() => void host.discordMarkRead(place).catch(() => { })}
            onLeave={() => void host.discordServer('leave', { guildId: place }).then(result => { if (result.ok) go(HOME) }).catch(() => { })}
            onInvite={() => open ? host.discordChannel(open, 'invite').then(result => result.url ?? null).catch(() => null) : Promise.resolve(null)} />
        )}
        {panel}
      </div>
      <Conversation place={place} open={open} home={home} list={list} voice={voice} active={active} channelName={channelName} onPick={pick}
        thread={thread} onThread={setThread} />
    </main>
    </ProfilePlaces.Provider>
  )
}

// Which section of Discord's settings is open, and the one they last showed: they stay mounted on it while closed.
function useSettingsAt() {
  const [settingsAt, setSettingsAt] = useState<DiscordSettingsSection | null>(takePendingSettings)
  const lastSettings = useRef<DiscordSettingsSection>('account')
  if (settingsAt) lastSettings.current = settingsAt
  const closeSettings = useCallback(() => void transition(() => setSettingsAt(null)), [])
  return { settingsAt, setSettingsAt, lastSettings, closeSettings }
}

// Your servers and opening one, for the profiles' Member Since and Mutual Servers.
function useProfilePlaces(home: DiscordHome | null, openServer: (guildId: string) => void) {
  const guilds = home?.guilds
  return useMemo(() => ({ guilds: guilds ?? [], openServer }), [guilds, openServer])
}

// Lists stay live: changes refetch, unread marks patch in place.
function useLiveLists(
  place: string,
  home: DiscordHome | null,
  refreshHome: () => void,
  refreshChannels: (guildId: string) => void,
  setHome: React.Dispatch<React.SetStateAction<DiscordHome | null>>,
  setList: React.Dispatch<React.SetStateAction<DiscordChannels | null>>,
) {
  useEffect(() => {
    const offChanged = onHostEvent('discord.changed', event => {
      // A server's voice change shows in its channel list and on its rail mark (patched in place); a DM call's shows on Home.
      if (event.voice && event.guildId && event.inVoice !== undefined) {
        const { guildId, inVoice } = event
        setHome(current => current && { ...current, guilds: current.guilds.map(g => g.id === guildId && g.inVoice !== inVoice ? { ...g, inVoice } : g) })
      }
      if (!(event.voice && event.guildId)) refreshHome()
      if (place !== HOME && (event.guildId === null || event.guildId === place)) refreshChannels(place)
    })
    const offUnread = onHostEvent('discord.unread', event => {
      setHome(current => current && {
        ...current,
        guilds: current.guilds.map(g => g.id === event.guildId ? { ...g, unread: event.guildUnread, mentions: event.guildMentions } : g),
        dms: event.guildId ? current.dms : sortDms(current.dms.map(d => d.id === event.channelId ? { ...d, unread: event.unread, mentions: event.mentions } : d), event),
      })
      setList(current => current && {
        ...current,
        channels: current.channels.map(c => c.id === event.channelId ? { ...c, unread: event.unread, mentions: event.mentions } : c),
      })
      if (!event.guildId && home && !home.dms.some(d => d.id === event.channelId)) refreshHome()
    })
    const offPresence = onHostEvent('discord.presence', event => setHome(current => current && {
      ...current,
      me: current.me && event.userId === current.me.id ? { ...current.me, status: event.status } : current.me,
      dms: current.dms.map(d => d.userId === event.userId ? { ...d, status: event.status } : d),
    }))
    return () => { offChanged(); offUnread(); offPresence() }
  }, [place, refreshHome, refreshChannels, home, setHome, setList])
}

function sortDms(dms: DiscordDm[], event: { channelId: string; unread: boolean }) {
  if (!event.unread) return dms
  const at = dms.findIndex(d => d.id === event.channelId)
  return at <= 0 ? dms : [dms[at], ...dms.slice(0, at), ...dms.slice(at + 1)]
}

function NoChat({ home, loaded }: { home: boolean; loaded: boolean }) {
  return <section className="exo-dc-chat is-empty"><p className="exo-dc-note">{home ? 'Pick a conversation.' : loaded ? 'No channel here you can read.' : 'Loading channels'}</p></section>
}

// A server's open channel of one kind (never in Direct messages).
function serverChannel(place: string, list: DiscordChannels | null, open: string | null, kind: DiscordChannel['kind']) {
  return place === HOME ? undefined : list?.channels.find(c => c.id === open && c.kind === kind)
}

function openChannel(place: string, open: string | null, home: DiscordHome | null, list: DiscordChannels | null): DiscordChannel | null {
  if (place === HOME) {
    const dm = home?.dms.find(d => d.id === open)
    return dm ? dmChannel(dm) : null
  }
  return list?.channels.find(c => c.id === open && c.kind === 'text') ?? null
}

// Where the call is, as Discord's voice bar names it: "channel / server" from any server you're looking at, or the DM.
function voicePlaceName(voice: VoiceControls, list: DiscordChannels | null, home: DiscordHome | null): string | null {
  const { channelId, guildId } = voice.voice
  if (!channelId) return null
  if (!guildId) return home?.dms.find(d => d.id === channelId)?.name ?? null
  const channels = (list?.channels.some(c => c.id === channelId) ? list : shownLists.get(guildId))?.channels
  const name = channels?.find(c => c.id === channelId)?.name
  const server = home?.guilds.find(g => g.id === guildId)?.name
  return name ? (server ? `${name} / ${server}` : name) : server ?? null
}

type ConversationProps = {
  place: string
  open: string | null
  home: DiscordHome | null
  list: DiscordChannels | null
  voice: VoiceControls
  active: boolean
  channelName: (id: string) => string | null
  onPick: (id: string) => void
  thread: DiscordChannel | null
  onThread: (thread: DiscordChannel | null) => void
}

// The right of the room: Friends, a conversation, or what to do next.
function Conversation(props: ConversationProps) {
  const { place, open, home, list, active, channelName, onPick } = props
  if (place === HOME && open === FRIENDS) return <FriendsPage onOpenDm={onPick} />
  if (place === HOME && open === SHOP) return <ShopPage me={home?.me ?? null} />
  if (place !== HOME && open && isPage(open)) return <ServerPage page={open} guildId={place} list={list} onPick={onPick} meId={home?.me?.id ?? null} channelName={channelName} active={active} />
  return <ConversationIn {...props} />
}

// A channel, a DM, or a forum and the post opened from it; a thread shows its channel above it to go back.
function ConversationIn({ place, open, home, list, voice, active, channelName, thread, onThread }: ConversationProps) {
  const forum = serverChannel(place, list, open, 'forum')
  if (forum && !thread) return <ForumView key={forum.id} channel={forum} active={active} onOpen={onThread} />
  const voiceChannel = serverChannel(place, list, open, 'voice')
  if (voiceChannel && !thread) return <VoiceWithChat {...{ place, home, list, voice, active, channelName, onThread }} channel={voiceChannel} />
  const parent = thread ? list?.channels.find(c => c.id === open) ?? null : null
  const channel = thread ?? openChannel(place, open, home, list)
  if (!channel) return <NoChat home={place === HOME} loaded={!!list} />
  return <ChannelChat {...{ place, home, list, voice, active, channelName, onThread, channel, parent }} />
}

const VOICE_CHAT_KEY = 'exo.discord.voiceChat'
function readVoiceChat() {
  try { return localStorage.getItem(VOICE_CHAT_KEY) !== 'off' } catch { return true }
}

// A voice or stage channel: the call, and its own text chat beside it as Discord's chat panel (not without Connect),
// which Hide Chat and Close Chat put away and Show Chat brings back, remembered.
function VoiceWithChat({ channel, ...props }: Omit<ConversationProps, 'open' | 'onPick' | 'thread'> & { channel: DiscordChannel }) {
  const [shown, setShown] = useState(readVoiceChat)
  const show = (next: boolean) => {
    setShown(next)
    try { localStorage.setItem(VOICE_CHAT_KEY, next ? 'on' : 'off') } catch { /* a convenience */ }
  }
  const { place, voice, home } = props
  const readable = channel.canRead !== false
  const room = <VoiceRoom channel={channel} controls={voice} meId={home?.me?.id ?? null} onJoin={() => void voice.join(place, channel.id)}
    chat={readable ? { shown, onToggle: () => show(!shown) } : undefined} />
  if (!readable || !shown) return room
  return <div className="exo-dc-voice-split">{room}<ChannelChat {...props} channel={channel} parent={null} onHide={() => show(false)} /></div>
}

// A text channel, thread or DM; a DM's call shows above it.
function ChannelChat({ place, home, list, voice, active, channelName, onThread, channel, parent, onHide }: Omit<ConversationProps, 'open' | 'onPick' | 'thread'> & {
  channel: DiscordChannel
  parent: DiscordChannel | null
  onHide?: () => void
}) {
  const meId = home?.me?.id ?? null
  const dm = place === HOME
  const inCall = voice.voice.channelId === channel.id && voice.voice.state !== 'off'
  const chat = (
    <Chat key={channel.id} channel={channel} guildId={dm ? null : place} serverName={list?.name} onOpenThread={onThread} meId={meId} channelName={channelName} active={active} onHide={onHide}
      me={home?.me ?? null} hidden={home} roles={list?.roles} parent={parent} onBack={() => onThread(null)}
      onCall={dm ? () => void voice.call(channel.id) : undefined} inCall={inCall} />
  )
  if (!dm) return chat
  return <DmCall channel={channel} members={home?.dms.find(d => d.id === channel.id)?.call} inCall={inCall} voice={voice} meId={meId}>{chat}</DmCall>
}

// A DM's call, as Discord shows it: the call's stage above the conversation while you're in it, or while it's on without you.
function DmCall({ channel, members, inCall, voice, meId, children }: {
  channel: DiscordChannel
  members: DiscordChannel['members']
  inCall: boolean
  voice: VoiceControls
  meId: string | null
  children: React.ReactNode
}) {
  return (
    <div className="exo-dc-dm-call">
      {inCall || members?.length ? <VoiceRoom dm channel={{ ...channel, members: members ?? [] }} controls={voice} meId={meId} onJoin={() => void voice.join(null, channel.id)} /> : null}
      {children}
    </div>
  )
}
