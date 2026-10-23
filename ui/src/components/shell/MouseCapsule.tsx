import { useEffect, useState } from 'react'
import { onMouseProfile, onMouseState, type MouseState } from '../../lib/mouseState'
import { BarCapsule } from './BarCapsule'

/**
 * The mouse in the Bar, right of the song: the saved profile it has now (its name when none matches), in words
 * alone, and its battery, red while low and not charging. Opens Devices. No mouse: nothing drawn.
 */
export function MouseCapsule({ onOpen }: { onOpen: () => void }) {
  const [state, setState] = useState<MouseState | null>(null)
  const [profile, setProfile] = useState<string | null>(null)
  useEffect(() => onMouseState(setState), [])
  useEffect(() => onMouseProfile(setProfile), [])
  const battery = state?.battery ?? null
  return (
    <BarCapsule>
      {state ? (
        <button type="button" className="exo-mouse-pill" onClick={onOpen}
          aria-label={`${state.name}${profile ? `, ${profile} profile` : ''}${battery != null ? `, battery ${battery}%` : ''}, open Devices`}>
          <span className="exo-mouse-pill-name">{profile ?? state.name}</span>
          {battery != null ? (
            <span className={`exo-mouse-pill-battery${state.low && !state.charging ? ' is-low' : ''}`}>
              <span className="exo-mouse-battery-bar" aria-hidden="true"><span style={{ width: `${Math.max(6, battery)}%` }} /></span>
              {battery}%
            </span>
          ) : null}
        </button>
      ) : null}
    </BarCapsule>
  )
}
