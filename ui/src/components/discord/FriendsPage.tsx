import { useCallback, useEffect, useRef, useState } from 'react'
import { host, onHostEvent } from '../../lib/host'
import { report } from '../../lib/report'
import { useGlide } from '../../lib/glide'
import type { DiscordFriend } from '../../lib/discord'
import { Icons } from './icons'
import { Avatar } from './presence'
import { Nameplate, StyledName } from './collectibles'
import { showFullProfile } from './profileParts'
import { Confirm, StrangerConfirm } from './admin'
import { openToggle } from './openToggle'
import { usePageActive } from '../../lib/pageActivity'

type Tab = 'online' | 'all' | 'pending' | 'add'

/** Discord's Friends page: Online, All, Pending and Add Friend, each row with Message and its request buttons. */
export function FriendsPage({ onOpenDm }: { onOpenDm: (channelId: string) => void }) {
  const [tab, setTab] = useState<Tab>('online')
  const [friends, setFriends] = useState<DiscordFriend[]>([])
  const [error, setError] = useState<string | null>(null)
  const [query, setQuery] = useState('')
  const [menu, setMenu] = useState<string | null>(null)
  const [removing, setRemoving] = useState<DiscordFriend | null>(null)
  const [stranger, setStranger] = useState<DiscordFriend | null>(null)
  const tabs = useGlide<HTMLSpanElement>()

  const refresh = useCallback(() => {
    host.discordFriends().then(result => setFriends(result.friends)).catch((err: unknown) => report(err, 'FriendsPage.load'))
  }, [])

  useEffect(() => {
    refresh()
    const offChanged = onHostEvent('discord.changed', event => { if (!event.voice) refresh() })
    const offPresence = onHostEvent('discord.presence', event =>
      setFriends(list => list.map(f => f.id === event.userId ? { ...f, status: event.status, activity: event.activity, activityType: event.activityType, rich: event.rich } : f)))
    return () => { offChanged(); offPresence() }
  }, [refresh])

  async function act(action: 'accept' | 'remove' | 'dm', friend: DiscordFriend, confirm = false) {
    setError(null)
    if (action === 'dm' && friend.dmId) { onOpenDm(friend.dmId); return }
    try {
      const result = await host.discordFriend(action, { userId: friend.id, confirm })
      if (result.stranger) setStranger(friend)
      else if (!result.ok) setError(result.message ?? 'Discord did not take that.')
      else if (action === 'dm' && result.channelId) onOpenDm(result.channelId)
    } catch (err) {
      report(err, 'FriendsPage.' + action)
      setError('Discord did not take that.')
    }
  }

  const pending = friends.filter(f => f.kind !== 'friend')
  const q = query.trim().toLowerCase()
  const shown = (tab === 'online' ? friends.filter(f => f.kind === 'friend' && f.status !== 'offline')
    : tab === 'all' ? friends.filter(f => f.kind === 'friend')
    : pending).filter(f => !q || f.name.toLowerCase().includes(q) || (f.username ?? '').toLowerCase().includes(q))
  const active = friends.filter(f => f.kind === 'friend' && f.status !== 'offline' && f.rich)
  const heading = tab === 'online' ? 'Online' : tab === 'all' ? 'All friends' : 'Pending'
  return (
    <section className="exo-dc-chat exo-dc-friends" aria-label="Friends">
      <header className="exo-dc-chat-head">
        <Icons.Friends size={22} />
        <strong>Friends</strong>
        <span className="exo-dc-head-line" aria-hidden="true" />
        <span ref={tabs} className="exo-dc-tabs exo-glide" role="group" aria-label="Friends">
        {(['online', 'all', 'pending'] as const).map(t => (
          <button key={t} type="button" className="exo-dc-tab" aria-pressed={tab === t} onClick={() => setTab(t)}>
            {t === 'online' ? 'Online' : t === 'all' ? 'All' : 'Pending'}
            {t === 'pending' && pending.some(f => f.kind === 'incoming') ? <span className="exo-dc-badge">{pending.filter(f => f.kind === 'incoming').length}</span> : null}
          </button>
        ))}
        </span>
        <button type="button" className="exo-dc-tab is-add" aria-pressed={tab === 'add'} onClick={() => setTab('add')}>Add Friend</button>
        <button type="button" className="exo-dc-icon-btn exo-dc-new-dm" aria-label="New Group DM" onClick={() => window.dispatchEvent(new Event('exo-dc-new-dm'))}><Icons.NewDm size={20} /></button>
      </header>
      <div className="exo-dc-friends-body">
      {tab === 'add' ? <AddFriend /> : (
        <div className="exo-dc-friend-list">
          <label className="exo-dc-friend-search">
            <Icons.Search size={16} />
            <input placeholder="Search" value={query} onChange={event => setQuery(event.target.value)} aria-label="Search friends" />
          </label>
          <p className="exo-dc-side-label">{heading} — {shown.length}</p>
          {error ? <p className="exo-dc-note" role="alert">{error}</p> : null}
          {shown.map(friend => (
            <div key={friend.id} className={`exo-dc-friend${friend.look?.nameplate ? ' has-nameplate' : ''}`} data-dc-user={friend.id} data-dc-name={friend.name} data-dc-username={friend.username ?? undefined}>
              <Nameplate look={friend.look} scope=".exo-dc-friend" />
              <button type="button" className="exo-dc-avatar-btn" aria-label={`${friend.name}, profile`} onClick={() => showFullProfile(friend.id)}>
                <Avatar src={friend.avatar} name={friend.name} status={friend.kind === 'friend' ? friend.status : null} size={36} look={friend.look} scope=".exo-dc-friend" />
              </button>
              <span className="exo-dc-friend-names">
                <strong><StyledName name={friend.name} look={friend.look} scope=".exo-dc-friend" /></strong>
                <span>{friend.kind === 'incoming' ? 'Incoming friend request' : friend.kind === 'outgoing' ? 'Outgoing friend request' : <ActivityLine friend={friend} />}</span>
              </span>
              {friend.kind === 'friend' ? (
                <>
                  <button type="button" className="exo-dc-round-btn" aria-label={`Message ${friend.name}`} onClick={() => void act('dm', friend)}><Icons.Chat size={18} /></button>
                  <span className="exo-dc-friend-more">
                    <button type="button" className="exo-dc-round-btn" aria-label="More" aria-expanded={menu === friend.id} onClick={() => setMenu(m => m === friend.id ? null : friend.id)}><Icons.More size={18} /></button>
                    {menu === friend.id ? <FriendMenu onClose={() => setMenu(null)} onRemove={() => { setMenu(null); setRemoving(friend) }} /> : null}
                  </span>
                </>
              ) : null}
              {friend.kind === 'incoming' ? (
                <button type="button" className="exo-dc-round-btn is-good" aria-label={`Accept ${friend.name}`} onClick={() => void act('accept', friend)}>✓</button>
              ) : null}
              {friend.kind !== 'friend' ? (
                <button type="button" className="exo-dc-round-btn is-bad" aria-label={friend.kind === 'incoming' ? `Ignore ${friend.name}` : `Cancel request to ${friend.name}`}
                  onClick={() => void act('remove', friend)}>×</button>
              ) : null}
            </div>
          ))}
          {shown.length === 0 ? <p className="exo-dc-note">{tab === 'pending' ? 'No pending friend requests.' : tab === 'online' ? 'No one is around right now.' : 'No friends yet.'}</p> : null}
        </div>
      )}
      <ActiveNow friends={active} onOpen={friend => void act('dm', friend)} />
      </div>
      {stranger ? (
        <StrangerConfirm name={stranger.name} onClose={() => setStranger(null)} onConfirm={() => { const friend = stranger; setStranger(null); void act('accept', friend, true) }} />
      ) : null}
      {removing ? (
        <Confirm title={`Remove '${removing.name}'`} action="Remove Friend" onClose={() => setRemoving(null)} onConfirm={() => { const friend = removing; setRemoving(null); void act('remove', friend) }}>
          <p>Are you sure you want to remove <strong>{removing.name}</strong> from your friends?</p>
        </Confirm>
      ) : null}
    </section>
  )
}

// A friend's More menu: closes on a press outside it or Escape.
function FriendMenu({ onClose, onRemove }: { onClose: () => void; onRemove: () => void }) {
  const box = useRef<HTMLDivElement>(null)
  useEffect(() => {
    const away = (event: PointerEvent) => { if (!box.current?.contains(event.target as Node) && !openToggle(event.target)) onClose() }
    const escape = (event: KeyboardEvent) => { if (event.key === 'Escape') onClose() }
    document.addEventListener('pointerdown', away, true)
    document.addEventListener('keydown', escape)
    return () => { document.removeEventListener('pointerdown', away, true); document.removeEventListener('keydown', escape) }
  }, [onClose])
  return (
    <div className="exo-dc-menu is-message" role="menu" ref={box}>
      <button type="button" role="menuitem" className="is-danger" onClick={onRemove}>Remove Friend</button>
    </div>
  )
}

function statusWord(status: string) {
  return status === 'idle' ? 'Idle' : status === 'dnd' ? 'Do Not Disturb' : status === 'offline' ? 'Offline' : 'Online'
}

function AddFriend() {
  const [name, setName] = useState('')
  const [result, setResult] = useState<{ ok: boolean; text: string } | null>(null)
  const [busy, setBusy] = useState(false)
  async function send() {
    if (!name.trim() || busy) return
    setBusy(true)
    try {
      const reply = await host.discordFriend('add', { username: name.trim() })
      setResult(reply.ok ? { ok: true, text: `Friend request sent to ${name.trim()}.` } : { ok: false, text: reply.message ?? 'Discord did not take that.' })
      if (reply.ok) setName('')
    } catch (err) {
      report(err, 'FriendsPage.add')
      setResult({ ok: false, text: 'Discord did not take that.' })
    } finally { setBusy(false) }
  }
  return (
    <div className="exo-dc-add-friend">
      <strong>Add Friend</strong>
      <p>You can add friends with their Discord username.</p>
      <div className="exo-dc-add-row">
        <input value={name} onChange={event => { setName(event.target.value); setResult(null) }} placeholder="Their username" aria-label="Username"
          onKeyDown={event => { if (event.key === 'Enter') void send() }} />
        <button type="button" className="button button--primary" disabled={!name.trim() || busy} onClick={() => void send()}>Send Friend Request</button>
      </div>
      {result ? <p className={result.ok ? 'is-good' : 'is-bad'} role={result.ok ? 'status' : 'alert'}>{result.text}</p> : null}
    </div>
  )
}

// The line under a friend: their custom status, else the activity with its icon, else their status.
function ActivityLine({ friend }: { friend: DiscordFriend }) {
  if (!friend.activity || friend.status === 'offline') return <>{statusWord(friend.status)}</>
  const Icon = KIND_ICONS[friend.activityType ?? -1]
  return <span className="exo-dc-activity">{Icon ? <Icon size={14} className="exo-dc-activity-icon" /> : null}{friend.activity}</span>
}

const KIND_ICONS: Record<number, typeof Icons.Playing | undefined> = { 0: Icons.Playing, 1: Icons.Streaming, 2: Icons.Listening, 3: Icons.Watching, 5: Icons.Competing }

// "35m", "1h", as Discord heads an Active Now card; "35:49 elapsed" inside it.
function since(start: number, now: number) {
  const minutes = Math.max(0, Math.floor((now - start) / 60_000))
  return minutes < 60 ? `${minutes}m` : `${Math.floor(minutes / 60)}h`
}

function elapsed(start: number, now: number) {
  const s = Math.max(0, Math.floor((now - start) / 1000))
  const pad = (n: number) => String(n).padStart(2, '0')
  return (s >= 3600 ? `${pad(Math.floor(s / 3600))}:` : '') + `${pad(Math.floor(s / 60) % 60)}:${pad(s % 60)} elapsed`
}

/** Discord's Active Now column: what friends are doing, each as a card with the game or song. */
function ActiveNow({ friends, onOpen }: { friends: DiscordFriend[]; onOpen: (friend: DiscordFriend) => void }) {
  const [now, setNow] = useState(() => Date.now())
  // The elapsed clocks tick each second while one shows and Exo is active.
  const active = usePageActive()
  const clocks = friends.some(f => f.rich?.start)
  useEffect(() => {
    if (!active || !clocks) return
    setNow(Date.now())
    const timer = window.setInterval(() => setNow(Date.now()), 1000)
    return () => window.clearInterval(timer)
  }, [active, clocks])
  return (
    <aside className="exo-dc-active" aria-label="Active now">
      <strong>Active Now</strong>
      {friends.length === 0 ? (
        <div className="exo-dc-active-empty">
          <strong>It's quiet for now...</strong>
          <span>When a friend starts an activity, like playing a game or listening to music, it shows here.</span>
        </div>
      ) : friends.map(friend => <ActiveCard key={friend.id} friend={friend} now={now} onOpen={onOpen} />)}
    </aside>
  )
}

function ActiveCard({ friend, now, onOpen }: { friend: DiscordFriend; now: number; onOpen: (friend: DiscordFriend) => void }) {
  const rich = friend.rich!
  const spotify = rich.type === 2 && rich.name === 'Spotify'
  const head = spotify ? 'Listening to Spotify' : [rich.name, rich.start ? since(rich.start, now) : null].filter(Boolean).join(' – ')
  const title = spotify ? rich.details : rich.details ?? rich.name
  const sub = spotify ? rich.state : rich.details ? rich.state : null
  const Icon = KIND_ICONS[rich.type]
  return (
    <button type="button" className="exo-dc-active-card" onClick={() => onOpen(friend)}>
      <span className="exo-dc-active-who">
        <Avatar src={friend.avatar} name={friend.name} status={friend.status} size={32} look={friend.look} scope=".exo-dc-active-card" />
        <span className="exo-dc-friend-names"><strong>{friend.name}</strong><span>{head}</span></span>
        {Icon ? <Icon size={18} className="exo-dc-active-kind" /> : null}
      </span>
      <span className="exo-dc-active-what">
        {rich.image ? <img src={rich.image} alt="" draggable={false} /> : Icon ? <span className="exo-dc-active-art"><Icon size={22} /></span> : null}
        <span className="exo-dc-friend-names">
          <strong>{title}</strong>
          {sub ? <span>{sub}</span> : null}
          {!spotify && rich.start ? <span>{elapsed(rich.start, now)}</span> : null}
        </span>
      </span>
    </button>
  )
}
