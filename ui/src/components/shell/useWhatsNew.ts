import { useCallback, useEffect, useRef, useState } from 'react'
import type { LauncherSettings } from '../../lib/host'
import { missedReleases, releasesThrough, WHATS_NEW, type WhatsNewRelease } from '../../lib/whatsNew'

/**
 * The What's New sheet's releases while it is open, newest first. What changed since the version last seen opens
 * once, while unseen and nothing in `busy` holds (booting, away from the Library, a game's page, a menu or dialog
 * open); Settings › About and the update prompt open every release up to the running one. Closing marks the version seen.
 */
export function useWhatsNew(settings: LauncherSettings | null, busy: readonly boolean[], persist: (patch: Partial<LauncherSettings>) => void) {
  const [open, setOpen] = useState<'missed' | 'all' | null>(null)
  const shown = useRef(false)
  const version = settings?.appVersion
  const seen = settings?.whatsNewSeen
  const due = !!version && missedReleases(version, seen).length > 0
  const free = !busy.includes(true)

  useEffect(() => {
    if (!due || !free) return
    // A beat after the Library settles, and not over a dialog of a room's own (a folder, a Discord dialog).
    const timer = window.setTimeout(() => {
      if (shown.current || document.querySelector('[aria-modal="true"]:not([hidden], [hidden] *, [inert] *)')) return
      shown.current = true
      setOpen('missed')
    }, 600)
    return () => window.clearTimeout(timer)
  }, [due, free])

  useEffect(() => {
    const show = () => {
      shown.current = true
      setOpen('all')
    }
    window.addEventListener(WHATS_NEW, show)
    return () => window.removeEventListener(WHATS_NEW, show)
  }, [])

  const close = useCallback(() => {
    setOpen(null)
    if (version && seen !== version) persist({ whatsNewSeen: version })
  }, [version, seen, persist])

  const releases: WhatsNewRelease[] | null = !open || !version ? null : open === 'missed' ? missedReleases(version, seen) : releasesThrough(version)
  return [releases, close] as const
}
