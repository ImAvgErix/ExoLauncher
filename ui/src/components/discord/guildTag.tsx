import { createElement, useContext, useEffect, useId, useLayoutEffect, useRef, useState } from 'react'
import { createPortal } from 'react-dom'
import { host } from '../../lib/host'
import { useDismiss } from '../../lib/dismiss'
import type { DiscordGuildProfile, DiscordLook } from '../../lib/discord'
import { ProfilePlaces } from './profileParts'
import { Skeleton } from '../ui/Skeleton'

/** A server tag's badge on Discord's CDN, at the size Discord asks for it (14 px shown from its 16 px file). */
export const tagBadgeUrl = (guildId: string, badge: string) => `https://cdn.discordapp.com/clan-badges/${guildId}/${badge}.png?size=16`

/**
 * Someone's server tag beside their name, as Discord shows it in messages, the member list, Friends and profiles: the
 * server's badge and its tag in a small chip. Pressing it opens the server's card (its public profile), with Go to
 * Server when you're in it.
 */
export function GuildTag({ look }: { look: DiscordLook | null | undefined }) {
  const [open, setOpen] = useState<DOMRect | null>(null)
  const tag = look?.tag
  if (!tag) return null
  return (
    <>
      <button type="button" className="exo-dc-guild-tag" aria-label={`Server tag: ${tag.tag}`} aria-expanded={!!open}
        onClick={event => { event.stopPropagation(); const at = event.currentTarget.getBoundingClientRect(); setOpen(o => o ? null : at) }}>
        {tag.badge ? <img src={tagBadgeUrl(tag.guildId, tag.badge)} alt="" width={14} height={14} draggable={false} /> : null}
        <span>{tag.tag}</span>
      </button>
      {open ? <GuildTagCard guildId={tag.guildId} tag={tag.tag} badge={tag.badge} at={open} onClose={() => setOpen(null)} /> : null}
    </>
  )
}

// The server's card beside the tag: its icon, name, tag, counts and description from Discord's profile of it.
function GuildTagCard({ guildId, tag, badge, at, onClose }: { guildId: string; tag: string; badge: string | null; at: DOMRect; onClose: () => void }) {
  const [profile, setProfile] = useState<DiscordGuildProfile | null>(null)
  const [error, setError] = useState<string | null>(null)
  const box = useRef<HTMLDivElement>(null)
  const [place, setPlace] = useState({ left: at.left, top: at.bottom + 6 })
  const { guilds, openServer } = useContext(ProfilePlaces)
  const member = guilds.some(g => g.id === guildId)
  useDismiss(box, onClose)
  useEffect(() => {
    let live = true
    void host.discordServer('profile', { guildId }).then(result => {
      if (!live) return
      if (result.ok && result.profile) setProfile(result.profile)
      else setError(result.message ?? 'Discord did not send this server.')
    }).catch(() => { if (live) setError('Discord did not answer.') })
    return () => { live = false }
  }, [guildId])
  // Kept on screen: above the tag when there's no room under it.
  useLayoutEffect(() => {
    const card = box.current?.getBoundingClientRect()
    if (!card) return
    const left = Math.max(8, Math.min(at.left, window.innerWidth - card.width - 8))
    const top = at.bottom + 6 + card.height > window.innerHeight - 8 ? Math.max(8, at.top - 6 - card.height) : at.bottom + 6
    setPlace(p => p.left === left && p.top === top ? p : { left, top })
  }, [at, profile, error])
  const icon = profile?.icon_hash ? `https://cdn.discordapp.com/icons/${guildId}/${profile.icon_hash}.webp?size=96` : null
  const card = (
    <div ref={box} className="exo-dc-profile exo-dc-tag-card" role="dialog" aria-label={profile?.name ?? tag} style={{ left: place.left, top: place.top }}>
      <div className="exo-dc-tag-card-head">
        {icon ? <img src={icon} alt="" width={48} height={48} draggable={false} /> : <span className="exo-dc-tag-card-icon">{(profile?.name ?? tag).slice(0, 1)}</span>}
        <span className="exo-dc-tag-card-who">
          <strong>{profile?.name ?? tag}</strong>
          <span className="exo-dc-guild-tag is-static">
            {badge ? <img src={tagBadgeUrl(guildId, badge)} alt="" width={14} height={14} draggable={false} /> : null}
            <span>{tag}</span>
          </span>
        </span>
      </div>
      {profile ? <TagCardAbout profile={profile} /> : null}
      {error ? <p className="exo-dc-note is-bad" role="alert">{error}</p> : null}
      {profile || error ? null : <Skeleton label="Loading the server" rows={2} />}
      {member ? <button type="button" className="exo-set-btn is-primary" onClick={() => { onClose(); openServer(guildId) }}>Go to server</button> : null}
    </div>
  )
  const room = document.querySelector('main.exo-dc')
  return room ? createPortal(card, room) : card
}

// What Discord's profile says of the server: who's on, how many, what it's about and its traits.
function TagCardAbout({ profile }: { profile: DiscordGuildProfile }) {
  return (
    <>
      <p className="exo-dc-tag-card-counts">
        <span className="is-online">{(profile.online_count ?? 0).toLocaleString()} Online</span>
        <span>{(profile.member_count ?? 0).toLocaleString()} Members</span>
      </p>
      {profile.description ? <p className="exo-dc-tag-card-about">{profile.description}</p> : null}
      {profile.traits?.length ? (
        <ul className="exo-dc-tag-card-traits">
          {profile.traits.map(trait => <li key={`${trait.position}-${trait.label}`}>{trait.emoji_name ? `${trait.emoji_name} ` : ''}{trait.label}</li>)}
        </ul>
      ) : null}
    </>
  )
}

/** One badge's art as Exo's host reads it out of Discord's client: SVG nodes whose colors are tint slots. */
export type BadgeArt = { id: number; nodes: ArtNode[]; primary: Ramp; secondary?: Ramp | null }
type Ramp = { lum: number[]; weights: { base: number; tint: number }[] }
type ArtValue = string | { p: number } | { s: number } | { url: true } | { id: true }
type ArtNode = { tag: string; attrs: Record<string, ArtValue>; children?: ArtNode[] }
type Rgb = [number, number, number]

const rgbOf = (color: string): Rgb => {
  const hex = color.replace('#', '')
  const full = hex.length === 3 ? [...hex].map(c => c + c).join('') : hex
  return [0, 2, 4].map(i => parseInt(full.slice(i, i + 2), 16) || 0) as Rgb
}
const linear = (v: number) => { const x = v / 255; return x <= 0.03928 ? x / 12.92 : ((x + 0.055) / 1.055) ** 2.4 }
const luminance = ([r, g, b]: Rgb) => 0.2126 * linear(r) + 0.7152 * linear(g) + 0.0722 * linear(b)
const half = (a: Rgb, b: Rgb): Rgb => [(a[0] + b[0]) / 2, (a[1] + b[1]) / 2, (a[2] + b[2]) / 2]

// The color at a luminance, as Discord's chroma.js sets one: halved toward black or white until it is there.
function atLuminance(color: Rgb, target: number): Rgb {
  if (target <= 0) return [0, 0, 0]
  if (target >= 1) return [255, 255, 255]
  let [low, high]: Rgb[] = luminance(color) > target ? [[0, 0, 0], color] : [color, [255, 255, 255]]
  let mid = half(low, high)
  for (let left = 20; left > 0 && Math.abs(target - luminance(mid)) >= 1e-7; left--) {
    if (luminance(mid) > target) high = mid
    else low = mid
    mid = half(low, high)
  }
  return mid
}

/** A badge's tint colors from the color picked, as Discord's badges take them: its luminance moved toward each step's. */
export function tints(color: string, ramp: Ramp) {
  const rgb = rgbOf(color), own = luminance(rgb)
  return ramp.lum.map((lum, i) => {
    const w = ramp.weights[i]
    const target = (lum * w.base + own * w.tint) / (w.base + w.tint)
    return '#' + atLuminance(rgb, target).map(v => Math.round(v).toString(16).padStart(2, '0')).join('')
  })
}

/** A Server Tag badge drawn in the colors picked, from Discord's own art. */
export function BadgeArtwork({ art, primary, secondary, size }: { art: BadgeArt; primary: string; secondary: string; size: number }) {
  const id = `exo-badge-${useId().replace(/[^a-zA-Z0-9]/g, '')}`
  const p = tints(primary, art.primary)
  const s = art.secondary ? tints(secondary, art.secondary) : []
  const value = (v: ArtValue) => typeof v === 'string' ? v : 'p' in v ? p[v.p] : 's' in v ? s[v.s] : 'url' in v ? `url(#${id})` : id
  const draw = (node: ArtNode, key: number): React.ReactNode =>
    createElement(node.tag, { key, ...Object.fromEntries(Object.entries(node.attrs).map(([name, v]) => [name, value(v)])) }, node.children?.map(draw))
  return <svg width={size} height={size} viewBox="0 0 16 16" fill="none" aria-hidden="true">{art.nodes.map(draw)}</svg>
}
