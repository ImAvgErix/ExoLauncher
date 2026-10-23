import { useSyncExternalStore } from 'react'
import { resolveGridCardLayout } from './gridWindow'

/**
 * Grid cards draw posters at ~150–310 CSS px; the cached files are up to
 * 1200 px wide. The host serves a downscale of the exact cached file at
 * https://thumbs.exo-launcher.local/<file>?w=<px> (CoverArtService.Thumbnails).
 *
 * The width is the widest card this screen can produce at any window size in
 * device pixels, rounded up to an 8 px step: the host's Lanczos pass is the
 * one resample and Chromium draws it about 1:1. Twice the card (Chromium
 * halving it through its mipmaps) read softer than the game page's cover. The
 * host serves the original whenever the thumbnail would be within a quarter
 * of it. Resizing the window never changes the URL. It only grows (a larger
 * or denser monitor), so a card never swaps to a softer bitmap mid-session.
 */

const THUMB_ORIGIN = 'https://thumbs.exo-launcher.local'
const COVER_ORIGIN = 'https://covers.exo-launcher.local'
const THUMB_STEP = 8
const THUMB_MIN = 120
/** Thumbnail pixels per device pixel of the widest card. */
const THUMB_OVERSAMPLE = 1
/** Past this the original is about as small as the thumbnail would be. */
const THUMB_MAX = 720
/** MainWindow's logical minimum width; the library pane spans the window. */
const MIN_PANE_WIDTH = 1100

let current: number | null = null
const listeners = new Set<() => void>()

function widestCard(maxPaneWidth: number): number {
  let widest = 0
  for (let pane = MIN_PANE_WIDTH; pane <= maxPaneWidth; pane += 1) {
    widest = Math.max(widest, resolveGridCardLayout(pane).cardWidth)
  }
  return widest || resolveGridCardLayout(MIN_PANE_WIDTH).cardWidth
}

/** Thumbnail width for this screen, or null when the full file is the right size. */
export function thumbWidthFor(screenCssWidth: number, devicePixelRatio: number): number | null {
  const wanted = Math.ceil(widestCard(Math.ceil(screenCssWidth)) * Math.max(1, devicePixelRatio) * THUMB_OVERSAMPLE)
  const width = Math.max(THUMB_MIN, Math.ceil(wanted / THUMB_STEP) * THUMB_STEP)
  return width > THUMB_MAX ? null : width
}

function screenThumbWidth(): number | null {
  return thumbWidthFor(Math.max(window.screen?.width ?? 0, window.innerWidth), window.devicePixelRatio || 1)
}

let measured = false
function ensureMeasured(): void {
  if (measured || typeof window === 'undefined') return
  measured = true
  current = screenThumbWidth()
  const remeasure = () => {
    // Only grow; null (the full file) is the largest.
    if (current === null) return
    const next = screenThumbWidth()
    if (next !== null && next <= current) return
    current = next
    for (const listener of listeners) listener()
  }
  window.addEventListener('resize', remeasure)
  // A move to a denser monitor (or a zoom) can change the ratio without a resize.
  const watchRatio = () => {
    window.matchMedia(`(resolution: ${window.devicePixelRatio || 1}dppx)`).addEventListener('change', () => {
      remeasure()
      watchRatio()
    }, { once: true })
  }
  watchRatio()
}

export function coverThumbWidth(): number | null {
  ensureMeasured()
  return current
}

function subscribe(listener: () => void): () => void {
  ensureMeasured()
  listeners.add(listener)
  return () => listeners.delete(listener)
}

export function useCoverThumbWidth(): number | null {
  return useSyncExternalStore(subscribe, coverThumbWidth, () => null)
}

/** Exo's cached cover URL as its grid thumbnail; other URLs pass through. */
export function thumbUrl(url: string, width: number | null): string {
  if (width == null || !url.startsWith(`${COVER_ORIGIN}/`)) return url
  const rest = url.slice(COVER_ORIGIN.length)
  return `${THUMB_ORIGIN}${rest}${rest.includes('?') ? '&' : '?'}w=${width}`
}

export function isThumbUrl(url: string): boolean {
  return url.startsWith(`${THUMB_ORIGIN}/`)
}