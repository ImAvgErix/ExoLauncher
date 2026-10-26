/**
 * What Exo Mouse tells the rest of Exo: a notice to show (a game profile came
 * on, the battery is low, the signal is weak) and the mouse's battery, for the
 * title bar. Exo Mouse posts these from its frame; the Devices relay hands
 * them here.
 */
export type MouseState = {
  name: string; battery: number | null; charging: boolean; low: boolean
  /** Its product render (OpenMouse's image server only), for its card in Devices; null when there is none. */
  image?: string | null
  /** Through a receiver (true), on its cable (false), or not known. */
  wireless?: boolean | null
}

const noticeListeners = new Set<(text: string) => void>()
const stateListeners = new Set<(state: MouseState | null) => void>()
let current: MouseState | null = null

export function publishMouseNotice(text: string) {
  for (const listener of noticeListeners) listener(text)
}

/** Exo Mouse posts its state after every status read; only a change reaches the title bar. */
export function publishMouseState(state: MouseState | null) {
  if (state === current || (state && current && state.name === current.name && state.battery === current.battery &&
    state.charging === current.charging && state.low === current.low && state.image === current.image && state.wireless === current.wireless)) return
  current = state
  for (const listener of stateListeners) listener(state)
}

export function onMouseNotice(listener: (text: string) => void) {
  noticeListeners.add(listener)
  return () => { noticeListeners.delete(listener) }
}

export function onMouseState(listener: (state: MouseState | null) => void) {
  stateListeners.add(listener)
  listener(current)
  return () => { stateListeners.delete(listener) }
}

/** The saved profile the mouse has now (Exo Mouse's own name for it), or null. */
const profileListeners = new Set<(profile: string | null) => void>()
let currentProfile: string | null = null

export function publishMouseProfile(profile: string | null) {
  if (profile === currentProfile) return
  currentProfile = profile
  for (const listener of profileListeners) listener(profile)
}

export function onMouseProfile(listener: (profile: string | null) => void) {
  profileListeners.add(listener)
  listener(currentProfile)
  return () => { profileListeners.delete(listener) }
}
