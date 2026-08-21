import { useState, type AnimationEvent, type CSSProperties } from 'react'
import {
  trophyBannerDesign,
  trophyBannerLabel,
  trophyBannerTier,
  trophyBannerVars,
  type TrophyBannerTier,
} from '../lib/trophyBanner'
import './TrophyBanner.css'

export type TrophyBannerProps = {
  tier?: string
  name?: string
  detail?: string
  game?: string
  iconUrl?: string | null
  animate?: boolean
  leaving?: boolean
  reduced?: boolean
  announce?: boolean
  className?: string
  onAnimationComplete?: () => void
}

export function TrophyBanner({
  tier,
  name,
  detail,
  game,
  iconUrl,
  animate = false,
  leaving = false,
  reduced = false,
  announce = false,
  className,
  onAnimationComplete,
}: TrophyBannerProps) {
  const resolved: TrophyBannerTier = trophyBannerTier(tier)
  const motion = trophyBannerDesign.motion.tiers[resolved]
  const effect = ((motion as { effect?: string }).effect ?? 'none') as string
  const copy = trophyBannerDesign.preview
  const title = (name ?? copy.achievementName).trim()
  const description = (detail ?? copy.detail).trim()
  const gameTitle = (game ?? copy.gameTitle).trim()
  const [iconFailed, setIconFailed] = useState(false)
  const showIcon = Boolean(iconUrl) && !iconFailed
  const platinumSheen = resolved === 'platinum' && motion.sheen && !reduced && !leaving
  const classes = [
    'exo-trophy-banner',
    `exo-trophy-banner--${resolved}`,
    reduced ? 'is-reduced' : '',
    leaving ? 'is-leaving' : animate ? 'is-entering' : '',
    !leaving && !reduced && motion.overshoot > 1 ? 'is-pop' : '',
    className ?? '',
  ]
    .filter(Boolean)
    .join(' ')

  return (
    <article
      className={classes}
      data-tier={resolved}
      data-exo-trophy-source="ui/src/lib/trophyBannerDesign.json"
      style={trophyBannerVars(resolved) as CSSProperties}
      role={announce ? 'status' : undefined}
      aria-live={announce ? 'polite' : undefined}
      aria-atomic={announce ? true : undefined}
      aria-hidden={announce ? undefined : true}
      onAnimationEnd={(event: AnimationEvent<HTMLElement>) => {
        const name = event.animationName
        if (event.target !== event.currentTarget) return
        if (name !== 'exo-trophy-enter' && name !== 'exo-trophy-enter-pop' && name !== 'exo-trophy-leave') return
        onAnimationComplete?.()
      }}
    >
      {motion.bloom ? <span className="exo-trophy-banner__bloom" /> : null}
      {motion.sheen ? <span className="exo-trophy-banner__sheen" /> : null}
      {platinumSheen ? <span className="exo-trophy-banner__sheen exo-trophy-banner__sheen--late" /> : null}
      {effect === 'flare' ? <span className="exo-trophy-banner__effect exo-trophy-banner__effect--flare" aria-hidden /> : null}
      {effect === 'nova' ? (
        <span className="exo-trophy-banner__effect exo-trophy-banner__effect--nova" aria-hidden>
          <span className="exo-trophy-banner__black-hole" />
          <span className="exo-trophy-banner__accretion" />
          <span className="exo-trophy-banner__particles" />
        </span>
      ) : null}
      <div className="exo-trophy-banner__icon">
        {showIcon ? (
          <img src={iconUrl ?? undefined} alt="" onError={() => setIconFailed(true)} />
        ) : (
          <span className="exo-trophy-banner__fallback" aria-hidden="true">
            <svg viewBox="0 0 64 64" width="20" height="20" focusable="false">
              <polygon points="17.69,13 53.69,13 51.94,22 15.94,22" fill="currentColor" />
              <polygon points="14.87,27.5 38.87,27.5 37.13,36.5 13.13,36.5" fill="currentColor" />
              <polygon points="12.06,42 48.06,42 46.31,51 10.31,51" fill="currentColor" />
            </svg>
          </span>
        )}
        {motion.ring ? <span className="exo-trophy-banner__ring" /> : null}
      </div>
      <div className="exo-trophy-banner__copy">
        <strong className="exo-trophy-banner__name">{title}</strong>
        {description ? <p className="exo-trophy-banner__detail">{description}</p> : null}
        <div className="exo-trophy-banner__meta">
          <span className="exo-trophy-banner__game">{gameTitle}</span>
          <span className="exo-trophy-banner__rarity">{trophyBannerLabel(resolved)}</span>
        </div>
      </div>
    </article>
  )
}
