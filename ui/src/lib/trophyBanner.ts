import spec from './trophyBannerDesign.json'

export type TrophyBannerTier = 'unknown' | 'bronze' | 'silver' | 'gold' | 'platinum'
export type TrophyBannerEffect = 'none' | 'ignition' | 'orbit' | 'flare' | 'nova'

export type TrophyBannerSpec = typeof spec

export const trophyBannerDesign: TrophyBannerSpec = spec

export const trophyBannerSource = 'ui/src/lib/trophyBannerDesign.json'

const tiers: TrophyBannerTier[] = ['unknown', 'bronze', 'silver', 'gold', 'platinum']

export function trophyBannerTier(value: string | null | undefined): TrophyBannerTier {
  const key = (value ?? '').toLowerCase()
  return tiers.includes(key as TrophyBannerTier) ? (key as TrophyBannerTier) : 'unknown'
}

export function trophyBannerCycle(): TrophyBannerTier[] {
  return spec.previewCycle.map((item) => trophyBannerTier(item)).filter((item) => item !== 'unknown')
}

export function trophyBannerVars(tier: TrophyBannerTier): Record<string, string> {
  const motion = spec.motion.tiers[tier]
  const accent = spec.accents[tier]
  return {
    '--exo-trophy-width': `${spec.width}px`,
    '--exo-trophy-height': `${spec.height}px`,
    '--exo-trophy-radius': `${spec.radius}px`,
    '--exo-trophy-icon': `${spec.icon}px`,
    '--exo-trophy-icon-radius': `${spec.iconRadius}px`,
    '--exo-trophy-pad-x': `${spec.padX}px`,
    '--exo-trophy-pad-y': `${spec.padY}px`,
    '--exo-trophy-gap': `${spec.gap}px`,
    '--exo-trophy-bg': spec.colors.bg,
    '--exo-trophy-fg': spec.colors.fg,
    '--exo-trophy-muted': spec.colors.muted,
    '--exo-trophy-faint': spec.colors.faint,
    '--exo-trophy-hairline': spec.colors.hairline,
    '--exo-trophy-line': spec.colors.line,
    '--exo-trophy-good': spec.colors.good,
    '--exo-trophy-name-size': `${spec.type.nameSize}px`,
    '--exo-trophy-detail-size': `${spec.type.detailSize}px`,
    '--exo-trophy-meta-size': `${spec.type.metaSize}px`,
    '--exo-trophy-rarity-size': `${spec.type.raritySize}px`,
    '--exo-trophy-rarity-tracking': `${spec.type.rarityTrackingEm}em`,
    '--exo-trophy-from-y': `${motion.fromY}px`,
    '--exo-trophy-from-scale': String(motion.fromScale),
    '--exo-trophy-overshoot': String(motion.overshoot),
    '--exo-trophy-enter-ms': `${motion.enterMs}ms`,
    '--exo-trophy-settle-ms': `${motion.settleMs}ms`,
    '--exo-trophy-exit-ms': `${spec.motion.exitMs}ms`,
    '--exo-trophy-reduced-ms': `${spec.motion.reducedFadeMs}ms`,
    '--exo-trophy-rarity': accent.rarity,
    '--exo-trophy-edge': accent.hairline,
    '--exo-trophy-font': `${spec.fontFamily} Variable, ${spec.fontFamily}, ${spec.fontFamilyFallback}, sans-serif`,
  }
}

export function trophyBannerLabel(tier: TrophyBannerTier): string {
  if (tier === 'bronze') return 'EXO IGNITION'
  if (tier === 'silver') return 'EXO ORBIT'
  if (tier === 'gold') return 'EXO FLARE'
  if (tier === 'platinum') return 'EXO NOVA'
  return 'EXO UNLOCK'
}

/** Same math as TrophyNotificationLayout.Calculate. */
export function trophyNotificationSlot(
  x: number,
  y: number,
  stageWidth: number,
  stageHeight: number,
  bannerWidth = spec.width,
  bannerHeight = spec.height,
  margin = spec.overlayPad,
): { left: number; top: number; width: number; height: number } {
  const safeW = Math.max(1, stageWidth)
  const safeH = Math.max(1, stageHeight)
  const width = Math.min(Math.max(1, bannerWidth), safeW)
  const height = Math.min(Math.max(1, bannerHeight), safeH)
  const maxM = Math.min(
    Math.max(0, Math.floor((safeW - width) / 2)),
    Math.max(0, Math.floor((safeH - height) / 2)),
  )
  const pad = Math.min(Math.max(0, margin), maxM)
  const availW = Math.max(0, safeW - width - pad * 2)
  const availH = Math.max(0, safeH - height - pad * 2)
  return {
    left: pad + Math.round(availW * x),
    top: pad + Math.round(availH * y),
    width,
    height,
  }
}
