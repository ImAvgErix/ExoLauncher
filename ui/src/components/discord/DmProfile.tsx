import { useEffect, useRef, useState } from 'react'
import { ChevronRight } from '../../brand/icons'
import { host } from '../../lib/host'
import { requestDiscordSettings, type DiscordUser } from '../../lib/discord'
import { StrangerConfirm, useLayer } from './admin'
import { Icons } from './icons'
import { openUserMenu } from './UserMenu'
import {
  AboutMe, ActivityCard, Connections, MemberSince, MutualFriends, MutualServers, NoteField, ProfileBanner, ProfileEffectOf, ProfileFace, profileName, profileTheme, ProfileWho,
  profileOf, RoleList, showFullProfile, useMutualFriends, useProfile, type FullProfileTarget, type Loaded, type ProfileTab,
} from './profileParts'
import { ModalBack } from './SettingsLayer'
import { Skeleton } from '../ui/Skeleton'

/**
 * Discord's profile panel beside a DM: banner, the person, badges, what they're doing, About Me, Member Since,
 * Connections, then Mutual Servers and Mutual Friends that open in place (a server opens it, a friend their
 * profile), and View Full Profile. Until (or when) Discord doesn't send it, the DM's own name and picture.
 */
export function DmProfile({ userId, status, fallback, hidden }: { userId: string; status: string | null; fallback: DiscordUser; hidden: boolean }) {
  const { data } = useProfile(userId, null, fallback)
  const [open, setOpen] = useState<'friends' | 'servers' | null>(null)
  const friends = useMutualFriends(userId, open === 'friends')
  if (!data) {
    return (
      <aside className="exo-dc-dm-profile exo-dc-pf" aria-label="User profile" hidden={hidden}>
        <div className="exo-dc-pf-banner" />
        <div className="exo-dc-pf-head"><ProfileFace data={{ profile: profileOf(fallback), extras: null, note: null }} guildId={null} status={status} size={80} /></div>
        <div className="exo-dc-pf-body"><ProfileWho data={{ profile: profileOf(fallback), extras: null, note: null }} guildId={null} /></div>
      </aside>
    )
  }
  const user = data.profile.user
  const full = () => showFullProfile(user.id, fallback)
  const toggle = (which: 'friends' | 'servers') => setOpen(current => current === which ? null : which)
  const servers = data.profile.mutual_guilds?.length ?? 0
  const mutualFriends = data.profile.mutual_friends_count ?? 0
  return (
    <aside className="exo-dc-dm-profile exo-dc-pf" aria-label="User profile" hidden={hidden} style={profileTheme(data, null)}>
      <ProfileBanner data={data} guildId={null} />
      <div className="exo-dc-pf-head"><ProfileFace data={data} guildId={null} status={status ?? data.extras?.presence?.status ?? null} size={80} onClick={full} /></div>
      <div className="exo-dc-pf-body">
        <ProfileWho data={data} guildId={null} onName={full} />
        <ActivityCard data={data} />
        <AboutMe data={data} guildId={null} />
        <MemberSince data={data} guildId={null} />
        <Connections data={data} />
      </div>
      {servers || mutualFriends ? (
        <div className="exo-dc-pf-mutuals">
          {servers ? (
            <MutualToggle label={`Mutual servers — ${servers}`} open={open === 'servers'} onToggle={() => toggle('servers')}>
              <MutualServers data={data} />
            </MutualToggle>
          ) : null}
          {mutualFriends ? (
            <MutualToggle label={`Mutual friends — ${mutualFriends}`} open={open === 'friends'} onToggle={() => toggle('friends')}>
              <MutualFriends friends={friends} onPerson={friend => showFullProfile(friend.id, friend)} />
            </MutualToggle>
          ) : null}
        </div>
      ) : null}
      <button type="button" className="exo-dc-pf-view" onClick={full}>View full profile</button>
      <ProfileEffectOf data={data} guildId={null} />
    </aside>
  )
}

function MutualToggle({ label, open, onToggle, children }: { label: string; open: boolean; onToggle: () => void; children: React.ReactNode }) {
  return (
    <>
      <button type="button" className="exo-dc-pf-toggle" aria-expanded={open} onClick={onToggle}>
        {label}<ChevronRight size={16} className={open ? 'is-open' : undefined} />
      </button>
      {open ? children : null}
    </>
  )
}

/** Discord's full profile over the room; another person's (a mutual friend) replaces it. */
export function FullProfileHost({ meId, onMessage }: { meId: string | null; onMessage: (userId: string) => void }) {
  const [target, setTarget] = useState<FullProfileTarget | null>(null)
  useEffect(() => {
    const show = (event: Event) => setTarget((event as CustomEvent<FullProfileTarget>).detail)
    window.addEventListener('exo-dc-full-profile', show)
    return () => window.removeEventListener('exo-dc-full-profile', show)
  }, [])
  if (!target) return null
  const close = () => setTarget(null)
  return <FullProfile key={`${target.userId}:${target.guildId ?? ''}:${target.tab ?? ''}`} target={target} meId={meId} onClose={close} onMessage={id => { close(); onMessage(id) }} />
}

/**
 * The person with Message and More (the person menu, a moderator's actions in it), their names and badges, then
 * About Me (with Member Since, their roles on the server it opened from, Connections and your note), Activity
 * while they're doing something, Mutual Friends and Mutual Servers.
 */
function FullProfile({ target, meId, onClose, onMessage }: {
  target: FullProfileTarget
  meId: string | null
  onClose: () => void
  onMessage: (userId: string) => void
}) {
  const guildId = target.guildId ?? null
  const { data, failed } = useProfile(target.userId, guildId, target.user)
  const [tab, setTab] = useState<ProfileTab>(target.tab ?? 'about')
  const friends = useMutualFriends(target.userId, target.userId !== meId)
  const box = useRef<HTMLDivElement>(null)
  useLayer(box, onClose)
  const me = target.userId === meId
  const name = data ? profileName(data) : 'Profile'
  const tabs = data ? tabsOf(data, friends, me) : []
  return (
    <ModalBack onClose={onClose}>
      <div ref={box} className="exo-dc-modal exo-dc-pf is-full" role="dialog" aria-modal="true" aria-label={name} tabIndex={-1} onClick={event => event.stopPropagation()}
        style={data ? profileTheme(data, guildId) : undefined}>
        {data ? (
          <>
            <ProfileBanner data={data} guildId={guildId} tall />
            <div className="exo-dc-pf-head">
              <ProfileFace data={data} guildId={guildId} status={data.extras?.presence?.status ?? null} size={120} />
              <span className="exo-dc-pf-actions">
                {me ? <button type="button" className="exo-set-btn is-primary" onClick={() => { onClose(); requestDiscordSettings('profiles') }}>Edit profile</button> : null}
                {me ? null : <button type="button" className="exo-set-btn is-primary" onClick={() => onMessage(data.profile.user.id)}>Message</button>}
                {me || data.profile.user.bot ? null : <FriendAction data={data} name={name} />}
                <button type="button" className="exo-dc-pf-more is-inline" aria-label="More"
                  onClick={event => { const b = event.currentTarget.getBoundingClientRect(); openUserMenu(data.profile.user.id, b.left, b.bottom + 4, name, data.profile.user.username) }}>
                  <Icons.More size={18} />
                </button>
              </span>
            </div>
            <div className="exo-dc-pf-body"><ProfileWho data={data} guildId={guildId} /></div>
            <div className="exo-dc-full-tabs" role="tablist">
              {tabs.map(([id, label]) => <button key={id} type="button" role="tab" aria-selected={tab === id} onClick={() => setTab(id)}>{label}</button>)}
            </div>
            <div className="exo-dc-pf-tab" role="tabpanel">
              <FullTab tab={tab} data={data} guildId={guildId} friends={friends} me={me} onClose={onClose} />
            </div>
            <ProfileEffectOf data={data} guildId={guildId} />
          </>
        ) : failed ? <p className="exo-dc-note exo-dc-pf-wait" role="alert">Discord didn't send this profile. Try again.</p> : <Skeleton label="Loading the profile" />}
      </div>
    </ModalBack>
  )
}

/**
 * The full profile's friend button, as Discord's: Add Friend, Pending while your request waits, Accept and Ignore for
 * theirs. A friend has none: Remove Friend is in the profile's ... menu. Discord's reason shows when it refuses.
 */
function FriendAction({ data, name }: { data: Loaded; name: string }) {
  const [relation, setRelation] = useState(data.extras?.relationship ?? 0)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [stranger, setStranger] = useState(false)
  const user = data.profile.user
  const act = (action: 'add' | 'accept' | 'remove', next: number, confirm = false) => {
    if (busy) return
    setBusy(true); setError(null); setStranger(false)
    const why = "Discord didn't take that."
    void host.discordFriend(action, { userId: user.id, username: user.username, confirm })
      .then(result => { if (result.ok) setRelation(next); else if (result.stranger) setStranger(true); else setError(result.message ?? why) }, () => setError(why))
      .finally(() => setBusy(false))
  }
  const raised = 'exo-dc-tab is-raised'
  return (
    <>
      {relation === 0 ? <button type="button" className={raised} disabled={busy} onClick={() => act('add', 4)}><Icons.AddFriend size={16} />Add friend</button> : null}
      {relation === 4 ? <button type="button" className={raised} disabled>Pending</button> : null}
      {relation === 3 ? (
        <>
          <button type="button" className={raised} disabled={busy} onClick={() => act('accept', 1)}>Accept</button>
          <button type="button" className={raised} disabled={busy} onClick={() => act('remove', 0)}>Ignore</button>
        </>
      ) : null}
      {error ? <span className="exo-dc-note is-bad" role="alert">{error}</span> : null}
      {stranger ? <StrangerConfirm name={name} busy={busy} onClose={() => setStranger(false)} onConfirm={() => act('accept', 1, true)} /> : null}
    </>
  )
}

// About Me, Activity while they're doing something, and (not on your own) Mutual Friends and Mutual Servers with their counts.
function tabsOf(data: Loaded, friends: DiscordUser[] | null, me: boolean) {
  const count = (n: number | undefined) => n ? ` (${n})` : ''
  const tabs: [ProfileTab, string][] = [['about', 'About me']]
  if (data.extras?.presence?.rich) tabs.push(['activity', 'Activity'])
  if (!me) tabs.push(['friends', `Mutual friends${count(friends?.length)}`], ['servers', `Mutual servers${count(data.profile.mutual_guilds?.length)}`])
  return tabs
}

function FullTab({ tab, data, guildId, friends, me, onClose }: {
  tab: ProfileTab
  data: Loaded
  guildId: string | null
  friends: DiscordUser[] | null
  me: boolean
  onClose: () => void
}) {
  if (tab === 'friends') return <MutualFriends friends={friends} onPerson={friend => showFullProfile(friend.id, friend, guildId)} />
  if (tab === 'servers') return <MutualServers data={data} onOpen={onClose} />
  if (tab === 'activity') return <div className="exo-dc-pf-body"><ActivityCard data={data} /></div>
  return (
    <div className="exo-dc-pf-body">
      <AboutMe data={data} guildId={guildId} />
      <MemberSince data={data} guildId={guildId} />
      <RoleList data={data} guildId={guildId} />
      <Connections data={data} />
      {me ? null : <NoteField userId={data.profile.user.id} note={data.note} />}
    </div>
  )
}
