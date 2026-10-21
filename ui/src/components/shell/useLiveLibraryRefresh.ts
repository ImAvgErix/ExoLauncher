import { useEffect } from 'react'
import { onPageActivityChanged, pageIsActive } from '../../lib/pageActivity'

const RESCAN_MS = 30 * 60_000

/** Native file events give immediate install updates; the timer and reconnect
 * rescan catches purchases made online and stores that do not expose a local
 * manifest change. Returning to the window never triggers a read of its own:
 * a rescan that fell due while Exo was in the background (hidden, or behind a
 * game) runs once on return, so a full store scan never competes with a game. */
export function useLiveLibraryRefresh(rescan: () => Promise<void>): void {
  useEffect(() => {
    let pending = false
    let overdue = false
    const scan = () => {
      if (pending) return
      if (!pageIsActive()) {
        overdue = true
        return
      }
      overdue = false
      pending = true
      void rescan().finally(() => {
        pending = false
      })
    }
    const timer = window.setInterval(scan, RESCAN_MS)
    window.addEventListener('online', scan)
    const offActivity = onPageActivityChanged(() => {
      if (overdue && pageIsActive()) scan()
    })
    return () => {
      window.clearInterval(timer)
      window.removeEventListener('online', scan)
      offActivity()
    }
  }, [rescan])
}
