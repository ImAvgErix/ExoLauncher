/**
 * Tile covers keep the host's portrait rule (2:3 or taller). Tiny store
 * icons stay letterboxed; anything else with a usable bitmap is shown
 * rather than dropping to the monogram placeholder.
 */

/** Native CoverArtService.MaxCoverAspect — posters, not landscape. */
export const MAX_COVER_ASPECT = 0.90

/**
 * Lazy images inside hidden rooms have not been requested yet. Their fallback
 * timer must begin only once the tile is near the viewport; eager art may arm
 * immediately because its request starts immediately.
 */
export function shouldArmCoverTimeout({
  eager,
  visible,
}: {
  eager: boolean
  visible: boolean
}): boolean {
  return eager || visible
}

export function isPosterShaped(width: number, height: number): boolean {
  if (height < 1) return false
  return width / height <= MAX_COVER_ASPECT
}

/** True when the bitmap is a poster large enough for a library tile. */
export function isPortraitBitmap(width: number, height: number): boolean {
  if (width < 120 || height < 160) return false
  return isPosterShaped(width, height)
}

/**
 * Keep a loaded cover whenever it is a real image. Size/aspect only
 * decide whether later Steam fallbacks are worth trying.
 */
export function shouldKeepCoverBitmap(
  width: number,
  height: number,
  options: { icon?: boolean; lastCandidate?: boolean } = {},
): boolean {
  if (width < 1 || height < 1) return false
  if (options.icon) return width >= 32 && height >= 32
  if (options.lastCandidate) return width >= 32 && height >= 32
  return isPortraitBitmap(width, height)
}

export function isWideBitmap(width: number, height: number): boolean {
  if (width < 400 || height < 140) return false
  return width / height >= 1.2
}

/**
 * Steam library heroes are purpose-built wide artwork and stay full bleed.
 * The home surface controls their crop with a top-biased focal position.
 * Shorter landscape assets still use the contained wash path.
 */
export function isHeroShaped(width: number, height: number): boolean {
  if (height < 1) return false
  return width / height >= 2.5
}

/** Smallest bitmap worth washing behind a banner. Below this it is a spacer, not art. */
export function isWashableBitmap(width: number, height: number): boolean {
  return width >= 120 && height >= 120
}
