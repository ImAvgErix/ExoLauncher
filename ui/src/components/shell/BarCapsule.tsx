import { useEffect, useRef, useState, type ReactNode } from 'react'
import { shouldReduceMotion } from '../../lib/motionPreference'
import { useLens } from '../../lib/glass'

// The way out's length (system.css › exo-capsule-out, the quick time), and a frame to spare.
const LEAVE_MS = 140

/**
 * A floating capsule of the Bar (a call, a download, the song). It slides down into place when
 * something is live and back up when it ends, still showing what it showed until it is gone. Each
 * has its own slot beside the Rooms, so nothing else in the Bar moves when one comes or goes.
 */
export function BarCapsule({ children }: { children: ReactNode }) {
  const shown = children !== null && children !== undefined && children !== false
  const last = useRef<ReactNode>(null)
  const [present, setPresent] = useState(shown)
  const glass = useRef<HTMLDivElement>(null)
  useLens(glass, shown || present)
  if (shown) last.current = children
  if (shown !== present && (shown || shouldReduceMotion())) setPresent(shown)
  const leaving = !shown && present
  // On a timer, not the animation's end: a minimized window draws no frames, and the capsule
  // must still be gone when it comes back.
  useEffect(() => {
    if (!leaving) return
    const timer = window.setTimeout(() => setPresent(false), LEAVE_MS)
    return () => clearTimeout(timer)
  }, [leaving])
  if (!shown && !present) return null
  return (
    <div ref={glass} className={`exo-glass exo-live exo-bar-capsule${leaving ? ' is-leaving' : ''}`}>
      {shown ? children : last.current}
    </div>
  )
}
