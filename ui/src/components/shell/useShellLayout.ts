import { useEffect, useSyncExternalStore } from 'react'
import { host, type ChromeLayout, type ChromeRect, type LauncherSettings } from '../../lib/host'
import { applyTextTheme, textThemeFrom } from '../../lib/exoTheme'
import { APPEARANCE_KEY, asAppearance, storedAppearance, subscribeWindowsMode, themeOf, windowsLight } from '../../lib/appearance'
import { transition } from '../../lib/viewTransition'
import { anythingOpen, watchLayers } from '../../lib/dismiss'

export type ShellLayout = {
  /** The app setting or Windows asks for less motion. */
  reduceMotion: boolean
}

const REDUCED_MOTION_QUERY = '(prefers-reduced-motion: reduce)'

function subscribeReducedMotion(onChange: () => void): () => void {
  const query = window.matchMedia(REDUCED_MOTION_QUERY)
  query.addEventListener('change', onChange)
  return () => query.removeEventListener('change', onChange)
}

function systemReducesMotion(): boolean {
  return window.matchMedia(REDUCED_MOTION_QUERY).matches
}

const TITLE_BAND = '.exo-titlebar'
/** What takes a click in the title band. Every other point of the band drags the window. */
const TITLE_BAND_CONTROLS = 'button,a,input,select,textarea,[role="button"],[role="slider"]'
/** While anything is open (a sheet, a dialog, a menu, a popover), only this top strip drags the window: a press anywhere
    else on the band reaches the page and closes it (before, a menu stayed open when the Bar's empty space was pressed). */
const MODAL_DRAG_STRIP = 6

function bandRect(element: Element, band: DOMRect): ChromeRect | null {
  const bounds = element.getBoundingClientRect()
  const left = Math.max(band.left, bounds.left)
  const top = Math.max(band.top, bounds.top)
  const right = Math.min(band.right, bounds.right)
  const bottom = Math.min(band.bottom, bounds.bottom)
  const width = Math.ceil(right) - Math.floor(left)
  const height = Math.ceil(bottom) - Math.floor(top)
  return width > 0 && height > 0 ? { x: Math.floor(left), y: Math.floor(top), width, height } : null
}

/**
 * Native caption holes for the title band. WinUI WebView2 ignores CSS
 * app-region, so the host makes the whole band a drag caption and punches out
 * exactly these rects: the tabs, the gear, the window buttons and each mini
 * player control. The pill's empty space, the gaps between controls and the
 * mini player's time readout drag the window; double-clicking them maximizes.
 * The maximize button goes separately: the host makes it Windows' own
 * maximize region, so the snap layouts flyout opens over it.
 */
function measureTitleBand(bar: Element): ChromeLayout {
  const band = bar.getBoundingClientRect()
  const maximize = bar.querySelector('[data-exo-maximize]')
  const below = { x: Math.floor(band.left), y: Math.floor(band.top) + MODAL_DRAG_STRIP, width: Math.ceil(band.width), height: Math.ceil(band.height) - MODAL_DRAG_STRIP }
  const interactiveRegions = anythingOpen() ? [below] : [...bar.querySelectorAll(TITLE_BAND_CONTROLS)]
    // A control inside another measured control is already covered.
    .filter(element => element !== maximize && !element.parentElement?.closest(TITLE_BAND_CONTROLS))
    .flatMap(element => bandRect(element, band) ?? [])
  const maximizeButton = maximize ? bandRect(maximize, band) : null
  return {
    ...(interactiveRegions.length ? { interactiveRegions } : {}),
    ...(maximizeButton ? { maximizeButton } : {}),
  }
}

const touchesTitleBand = (node: Element) => node.matches(TITLE_BAND) || !!node.querySelector(TITLE_BAND)

/**
 * Caption holes, motion, glass and text theme — everything about how the shell is
 * dressed, kept in sync with the host window and the document.
 */
export function useShellLayout(settings: LauncherSettings | null): ShellLayout {
  const systemReduce = useSyncExternalStore(subscribeReducedMotion, systemReducesMotion)
  const reduceMotion = settings?.reduceMotion === true || systemReduce
  const textColour = settings?.textColour
  const textSize = settings?.textSize
  // Glass or Solid (Settings › General › Material). Glass stays glass with Windows' transparency effects off.
  const glass = settings?.glass === 'solid' ? 'solid' : 'clear'
  // Until the host answers, the choice public/theme.js drew the first frame with.
  const appearance = settings ? asAppearance(settings.appearance) : storedAppearance()
  const light = useSyncExternalStore(subscribeWindowsMode, windowsLight)
  const theme = themeOf(appearance, light)

  useEffect(() => {
    document.documentElement.dataset.exoMotion = reduceMotion ? 'reduce' : 'full'
  }, [reduceMotion])

  useEffect(() => {
    document.documentElement.dataset.exoGlass = glass
  }, [glass])

  // A new Appearance crossfades the whole window from the old to the new (transitions.css › theme), as macOS changes
  // Light and Dark; the first frame's (public/theme.js) is already in place.
  useEffect(() => {
    const root = document.documentElement
    if (root.dataset.exoTheme !== theme) void transition(() => { root.dataset.exoTheme = theme }, { kind: 'theme' })
    try { localStorage.setItem(APPEARANCE_KEY, appearance) } catch { /* the next start opens in Match Windows */ }
  }, [theme, appearance])

  useEffect(() => {
    applyTextTheme(textThemeFrom({ textColour, textSize }))
  }, [textColour, textSize])

  // The holes follow every layout change: window size and DPI (resize), the
  // band's own contents changing size (tab badge, fonts, the mini player's
  // readouts), and the band or the mini player appearing, disappearing or
  // being replaced (update splash, onboarding, first song, Music room).
  useEffect(() => {
    let frame = 0
    let last = ''
    let settling: Animation[] = []
    const publish = () => {
      const bar = document.querySelector(TITLE_BAND)
      // A control measured mid-glide (the rooms making room for the song, the player docking) is
      // off by the glide's offset, and a transform resizes nothing, so nothing else would measure
      // again: once the band's moves finish, measure where everything came to rest.
      const moving = bar?.getAnimations({ subtree: true }).filter(a => a.playState === 'running' && a.effect?.getTiming().iterations !== Infinity) ?? []
      if (moving.some(a => !settling.includes(a))) {
        settling = moving
        void Promise.allSettled(moving.map(a => a.finished)).then(() => { if (settling === moving) { settling = []; schedule() } })
      }
      const layout = bar ? measureTitleBand(bar) : {}
      const key = JSON.stringify(layout)
      if (key !== last) { last = key; void host.setChromeLayout(layout).catch(() => { last = '' }) }
    }
    const schedule = () => { cancelAnimationFrame(frame); frame = requestAnimationFrame(publish) }
    const sizes = new ResizeObserver(schedule)
    const watchBand = () => {
      sizes.disconnect()
      const bar = document.querySelector(TITLE_BAND)
      if (bar) for (const element of [bar, ...bar.querySelectorAll('*')]) sizes.observe(element)
      schedule()
    }
    watchBand()
    const mutations = new MutationObserver(records => {
      if (records.some(record => {
        // Text-only updates (the song clock) resize an observed element instead.
        const nodes = [...record.addedNodes, ...record.removedNodes].filter((node): node is Element => node instanceof Element)
        return nodes.length > 0 && ((record.target instanceof Element && !!record.target.closest(TITLE_BAND)) || nodes.some(touchesTitleBand))
      })) watchBand()
    })
    mutations.observe(document.body, { childList: true, subtree: true })
    window.addEventListener('resize', schedule)
    const unwatch = watchLayers(schedule)
    return () => { sizes.disconnect(); mutations.disconnect(); unwatch(); window.removeEventListener('resize', schedule); cancelAnimationFrame(frame) }
  }, [])

  return { reduceMotion }
}
