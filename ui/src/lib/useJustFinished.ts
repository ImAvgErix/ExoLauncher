import { useEffect, useRef, useState } from 'react'

/** How long after the work stops its result may still land (the library read that marks it installed). */
const LAND_MS = 30_000

/**
 * Done (DESIGN.md › Motion): true for the 3 s the done motion plays, once
 * something that was working stops and lands where it should (a download on
 * an installed game, an update on current files). The landing usually comes a
 * moment after the work stops, with the next library read. Stopping any other
 * way (canceled, failed) shows nothing.
 */
export function useJustFinished(working: boolean, landed: boolean): boolean {
  const was = useRef(working)
  const endedAt = useRef<number | null>(null)
  const [finished, setFinished] = useState(false)
  useEffect(() => {
    if (working) endedAt.current = null
    else if (was.current) endedAt.current = performance.now()
    was.current = working
    if (endedAt.current === null || !landed) return
    const fresh = performance.now() - endedAt.current < LAND_MS
    endedAt.current = null
    if (fresh) setFinished(true)
  }, [working, landed])
  useEffect(() => {
    if (!finished) return
    const timer = window.setTimeout(() => setFinished(false), 3000)
    return () => window.clearTimeout(timer)
  }, [finished])
  return finished
}
