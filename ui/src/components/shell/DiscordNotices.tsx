import { memo, useCallback, useEffect, useRef, useState } from 'react'
import { onHostEvent } from '../../lib/host'
import type { DiscordNotice } from '../../lib/discord'
import { Avatar } from '../discord/presence'
import { Icons } from '../discord/icons'

/** How long a banner stays (Windows' and macOS's own banners), paused while the pointer or focus is on the stack. */
const STAY_MS = 5000
/** The exit's length in exo-shell's CSS. */
const LEAVE_MS = 200
/** The most banners at once; a newer one pushes the oldest out. */
const MAX = 3

/** key: the banner's own, kept while a newer message takes its place and while it leaves, so it leaves as itself. */
type Banner = DiscordNotice & { key: number; count: number; leaving: boolean; at: number }
let keys = 0

/**
 * Discord messages that notify while Exo is in front: one banner per conversation at the top right, the person's
 * picture and name, where it was (#channel · Server, or the group), and the message on one or two lines. A newer
 * message in the same conversation takes its banner's place and counts up. Clicking one opens that conversation;
 * reading or opening it anywhere (the host's discord.notifyClear) takes its banner away.
 */
export const DiscordNotices = memo(function DiscordNotices({ onOpen }: {
  onOpen: (notice: DiscordNotice) => void
}) {
  const [banners, setBanners] = useState<Banner[]>([])
  const [held, setHeld] = useState(false)

  const leave = useCallback((match: (b: Banner) => boolean) => {
    setBanners(list => list.some(b => match(b) && !b.leaving) ? list.map(b => match(b) ? { ...b, leaving: true } : b) : list)
  }, [])

  useEffect(() => onHostEvent('discord.notify', notice => setBanners(list => {
    const same = list.find(b => b.channelId === notice.channelId && !b.leaving)
    const next: Banner = { ...notice, key: same?.key ?? ++keys, count: (same?.count ?? 0) + 1, leaving: false, at: Date.now() }
    const rest = list.filter(b => b !== same)
    const live = rest.filter(b => !b.leaving)
    const over = live.length - (MAX - 1)
    const pushed = over > 0 ? new Set(live.slice(-over)) : null
    return [next, ...rest.map(b => pushed?.has(b) ? { ...b, leaving: true } : b)]
  })), [])

  useEffect(() => onHostEvent('discord.notifyClear', ({ channelId, messageId }) =>
    leave(b => b.channelId === channelId && (!messageId || b.messageId === messageId))), [leave])

  // Banners that have left are dropped once their exit has played.
  const leaving = banners.some(b => b.leaving)
  useEffect(() => {
    if (!leaving) return
    const timer = window.setTimeout(() => setBanners(list => list.filter(b => !b.leaving)), LEAVE_MS)
    return () => window.clearTimeout(timer)
  }, [leaving, banners])

  // Each banner goes STAY_MS after it last changed; none goes while the stack is held.
  const nextDue = held ? null : Math.min(...banners.filter(b => !b.leaving).map(b => b.at + STAY_MS))
  useEffect(() => {
    if (nextDue === null || !Number.isFinite(nextDue)) return
    const timer = window.setTimeout(() => leave(b => b.at + STAY_MS <= Date.now()), Math.max(0, nextDue - Date.now()))
    return () => window.clearTimeout(timer)
  }, [nextDue, leave])

  // Coming back from a hold gives every banner its full time again.
  const wasHeld = useRef(false)
  useEffect(() => {
    if (wasHeld.current && !held) setBanners(list => list.map(b => ({ ...b, at: Date.now() })))
    wasHeld.current = held
  }, [held])

  if (!banners.length) return null
  return (
    <section className="exo-dc-notices" aria-label="Discord notifications" aria-live="polite"
      onPointerEnter={() => setHeld(true)} onPointerLeave={() => setHeld(false)}
      onFocus={() => setHeld(true)} onBlur={event => { if (!event.currentTarget.contains(event.relatedTarget)) setHeld(false) }}>
      {banners.map(banner => <NoticeBanner key={banner.key} banner={banner}
        onOpen={() => { leave(b => b.channelId === banner.channelId); onOpen(banner) }}
        onClose={() => leave(b => b.channelId === banner.channelId)} />)}
    </section>
  )
})

function NoticeBanner({ banner, onOpen, onClose }: { banner: Banner; onOpen: () => void; onClose: () => void }) {
  const where = banner.place ? banner.server ? `${banner.place} · ${banner.server}` : banner.place : banner.server
  return (
    <div className={`exo-dc-notice-slot${banner.leaving ? ' is-leaving' : ''}`} inert={banner.leaving}>
      <div className="exo-dc-notice">
        <button type="button" className="exo-dc-notice-main" onClick={onOpen}
          aria-label={`${banner.author}${where ? `, ${where}` : ''}: ${banner.body}. Open the conversation.`}>
          <Avatar src={banner.avatar} name={banner.author} status={null} size={36} />
          <span className="exo-dc-notice-text">
            <span className="exo-dc-notice-head">
              <strong>{banner.author}</strong>
              {banner.count > 1 ? <span className="exo-dc-notice-count">{banner.count} new</span> : null}
            </span>
            {where ? <span className="exo-dc-notice-where">{where}</span> : null}
            <span className="exo-dc-notice-body">{banner.body}</span>
          </span>
        </button>
        <button type="button" className="exo-dc-notice-close" aria-label="Dismiss" onClick={onClose}>
          <Icons.Close size={14} />
        </button>
      </div>
    </div>
  )
}
