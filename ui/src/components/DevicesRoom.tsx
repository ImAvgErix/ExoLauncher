import { useEffect, useRef, useState } from 'react'
import { MOUSE_ORIGIN, postToMouse } from '../lib/mouseRelay'
import { onPageActivityChanged, pageIsActive } from '../lib/pageActivity'

/** How long Exo Mouse has to say anything after its frame starts loading before the room says it didn't load. */
const LOAD_MS = 15_000

/**
 * Devices: what you plug in (mice now; keyboards, IEMs and headsets as Exo learns them).
 *
 * Exo Mouse, a separate program (AGPL, in /mouse beside Exo's UI), fills the
 * room in a frame. Exo relays its HID calls to the native host and passes
 * device events, game starts and stops, and whether the room is on screen
 * back to it. The relay (lib/mouseRelay.ts) starts with Exo; messages from
 * anywhere but that frame, and calls other than HID, the games list and the
 * module's profiles, are ignored. The room counts as on screen only while Exo
 * is in the foreground, so the module's status reads slow down behind a game.
 * The module asks for Exo's text size and motion as it starts: until it has
 * said anything, it isn't running, and after a while the room says so with Reload.
 */
export function DevicesRoom({ active }: { active: boolean }) {
  const [foreground, setForeground] = useState(pageIsActive)
  useEffect(() => onPageActivityChanged(() => setForeground(pageIsActive())), [])
  const visible = active && foreground
  useEffect(() => {
    postToMouse({ event: 'room.visible', data: visible })
    return () => postToMouse({ event: 'room.visible', data: false })
  }, [visible])

  const frame = useRef<HTMLIFrameElement>(null)
  const [load, setLoad] = useState(0)
  const [failed, setFailed] = useState(false)
  useEffect(() => {
    let heard = false
    const listen = (event: MessageEvent) => {
      if (event.origin !== MOUSE_ORIGIN || event.source === null || event.source !== frame.current?.contentWindow) return
      // A slow start that answers after the wait still gets its room back.
      heard = true
      setFailed(false)
    }
    window.addEventListener('message', listen)
    const timer = window.setTimeout(() => { if (!heard) setFailed(true) }, LOAD_MS)
    return () => {
      window.removeEventListener('message', listen)
      window.clearTimeout(timer)
    }
  }, [load])

  return (
    <>
      {failed && (
        <div className="exo-hosted-room-state" role="alert">
          <p>Exo Mouse didn't load.</p>
          <button type="button" className="exo-set-btn" onClick={() => { setFailed(false); setLoad(value => value + 1) }}>Reload</button>
        </div>
      )}
      <iframe
        key={load}
        ref={frame}
        className="exo-devices-frame"
        hidden={failed}
        src={`${MOUSE_ORIGIN}/index.html`}
        title="Mouse"
        onLoad={() => postToMouse({ event: 'room.visible', data: visible })}
      />
    </>
  )
}
