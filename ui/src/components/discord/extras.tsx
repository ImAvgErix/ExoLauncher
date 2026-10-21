import { openToggle } from './openToggle'
import { LottieSticker } from './LottieSticker'
import { FavoriteMenu } from './EmojiPicker'
import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react'
import { host } from '../../lib/host'
import { report } from '../../lib/report'
import { requestDiscordSettings, type DiscordStickerGroup, type DiscordUser } from '../../lib/discord'
import { Icons } from './icons'
import { openUserMenu } from './UserMenu'
import { StrangerConfirm } from './admin'
import { AboutMe, ActivityCard, MemberSince, MutualLine, NoteField, ProfileBanner, ProfileEffectOf, ProfileFace, profileName, profileTheme, ProfileWho, RoleList, showFullProfile, useProfile, type Loaded, type ProfileTab } from './profileParts'
import { onScreen, pressedArt, transition, visible } from '../../lib/viewTransition'

/** What a person's picture can be, where it is pressed. */
const FACES = '.exo-dc-avatar, .exo-dc-avatar-wrap, .exo-dc-pf-face'

// The room's overlays are asked for by event, so any message or list can open them. The person as the
// message or list shows them stands in when Discord doesn't send their profile.
// The picture pressed flies into the popout's.
export function showProfile(userId: string, guildId: string | null, x: number, y: number, user?: DiscordUser) {
  void transition(() => window.dispatchEvent(new CustomEvent('exo-dc-profile', { detail: { userId, guildId, x, y, user } })),
    { kind: 'morph', morph: { name: 'exo-dc-face', from: pressedArt(FACES), to: () => visible('.exo-dc-profile .exo-dc-pf-face') } })
}

/** The picture opened in the viewer, where it flies back to on close. */
let imageFrom: Element | null = null

/** Opens a picture (or a GIF, which plays) full size over the room, flying up from where it was pressed. */
export function showImage(src: string, original: string, video = false) {
  imageFrom = pressedArt('img, video')
  void transition(() => window.dispatchEvent(new CustomEvent('exo-dc-image', { detail: { src, original, video } })),
    { morph: { name: 'exo-dc-image', from: imageFrom, to: () => visible('.exo-dc-lightbox > :is(img, video)') } })
}

function useEscape(onClose: () => void) {
  useEffect(() => {
    const escape = (event: KeyboardEvent) => { if (event.key === 'Escape') onClose() }
    document.addEventListener('keydown', escape)
    return () => document.removeEventListener('keydown', escape)
  }, [onClose])
}

type ProfileAt = { userId: string; guildId: string | null; x: number; y: number; user?: DiscordUser }

/** Discord's profile popout where you clicked a person; a new click elsewhere opens theirs. */
export function ProfileHost({ meId, onMessage }: { meId: string | null; onMessage: (userId: string) => void }) {
  const [at, setAt] = useState<ProfileAt | null>(null)
  useEffect(() => {
    const show = (event: Event) => setAt((event as CustomEvent<ProfileAt>).detail)
    // The full profile takes over from the popout (in the same update, so the picture flies between them).
    const hide = () => setAt(null)
    window.addEventListener('exo-dc-profile', show)
    window.addEventListener('exo-dc-full-profile', hide)
    return () => {
      window.removeEventListener('exo-dc-profile', show)
      window.removeEventListener('exo-dc-full-profile', hide)
    }
  }, [])
  const close = useCallback(() => setAt(null), [])
  if (!at) return null
  return <ProfilePopout key={`${at.userId}:${at.x}:${at.y}`} at={at} meId={meId} onClose={close} onMessage={id => { close(); onMessage(id) }} />
}

/**
 * The popout, as Discord's: banner with the friend button and More (the person menu: friend, Ignore, Block and what a
 * moderator may do), picture with its decoration and status, names, badges, mutuals, what they're doing, About Me,
 * Member Since, their roles here, your note, and Message. The picture or name opens the full profile.
 */
function ProfilePopout({ at, meId, onClose, onMessage }: { at: ProfileAt; meId: string | null; onClose: () => void; onMessage: (userId: string) => void }) {
  const { data, failed } = useProfile(at.userId, at.guildId, at.user)
  const box = useRef<HTMLDivElement>(null)
  useEffect(() => {
    const away = (event: PointerEvent) => { if (box.current && !box.current.contains(event.target as Node) && !openToggle(event.target)) onClose() }
    document.addEventListener('pointerdown', away, true)
    return () => document.removeEventListener('pointerdown', away, true)
  }, [onClose])
  useEscape(onClose)
  const user = data?.profile.user
  const name = data ? profileName(data) : at.user?.global_name ?? at.user?.username ?? 'Profile'
  const full = (tab?: ProfileTab) => { if (user) showFullProfile(user.id, at.user, at.guildId, tab); else onClose() }
  const [friendError, setFriendError] = useState<string | null>(null)
  return (
    <div className="exo-dc-profile exo-dc-pf" ref={box} style={{ left: at.x, top: at.y, ...(data ? profileTheme(data, at.guildId) : null) }} role="dialog" aria-label={name}>
      {data && user ? (
        <>
          <ProfileBanner data={data} guildId={at.guildId} />
          <span className="exo-dc-pf-corner">
            {user.id === meId || user.bot ? null : <PopoutFriend data={data} onError={setFriendError} />}
            <button type="button" className="exo-dc-pf-more is-inline" aria-label="More"
              onClick={event => { const b = event.currentTarget.getBoundingClientRect(); openUserMenu(user.id, b.left, b.bottom + 4, name, user.username) }}>
              <Icons.More size={18} />
            </button>
          </span>
          <div className="exo-dc-pf-head"><ProfileFace data={data} guildId={at.guildId} status={data.extras?.presence?.status ?? null} size={80} onClick={() => full()} /></div>
          <div className="exo-dc-pf-body">
            <ProfileWho data={data} guildId={at.guildId} onName={() => full()} />
            {friendError ? <p className="exo-dc-note is-bad" role="alert">{friendError}</p> : null}
            <MutualLine data={data} onFriends={() => full('friends')} onServers={() => full('servers')} />
            <ActivityCard data={data} />
            <AboutMe data={data} guildId={at.guildId} />
            <MemberSince data={data} guildId={at.guildId} />
            <RoleList data={data} guildId={at.guildId} />
            {user.id === meId ? null : <NoteField userId={user.id} note={data.note} />}
            {user.id === meId
              ? <button type="button" className="button button--primary exo-dc-pf-message" onClick={() => { onClose(); requestDiscordSettings('profiles') }}>Edit Profile</button>
              : <button type="button" className="button button--primary exo-dc-pf-message" onClick={() => onMessage(user.id)}>Message</button>}
          </div>
          <ProfileEffectOf data={data} guildId={at.guildId} />
        </>
      ) : <p className="exo-dc-note exo-dc-pf-wait" role={failed ? 'alert' : 'status'}>{failed ? "Discord didn't send this profile. Try again." : 'Loading'}</p>}
    </div>
  )
}

// The friend button beside More, as Discord's popout has it: Add Friend, or Accept Friend Request for theirs, then
// Friend Request Sent while yours waits; none for a friend (Remove Friend is in More) or someone blocked.
function PopoutFriend({ data, onError }: { data: Loaded; onError: (error: string | null) => void }) {
  const [relation, setRelation] = useState(data.extras?.relationship ?? 0)
  const [busy, setBusy] = useState(false)
  const [stranger, setStranger] = useState(false)
  if (relation !== 0 && relation !== 3 && relation !== 4) return null
  const send = (confirm = false) => {
    setBusy(true); onError(null); setStranger(false)
    const why = "Discord didn't take that."
    void host.discordFriend('accept', { userId: data.profile.user.id, confirm })
      .then(result => { if (result.ok) setRelation(relation === 3 ? 1 : 4); else if (result.stranger) setStranger(true); else onError(result.message ?? why) }, () => onError(why))
      .finally(() => setBusy(false))
  }
  const label = relation === 3 ? 'Accept Friend Request' : relation === 4 ? 'Friend Request Sent' : 'Add Friend'
  return (
    <>
      <button type="button" className="exo-dc-pf-more is-inline" aria-label={label} disabled={busy || relation === 4} onClick={() => send()}>
        {relation === 3 ? <Icons.Check size={18} /> : <Icons.AddFriend size={18} />}
      </button>
      {stranger ? <StrangerConfirm name={profileName(data)} busy={busy} onConfirm={() => send(true)} onClose={() => setStranger(false)} /> : null}
    </>
  )
}

/** A picture full size over the room, as Discord opens it; its right-click menu copies, saves and opens it. */
export function ImageHost() {
  const [image, setImage] = useState<{ src: string; original: string; video?: boolean } | null>(null)
  useEffect(() => {
    const show = (event: Event) => setImage((event as CustomEvent).detail)
    window.addEventListener('exo-dc-image', show)
    return () => window.removeEventListener('exo-dc-image', show)
  }, [])
  const close = () => {
    const to = imageFrom
    void transition(() => setImage(null), { back: true, morph: { name: 'exo-dc-image', from: visible('.exo-dc-lightbox > :is(img, video)'), to: () => onScreen(to) ? to : null } })
  }
  useEscape(close)
  if (!image) return null
  return (
    <div className="exo-dc-lightbox" role="dialog" aria-label="Image" onClick={close}>
      {image.video
        ? <video src={image.src} autoPlay loop muted playsInline data-dc-link={image.original} onClick={event => event.stopPropagation()} />
        : <img src={image.src} alt="" draggable={false} data-dc-link={image.original} onClick={event => event.stopPropagation()} />}
    </div>
  )
}

/** Discord's Add a Server: join with an invite link. */
export function AddServer({ onJoined, onClose }: { onJoined: (guildId: string) => void; onClose: () => void }) {
  const [invite, setInvite] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  useEscape(onClose)
  async function join() {
    if (!invite.trim() || busy) return
    setBusy(true)
    setError(null)
    try {
      const result = await host.discordServer('join', { invite: invite.trim() })
      if (result.ok && result.guildId) onJoined(result.guildId)
      else setError(result.message ?? 'Discord did not take that invite.')
    } catch (err) {
      report(err, 'AddServer.join')
      setError('Discord did not take that invite.')
    } finally { setBusy(false) }
  }
  return (
    <div className="exo-dc-modal-back" onClick={onClose}>
      <div className="exo-dc-modal" role="dialog" aria-label="Join a server" onClick={event => event.stopPropagation()}>
        <strong>Join a Server</strong>
        <p>Enter an invite below to join an existing server.</p>
        <label className="exo-dc-side-label" htmlFor="exo-dc-invite">Invite link</label>
        <input id="exo-dc-invite" className="exo-dc-modal-input" placeholder="https://discord.gg/hTKzmak" value={invite} autoFocus
          onChange={event => setInvite(event.target.value)} onKeyDown={event => { if (event.key === 'Enter') void join() }} />
        {error ? <p className="is-bad" role="alert">{error}</p> : null}
        <div className="exo-dc-modal-row">
          <button type="button" className="exo-dc-tab" onClick={onClose}>Back</button>
          <button type="button" className="button button--primary" disabled={!invite.trim() || busy} onClick={() => void join()}>Join Server</button>
        </div>
      </div>
    </div>
  )
}

/**
 * Discord's sticker tab: your Favorites (the account's, as Discord keeps them), then this server's stickers, Discord's
 * own packs and your other servers', each group under its name and every sticker with its own, with a search over all
 * of them. Another server's are locked without Nitro, as in Discord. Animated (Lottie) ones play too. A right-click
 * favorites or unfavorites a sticker.
 */
export function StickerPicker({ guildId, tabs, onPick, onClose }: { guildId: string | null; tabs?: ReactNode; onPick: (id: string) => void; onClose: () => void }) {
  const [groups, setGroups] = useState<DiscordStickerGroup[] | null>(null)
  const [favorites, setFavorites] = useState<string[]>([])
  const [query, setQuery] = useState('')
  const [menu, setMenu] = useState<{ id: string; x: number; y: number } | null>(null)
  const box = useRef<HTMLDivElement>(null)
  useEffect(() => {
    host.discordStickers(guildId).then(result => { setGroups(result.groups ?? []); setFavorites(result.favorites ?? []) }).catch(() => setGroups([]))
  }, [guildId])
  useEffect(() => {
    const away = (event: PointerEvent) => { if (box.current && !box.current.contains(event.target as Node) && !openToggle(event.target)) onClose() }
    document.addEventListener('pointerdown', away, true)
    return () => document.removeEventListener('pointerdown', away, true)
  }, [onClose])
  useEscape(menu ? () => setMenu(null) : onClose)
  const setFavorite = (id: string, add: boolean) => {
    setMenu(null)
    host.discordStickers(guildId, add ? 'add' : 'remove', id).then(result => {
      if (!result.favorites) throw new Error(add ? 'Discord did not save the favorite.' : 'Discord did not remove the favorite.')
      setFavorites(result.favorites)
    }).catch((error: unknown) => report(error, 'StickerPicker.favorite'))
  }
  const q = query.trim().toLowerCase()
  const usable = new Map((groups ?? []).flatMap(group => group.stickers.map(sticker => [sticker.id, { sticker, usable: group.usable }] as const)))
  // A favorite Discord no longer has (a server you left) is left out, as Discord does.
  const favorite: DiscordStickerGroup = { id: 'favorites', name: 'Favorites', icon: null, usable: true, stickers: favorites.flatMap(id => usable.get(id)?.sticker ?? []) }
  const shown = [...(q ? [] : [favorite]), ...(groups ?? [])]
    .map(group => ({ ...group, stickers: group.stickers.filter(sticker => !q || sticker.name.toLowerCase().includes(q)) }))
    .filter(group => group.stickers.length > 0)
  return (
    <div className="exo-dc-picker is-gif" ref={box} role="dialog" aria-label="Stickers">
      {tabs}
      <header className="exo-dc-picker-top">
        <input className="exo-dc-picker-search" placeholder="Find stickers" value={query} autoFocus onChange={event => setQuery(event.target.value)} aria-label="Find stickers" />
      </header>
      <div className="exo-dc-sticker-list">
        {shown.map(group => (
          <section key={group.id} className={group.usable ? undefined : 'is-locked'}>
            <p className="exo-dc-picker-head">
              {group.id === 'favorites' ? <Icons.Star size={14} /> : group.icon ? <img src={group.icon} alt="" draggable={false} /> : null}
              {group.name}{group.usable ? null : <span> · Nitro</span>}
            </p>
            <div className="exo-dc-sticker-grid">
              {group.stickers.map(sticker => {
                const locked = usable.get(sticker.id)?.usable === false
                return (
                  <button key={sticker.id} type="button" className="exo-dc-sticker-tile" disabled={locked} aria-label={sticker.name} onClick={() => onPick(sticker.id)}
                    onContextMenu={event => {
                      event.preventDefault()
                      event.stopPropagation()
                      const at = box.current!.getBoundingClientRect()
                      setMenu({ id: sticker.id, x: event.clientX - at.left, y: event.clientY - at.top })
                    }}>
                    {sticker.format === 3
                      ? <LottieSticker id={sticker.id} size={72} />
                      : <img src={`https://media.discordapp.net/stickers/${sticker.id}.${sticker.format === 4 ? 'gif' : 'png'}?size=320`} alt="" loading="lazy" draggable={false} />}
                    <span>{sticker.name}</span>
                  </button>
                )
              })}
            </div>
          </section>
        ))}
        {groups && shown.length === 0 ? <p className="exo-dc-note">{q ? 'No stickers match.' : 'None of your servers has stickers.'}</p> : null}
      </div>
      {menu ? <FavoriteMenu at={menu} favorite={favorites.includes(menu.id)} onClose={() => setMenu(null)} onFavorite={add => setFavorite(menu.id, add)} /> : null}
    </div>
  )
}
