import { useEffect, useState, useSyncExternalStore } from 'react'

export function pageIsActive(): boolean {
  return !document.hidden && document.documentElement.dataset.exoInactive !== 'true'
}
/** On screen: not hidden or minimized, whether or not Exo has focus. For what the user can still see, like a running clock. */
export function pageIsVisible(): boolean {
  return !document.hidden && document.documentElement.dataset.exoHidden !== 'true'
}
export function onPageActivityChanged(update: () => void): () => void {
  document.addEventListener('visibilitychange', update)
  document.addEventListener('exo:activity', update)
  return () => {
    document.removeEventListener('visibilitychange', update)
    document.removeEventListener('exo:activity', update)
  }
}

/**
 * A clock that ticks every `ms` while Exo is on screen and stops while it is hidden or minimized,
 * reading again the moment Exo comes back. `read` is Date.now or performance.now (a stable reference).
 */
export function usePageClock(ms: number, read: () => number): number {
  const [now, setNow] = useState(read)
  useEffect(() => {
    let timer = 0
    const sync = () => {
      window.clearInterval(timer)
      timer = 0
      setNow(read())
      if (pageIsVisible()) timer = window.setInterval(() => setNow(read()), ms)
    }
    sync()
    const off = onPageActivityChanged(sync)
    return () => { window.clearInterval(timer); off() }
  }, [ms, read])
  return now
}

/** Whether the page is active, kept current: for animations that rest while Exo is in the background. */
export function usePageActive(): boolean {
  return useSyncExternalStore(onPageActivityChanged, pageIsActive)
}

/** Whether Exo is on screen, focused or not, kept current: for what the user watches beside a game on another screen. */
export function usePageVisible(): boolean {
  return useSyncExternalStore(onPageActivityChanged, pageIsVisible)
}
