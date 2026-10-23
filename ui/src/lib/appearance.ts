import { useSyncExternalStore } from 'react'

/**
 * Appearance (Settings › General): Light, Dark, Void (AMOLED black) or Match Windows (Light or Dark, as Windows'
 * apps are). The page draws it from <html data-exo-theme> (tokens.css); public/theme.js sets it before the first
 * frame from what was last chosen, so Exo never opens in the wrong one.
 */
export type Appearance = 'system' | 'light' | 'dark' | 'void'
export type Theme = 'light' | 'dark' | 'void'

/** Where the choice is kept for public/theme.js (which can't wait for the host). */
export const APPEARANCE_KEY = 'exo.appearance'

const LIGHT_QUERY = '(prefers-color-scheme: light)'

export function subscribeWindowsMode(onChange: () => void): () => void {
  const query = window.matchMedia(LIGHT_QUERY)
  query.addEventListener('change', onChange)
  return () => query.removeEventListener('change', onChange)
}

export const windowsLight = () => window.matchMedia(LIGHT_QUERY).matches

export function asAppearance(value: unknown): Appearance {
  return value === 'light' || value === 'dark' || value === 'void' ? value : 'system'
}

export function storedAppearance(): Appearance {
  try { return asAppearance(localStorage.getItem(APPEARANCE_KEY)) } catch { return 'system' }
}

export function themeOf(appearance: Appearance, light: boolean): Theme {
  return appearance === 'system' ? (light ? 'light' : 'dark') : appearance
}

// The theme the page is drawn in right now, for what React must compute for it (a color made readable on the page).
const pageThemeListeners = new Set<() => void>()
let pageThemeWatch: MutationObserver | null = null
const readPageTheme = (): Theme => {
  const theme = document.documentElement.dataset.exoTheme
  return theme === 'light' || theme === 'dark' ? theme : 'void'
}
function subscribePageTheme(onChange: () => void) {
  pageThemeListeners.add(onChange)
  pageThemeWatch ??= new MutationObserver(() => { for (const listener of pageThemeListeners) listener() })
  if (pageThemeListeners.size === 1) pageThemeWatch.observe(document.documentElement, { attributes: true, attributeFilter: ['data-exo-theme'] })
  return () => {
    pageThemeListeners.delete(onChange)
    if (!pageThemeListeners.size) pageThemeWatch?.disconnect()
  }
}

/** <html data-exo-theme>, following every change; one observer for every reader. */
export function usePageTheme(): Theme {
  return useSyncExternalStore(subscribePageTheme, readPageTheme, () => 'void')
}
