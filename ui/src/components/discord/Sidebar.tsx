import { lazy, Suspense, useEffect, useRef, useState } from 'react'
import { useGlide } from '../../lib/glide'
import { ChevronDown, ChevronUp, Close } from '../../brand/icons'
import { host } from '../../lib/host'
import { guildBannerUrl, moveInRail, performanceMode, railShape, requestDiscordSettings, type DiscordChannel, type DiscordChannels, type DiscordDm, type DiscordFriend, type DiscordGuild, type DiscordHome, type DiscordRailItem, type DiscordVoiceMember, type RailDrop } from '../../lib/discord'
import { Icons } from './icons'
import { emojiUrl } from './markdown'
import { Avatar, CustomStatusLine } from './presence'
import { Nameplate, StyledName } from './collectibles'
import { VoiceMembers, VoicePanel, type VoiceControls } from './voice'
import { CreateChannel, PAGES, ServerPrivacy, ServerRows } from './serverPages'
import { EventsDialog } from './events'
// Server Settings and Edit Channel load the first time they open, not with the room.
const ServerSettings = lazy(() => import('./ServerSettings').then(m => ({ default: m.ServerSettings })))
const ChannelSettings = lazy(() => import('./ChannelSettings').then(m => ({ default: m.ChannelSettings })))
import { useAccountSettings } from '../../lib/discordPrefs'
import { Confirm } from './admin'
import { InviteDialog } from './InviteDialog'
import { EditGroup } from './GroupDm'
import { prefetchMessages } from './messageCache'
import { transition } from '../../lib/viewTransition'
import { shouldReduceMotion } from '../../lib/motionPreference'
import { LEVELS, MUTE_FOR } from './ChatHeader'
import { SubContext, Submenu } from './UserMenu'
import { dmData, DmCopyId, DmMuteItem, DmReadItem } from './dmMenu'
import { useDismiss } from '../../lib/dismiss'

export const FRIENDS = 'friends'
export const SHOP = 'shop'
export const REQUESTS = 'requests'

// A conversation the pointer rests on is read before the click, as Discord's own client does.
const peek = (event: React.PointerEvent) => prefetchMessages((event.target as HTMLElement).closest<HTMLElement>('[data-dc-channel]')?.dataset.dcChannel)
const unpeek = () => prefetchMessages(null)

function initials(name: string) {
  return name.split(/\s+/).filter(Boolean).slice(0, 3).map(word => [...word][0]).join('')
}

/** A server's channel list as Discord shows it: its name on top, categories that fold, text, announcement, forum and voice channels. */
export function ServerSidebar({ guildId, name, guild, list, open, voice, onPick, onJoin, onMarkRead, onLeave }: {
  guildId: string
  name: string
  /** The server as the rail has it: its notification settings for the header's menu. */
  guild: DiscordGuild | null
  list: DiscordChannels | null
  open: string | null
  voice: VoiceControls
  onPick: (id: string) => void
  onJoin: (id: string) => void
  onMarkRead: () => void
  onLeave: () => void
}) {
  const glide = useGlide<HTMLDivElement>()
  const [collapsed, setCollapsed] = useState<Set<string>>(new Set())
  const [collapsedOf, setCollapsedOf] = useState<DiscordChannels | null>(null)
  if (list !== collapsedOf) { setCollapsedOf(list); setCollapsed(new Set(list?.channels.filter(c => c.collapsed).map(c => c.id))) }
  // Create Channel (in a category, or none) or Create Category; the Events dialog, or straight to Create Event.
  const [creating, setCreating] = useState<{ parentId: string | null; category: string; asCategory?: boolean } | null>(null)
  const [eventsOpen, setEventsOpen] = useState<'list' | 'create' | null>(null)
  const [channelMenu, setChannelMenu] = useState<MenuAt | null>(null)
  const [editing, setEditing] = useState<string | null>(null)
  // A category folds or opens (folded: absent, it flips), kept on your account as Discord keeps it.
  const toggle = (id: string, folded?: boolean) => setCollapsed(set => {
    const next = new Set(set)
    const fold = folded ?? !next.has(id)
    if (fold === next.has(id)) return set
    if (fold) next.add(id); else next.delete(id)
    void host.discordChannel(id, 'collapse', fold).catch(() => { })
    return next
  })
  return (
    <nav className="exo-dc-side" aria-label="Channels" onPointerOver={peek} onPointerLeave={unpeek}>
      <ServerHeader guildId={guildId} name={name} guild={guild} list={list} onMarkRead={onMarkRead} onLeave={onLeave} inviteTo={inviteChannel(list, open)}
        onMembers={list?.rows?.members ? () => onPick(PAGES.members) : undefined}
        onCreate={what => what === 'event' ? setEventsOpen('create') : setCreating({ parentId: null, category: '', asCategory: what === 'category' })} />
      <JumpBars list={glide} channels={list?.channels} />
      <div ref={glide} className="exo-dc-side-list exo-glide">
        {list?.banner ? <img className="exo-dc-guild-banner" src={guildBannerUrl(guildId, list.banner, !performanceMode() && !shouldReduceMotion())} alt="" draggable={false} /> : null}
        <ServerRows rows={list?.rows} events={list?.events ?? 0} open={open} onPick={onPick} onEvents={() => setEventsOpen('list')} />
        <ChannelList channels={followed(list, open)} open={open} collapsed={collapsed} onPick={onPick} voice={voice} onJoin={onJoin}
          onCreate={list?.canManage ? c => setCreating({ parentId: c.id, category: c.name }) : undefined}
          onMenu={(channel, x, y, inCategory) => setChannelMenu({ channel, x, y, inCategory })}
          onToggle={id => toggle(id)} />
      </div>
      {eventsOpen ? <EventsDialog guildId={guildId} list={list} create={eventsOpen === 'create'} onClose={() => setEventsOpen(null)} /> : null}
      {channelMenu ? <SideMenu menu={channelMenu} guildId={guildId} serverName={name} list={list} collapsed={collapsed} voice={voice.voice} onToggle={toggle}
        onClose={() => setChannelMenu(null)} onEdit={() => void transition(() => { setEditing(channelMenu.channel.id); setChannelMenu(null) }, { kind: 'none' })} /> : null}
      {editing ? <Suspense fallback={null}><ChannelSettings channelId={editing} onClose={() => void transition(() => setEditing(null))} /></Suspense> : null}
      {creating && guildId ? <CreateChannel guildId={guildId} {...creating} onClose={() => setCreating(null)} onCreated={id => { setCreating(null); if (!creating.asCategory) onPick(id) }} /> : null}
    </nav>
  )
}

/** Direct messages as Discord lists them: a search, Friends, then every conversation, newest first. */
export function DmSidebar({ home, open, onPick }: {
  home: DiscordHome | null
  open: string | null
  onPick: (id: string) => void
}) {
  const [query, setQuery] = useState('')
  const [starting, setStarting] = useState(false)
  const glide = useGlide<HTMLDivElement>()
  useEffect(() => {
    const open = () => setStarting(true)
    window.addEventListener('exo-dc-new-dm', open)
    return () => window.removeEventListener('exo-dc-new-dm', open)
  }, [])
  const q = query.trim().toLowerCase()
  // Message requests and spam wait under Message Requests, as in Discord, not among your DMs.
  const dms = (home?.dms ?? []).filter(d => !d.request && !d.spam && (!q || d.name.toLowerCase().includes(q)))
  const requests = (home?.dms ?? []).filter(d => d.request && !d.spam).length
  const anyRequests = requests > 0 || (home?.dms ?? []).some(d => d.spam)
  return (
    <nav className="exo-dc-side" aria-label="Direct messages" onPointerOver={peek} onPointerLeave={unpeek}>
      <header className="exo-dc-side-head">
        <input className="exo-dc-find" placeholder="Find or start a conversation" value={query} onChange={event => setQuery(event.target.value)} aria-label="Find or start a conversation" />
      </header>
      <div ref={glide} className="exo-dc-side-list exo-glide">
        <button type="button" className="exo-dc-item is-big" aria-current={open === FRIENDS} onClick={() => onPick(FRIENDS)}>
          <Icons.Friends size={22} />
          <span className="exo-dc-item-name">Friends</span>
        </button>
        {anyRequests || open === REQUESTS ? (
          <button type="button" className="exo-dc-item is-big" aria-current={open === REQUESTS} onClick={() => onPick(REQUESTS)}>
            <Icons.Requests size={22} />
            <span className="exo-dc-item-name">Message requests</span>
            {requests ? <span className="exo-dc-badge">{requests}</span> : null}
          </button>
        ) : null}
        <button type="button" className="exo-dc-item is-big" aria-current={open === SHOP} onClick={() => onPick(SHOP)}>
          <Icons.Shop size={22} />
          <span className="exo-dc-item-name">Shop</span>
        </button>
        <p className="exo-dc-side-label is-row">
          Direct messages
          <button type="button" className="exo-dc-label-btn" aria-label="Create DM" onClick={() => setStarting(value => !value)}><Icons.Plus size={16} /></button>
        </p>
        {starting ? <StartDm onClose={() => setStarting(false)} onOpen={id => { setStarting(false); onPick(id) }} /> : null}
        {dms.map(d => <DmRow key={d.id} dm={d} current={d.id === open} onPick={onPick} />)}
      </div>
    </nav>
  )
}

function DmRow({ dm, current, onPick }: { dm: DiscordDm; current: boolean; onPick: (id: string) => void }) {
  // Leaving a group asks first, as Discord does; closing a 1:1 DM just hides it until the next message.
  const [leaving, setLeaving] = useState(false)
  // A group's right-click menu (a 1:1 DM's is the person menu, with the DM's rows).
  const [menu, setMenu] = useState<{ x: number; y: number } | null>(null)
  const close = () => void host.discordChannel(dm.id, 'close').catch(() => { })
  return (
    <div className="exo-dc-dm-row">
      <button type="button" className={`exo-dc-item is-dm${dm.unread ? ' is-unread' : ''}${dm.muted ? ' is-muted' : ''}${dm.look?.nameplate ? ' has-nameplate' : ''}`} aria-current={current} onClick={() => onPick(dm.id)} data-dc-channel={dm.id}
        data-dc-user={dm.group ? undefined : dm.userId ?? undefined} data-dc-name={dm.name} data-dc-username={dm.username ?? undefined} {...dmData(dm)}
        onContextMenu={dm.group ? event => { event.preventDefault(); setMenu({ x: event.clientX, y: event.clientY }) } : undefined}>
        <Nameplate look={dm.look} scope=".exo-dc-item" selected={current} />
        <Avatar src={dm.avatar} name={dm.name} status={dm.group ? null : dm.status ?? null} size={32} look={dm.look} scope=".exo-dc-item" />
        <span className="exo-dc-item-names">
          <span className="exo-dc-item-name"><StyledName name={dm.name} look={dm.look} scope=".exo-dc-item" /></span>
          {dm.subtitle ? <span className="exo-dc-item-sub">{dm.subtitle}</span> : null}
        </span>
        {dm.mentions ? <span className="exo-dc-badge">{dm.mentions}</span> : null}
      </button>
      <button type="button" className="exo-dc-icon-btn exo-dc-dm-close" aria-label={dm.group ? `Leave ${dm.name}` : 'Close DM'}
        onClick={() => { if (dm.group) setLeaving(true); else close() }}><Close size={14} /></button>
      {menu ? <GroupDmMenu dm={dm} {...menu} onLeave={() => { setMenu(null); setLeaving(true) }} onClose={() => setMenu(null)} /> : null}
      {leaving ? (
        <Confirm title={`Leave '${dm.name}'`} action="Leave group" onClose={() => setLeaving(false)} onConfirm={() => { setLeaving(false); close() }}>
          <p>Are you sure you want to leave <strong>{dm.name}</strong>? You won't be able to rejoin this group unless you are re-invited.</p>
        </Confirm>
      ) : null}
    </div>
  )
}

// Discord's group DM menu: Mark As Read, Mute Conversation (or Unmute), Leave Group, and Copy Channel ID.
function GroupDmMenu({ dm, x, y, onLeave, onClose }: { dm: DiscordDm; x: number; y: number; onLeave: () => void; onClose: () => void }) {
  const [sub, setSub] = useState<string | null>(null)
  const [note, setNote] = useState<string | null>(null)
  const [editing, setEditing] = useState(false)
  const info = { id: dm.id, unread: dm.unread || dm.mentions > 0, muted: !!dm.muted, mutedUntil: dm.mutedUntil ?? null }
  const done = (result: { ok: boolean; message?: string | null }) => { if (result.ok) onClose(); else setNote(result.message ?? 'Discord did not take that.') }
  const box = useRef<HTMLDivElement>(null)
  useDismiss(box, onClose, { on: !editing })
  useEffect(() => {
    if (editing) return
    const escape = (event: KeyboardEvent) => { if (event.key === 'Escape') onClose() }
    window.addEventListener('keydown', escape)
    return () => window.removeEventListener('keydown', escape)
  }, [onClose, editing])
  if (editing) return <EditGroup dm={dm} onClose={onClose} />
  return (
    <SubContext.Provider value={{ open: sub, show: setSub }}>
    <div ref={box} className="exo-dc-menu exo-dc-channel-menu exo-dc-gdm-menu" role="menu" aria-label={dm.name} style={{ left: x, top: y }}>
      <DmReadItem dm={info} onDone={done} />
      <span className="exo-dc-menu-line" role="separator" />
      <DmMuteItem dm={info} label="Mute conversation" onDone={done} />
      <button type="button" role="menuitem" onClick={() => setEditing(true)}>Edit group</button>
      <span className="exo-dc-menu-line" role="separator" />
      <button type="button" role="menuitem" className="is-danger" onClick={onLeave}>Leave group</button>
      <DmCopyId dm={info} onClose={onClose} />
      {note ? <p className="exo-dc-note" role="status">{note}</p> : null}
    </div>
    </SubContext.Provider>
  )
}

// The server header's dialogs: Invite friends (to the channel invites land in) and Privacy settings.
function HeaderDialog({ dialog, guildId, name, inviteTo, onClose }: { dialog: 'invite' | 'privacy'; guildId: string; name: string; inviteTo: DiscordChannel | null; onClose: () => void }) {
  if (dialog === 'privacy') return <ServerPrivacy guildId={guildId} name={name} onClose={onClose} />
  return inviteTo ? <InviteDialog name={name} channelId={inviteTo.id} channelName={inviteTo.name} onClose={onClose} /> : null
}

// The server menu's Create Channel and Create Category (Manage Channels) and Create Event (Manage Events), as Discord's.
function CreateItems({ list, onCreate }: { list: DiscordChannels | null; onCreate: (what: 'channel' | 'category' | 'event') => void }) {
  return (
    <>
      {list?.canManage ? <button type="button" role="menuitem" onClick={() => onCreate('channel')}><Icons.Add size={16} />Create channel</button> : null}
      {list?.canManage ? <button type="button" role="menuitem" onClick={() => onCreate('category')}><Icons.Folder size={16} />Create category</button> : null}
      {list?.canManageEvents ? <button type="button" role="menuitem" onClick={() => onCreate('event')}><Icons.Events size={16} />Create event</button> : null}
    </>
  )
}

// The server menu's Notification Settings, Privacy Settings and Hide Muted Channels, saved on your account as Discord's are.
function ViewItems({ guildId, guild, list, onDone, onPrivacy }: { guildId: string; guild: DiscordGuild | null; list: DiscordChannels | null; onDone: () => void; onPrivacy: () => void }) {
  const save = (params: Parameters<typeof host.discordServer>[1]) => { onDone(); void host.discordServer('notify', { guildId, ...params }).catch(() => { }) }
  return (
    <>
      {guild ? <NotifyItems guild={guild} onChange={save} /> : null}
      <button type="button" role="menuitem" onClick={() => { onDone(); onPrivacy() }}>Privacy settings</button>
      <button type="button" role="menuitemcheckbox" aria-checked={!!list?.hideMuted} onClick={() => save({ hideMuted: !list?.hideMuted })}>
        <span className="exo-dc-ctx-name">Hide muted channels</span>
        {list?.hideMuted ? <Icons.Check size={14} className="exo-dc-ctx-check" /> : null}
      </button>
    </>
  )
}

// Where an invite lands, as Discord picks: the open channel, else the first text channel you can read.
function inviteChannel(list: DiscordChannels | null, open: string | null) {
  return list?.channels.find(c => c.id === open && c.kind !== 'category') ?? list?.channels.find(c => c.kind === 'text' && c.canRead !== false) ?? null
}

/** The server's name with its menu (Invite People, Mark As Read, Server Settings where allowed, Create, Notification and Privacy Settings, Hide Muted Channels, Show All Channels, Leave) beside its Invite People button. */
function ServerHeader({ guildId, name, guild, list, onMarkRead, onLeave, inviteTo, onMembers, onCreate }: {
  guildId: string
  name: string
  guild: DiscordGuild | null
  list: DiscordChannels | null
  onMarkRead: () => void
  onLeave: () => void
  /** The channel Invite People's link lands in. */
  inviteTo: DiscordChannel | null
  onMembers?: () => void
  /** The menu's Create Channel, Create Category and Create Event. */
  onCreate: (what: 'channel' | 'category' | 'event') => void
}) {
  const [confirmLeave, setConfirmLeave] = useState(false)
  const [dialog, setDialog] = useState<'invite' | 'privacy' | null>(null)
  const developer = useAccountSettings().developerMode
  const [menu, setMenu] = useState(false)
  const [sub, setSub] = useState<string | null>(null)
  const [settings, setSettings] = useState(false)
  const menuBox = useRef<HTMLDivElement>(null)
  // The menu closes on a press outside it or Escape, as Discord's does.
  useDismiss(menuBox, () => setMenu(false), { on: menu })
  useEffect(() => {
    if (!menu) return
    const escape = (event: KeyboardEvent) => { if (event.key === 'Escape') setMenu(false) }
    document.addEventListener('keydown', escape)
    return () => document.removeEventListener('keydown', escape)
  }, [menu])
  return (
    <>
        <header className="exo-dc-side-head is-server">
          <button type="button" className="exo-dc-server-name" aria-expanded={menu} onClick={() => setMenu(value => !value)}>
            <span>{name}</span>
            <ChevronDown size={16} />
          </button>
          <button type="button" className="exo-dc-icon-btn exo-dc-invite" aria-label="Invite people" disabled={!inviteTo} onClick={() => setDialog('invite')}>
            <Icons.AddFriend size={18} />
          </button>
          {menu ? (
            <SubContext.Provider value={{ open: sub, show: setSub }}>
            <div className="exo-dc-menu" role="menu" ref={menuBox}>
              {inviteTo ? <button type="button" role="menuitem" onClick={() => { setMenu(false); setDialog('invite') }}><Icons.AddFriend size={16} />Invite people</button> : null}
              <button type="button" role="menuitem" onClick={() => { setMenu(false); onMarkRead() }}>Mark as read</button>
              {list?.moderation && Object.values(list.moderation).some(Boolean) ? (
                <button type="button" role="menuitem" onClick={() => void transition(() => { setMenu(false); setSettings(true) }, { kind: 'none' })}><Icons.Settings size={16} />Server settings</button>
              ) : null}
              <CreateItems list={list} onCreate={what => { setMenu(false); onCreate(what) }} />
              <ViewItems guildId={guildId} guild={guild} list={list} onDone={() => setMenu(false)} onPrivacy={() => setDialog('privacy')} />
              {/* Discord offers Show All Channels in community servers. */}
              {list?.rows?.guide ? (
                <button type="button" role="menuitemcheckbox" aria-checked={!list.optIn}
                  onClick={() => { setMenu(false); void host.discordServer('showAll', { guildId, on: list.optIn === true }).catch(() => { }) }}>
                  Show all channels {list.optIn ? '' : '✓'}
                </button>
              ) : null}
              {/* The owner can't leave their own server, so Discord doesn't offer it. */}
              {list?.moderation?.owner ? null : <button type="button" role="menuitem" className="is-danger" onClick={() => { setMenu(false); setConfirmLeave(true) }}><Icons.Leave size={16} />Leave server</button>}
              {developer ? <button type="button" role="menuitem" onClick={() => { setMenu(false); void navigator.clipboard.writeText(guildId).catch(() => { }) }}><Icons.CopyId size={16} />Copy server ID</button> : null}
            </div>
            </SubContext.Provider>
          ) : null}
        </header>
        {dialog ? <HeaderDialog dialog={dialog} guildId={guildId} name={name} inviteTo={inviteTo} onClose={() => setDialog(null)} /> : null}
        {confirmLeave ? (
          <Confirm title={`Leave '${name}'`} action="Leave server" onClose={() => setConfirmLeave(false)} onConfirm={() => { setConfirmLeave(false); onLeave() }}>
            <p>Are you sure you want to leave <strong>{name}</strong>? You won't be able to rejoin this server unless you are re-invited.</p>
          </Confirm>
        ) : null}
        {settings && list?.moderation ? <Suspense fallback={null}><ServerSettings guildId={guildId} name={name} moderation={list.moderation} channels={list.channels} onClose={() => void transition(() => setSettings(false))} onMembers={onMembers} /></Suspense> : null}
    </>
  )
}

/** Floating under the sidebar: a call in progress, then you, with mute, deafen and settings (devices and status are in settings). */
export function UserPanel({ home, voice, voicePlace, status, onOpenVoice }: {
  home: DiscordHome | null
  voice: VoiceControls
  voicePlace: string | null
  status: string | null
  onOpenVoice: () => void
}) {
  const { muted, deafened } = voice.voice
  const custom = useAccountSettings().customStatus
  if (!home?.me) return null
  return (
    <div className="exo-dc-dock">
      <VoicePanel controls={voice} place={voicePlace} onOpen={onOpenVoice} />
      <footer className="exo-dc-me">
        <button type="button" className={`exo-dc-me-who${home.me.look?.nameplate ? ' has-nameplate' : ''}`} aria-label="Your account" onClick={() => requestDiscordSettings('account')}>
          <Nameplate look={home.me.look} scope=".exo-dc-me-who" />
          <Avatar src={home.me.avatar} name={home.me.name} status={status ?? 'online'} size={32} look={home.me.look} scope=".exo-dc-me-who" />
          <span className="exo-dc-me-names">
            <strong>{home.me.name}</strong>
            {/* Your custom status in place of the status, as Discord's panel shows it. */}
            <span>{custom ? <CustomStatusLine custom={custom} /> : statusLabel(status)}</span>
          </span>
        </button>
        <button type="button" className={`exo-dc-icon-btn${muted ? ' is-off' : ''}`} aria-label={muted ? 'Unmute' : 'Mute'} aria-pressed={muted}
          onClick={() => void voice.setMuted(!muted)}>
          {muted ? <Icons.Muted size={18} /> : <Icons.Mic size={18} />}
        </button>
        <button type="button" className={`exo-dc-icon-btn${deafened ? ' is-off' : ''}`} aria-label={deafened ? 'Undeafen' : 'Deafen'} aria-pressed={deafened}
          onClick={() => void voice.setDeafened(!deafened)}>
          {deafened ? <Icons.Deafened size={18} /> : <Icons.Headphones size={18} />}
        </button>
        <button type="button" className="exo-dc-icon-btn" aria-label="Discord settings" onClick={() => requestDiscordSettings()}>
          <Icons.Settings size={18} />
        </button>
      </footer>
    </div>
  )
}

export function statusLabel(status: string | null) {
  switch (status) {
    case 'idle': return 'Idle'
    case 'dnd': return 'Do not disturb'
    case 'invisible': return 'Invisible'
    default: return 'Online'
  }
}

/**
 * The server icons down the far left, Discord's way: Home on top, a line, then the server list (folders and servers
 * alone, in the account's order). Servers and folders drag to a new place, or a server onto another to make a folder.
 */
export function Rail({ home, place, onGo, onOpenDm, onPeek, onAdd, homeIcon }: {
  home: DiscordHome | null
  place: string
  onGo: (place: string) => void
  /** An unread DM under Home: opens that conversation. */
  onOpenDm: (channelId: string) => void
  /** The pointer rests on a server (or Home): its conversation can be read ahead. */
  onPeek: (place: string | null) => void
  onAdd: () => void
  homeIcon: React.ReactNode
}) {
  const [openFolders, setOpenFolders] = useState<Set<string>>(new Set())
  const [menu, setMenu] = useState<{ guild: DiscordGuild; x: number; y: number } | null>(null)
  const [tip, setTip] = useState<{ label: string; left: number; top: number; place?: string } | null>(null)
  // Who is in the hovered server's voice channels, read from its channel list (Exo's own copy, no request).
  const [inVoice, setInVoice] = useState<{ place: string; talking: DiscordVoiceMember[]; streaming: DiscordVoiceMember[] } | null>(null)
  // A drop shows at once and stays while Discord saves it; the host moved its own list too, so home has it after.
  const [moved, setMoved] = useState<DiscordRailItem[] | null>(null)
  const rail = moved ?? home?.rail ?? []
  const drag = useRailDrag(rail, next => {
    setMoved(next)
    void host.discordUserSettings({ guildFolders: next }).catch(() => null).finally(() => setMoved(current => current === next ? null : current))
  })
  const dragging = drag.state?.moving === true
  const guilds = new Map(home?.guilds.map(g => [g.id, g]))
  return (
    <nav className={`exo-dc-rail${dragging ? ' is-dragging' : ''}`} aria-label="Servers" onScroll={() => setTip(null)} {...drag.handlers}
      onPointerOver={event => {
        if (dragging) return
        const button = (event.target as HTMLElement).closest<HTMLElement>('.exo-dc-guild')
        onPeek(button?.dataset.dcPlace ?? null)
        if (!button) { setTip(null); return }
        const box = button.getBoundingClientRect()
        const at = button.dataset.dcPlace && button.dataset.dcPlace !== '@me' ? button.dataset.dcPlace : undefined
        setTip({ label: (button.getAttribute('aria-label') ?? '').replace(/, \d+ mentions$/, ''), left: box.right + 14, top: box.top + box.height / 2, place: at })
        if (at && at !== tip?.place) {
          host.discordChannels(at).then(list => {
            const people = list.channels.filter(c => c.kind === 'voice' && !c.afk && !c.muted).flatMap(c => c.members ?? [])
            setInVoice({ place: at, talking: people.filter(m => !m.stream), streaming: people.filter(m => m.stream) })
          }).catch(() => { })
        }
      }}
      onPointerLeave={() => { setTip(null); onPeek(null) }}>
      {tip && !dragging ? (
        <div className="exo-dc-tip exo-dc-guild-tip" style={{ left: tip.left, top: tip.top }}>
          <span>{tip.label}</span>
          {inVoice && inVoice.place === tip.place ? <>
            <TipFaces icon={<Icons.Voice size={16} />} people={inVoice.talking} />
            <TipFaces icon={<Icons.Streaming size={16} />} people={inVoice.streaming} />
          </> : null}
        </div>
      ) : null}
      {/* No badge of its own: each DM with new messages shows below it with its count, as in Discord. */}
      <RailButton label="Direct messages" home place="@me" current={place === '@me'} unread={false} mentions={0} onClick={() => onGo('@me')}>
        {homeIcon}
      </RailButton>
      {/* Discord stacks each DM with messages waiting under Home, newest first, with how many (up to 20 DMs). */}
      {home?.dms.filter(d => d.mentions > 0).slice(0, 20).map(d => (
        <RailButton key={d.id} label={d.name} current={false} unread={false} mentions={d.mentions} onClick={() => onOpenDm(d.id)}>
          {d.avatar ? <img className="exo-dc-rail-dm" src={d.avatar} alt="" draggable={false} /> : <span className="exo-dc-initials">{initials(d.name)}</span>}
        </RailButton>
      ))}
      <span className="exo-dc-rail-line" aria-hidden="true" />
      {rail.map(item => {
        const members = item.guildIds.map(id => guilds.get(id)).filter(g => g !== undefined)
        if (members.length === 0) return null
        if (item.id == null) return <GuildButton key={members[0].id} guild={members[0]} place={place} onGo={onGo} onMenu={setMenu} drop={drag.mark(members[0].id)} />
        const id = item.id
        return (
          <Folder key={id} folder={item} members={members} open={openFolders.has(id)} place={place} onGo={onGo} onMenu={setMenu} mark={drag.mark}
            onToggle={() => setOpenFolders(set => { const next = new Set(set); if (!next.delete(id)) next.add(id); return next })} />
        )
      })}
      <button type="button" className="exo-dc-guild is-add" aria-label="Add a server" onClick={onAdd}><Icons.Plus size={24} /></button>
      {menu ? <GuildMenu key={menu.guild.id} {...menu} onGo={onGo} onClose={() => setMenu(null)} /> : null}
      <div ref={drag.ghost} className="exo-dc-rail-ghost" aria-hidden="true" hidden={!dragging} />
    </nav>
  )
}

/** A server folder in the rail: its servers' icons while closed, the folder and each server while open. */
function Folder({ folder, members, open, place, onGo, onMenu, onToggle, mark }: {
  folder: DiscordRailItem
  members: DiscordGuild[]
  open: boolean
  place: string
  onGo: (place: string) => void
  onMenu: (menu: { guild: DiscordGuild; x: number; y: number }) => void
  onToggle: () => void
  mark: (key: string, container?: boolean) => string
}) {
  const key = `folder:${folder.id}`
  const color = folder.color != null ? `#${folder.color.toString(16).padStart(6, '0')}` : undefined
  return (
    <div className={`exo-dc-folder${open ? ' is-open' : ''}${mark(key, true)}`}>
      <RailButton label={folder.name ?? 'Server folder'} current={false} unread={!open && members.some(g => g.unread)} node={key} drop={mark(key)}
        mentions={open ? 0 : members.reduce((sum, g) => sum + g.mentions, 0)} onClick={onToggle}>
        {open
          ? <span className="exo-dc-folder-icon" style={color ? { color } : undefined}><Icons.Folder size={22} /></span>
          : <span className="exo-dc-folder-grid">{members.slice(0, 4).map(g => g.icon ? <img key={g.id} src={g.icon} alt="" draggable={false} /> : <i key={g.id}>{initials(g.name)}</i>)}</span>}
      </RailButton>
      {open ? members.map((g, at) => <GuildButton key={g.id} guild={g} place={place} onGo={onGo} onMenu={onMenu} drop={mark(g.id)} within={at === members.length - 1 ? 'last' : 'in'} />) : null}
    </div>
  )
}

type RailDragState = { source: string; pointer: number; x: number; y: number; moving: boolean; drop: RailDrop | null; next: DiscordRailItem[] | null }

/**
 * Pointer dragging in the rail, Discord's way: past a few pixels a press on a server or folder lifts it, its picture
 * follows the pointer, and where it would land shows as a bar between icons (or a ring on the server or folder it
 * would join). Only a drop that changes the list counts; the click that ends a drag does nothing else.
 */
function useRailDrag(rail: DiscordRailItem[], onDrop: (next: DiscordRailItem[]) => void) {
  const [state, setState] = useState<RailDragState | null>(null)
  const ghost = useRef<HTMLDivElement>(null)
  const dragged = useRef(false)
  const lift = (nav: HTMLElement, event: React.PointerEvent<HTMLElement>, source: string) => {
    try { nav.setPointerCapture(event.pointerId) } catch { /* a pointer already up; the drag still follows the rail */ }
    dragged.current = true
    const node = nav.querySelector<HTMLElement>(`[data-dc-node="${source}"]`)
    if (!ghost.current || !node) return
    // A picture of it, not another drag target.
    const copy = node.cloneNode(true) as HTMLElement
    copy.removeAttribute('data-dc-node')
    ghost.current.replaceChildren(copy)
  }
  const handlers = {
    onPointerDown: (event: React.PointerEvent<HTMLElement>) => {
      dragged.current = false
      const node = (event.target as HTMLElement).closest<HTMLElement>('[data-dc-node]')
      if (event.button !== 0 || !node?.dataset.dcNode) return
      setState({ source: node.dataset.dcNode, pointer: event.pointerId, x: event.clientX, y: event.clientY, moving: false, drop: null, next: null })
    },
    onPointerMove: (event: React.PointerEvent<HTMLElement>) => {
      if (!state || event.pointerId !== state.pointer) return
      const nav = event.currentTarget
      if (!state.moving) {
        if (Math.hypot(event.clientX - state.x, event.clientY - state.y) < 6) return
        lift(nav, event, state.source)
      }
      if (ghost.current) ghost.current.style.transform = `translate(${event.clientX}px, ${event.clientY}px)`
      const box = nav.getBoundingClientRect()
      const edge = event.clientY < box.top + 48 ? -1 : event.clientY > box.bottom - 48 ? 1 : 0
      if (edge) nav.scrollBy({ top: edge * 16 })
      const drop = dropAt(nav, event.clientY)
      if (state.moving && drop?.kind === state.drop?.kind && drop?.target === state.drop?.target) return
      const next = drop ? moveInRail(rail, state.source, drop) : null
      const changes = next !== null && railShape(next) !== railShape(rail)
      setState({ ...state, moving: true, drop: changes ? drop : null, next: changes ? next : null })
    },
    onPointerUp: (event: React.PointerEvent<HTMLElement>) => {
      if (!state || event.pointerId !== state.pointer) return
      if (state.next) onDrop(state.next)
      setState(null)
    },
    onPointerCancel: () => setState(null),
    onClickCapture: (event: React.MouseEvent) => { if (dragged.current) { dragged.current = false; event.preventDefault(); event.stopPropagation() } },
  }
  /** A server's or folder's drag mark: lifted, a bar before or after it, or a ring when the drag would join it. */
  const mark = (key: string, container = false) => {
    if (!state?.moving) return ''
    if (key === state.source) return container ? '' : ' is-lifted'
    const drop = state.drop
    if (drop?.target !== key) return ''
    // After a folder: below the whole folder, not its own icon.
    if (key.startsWith('folder:') && drop.kind === 'after') return container ? ' is-drop-after' : ''
    return container ? '' : ` is-drop-${drop.kind}`
  }
  return { state, ghost, handlers, mark }
}

/**
 * Where a drop at this height lands, by Discord's targets: the top of an icon (and the gap above it) puts it before;
 * the rest joins it (a server or folder in the list) or still goes before it (a server in a folder); the foot of an
 * open folder adds it at the folder's end; below the last icon puts it after.
 */
function dropAt(nav: HTMLElement, y: number): RailDrop | null {
  let last: string | null = null
  for (const node of nav.querySelectorAll<HTMLElement>('[data-dc-node]')) {
    const key = node.dataset.dcNode ?? ''
    const box = node.getBoundingClientRect()
    const within = node.dataset.dcWithin
    if (!within) last = key
    if (y < box.top + box.height * 0.375) return { kind: 'before', target: key }
    if (y < box.bottom - 7) return { kind: within ? 'before' : 'into', target: key }
    const folder = within === 'last' ? node.closest('.exo-dc-folder') : null
    if (folder && y < folder.getBoundingClientRect().bottom) return { kind: 'after', target: key }
  }
  return last ? { kind: 'after', target: last } : null
}

// One row of Discord's server tooltip: what they're doing, then up to six faces and how many more.
function TipFaces({ icon, people }: { icon: React.ReactNode; people: DiscordVoiceMember[] }) {
  if (people.length === 0) return null
  return (
    <span className="exo-dc-tip-faces">
      {icon}
      {people.slice(0, 6).map(m => <Avatar key={m.id} src={m.avatar} name={m.name} status={null} size={20} />)}
      {people.length > 6 ? <span className="exo-dc-tip-more">+{people.length - 6}</span> : null}
    </span>
  )
}

function GuildButton({ guild, place, onGo, onMenu, drop, within }: {
  guild: DiscordGuild
  place: string
  onGo: (place: string) => void
  onMenu: (menu: { guild: DiscordGuild; x: number; y: number }) => void
  /** Its drag mark (useRailDrag). */
  drop: string
  /** In an open folder: its last server, or another. */
  within?: 'in' | 'last'
}) {
  return (
    <RailButton label={guild.name} place={guild.id} current={place === guild.id} unread={guild.unread} mentions={guild.mentions} inVoice={guild.inVoice}
      muted={guild.muted} onClick={() => onGo(guild.id)} onMenu={(x, y) => onMenu({ guild, x, y })} node={guild.id} drop={drop} within={within}>
      {guild.icon ? <img src={guild.icon} alt="" draggable={false} loading="lazy" /> : <span className="exo-dc-initials">{initials(guild.name)}</span>}
    </RailButton>
  )
}

function RailButton({ label, home, place, current, unread, mentions, inVoice = 0, muted, onClick, onMenu, node, drop = '', within, children }: {
  label: string
  /** People in its voice channels: a speaker mark at its foot. */
  inVoice?: number
  home?: boolean
  /** The server (or Home) it opens. */
  place?: string
  current: boolean
  unread: boolean
  mentions: number
  muted?: boolean
  onClick: () => void
  /** Right-click: the server's menu at the pointer. */
  onMenu?: (x: number, y: number) => void
  /** What it is in the server list, for dragging: a server id or folder:<id>. */
  node?: string
  drop?: string
  within?: 'in' | 'last'
  children: React.ReactNode
}) {
  return (
    <button type="button" className={`exo-dc-guild${home ? ' is-home' : ''}${unread ? ' is-unread' : ''}${muted ? ' is-muted' : ''}${drop}`} aria-current={current}
      aria-label={[label, mentions ? `${mentions} mentions` : null, inVoice ? `${inVoice} in voice` : null].filter(Boolean).join(', ')} onClick={onClick} data-dc-place={place} data-dc-node={node} data-dc-within={within}
      onContextMenu={onMenu ? event => { event.preventDefault(); onMenu(event.clientX, event.clientY) } : undefined}>
      {children}
      {inVoice ? <span className="exo-dc-guild-voice" aria-hidden="true"><Icons.Sound size={10} /></span> : null}
      {mentions ? <span className="exo-dc-badge">{mentions > 99 ? '99+' : mentions}</span> : null}
    </button>
  )
}

/**
 * Discord's right-click menu on a server in the rail: Mark As Read (off when nothing is unread), Invite to Server, Mute
 * Server for a while (or Unmute, with when it ends), Notification Settings with the two Suppress switches, Leave Server
 * (not for its owner), and Copy Server ID in Developer Mode.
 */
function GuildMenu({ guild, x, y, onGo, onClose }: { guild: DiscordGuild; x: number; y: number; onGo: (place: string) => void; onClose: () => void }) {
  const developer = useAccountSettings().developerMode
  const [note, setNote] = useState<string | null>(null)
  const [leaving, setLeaving] = useState(false)
  // Invite to Server opens Discord's invite dialog for the first channel you can read.
  const [inviteTo, setInviteTo] = useState<DiscordChannel | null>(null)
  const [sub, setSub] = useState<string | null>(null)
  const box = useRef<HTMLDivElement>(null)
  useDismiss(box, onClose, { on: !leaving && !inviteTo })
  useEffect(() => {
    if (leaving) return
    const escape = (event: KeyboardEvent) => { if (event.key === 'Escape') onClose() }
    window.addEventListener('keydown', escape)
    return () => window.removeEventListener('keydown', escape)
  }, [onClose, leaving])
  const why = 'Discord did not take that.'
  const run = (work: Promise<{ ok: boolean; message?: string | null }>) =>
    void work.then(result => { if (result.ok) onClose(); else setNote(result.message ?? why) }).catch(() => setNote(why))
  const server = (action: 'mute' | 'notify', params: Parameters<typeof host.discordServer>[1]) => run(host.discordServer(action, { guildId: guild.id, ...params }))
  const invite = () => void host.discordChannels(guild.id).then(list => {
    const channel = inviteChannel(list, null)
    if (channel) setInviteTo(channel); else setNote("There's no channel to invite people to.")
  }).catch(() => setNote(why))
  if (inviteTo) return <InviteDialog name={guild.name} channelId={inviteTo.id} channelName={inviteTo.name} onClose={onClose} />
  if (leaving) {
    return (
      <Confirm title={`Leave '${guild.name}'`} action="Leave server" error={note} onClose={onClose}
        onConfirm={() => void host.discordServer('leave', { guildId: guild.id }).then(result => { if (result.ok) { onClose(); onGo('@me') } else setNote(result.message ?? why) }, () => setNote(why))}>
        <p>Are you sure you want to leave <strong>{guild.name}</strong>? You won't be able to rejoin this server unless you are re-invited.</p>
      </Confirm>
    )
  }
  return (
    <SubContext.Provider value={{ open: sub, show: setSub }}>
    <div ref={box} className="exo-dc-menu exo-dc-channel-menu exo-dc-guild-menu" role="menu" aria-label={guild.name} style={{ left: x, top: y }}>
      <button type="button" role="menuitem" disabled={!guild.unread && !guild.mentions} onClick={() => run(host.discordMarkRead(guild.id))}>Mark as read</button>
      <span className="exo-dc-menu-line" role="separator" />
      <button type="button" role="menuitem" onClick={invite}>Invite to server</button>
      <span className="exo-dc-menu-line" role="separator" />
      {guild.muted ? (
        <button type="button" role="menuitem" onClick={() => server('mute', { muted: false })}>
          <span className="exo-dc-menu-text">Unmute server{guild.mutedUntil ? <small>Muted until {new Date(guild.mutedUntil).toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' })}</small> : null}</span>
        </button>
      ) : (
        <Submenu label="Mute server">
          {MUTE_FOR.map(([minutes, label]) => <button key={minutes} type="button" role="menuitem" onClick={() => server('mute', minutes ? { muted: true, minutes } : { muted: true })}>{label}</button>)}
        </Submenu>
      )}
      <NotifyItems guild={guild} onChange={params => server('notify', params)} />
      {guild.owner ? null : <>
        <span className="exo-dc-menu-line" role="separator" />
        <button type="button" role="menuitem" className="is-danger" onClick={() => setLeaving(true)}>Leave server</button>
      </>}
      {developer ? <>
        <span className="exo-dc-menu-line" role="separator" />
        <button type="button" role="menuitem" onClick={() => { void navigator.clipboard.writeText(guild.id).catch(() => { }); onClose() }}><Icons.CopyId size={16} />Copy server ID</button>
      </> : null}
      {note ? <p className="exo-dc-note" role="status">{note}</p> : null}
    </div>
    </SubContext.Provider>
  )
}

// A server's Notification Settings, in its menus: the level, then Suppress @everyone and @here and Suppress all role @mentions.
function NotifyItems({ guild, onChange }: { guild: DiscordGuild; onChange: (params: Parameters<typeof host.discordServer>[1]) => void }) {
  const level = guild.notify ?? 0
  return (
    <Submenu label="Notification settings">
      {LEVELS.map(([value, label]) => (
        <button key={value} type="button" role="menuitemradio" aria-checked={level === value} onClick={() => onChange({ level: value })}>
          <span className="exo-dc-ctx-name">{label}</span>
          {level === value ? <Icons.Check size={14} className="exo-dc-ctx-check" /> : null}
        </button>
      ))}
      <span className="exo-dc-menu-line" role="separator" />
      <button type="button" role="menuitemcheckbox" aria-checked={!!guild.suppressEveryone} onClick={() => onChange({ suppressEveryone: !guild.suppressEveryone })}>
        <span className="exo-dc-ctx-name">Suppress @everyone and @here</span>
        {guild.suppressEveryone ? <Icons.Check size={14} className="exo-dc-ctx-check" /> : null}
      </button>
      <button type="button" role="menuitemcheckbox" aria-checked={!!guild.suppressRoles} onClick={() => onChange({ suppressRoles: !guild.suppressRoles })}>
        <span className="exo-dc-ctx-name">Suppress all role @mentions</span>
        {guild.suppressRoles ? <Icons.Check size={14} className="exo-dc-ctx-check" /> : null}
      </button>
    </Submenu>
  )
}

/** A channel's mark, as Discord draws it: with a lock when @everyone can't see it (a voice one: can't join it). */
export function ChannelIcon({ channel, size = 18 }: { channel: DiscordChannel; size?: number }) {
  if (channel.kind === 'voice') return channel.locked ? <Icons.VoiceLock size={size} /> : <Icons.Voice size={size} />
  if (channel.rules) return <Icons.Rules size={size} />
  if (channel.kind === 'forum') return <Icons.Forum size={size} />
  if (channel.type === 5) return <Icons.Announcement size={size} />
  return channel.locked ? <Icons.HashLock size={size} /> : <Icons.Hash size={size} />
}

function ChannelList({ channels, open, collapsed, onPick, onToggle, onJoin, onCreate, onMenu, voice }: {
  channels: DiscordChannel[]
  open: string | null
  collapsed: Set<string>
  onPick: (id: string) => void
  onToggle: (id: string) => void
  onJoin: (id: string) => void
  onCreate?: (category: DiscordChannel) => void
  onMenu: (channel: DiscordChannel, x: number, y: number, inCategory: boolean) => void
  voice: VoiceControls
}) {
  let category: string | null = null
  return channels.map(c => {
    if (c.kind === 'category') {
      category = c.id
      return (
        <div key={c.id} className="exo-dc-category-row">
          <button type="button" className="exo-dc-category" aria-expanded={!collapsed.has(c.id)} onClick={() => onToggle(c.id)}
            onContextMenu={event => { event.preventDefault(); onMenu(c, event.clientX, event.clientY, false) }}>
            <span>{c.name}</span>
            <ChevronDown size={12} />
          </button>
          {onCreate ? <button type="button" className="exo-dc-icon-btn exo-dc-category-add" aria-label={`Create channel in ${c.name}`} onClick={() => onCreate(c)}><Icons.Plus size={16} /></button> : null}
        </div>
      )
    }
    if (category && collapsed.has(category) && !(c.unread && !c.muted) && c.id !== open) return null
    const inCategory = category !== null
    if (c.kind === 'voice') return <VoiceChannel key={c.id} channel={c} voice={voice} onJoin={onJoin} onMenu={(channel, x, y) => onMenu(channel, x, y, inCategory)} />
    // Any text channel listed opens, as in Discord: one without Read Message History shows only what arrives while open.
    const readable = c.kind === 'text' || c.kind === 'forum'
    return (
      <button key={c.id} type="button"
        className={`exo-dc-item${c.unread ? ' is-unread' : ''}${c.muted ? ' is-muted' : ''}`}
        aria-current={c.id === open}
        disabled={!readable}
        data-dc-channel={c.kind === 'text' && readable ? c.id : undefined}
        onContextMenu={event => { event.preventDefault(); onMenu(c, event.clientX, event.clientY, inCategory) }}
        onClick={() => onPick(c.id)}>
        <span className="exo-dc-hash" aria-hidden="true"><ChannelIcon channel={c} /></span>
        <span className="exo-dc-item-name">{c.name}</span>
        {c.mentions ? <span className="exo-dc-badge">{c.mentions}</span> : null}
      </button>
    )
  })
}

/** A voice channel: click to join; who is in it underneath, ringed while they speak. */
function VoiceChannel({ channel, voice, onJoin, onMenu }: { channel: DiscordChannel; voice: VoiceControls; onJoin: (id: string) => void; onMenu: (channel: DiscordChannel, x: number, y: number) => void }) {
  const here = voice.voice.channelId === channel.id && voice.voice.state !== 'off'
  return (
    <div className="exo-dc-voice-channel" onContextMenu={event => { if ((event.target as Element).closest('[data-dc-user]')) return; event.preventDefault(); onMenu(channel, event.clientX, event.clientY) }}>
      <button type="button" className={`exo-dc-item${here ? ' is-here' : ''}`} disabled={channel.canConnect === false && !here}
        aria-label={here ? `${channel.name}, you're here` : `Join ${channel.name}`} aria-current={here} onClick={() => onJoin(channel.id)}>
        <span className="exo-dc-hash" aria-hidden="true"><ChannelIcon channel={channel} /></span>
        <span className="exo-dc-item-name">{channel.name}</span>
        {channel.userLimit ? <span className="exo-dc-voice-limit" aria-label={`${channel.members?.length ?? 0} of ${channel.userLimit} users`}>
          {channel.members?.length ?? 0}/{channel.userLimit}</span> : null}
      </button>
      {channel.status ? <VoiceStatus text={channel.status} /> : null}
      {channel.members?.length ? <VoiceMembers members={channel.members} speaking={voice.speaking} /> : null}
    </div>
  )
}

/** Discord's Select Friends for a new DM: one friend opens your DM with them; two to nine make a group (10 with you). */
function StartDm({ onOpen, onClose }: { onOpen: (channelId: string) => void; onClose: () => void }) {
  const [friends, setFriends] = useState<DiscordFriend[]>([])
  const [query, setQuery] = useState('')
  const [picked, setPicked] = useState<string[]>([])
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const box = useRef<HTMLDivElement>(null)
  useDismiss(box, onClose)
  useEffect(() => { host.discordFriends().then(result => setFriends(result.friends.filter(f => f.kind === 'friend'))).catch(() => { }) }, [])
  const left = 9 - picked.length
  const toggle = (id: string) => setPicked(list => list.includes(id) ? list.filter(x => x !== id) : list.length < 9 ? [...list, id] : list)
  async function create() {
    const one = picked.length === 1 ? friends.find(f => f.id === picked[0]) : null
    if (one?.dmId) { onOpen(one.dmId); return }
    setBusy(true)
    setError(null)
    const result = await host.discordFriend('dm', one ? { userId: one.id } : { userIds: picked }).catch(() => null)
    setBusy(false)
    if (result?.channelId) onOpen(result.channelId); else setError(result?.message ?? 'Discord did not make that DM.')
  }
  const q = query.trim().toLowerCase()
  return (
    <div ref={box} className="exo-dc-menu is-start" role="dialog" aria-label="Select friends">
      <strong>Select friends</strong>
      <span className="exo-dc-note">{left === 0 ? 'This group has a 10 member limit.' : left === 1 ? 'You can select 1 more friend.' : `You can select ${left} more friends.`}</span>
      <input className="exo-dc-find" placeholder="Type the username of a friend" value={query} autoFocus
        onChange={event => setQuery(event.target.value)} onKeyDown={event => { if (event.key === 'Escape') onClose() }} aria-label="Find a friend" />
      <div className="exo-dc-start-list">
        {friends.filter(f => !q || f.name.toLowerCase().includes(q) || (f.username ?? '').toLowerCase().includes(q)).slice(0, 40).map(friend => (
          <button key={friend.id} type="button" aria-pressed={picked.includes(friend.id)} disabled={!picked.includes(friend.id) && left === 0} onClick={() => toggle(friend.id)}>
            <Avatar src={friend.avatar} name={friend.name} status={friend.status} size={24} />
            <span>{friend.name}</span>
            {picked.includes(friend.id) ? <Icons.Check size={16} className="exo-dc-ctx-check" /> : null}
          </button>
        ))}
      </div>
      {error ? <p className="is-bad" role="alert">{error}</p> : null}
      <button type="button" className="exo-set-btn is-primary" disabled={!picked.length || busy} onClick={() => void create()}>Create DM</button>
    </div>
  )
}

/** Discord's friend picker: type a name, pick a friend. */
export function FriendPicker({ title, onPick, onClose }: { title: string; onPick: (friend: DiscordFriend) => void; onClose: () => void }) {
  const [friends, setFriends] = useState<DiscordFriend[]>([])
  const [query, setQuery] = useState('')
  const box = useRef<HTMLDivElement>(null)
  useDismiss(box, onClose)
  useEffect(() => { host.discordFriends().then(result => setFriends(result.friends.filter(f => f.kind === 'friend'))).catch(() => { }) }, [])
  const q = query.trim().toLowerCase()
  return (
    <div ref={box} className="exo-dc-menu is-start" role="dialog" aria-label={title}>
      <input className="exo-dc-find" placeholder="Type the username of a friend" value={query} autoFocus
        onChange={event => setQuery(event.target.value)} onKeyDown={event => { if (event.key === 'Escape') onClose() }} aria-label="Find a friend" />
      <div className="exo-dc-start-list">
        {friends.filter(f => !q || f.name.toLowerCase().includes(q) || (f.username ?? '').toLowerCase().includes(q)).slice(0, 40).map(friend => (
          <button key={friend.id} type="button" onClick={() => onPick(friend)}>
            <Avatar src={friend.avatar} name={friend.name} status={friend.status} size={24} />
            <span>{friend.name}</span>
          </button>
        ))}
      </div>
    </div>
  )
}

/**
 * Discord's right-click menu on a channel: Mark As Read (off when nothing is unread), Invite People, Copy Link, Mute
 * Channel for a while (or Unmute, with when it ends), Notification Settings (not on voice), Edit and Delete Channel,
 * and Copy Channel ID in Developer Mode.
 */
function ChannelMenu({ channel, x, y, inCategory, guildId, serverName, canManage, voice, onClose, onEdit }: {
  channel: DiscordChannel
  x: number
  y: number
  inCategory: boolean
  guildId: string
  serverName: string
  canManage: boolean
  /** Your call: in this voice channel, Set Channel Status is offered. */
  voice: VoiceControls['voice']
  onClose: () => void
  onEdit: () => void
}) {
  const developer = useAccountSettings().developerMode
  const [confirmDelete, setConfirmDelete] = useState(false)
  // Set Channel Status or Invite People, open over the room in the menu's place.
  const [dialog, setDialog] = useState<'status' | 'invite' | null>(null)
  const here = voice.state !== 'off' && voice.channelId === channel.id
  const [note, setNote] = useState<string | null>(null)
  const [sub, setSub] = useState<string | null>(null)
  const box = useRef<HTMLDivElement>(null)
  useDismiss(box, onClose, { on: !confirmDelete && !dialog })
  useEffect(() => {
    if (confirmDelete) return
    const escape = (event: KeyboardEvent) => { if (event.key === 'Escape') onClose() }
    window.addEventListener('keydown', escape)
    return () => window.removeEventListener('keydown', escape)
  }, [onClose, confirmDelete])
  const run = (work: Promise<{ ok: boolean; message?: string | null }>) =>
    void work.then(result => { if (result.ok) onClose(); else setNote(result.message ?? 'Discord did not take that.') }).catch(() => setNote('Discord did not take that.'))
  const copy = (text: string) => { void navigator.clipboard.writeText(text).catch(() => { }); onClose() }
  const notify = (change: Parameters<typeof host.discordChannelNotify>[1]) => run(host.discordChannelNotify(channel.id, change))
  if (dialog) return { status: <VoiceStatusDialog channel={channel} onClose={onClose} />, invite: <InviteDialog name={serverName} channelId={channel.id} channelName={channel.name} onClose={onClose} /> }[dialog]
  if (confirmDelete) {
    return (
      <Confirm title="Delete channel" action="Delete channel" error={note} onClose={onClose} onConfirm={() => run(host.discordChannel(channel.id, 'delete'))}>
        <p>Are you sure you want to delete <strong>#{channel.name}</strong>? This cannot be undone.</p>
      </Confirm>
    )
  }
  return (
    <SubContext.Provider value={{ open: sub, show: setSub }}>
    <div className="exo-dc-menu exo-dc-channel-menu" role="menu" aria-label={channel.name} style={{ left: x, top: y }} ref={box}>
      <button type="button" role="menuitem" disabled={!channel.unread && !channel.mentions} onClick={() => run(host.discordChannel(channel.id, 'read'))}>Mark as read</button>
      <button type="button" role="menuitem" onClick={() => setDialog('invite')}>Invite people</button>
      <button type="button" role="menuitem" onClick={() => copy(`https://discord.com/channels/${guildId}/${channel.id}`)}>Copy link</button>
      <span className="exo-dc-menu-line" role="separator" />
      <MuteItems what="channel" channel={channel} onChange={notify} />
      {channel.kind === 'voice' ? null : <NotifyLevels channel={channel} inCategory={inCategory} onPick={level => notify({ level })} />}
      {/* Discord's: Set Channel Status while you're in it, Clear Channel Status for a channel manager outside it. */}
      {channel.kind === 'voice' && here && channel.canSetStatus ? <button type="button" role="menuitem" onClick={() => setDialog('status')}>Set channel status</button>
        : channel.kind === 'voice' && channel.canEdit && channel.status ? <button type="button" role="menuitem" onClick={() => run(host.discordServer('voiceStatus', { channelId: channel.id, text: '' }))}>Clear channel status</button> : null}
      <span className="exo-dc-menu-line" role="separator" />
      {channel.canEdit ? <button type="button" role="menuitem" onClick={onEdit}>Edit channel</button> : null}
      {canManage ? (
        <button type="button" role="menuitem" className="is-danger" onClick={() => setConfirmDelete(true)}>Delete channel</button>
      ) : null}

      {/* Copy Channel ID is Developer Mode's (Discord settings > Developer), as in Discord. */}
      {developer ? <button type="button" role="menuitem" onClick={() => copy(channel.id)}><Icons.CopyId size={16} />Copy channel ID</button> : null}
      {note ? <p className="exo-dc-note" role="status">{note}</p> : null}
    </div>
    </SubContext.Provider>
  )
}

type Jump = { kind: 'unread' | 'mentions'; count: number } | null

// What lies out of sight above or below in the channel list: channels that mention you (counted), else unread ones;
// muted channels don't count, as in Discord.
function outOfSight(box: HTMLElement, above: boolean): Jump {
  const view = box.getBoundingClientRect()
  const away = (el: Element) => { const r = el.getBoundingClientRect(); return above ? r.bottom <= view.top + 4 : r.top >= view.bottom - 4 }
  const unread = [...box.querySelectorAll('.exo-dc-item.is-unread:not(.is-muted)')].filter(away)
  const mentions = [...box.querySelectorAll('.exo-dc-item .exo-dc-badge')].filter(away)
  if (mentions.length) return { kind: 'mentions', count: mentions.reduce((sum, badge) => sum + (Number(badge.textContent) || 1), 0) }
  return unread.length ? { kind: 'unread', count: unread.length } : null
}

/** Discord's New Unreads and New Mentions bars over the channel list, for what is scrolled out of sight; a press goes to it. */
function JumpBars({ list, channels }: { list: React.RefObject<HTMLDivElement | null>; channels?: DiscordChannel[] }) {
  const [jumps, setJumps] = useState<{ up: Jump; down: Jump }>({ up: null, down: null })
  useEffect(() => {
    const box = list.current
    if (!box) return
    let frame = 0
    const measure = () => {
      cancelAnimationFrame(frame)
      frame = requestAnimationFrame(() => setJumps(now => {
        const next = { up: outOfSight(box, true), down: outOfSight(box, false) }
        return JSON.stringify(next) === JSON.stringify(now) ? now : next
      }))
    }
    measure()
    box.addEventListener('scroll', measure, { passive: true })
    const resized = new ResizeObserver(measure)
    resized.observe(box)
    return () => { box.removeEventListener('scroll', measure); resized.disconnect(); cancelAnimationFrame(frame) }
  }, [list, channels])
  const go = (above: boolean) => {
    const box = list.current
    if (!box) return
    const view = box.getBoundingClientRect()
    const targets = [...box.querySelectorAll<HTMLElement>('.exo-dc-item.is-unread:not(.is-muted), .exo-dc-item:has(.exo-dc-badge)')]
      .filter(el => { const r = el.getBoundingClientRect(); return above ? r.bottom <= view.top + 4 : r.top >= view.bottom - 4 })
    ;(above ? targets.at(-1) : targets[0])?.scrollIntoView({ block: 'center', behavior: shouldReduceMotion() ? 'auto' : 'smooth' })
  }
  const label = (jump: NonNullable<Jump>) => jump.kind === 'mentions' ? `${jump.count} new mention${jump.count === 1 ? '' : 's'}` : 'New unreads'
  return (
    <>
      {jumps.up ? <button type="button" className={`exo-dc-side-jump is-up${jumps.up.kind === 'mentions' ? ' is-mention' : ''}`} onClick={() => go(true)}><ChevronUp size={14} />{label(jumps.up)}</button> : null}
      {jumps.down ? <button type="button" className={`exo-dc-side-jump is-down${jumps.down.kind === 'mentions' ? ' is-mention' : ''}`} onClick={() => go(false)}><ChevronDown size={14} />{label(jumps.down)}</button> : null}
    </>
  )
}

type MenuAt = { channel: DiscordChannel; x: number; y: number; inCategory: boolean }

// A right-click in the channel list: a category's menu, or a channel's.
function SideMenu({ menu, guildId, serverName, list, collapsed, voice, onToggle, onClose, onEdit }: {
  menu: MenuAt
  guildId: string
  serverName: string
  list: DiscordChannels | null
  collapsed: Set<string>
  voice: VoiceControls['voice']
  onToggle: (id: string, folded?: boolean) => void
  onClose: () => void
  onEdit: () => void
}) {
  const canManage = list?.canManage === true
  if (menu.channel.kind === 'category') {
    return <CategoryMenu category={menu.channel} x={menu.x} y={menu.y} channels={list?.channels ?? []} collapsed={collapsed} canManage={canManage} onToggle={onToggle} onClose={onClose} onEdit={onEdit} />
  }
  return <ChannelMenu {...menu} guildId={guildId} serverName={serverName} canManage={canManage} voice={voice} onClose={onClose} onEdit={onEdit} />
}

/**
 * Discord's category menu: Mark as read (its channels), Collapse category and Collapse all categories, Mute category,
 * its Notification settings, then Edit category and Delete category for those who may, and Copy category ID.
 */
function CategoryMenu({ category, x, y, channels, collapsed, canManage, onToggle, onClose, onEdit }: {
  category: DiscordChannel
  x: number
  y: number
  channels: DiscordChannel[]
  collapsed: Set<string>
  canManage: boolean
  onToggle: (id: string, folded?: boolean) => void
  onClose: () => void
  onEdit: () => void
}) {
  const developer = useAccountSettings().developerMode
  const [sub, setSub] = useState<string | null>(null)
  const [deleting, setDeleting] = useState(false)
  const [note, setNote] = useState<string | null>(null)
  const box = useRef<HTMLDivElement>(null)
  useDismiss(box, onClose, { on: !deleting })
  const run = (work: Promise<{ ok: boolean; message?: string | null }>) =>
    void work.then(result => { if (result.ok) onClose(); else setNote(result.message ?? 'Discord did not take that.') }).catch(() => setNote('Discord did not take that.'))
  const categories = channels.filter(c => c.kind === 'category')
  // Its channels: the ones listed after it, up to the next category.
  const at = channels.indexOf(category)
  const next = channels.findIndex((c, i) => i > at && c.kind === 'category')
  const unread = channels.slice(at + 1, next < 0 ? undefined : next).filter(c => c.unread || c.mentions)
  const notify = (change: Parameters<typeof host.discordChannelNotify>[1]) => run(host.discordChannelNotify(category.id, change))
  if (deleting) {
    return (
      <Confirm title="Delete category" action="Delete category" error={note} onClose={onClose} onConfirm={() => run(host.discordChannel(category.id, 'delete'))}>
        <p>Are you sure you want to delete <strong>{category.name}</strong>? This cannot be undone.</p>
      </Confirm>
    )
  }
  return (
    <SubContext.Provider value={{ open: sub, show: setSub }}>
    <div className="exo-dc-menu exo-dc-channel-menu" role="menu" aria-label={category.name} style={{ left: x, top: y }} ref={box}>
      <button type="button" role="menuitem" disabled={!unread.length} onClick={() => run(Promise.all(unread.map(c => host.discordChannel(c.id, 'read'))).then(() => ({ ok: true })))}>Mark as read</button>
      <span className="exo-dc-menu-line" role="separator" />
      <button type="button" role="menuitemcheckbox" aria-checked={collapsed.has(category.id)} onClick={() => { onToggle(category.id); onClose() }}>
        <span className="exo-dc-ctx-name">Collapse category</span>
        {collapsed.has(category.id) ? <Icons.Check size={14} className="exo-dc-ctx-check" /> : null}
      </button>
      {categories.every(c => collapsed.has(c.id)) ? null
        : <button type="button" role="menuitem" onClick={() => { for (const c of categories) onToggle(c.id, true); onClose() }}>Collapse all categories</button>}
      <span className="exo-dc-menu-line" role="separator" />
      <MuteItems what="category" channel={category} onChange={notify} />
      <NotifyLevels channel={category} inCategory={false} onPick={level => notify({ level })} />
      {category.canEdit || canManage ? <span className="exo-dc-menu-line" role="separator" /> : null}
      {category.canEdit ? <button type="button" role="menuitem" onClick={onEdit}>Edit category</button> : null}
      {canManage ? <button type="button" role="menuitem" className="is-danger" onClick={() => setDeleting(true)}>Delete category</button> : null}
      {developer ? <button type="button" role="menuitem" onClick={() => { void navigator.clipboard.writeText(category.id).catch(() => { }); onClose() }}><Icons.CopyId size={16} />Copy category ID</button> : null}
      {note ? <p className="exo-dc-note" role="status">{note}</p> : null}
    </div>
    </SubContext.Provider>
  )
}

// Mute a channel or category for a while, or unmute it (saying until when a timed mute lasts).
function MuteItems({ what, channel, onChange }: { what: 'channel' | 'category'; channel: DiscordChannel; onChange: (change: { muted: boolean; minutes?: number }) => void }) {
  if (channel.muted) {
    return (
      <button type="button" role="menuitem" onClick={() => onChange({ muted: false })}>
        <span className="exo-dc-menu-text">Unmute {what}{channel.mutedUntil ? <small>Muted until {new Date(channel.mutedUntil).toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' })}</small> : null}</span>
      </button>
    )
  }
  return (
    <Submenu label={`Mute ${what}`}>
      {MUTE_FOR.map(([minutes, label]) => <button key={minutes} type="button" role="menuitem" onClick={() => onChange(minutes ? { muted: true, minutes } : { muted: true })}>{label}</button>)}
    </Submenu>
  )
}

// A voice channel's status under its name, as Discord draws it: its words, server emoji as pictures.
function VoiceStatus({ text }: { text: string }) {
  const parts = text.split(/<(a?):(\w+):(\d+)>/)
  return (
    <span className="exo-dc-channel-status">
      {parts.map((part, i) => i % 4 === 3 ? <img key={i} src={emojiUrl(part, parts[i - 2] === 'a', 32)} alt={`:${parts[i - 1]}:`} width={14} height={14} draggable={false} />
        : i % 4 === 0 ? part : null)}
    </span>
  )
}

// Discord's Set Channel Status: what the call is about (500 characters), under the channel's name for everyone.
function VoiceStatusDialog({ channel, onClose }: { channel: DiscordChannel; onClose: () => void }) {
  const [text, setText] = useState(channel.status ?? '')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const save = () => {
    if (text === (channel.status ?? '')) { onClose(); return }
    setBusy(true)
    void host.discordServer('voiceStatus', { channelId: channel.id, text }).then(result => {
      setBusy(false)
      if (result.ok) onClose(); else setError(result.message ?? 'Discord did not set that status.')
    }).catch(() => { setBusy(false); setError('Discord did not set that status.') })
  }
  return (
    <Confirm title="What are we chatting about?" action="Set status" danger={false} busy={busy || text.length > 500} error={error} onClose={onClose} onConfirm={save}>
      <p>Let others know what you're up to in the voice channel!</p>
      <input className="exo-dc-modal-input" value={text} autoFocus maxLength={500} placeholder={`Status for ${channel.name}`} aria-label="Status"
        onChange={event => setText(event.target.value.replace(/\n/g, ' '))} onKeyDown={event => { if (event.key === 'Enter') save() }} />
    </Confirm>
  )
}

// Notification Settings: the level above (Use Category Default, or Use Server Default outside a category, with what
// that is), All Messages, Only @mentions or Nothing, as Discord's channel menu offers them.
function NotifyLevels({ channel, inCategory, onPick }: { channel: DiscordChannel; inCategory: boolean; onPick: (level: 0 | 1 | 2 | 3) => void }) {
  const level = channel.notify ?? null
  return (
    <Submenu label="Notification settings">
      <button type="button" role="menuitemradio" aria-checked={level === null} onClick={() => onPick(3)}>
        <span className="exo-dc-menu-text">{inCategory ? 'Use category default' : 'Use server default'}<small>{LEVELS.find(([value]) => value === (channel.notifyDefault ?? 0))?.[1]}</small></span>
        {level === null ? <Icons.Check size={14} className="exo-dc-ctx-check" /> : null}
      </button>
      {LEVELS.map(([value, label]) => (
        <button key={value} type="button" role="menuitemradio" aria-checked={level === value} onClick={() => onPick(value)}>
          <span className="exo-dc-ctx-name">{label}</span>
          {level === value ? <Icons.Check size={14} className="exo-dc-ctx-check" /> : null}
        </button>
      ))}
    </Submenu>
  )
}

// In opt-in mode Discord lists only the channels followed (or in a followed category), private ones (Browse Channels
// never offers them), the open one, and their categories.
// The channels the sidebar lists: in opt-in mode the followed ones; with Hide Muted Channels, none that are muted (the
// open one stays); a category with nothing left under it goes too, as in Discord.
function followed(list: DiscordChannels | null, open: string | null) {
  const all = list?.channels ?? []
  const channels = list?.hideMuted ? withoutEmpty(all.filter(c => c.kind === 'category' || !c.muted || c.id === open)) : all
  if (!list?.optIn) return channels
  return withoutEmpty(channels.filter(c => c.kind === 'category' || c.id === open || c.private || (c.optedIn && !(c.resource && list.rows?.guide))))
}

const withoutEmpty = (kept: DiscordChannel[]) => kept.filter((c, i) => c.kind !== 'category' || (kept[i + 1] && kept[i + 1].kind !== 'category'))
