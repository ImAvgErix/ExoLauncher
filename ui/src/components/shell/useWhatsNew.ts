import { useCallback, useEffect, useRef, useState } from 'react'
import type { LauncherSettings } from '../../lib/host'
import { RELEASES, unseenRelease, WHATS_NEW, type WhatsNewRelease } from '../../lib/whatsNew'

/**
 * The What's New sheet's release while it is open. The running version's notes open once, while unseen and nothing
 * in `busy` holds (booting, away from the Library, a game's page, a menu or dialog open); Settings › About opens them
 * again. Closing marks the version seen.
 */
export function useWhatsNew(settings: LauncherSettings | null, busy: readonly boolean[], persist: (patch: Partial<LauncherSettings>) => void) {
  const [open, setOpen] = useState<WhatsNewRelease | null>(null)
  const shown = useRef(false)
  const version = settings?.appVersion
  const due = settings ? unseenRelease(settings.appVersion, settings.whatsNewSeen) : null
  const seen = settings?.whatsNewSeen
  const free = !busy.includes(true)

  useEffect(() => {
    if (!due || !free) return
    // A beat after the Library settles, and not over a dialog of a room's own (a folder, a Discord dialog).
    const timer = window.setTimeout(() => {
      if (shown.current || document.querySelector('[aria-modal="true"]:not([hidden], [hidden] *, [inert] *)')) return
      shown.current = true
      setOpen(due)
    }, 600)
    return () => window.clearTimeout(timer)
  }, [due, free])

  useEffect(() => {
    const show = () => {
      shown.current = true
      setOpen(RELEASES.find(release => release.version === version) ?? RELEASES[0] ?? null)
    }
    window.addEventListener(WHATS_NEW, show)
    return () => window.removeEventListener(WHATS_NEW, show)
  }, [version])

  const close = useCallback(() => {
    setOpen(null)
    if (version && seen !== version) persist({ whatsNewSeen: version })
  }, [version, seen, persist])

  return [open, close] as const
}
