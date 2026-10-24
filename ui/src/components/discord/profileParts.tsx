import { createContext, useContext, useEffect, useState } from 'react'
import { CompanyLogo } from '../../brand/CompanyLogo'
import { Close } from '../../brand/icons'
import { host, onHostEvent } from '../../lib/host'
import { report } from '../../lib/report'
import { avatarUrl, cdnSize, cdnStep, performanceMode, snowflakeTime, type DiscordBadge, type DiscordConnection, type DiscordGuild, type DiscordProfile, type DiscordProfileExtras, type DiscordRich, type DiscordUser } from '../../lib/discord'
import { admin } from './admin'
import { CONNECTION_MARKS } from './connectionMarks'
import { Icons } from './icons'
import { Markdown, open } from './markdown'
import { Avatar } from './presence'
import { pressedArt, transition, visible } from '../../lib/viewTransition'
import { Decoration, effectSkuOf, lookOf, ProfileEffect, StyledName, themeStyle } from './collectibles'
import { GuildTag } from './guildTag'

/** A profile as every surface shows it: Discord's reply, what Exo adds (roles, presence), and your note. */
export type Loaded = { profile: DiscordProfile; extras: DiscordProfileExtras | null; note: string | null }

/** Your servers (for Member Since and Mutual Servers) and opening one, for every profile surface in the room. */
export const ProfilePlaces = createContext<{ guilds: DiscordGuild[]; openServer: (guildId: string) => void }>({ guilds: [], openServer: () => { } })

export type ProfileTab = 'about' | 'activity' | 'friends' | 'servers'
export type FullProfileTarget = { userId: string; user?: DiscordUser; guildId?: string | null; tab?: ProfileTab }

/** Opens someone's full profile (on a server: their server profile and roles there), at a tab; the person as the message or list shows them stands in if Discord doesn't send it. */
export function showFullProfile(userId: string, user?: DiscordUser, guildId: string | null = null, tab?: ProfileTab) {
  // The picture pressed (the popout's, a friend's) flies into the full profile's as it fades in.
  void transition(() => window.dispatchEvent(new CustomEvent<FullProfileTarget>('exo-dc-full-profile', { detail: { userId, user, guildId, tab } })),
    { morph: { name: 'exo-dc-face', from: pressedArt('.exo-dc-avatar, .exo-dc-avatar-wrap, .exo-dc-pf-face'), to: () => visible('.exo-dc-modal .exo-dc-pf-face') } })
}

/** A profile with only what a message or list knows of someone: their names, picture and collectibles. */
export function profileOf(user: DiscordUser): DiscordProfile {
  return {
    user: {
      id: user.id, username: user.username, global_name: user.global_name, avatar: user.avatar,
      avatar_decoration_data: user.avatar_decoration_data, collectibles: user.collectibles, display_name_styles: user.display_name_styles,
    },
  }
}

// The collectibles a profile shows: on a server, the member's own first.
const profileLook = ({ profile }: Loaded, guildId: string | null) => lookOf(profile.user, guildId ? profile.guild_member : null)

/** A profile card's theme colors (the server profile's first), as a style for its panel. */
export function profileTheme({ profile }: Loaded, guildId: string | null) {
  return themeStyle((guildId ? profile.guild_member_profile?.theme_colors : null) ?? profile.user_profile?.theme_colors)
}

/** The profile effect a profile wears (the server profile's first), over the card. */
export function ProfileEffectOf({ data, guildId, scope }: { data: Loaded; guildId: string | null; scope?: string }) {
  const { profile } = data
  return <ProfileEffect skuId={effectSkuOf([guildId ? profile.guild_member_profile?.collectibles : null, profile.user_profile?.collectibles])} scope={scope} />
}

/**
 * A profile as its card shows it, for previews (the Shop, Profiles, Account): banner, picture with its decoration,
 * names, About Me and Member Since, with its profile effect and colors. avatar and banner stand in for its own
 * pictures (a picture picked but not saved yet); compact keeps the banner, picture and names.
 */
export function ProfileCard({ data, avatar, banner, compact = false, scope }: { data: Loaded; avatar?: string | null; banner?: string | null; compact?: boolean; scope?: string }) {
  return (
    <div className={`exo-dc-pf exo-dc-pf-card${compact ? ' is-compact' : ''}`} style={profileTheme(data, null)}>
      <ProfileBanner data={data} guildId={null} src={banner} />
      <div className="exo-dc-pf-head"><ProfileFace data={data} guildId={null} status={null} size={compact ? 64 : 80} src={avatar} scope={scope} /></div>
      <div className="exo-dc-pf-body">
        <ProfileWho data={data} guildId={null} />
        {compact ? null : <><AboutMe data={data} guildId={null} /><MemberSince data={data} guildId={null} /></>}
      </div>
      <ProfileEffectOf data={data} guildId={null} scope={scope} />
    </div>
  )
}

/** Profiles already read this session: opened again, one draws at once and is read again behind it. */
const loadedProfiles = new Map<string, Loaded>()

/**
 * Someone's profile on a server (or anywhere, with no server). Nothing waits on the network to draw: until Discord
 * answers it shows what was read before, or what the message or list knew of them (fallback); failed is true only
 * when there is nothing to show.
 */
// Reads in flight, so a hover's read-ahead and the card that opens share one.
const loadingProfiles = new Map<string, Promise<Loaded | null>>()

/** Someone's profile, read once and kept: a pointer resting on them reads it ahead, so their card opens whole. */
export function loadProfile(userId: string, guildId: string | null): Promise<Loaded | null> {
  const at = `${userId}:${guildId ?? ''}`
  const going = loadingProfiles.get(at)
  if (going) return going
  const read = host.discordProfile(userId, guildId).then(result => {
    const loaded = result.profile?.user ? { profile: result.profile, extras: result.extras ?? null, note: result.note ?? null } : null
    if (loaded) {
      if (loadedProfiles.size >= 200) loadedProfiles.delete(loadedProfiles.keys().next().value!)
      loadedProfiles.set(at, loaded)
    }
    return loaded
  }).finally(() => loadingProfiles.delete(at))
  loadingProfiles.set(at, read)
  return read
}

/** Whether someone's profile has been read already. */
export const profileLoaded = (userId: string, guildId: string | null) => loadedProfiles.has(`${userId}:${guildId ?? ''}`)

export function useProfile(userId: string, guildId: string | null, fallback?: DiscordUser) {
  const key = `${userId}:${guildId ?? ''}`
  const [got, setGot] = useState<{ key: string; data: Loaded | null } | null>(null)
  useEffect(() => {
    let live = true
    const at = `${userId}:${guildId ?? ''}`
    loadProfile(userId, guildId).then(loaded => { if (live) setGot({ key: at, data: loaded }) })
      .catch((error: unknown) => { report(error, 'DiscordProfile.load'); if (live) setGot({ key: at, data: null }) })
    return () => { live = false }
  }, [userId, guildId])
  const current = got?.key === key ? got : null
  const data = current?.data ?? loadedProfiles.get(key) ?? (fallback ? { profile: profileOf(fallback), extras: null, note: null } : null)
  return { data, failed: !!current && !data }
}

/** A profile's Mutual Friends, asked for once it's wanted. */
export function useMutualFriends(userId: string, wanted: boolean) {
  const [got, setGot] = useState<{ userId: string; friends: DiscordUser[] } | null>(null)
  useEffect(() => {
    if (!wanted) return
    let live = true
    host.discordMutualFriends(userId).then(result => { if (live) setGot({ userId, friends: result.friends ?? [] }) })
      .catch(() => { if (live) setGot({ userId, friends: [] }) })
    return () => { live = false }
  }, [userId, wanted])
  return got?.userId === userId ? got.friends : null
}

/** The name Discord shows: their server nickname, else display name, else username. */
export function profileName({ profile }: Loaded) {
  return profile.guild_member?.nick || profile.user.global_name || profile.user.username
}

const animated = (hash: string) => hash.startsWith('a_') && !performanceMode()

// A picture on Discord's CDN at the size it shows (times the screen's scale), lossless, moving when it moves.
function cdn(path: string, hash: string, size: number) {
  return `https://cdn.discordapp.com/${path}/${hash}.webp?size=${cdnSize(size)}&quality=lossless${animated(hash) ? '&animated=true' : ''}`
}

// A banner at the width it shows (times the screen's scale), as Discord's client asks for one: the nearest size
// its CDN has, moving when it moves; not lossless, which made a 300 px banner a multi-megabyte animation.
function bannerUrl(path: string, hash: string, width: number) {
  return `https://cdn.discordapp.com/${path}/${hash}.webp?size=${cdnStep(width)}${animated(hash) ? '&animated=true' : ''}`
}

/** The banner picture (the server profile's first), else a strip of their banner color, else the fill. */
export function ProfileBanner({ data, guildId, tall, src }: { data: Loaded; guildId: string | null; tall?: boolean; src?: string | null }) {
  const { user, guild_member: member, user_profile: about } = data.profile
  const width = tall ? 600 : 340
  const banner = src !== undefined ? src : guildId && member?.banner ? bannerUrl(`guilds/${guildId}/users/${user.id}/banners`, member.banner, width)
    : user.banner ? bannerUrl(`banners/${user.id}`, user.banner, width) : null
  const accent = about?.accent_color ?? user.accent_color
  const style = banner ? { backgroundImage: `url("${banner}")` } : accent ? { backgroundColor: `#${accent.toString(16).padStart(6, '0')}` } : undefined
  return <div className={`exo-dc-pf-banner${banner ? ' has-picture' : ''}${tall ? ' is-tall' : ''}`} style={style} />
}

/** Their picture (the server's first) with their avatar decoration and status, as Discord frames it. */
export function ProfileFace({ data, guildId, status, size, onClick, src: picked, scope }: {
  data: Loaded; guildId: string | null; status: string | null; size: number; onClick?: () => void; src?: string | null; scope?: string
}) {
  const { user, guild_member: member } = data.profile
  const src = picked !== undefined ? picked : guildId && member?.avatar ? cdn(`guilds/${guildId}/users/${user.id}/avatars`, member.avatar, size)
    : avatarUrl({ id: user.id, username: user.username, avatar: user.avatar }, size)
  const face = (
    <>
      <Avatar src={src} name={user.username} status={status} size={size} />
      <Decoration look={profileLook(data, guildId)} size={size} scope={scope} />
    </>
  )
  return onClick
    ? <button type="button" className="exo-dc-pf-face" aria-label={`${profileName(data)}'s full profile`} onClick={onClick}>{face}</button>
    : <span className="exo-dc-pf-face">{face}</span>
}

/** Their name (it opens the full profile where asked), username, pronouns, custom status and badges. */
export function ProfileWho({ data, guildId, onName }: { data: Loaded; guildId: string | null; onName?: () => void }) {
  const { profile, extras } = data
  const pronouns = profile.guild_member_profile?.pronouns || profile.user_profile?.pronouns
  const custom = extras?.presence?.activityType === 4 ? extras.presence.activity : null
  const look = profileLook(data, guildId)
  const name = <StyledName name={profileName(data)} look={look} loop />
  return (
    <div className="exo-dc-pf-who">
      <span className="exo-dc-pf-name-row">
        {onName ? <button type="button" className="exo-dc-pf-name" onClick={onName}>{name}</button> : <strong className="exo-dc-pf-name">{name}</strong>}
        <GuildTag look={look} />
      </span>
      <span className="exo-dc-pf-user">
        {profile.user.username}{profile.user.bot ? <span className="exo-dc-tag">App</span> : null}{pronouns ? <> · {pronouns}</> : null}
      </span>
      {custom ? <span className="exo-dc-pf-custom">{custom}</span> : null}
      <Badges badges={[...(profile.badges ?? []), ...(profile.guild_badges ?? [])]} />
    </div>
  )
}

/**
 * Discord's badges at the icon's full size (the CDN's 72px PNG, drawn at 22px), each saying what it is on hover or
 * focus in Exo's tip; one with a page (Nitro, boosting, Quests) opens it.
 */
function Badges({ badges }: { badges: DiscordBadge[] }) {
  const [tip, setTip] = useState<{ label: string; left: number; top: number } | null>(null)
  if (!badges.length) return null
  const show = (badge: DiscordBadge, el: HTMLElement) => {
    const box = el.getBoundingClientRect()
    setTip({ label: badge.description, left: box.left + box.width / 2, top: box.top - 8 })
  }
  return (
    <span className="exo-dc-pf-badges" onPointerLeave={() => setTip(null)}>
      {badges.map(badge => {
        const icon = <img src={`https://cdn.discordapp.com/badge-icons/${badge.icon}.png`} alt="" width={22} height={22} draggable={false} />
        const events = { onPointerEnter: (e: React.PointerEvent<HTMLElement>) => show(badge, e.currentTarget), onFocus: (e: React.FocusEvent<HTMLElement>) => show(badge, e.currentTarget), onBlur: () => setTip(null) }
        return badge.link
          ? <button key={badge.id} type="button" className="exo-dc-pf-badge" aria-label={badge.description} onClick={() => open(badge.link!)} {...events}>{icon}</button>
          : <span key={badge.id} className="exo-dc-pf-badge" role="img" aria-label={badge.description} tabIndex={0} {...events}>{icon}</span>
      })}
      {tip ? <span className="exo-dc-tip is-above" role="tooltip" style={{ left: tip.left, top: tip.top }}>{tip.label}</span> : null}
    </span>
  )
}

/** One part of a profile: its quiet heading, then what it holds. */
export function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return <section className="exo-dc-pf-section"><h3>{title}</h3>{children}</section>
}

/** About Me (the server profile's, else their own), in Discord's markdown. */
export function AboutMe({ data, guildId }: { data: Loaded; guildId: string | null }) {
  const { profile } = data
  const bio = (guildId && (profile.guild_member_profile?.bio || profile.guild_member?.bio)) || profile.user_profile?.bio || profile.user.bio
  if (!bio) return null
  return <Section title="About me"><div className="exo-dc-pf-bio"><Markdown text={bio} ctx={{ users: new Map(), channelName: () => null, meId: null, guildId, colors: new Map() }} /></div></Section>
}

const day = (ms: number) => new Date(ms).toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' })

/** Member Since: on Discord (the account's age), and on this server when they're in it. */
export function MemberSince({ data, guildId }: { data: Loaded; guildId: string | null }) {
  const guild = useContext(ProfilePlaces).guilds.find(g => g.id === guildId)
  const joined = data.profile.guild_member?.joined_at
  return (
    <Section title="Member since">
      <span className="exo-dc-pf-since">
        <span><CompanyLogo company="discord" size={16} />{day(snowflakeTime(data.profile.user.id))}</span>
        {guild && joined ? (
          <span>
            {guild.icon ? <img src={guild.icon} alt="" draggable={false} /> : <span className="exo-dc-pf-initial">{[...guild.name][0]}</span>}
            {day(Date.parse(joined))}
          </span>
        ) : null}
      </span>
    </Section>
  )
}

// What the person menu knows of them on a server: their roles, the server's roles (which the user may give), and whether they may.
type RolePerson = { ok: boolean; known: boolean; roles: string[]; choices: { id: string; name: string; color: string | null; assignable: boolean }[]; can: { roles: boolean } }

const dot = (color: string | null) => <span className="exo-dc-role-dot" style={{ background: color ?? 'var(--dc-muted)' }} aria-hidden="true" />

/**
 * Their roles on this server, highest first, each in its color. Where the user may manage roles and outranks them
 * (the host checks both again), as in Discord's popout: each role they may take shows a remove mark on hover, and
 * Add Role lists the roles they may give, with a search.
 */
export function RoleList({ data, guildId }: { data: Loaded; guildId: string | null }) {
  const userId = data.profile.user.id
  const [person, setPerson] = useState<RolePerson | null>(null)
  const [picking, setPicking] = useState(false)
  const [query, setQuery] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  useEffect(() => {
    if (!guildId) return
    let live = true
    const load = () => void admin<RolePerson>('person', { guildId, userId }).then(result => { if (live && result.data?.ok && result.data.known) setPerson(result.data) })
    load()
    // A member Exo didn't know yet arrives from the gateway.
    const off = onHostEvent('discord.colors', event => { if (event.guildId === guildId) load() })
    return () => { live = false; off() }
  }, [guildId, userId])
  const editable = !!guildId && !!person?.can.roles
  // As changed here once Exo knows them, else as the profile came.
  const roles = person ? person.choices.filter(role => person.roles.includes(role.id)) : data.extras?.roles ?? []
  if (!roles.length && !editable) return null
  const change = (roleId: string, add: boolean) => {
    if (busy) return
    setBusy(true)
    setError(null)
    void admin(add ? 'addRole' : 'removeRole', { guildId, userId, roleId }).then(result => {
      setBusy(false)
      if (!result.ok) { setError(result.message ?? "Discord didn't change that role."); return }
      setPerson(p => p && { ...p, roles: add ? [...p.roles, roleId] : p.roles.filter(id => id !== roleId) })
      if (add) { setPicking(false); setQuery('') }
      window.dispatchEvent(new Event('exo-dc-members-changed'))
    })
  }
  const q = query.trim().toLowerCase()
  const options = person?.choices.filter(role => role.assignable && !person.roles.includes(role.id) && (!q || role.name.toLowerCase().includes(q))) ?? []
  return (
    <Section title={roles.length === 1 ? 'Role' : 'Roles'}>
      <ul className="exo-dc-pf-roles">
        {roles.map(role => {
          const removable = editable && person?.choices.find(c => c.id === role.id)?.assignable
          return (
            <li key={role.id}>
              {removable ? (
                <button type="button" className="exo-dc-pf-role-x" aria-label={`Remove ${role.name}`} disabled={busy} onClick={() => change(role.id, false)}>
                  {dot(role.color)}<Close size={12} />
                </button>
              ) : dot(role.color)}
              {role.name}
            </li>
          )
        })}
        {editable ? (
          <li className="exo-dc-pf-role-add">
            <button type="button" className="exo-dc-pf-role-add-btn" aria-label="Add role" aria-expanded={picking} onClick={() => { setPicking(open => !open); setQuery('') }}><Icons.Plus size={14} /></button>
          </li>
        ) : null}
      </ul>
      {picking ? (
        <div className="exo-dc-pf-role-pick">
          <input className="exo-dc-picker-search" placeholder="Role" value={query} autoFocus aria-label="Find a role" onChange={event => setQuery(event.target.value)}
            onKeyDown={event => { if (event.key === 'Enter' && options[0]) change(options[0].id, true) }} />
          <div className="exo-dc-pf-role-options" role="listbox" aria-label="Roles">
            {options.map(role => (
              <button key={role.id} type="button" role="option" aria-selected={false} disabled={busy} onClick={() => change(role.id, true)}>
                {dot(role.color)}<span>{role.name}</span>
              </button>
            ))}
            {options.length ? null : <p className="exo-dc-note">No roles</p>}
          </div>
        </div>
      ) : null}
      {error ? <p className="exo-dc-note is-bad" role="alert">{error}</p> : null}
    </Section>
  )
}

const ACTIVITY = ['Playing', 'Streaming', 'Listening to', 'Watching', '', 'Competing in']

/** What they're doing now (a game, a song, a stream), with its art and how long. */
/**
 * What they're doing, as Discord's profile shows it: the voice channel they're in (Join voice), then each activity, a
 * song with its progress and Play on Spotify, a stream with Watch.
 */
export function ActivityCard({ data }: { data: Loaded }) {
  const presence = data.extras?.presence
  const activities = presence?.activities?.length ? presence.activities : presence?.rich ? [presence.rich] : []
  const voice = data.extras?.voice
  if (!activities.length && !voice) return null
  return (
    <>
      {voice ? <VoiceActivity voice={voice} /> : null}
      {activities.map((rich, i) => <OneActivity key={`${rich.type}:${rich.name}:${i}`} rich={rich} />)}
    </>
  )
}

// The voice channel they're in, with its server and how many are there; Join voice joins it, as Discord's card does.
function VoiceActivity({ voice }: { voice: NonNullable<NonNullable<Loaded['extras']>['voice']> }) {
  return (
    <Section title="In voice">
      <div className="exo-dc-pf-activity">
        <span className="exo-dc-pf-activity-icon" aria-hidden="true"><Icons.Voice size={20} /></span>
        <span>
          <strong>{voice.channel}</strong>
          <span>{voice.guild}</span>
          <span>{voice.count === 1 ? '1 person' : `${voice.count} people`}</span>
        </span>
      </div>
      <button type="button" className="exo-set-btn exo-dc-pf-activity-action" onClick={() => window.dispatchEvent(new CustomEvent('exo-dc-join-voice', { detail: { guildId: voice.guildId, channelId: voice.channelId } }))}>Join voice</button>
    </Section>
  )
}

const clock = (ms: number) => { const s = Math.max(0, Math.floor(ms / 1000)); return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}` }

function OneActivity({ rich }: { rich: DiscordRich }) {
  return rich.type === 2 && rich.name === 'Spotify' ? <SpotifyActivity rich={rich} /> : <GameActivity rich={rich} />
}

// Discord's heading for an activity: Playing, Streaming, Listening to X, Watching X, Competing in.
const activityTitle = (rich: DiscordRich) => rich.type === 2 || rich.type === 3 ? `${ACTIVITY[rich.type]} ${rich.name}` : ACTIVITY[rich.type] || 'Activity'

const ActivityPicture = ({ rich }: { rich: DiscordRich }) => rich.image
  ? <img src={rich.image} alt="" draggable={false} />
  : <span className="exo-dc-pf-activity-icon" aria-hidden="true"><Icons.Playing size={20} /></span>

// A game, video or stream: its name, details and state, how long, and Watch for a stream.
function GameActivity({ rich }: { rich: DiscordRich }) {
  const minutes = rich.start ? Math.max(0, Math.floor((Date.now() - rich.start) / 60_000)) : null
  const elapsed = minutes === null ? null : minutes < 60 ? `${minutes}m` : `${Math.floor(minutes / 60)}h ${minutes % 60}m`
  return (
    <Section title={activityTitle(rich)}>
      <div className="exo-dc-pf-activity">
        <ActivityPicture rich={rich} />
        <span>
          <strong>{rich.name}</strong>
          {rich.details ? <span>{rich.details}</span> : null}
          {rich.state ? <span>{rich.state}</span> : null}
          {elapsed ? <span>for {elapsed}</span> : null}
        </span>
      </div>
      {rich.type === 1 && rich.url ? <button type="button" className="exo-set-btn exo-dc-pf-activity-action" onClick={() => open(rich.url!)}>Watch</button> : null}
    </Section>
  )
}

// A song on Spotify: its title, artist and album, how far in, and Play on Spotify.
function SpotifyActivity({ rich }: { rich: DiscordRich }) {
  return (
    <Section title="Listening to Spotify">
      <div className="exo-dc-pf-activity">
        <ActivityPicture rich={rich} />
        <span>
          <strong>{rich.details ?? rich.name}</strong>
          {rich.state ? <span>by {rich.state}</span> : null}
          {rich.album ? <span>on {rich.album}</span> : null}
        </span>
      </div>
      {rich.start && rich.end ? <SongProgress start={rich.start} end={rich.end} /> : null}
      {rich.syncId ? <button type="button" className="exo-set-btn exo-dc-pf-activity-action" onClick={() => open(`https://open.spotify.com/track/${rich.syncId}`)}>Play on Spotify</button> : null}
    </Section>
  )
}

// How far into the song they are, ticking each second as Discord's bar does.
function SongProgress({ start, end }: { start: number; end: number }) {
  const [now, setNow] = useState(Date.now)
  useEffect(() => {
    const timer = window.setInterval(() => setNow(Date.now()), 1000)
    return () => window.clearInterval(timer)
  }, [])
  const total = Math.max(1, end - start)
  const done = Math.min(total, Math.max(0, now - start))
  return (
    <div className="exo-dc-pf-song" role="progressbar" aria-label="Song progress" aria-valuemin={0} aria-valuemax={Math.round(total / 1000)} aria-valuenow={Math.round(done / 1000)}>
      <span className="exo-dc-pf-song-bar"><span style={{ width: `${(done / total) * 100}%` }} /></span>
      <span className="exo-dc-pf-song-times"><span>{clock(done)}</span><span>{clock(total)}</span></span>
    </div>
  )
}

/** Your note on them (only you see it), saved to Discord when you leave the field, as Discord saves it. */
export function NoteField({ userId, note }: { userId: string; note: string | null }) {
  const [text, setText] = useState(note ?? '')
  const [saved, setSaved] = useState(note ?? '')
  const [error, setError] = useState<string | null>(null)
  const save = () => {
    if (text === saved) return
    setError(null)
    host.discordSaveNote(userId, text).then(result => { if (result.ok) setSaved(text); else setError(result.message ?? "Discord didn't save your note.") })
      .catch(() => setError("Discord didn't save your note."))
  }
  return (
    <Section title="Note">
      <textarea className="exo-dc-pf-note" value={text} maxLength={256} rows={1} placeholder="Click to add a note" aria-label="Note (only visible to you)"
        onChange={event => setText(event.target.value)} onBlur={save} />
      {error ? <p className="exo-dc-note is-bad" role="alert">{error}</p> : null}
    </Section>
  )
}

// Discord's connection types (its platform list): the name it shows and, where Discord links one, the page it opens.
const at = (base: string) => (c: DiscordConnection) => `${base}${encodeURIComponent(c.name)}`
const byId = (base: string) => (c: DiscordConnection) => `${base}${encodeURIComponent(c.id)}`
const SERVICES: Record<string, { label: string; url?: (c: DiscordConnection) => string }> = {
  twitch: { label: 'Twitch', url: at('https://www.twitch.tv/') },
  youtube: { label: 'YouTube', url: byId('https://www.youtube.com/channel/') },
  skype: { label: 'Skype' },
  steam: { label: 'Steam', url: byId('https://steamcommunity.com/profiles/') },
  leagueoflegends: { label: 'League of Legends' },
  battlenet: { label: 'Battle.net' },
  bluesky: { label: 'Bluesky', url: c => `https://bsky.app/profile/${encodeURIComponent(c.id).replaceAll('%3A', ':')}` },
  bungie: { label: 'Bungie.net' },
  reddit: { label: 'Reddit', url: at('https://www.reddit.com/u/') },
  twitter: { label: 'X', url: at('https://x.com/') },
  twitter_legacy: { label: 'Twitter', url: at('https://twitter.com/') },
  spotify: { label: 'Spotify', url: byId('https://open.spotify.com/user/') },
  facebook: { label: 'Facebook' },
  xbox: { label: 'Xbox' },
  samsung: { label: 'Samsung Galaxy' },
  instagram: { label: 'Instagram', url: at('https://www.instagram.com/') },
  // A Mastodon account's id is its own address.
  mastodon: { label: 'Mastodon', url: c => c.id },
  soundcloud: { label: 'SoundCloud' },
  github: { label: 'GitHub', url: at('https://github.com/') },
  playstation: { label: 'PlayStation Network' },
  'playstation-stg': { label: 'PlayStation Network' },
  epicgames: { label: 'Epic Games' },
  riotgames: { label: 'Riot Games' },
  roblox: { label: 'Roblox', url: byId('https://roblox.com/users/') },
  letterboxd: { label: 'Letterboxd', url: c => `https://letterboxd.com/${encodeURIComponent(c.name)}/` },
  paypal: { label: 'PayPal' },
  ebay: { label: 'eBay', url: at('https://www.ebay.com/usr/') },
  tiktok: { label: 'TikTok', url: at('https://www.tiktok.com/@') },
  crunchyroll: { label: 'Crunchyroll' },
  domain: { label: 'Domain', url: c => `https://${c.id}/` },
  'amazon-music': { label: 'Amazon Music' },
  meta_quest_or_horizon: { label: 'Meta Quest' },
}

const MARK_OF: Record<string, string> = { twitter_legacy: 'twitter', 'playstation-stg': 'playstation' }

/** The service's own mark in one white color; a domain (or a type Discord adds later) is a link. */
export function ConnectionMark({ type }: { type: string }) {
  const d = CONNECTION_MARKS[MARK_OF[type] ?? type]
  if (d) return <svg className="exo-dc-pf-mark" viewBox="0 0 24 24" width={24} height={24} aria-hidden="true"><path d={d} fill="currentColor" /></svg>
  return <span className="exo-dc-pf-mark is-plain" aria-hidden="true"><Icons.Link size={16} /></span>
}

/** Their connected accounts: each service's mark, the account name, a check when verified, and its page where Discord links one. */
export function Connections({ data }: { data: Loaded }) {
  const accounts = data.profile.connected_accounts ?? []
  if (!accounts.length) return null
  return (
    <Section title="Connections">
      <ul className="exo-dc-pf-connections">
        {accounts.map(account => {
          const service = SERVICES[account.type]
          const url = service?.url?.(account) ?? null
          const body = (
            <>
              <ConnectionMark type={account.type} />
              <span className="exo-dc-pf-connection-name">{account.name}</span>
              {account.verified ? <Icons.Verified size={16} className="exo-dc-pf-verified" aria-label="Verified" /> : null}
            </>
          )
          return (
            <li key={`${account.type}:${account.id}`}>
              {url
                ? <button type="button" className="exo-dc-pf-connection" aria-label={`${account.name} on ${service?.label ?? account.type}`} onClick={() => open(url)}>{body}</button>
                : <span className="exo-dc-pf-connection" aria-label={`${account.name} on ${service?.label ?? account.type}`}>{body}</span>}
            </li>
          )
        })}
      </ul>
    </Section>
  )
}

/** The name Discord shows for a connection type (Settings > Connected Apps). */
export function connectionLabel(type: string | null) {
  return (type && SERVICES[type]?.label) || type || 'Account'
}

/** Mutual Servers: each opens that server (after onOpen, which closes a dialog). */
export function MutualServers({ data, onOpen }: { data: Loaded; onOpen?: () => void }) {
  const { guilds, openServer } = useContext(ProfilePlaces)
  const onServer = (guildId: string) => { onOpen?.(); openServer(guildId) }
  const mutual = (data.profile.mutual_guilds ?? []).flatMap(m => { const guild = guilds.find(g => g.id === m.id); return guild ? [{ guild, nick: m.nick }] : [] })
  if (!mutual.length) return <p className="exo-dc-note">No servers in common</p>
  return (
    <div className="exo-dc-pf-list">
      {mutual.map(({ guild, nick }) => (
        <button key={guild.id} type="button" onClick={() => onServer(guild.id)}>
          {guild.icon ? <img src={guild.icon} alt="" draggable={false} /> : <span className="exo-dc-pf-initial">{[...guild.name][0]}</span>}
          <span className="exo-dc-pf-list-name">{guild.name}{nick ? <small>{nick}</small> : null}</span>
        </button>
      ))}
    </div>
  )
}

/** Mutual Friends: each opens their profile. */
export function MutualFriends({ friends, onPerson }: { friends: DiscordUser[] | null; onPerson: (friend: DiscordUser) => void }) {
  if (!friends) return <p className="exo-dc-note" role="status">Loading</p>
  if (!friends.length) return <p className="exo-dc-note">No friends in common</p>
  return (
    <div className="exo-dc-pf-list">
      {friends.map(friend => (
        <button key={friend.id} type="button" data-dc-user={friend.id} data-dc-name={friend.global_name ?? friend.username} data-dc-username={friend.username} onClick={() => onPerson(friend)}>
          <Avatar src={avatarUrl(friend, 40)} name={friend.username} status={null} size={40} look={lookOf(friend)} scope=".exo-dc-pf-list > button" />
          <span className="exo-dc-pf-list-name">{friend.global_name ?? friend.username}<small>{friend.username}</small></span>
        </button>
      ))}
    </div>
  )
}

/** "14 Mutual Friends · 1 Mutual Server", each part a button to that list. Never on your own card (mine):
 * nothing is mutual with yourself, and your full profile has no such tabs to open. */
export function MutualLine({ data, mine = false, onFriends, onServers }: { data: Loaded; mine?: boolean; onFriends: () => void; onServers: () => void }) {
  const friends = data.profile.mutual_friends_count ?? 0
  const servers = data.profile.mutual_guilds?.length ?? 0
  if (mine || (!friends && !servers)) return null
  const plural = (n: number, what: string) => `${n} mutual ${what}${n === 1 ? '' : 's'}`
  return (
    <span className="exo-dc-pf-mutual">
      {friends ? <button type="button" onClick={onFriends}>{plural(friends, 'friend')}</button> : null}
      {friends && servers ? <span aria-hidden="true">·</span> : null}
      {servers ? <button type="button" onClick={onServers}>{plural(servers, 'server')}</button> : null}
    </span>
  )
}
