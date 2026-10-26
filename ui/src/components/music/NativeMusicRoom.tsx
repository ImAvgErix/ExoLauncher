import { useEffect, useRef, useState } from 'react'
import { host, onHostEvent } from '../../lib/host'
import { rememberSignedIn, wasSignedIn } from '../../lib/music'
import { report } from '../../lib/report'
import { appleAdapter, spotifyAdapter, youtubeAdapter } from '../../lib/musicAdapters'
import type { PlayerRoomState } from '../../lib/player'
import { MusicRoom } from '../MusicRoom'
import { MusicPlayer } from './MusicPlayer'
import { MusicSignIn } from './MusicSignIn'
import { MusicWait } from './MusicWait'
import { Spinner } from '../ui/Spinner'

const adapters = { spotify: spotifyAdapter, youtube: youtubeAdapter, apple: appleAdapter }

// Cache-first: an engine that is still starting can report signed out for a moment. If this
// service was signed in last time, the room keeps "Opening" that long instead of flashing sign-in.
const SIGNED_IN_GRACE_MS = 15000

/**
 * A service in Exo's own player. Its engine (the Spotify app, or the YouTube
 * Music or Apple Music page) runs hidden. Installing shows the hosted room;
 * signed out, Exo's sign-in screen starts the service's own sign-in.
 */
export function NativeMusicRoom({ service, active, shown, nowPlaying }: {
  service: keyof typeof adapters
  /** On screen with nothing over it: the hosted sign-in or install view may show. */
  active: boolean
  /** Counts the mini player's presses: each opens Now playing. */
  nowPlaying: number
  /** The room is the one open, a dialog over it or not: the engine is wanted. */
  shown: boolean
}) {
  const adapter = adapters[service]
  const [state, setState] = useState<PlayerRoomState>('loading')
  const [message, setMessage] = useState<string | null>(null)
  const [attempt, setAttempt] = useState(0)
  // Signed out: Exo's sign-in screen. Its button starts the service's own sign-in: Spotify's hidden
  // app hands off to the default browser, the web services open their own sign-in window.
  const [signingIn, setSigningIn] = useState(false)
  const [signInMessage, setSignInMessage] = useState<string | null>(null)
  const [signInStatus, setSignInStatus] = useState<string | null>(null)

  // Only Spotify's Store edition: Exo installs Spotify's own app and removes the Store one, then opens it.
  const [switching, setSwitching] = useState(false)
  const [switchError, setSwitchError] = useState<string | null>(null)
  async function switchToClassic() {
    setSwitching(true)
    setSwitchError(null)
    try {
      const result = await host.musicInstall('spotify')
      if (result.ok) setAttempt(n => n + 1)
      else setSwitchError(result.message ?? 'Exo could not switch Spotify. Try again.')
    } catch (error) {
      report(error, 'NativeMusicRoom.switchToClassic')
      setSwitchError('Exo could not switch Spotify. Try again.')
    } finally {
      setSwitching(false)
    }
  }

  async function signIn() {
    setSigningIn(true)
    setSignInMessage(null)
    setSignInStatus(null)
    try {
      const result = await adapter.signIn()
      if (!result.ok) setSignInMessage(result.message ?? `${adapter.name} could not start sign-in. Try again.`)
      else if (service === 'spotify') setSignInStatus('Finish signing in in your browser.')
    } catch (error) {
      report(error, 'NativeMusicRoom.signIn')
      setSignInMessage(`${adapter.name} could not start sign-in. Try again.`)
    } finally {
      setSigningIn(false)
    }
  }

  // The engine starts with the room: once when the room is preloaded behind the splash (so Home
  // is there the first time Music opens), then each time it shows. Left, the room hands it back
  // (cleanup) so it can sleep; a hidden room never asks for it again. A dialog over the room
  // (Ctrl+K) is not leaving it.
  const warmed = useRef(false)
  useEffect(() => {
    if (!shown && warmed.current) return
    warmed.current = true
    let live = true
    let timer = 0
    let tries = 0
    const started = Date.now()
    const check = async () => {
      try {
        const raw = await adapter.start()
        if (!live) return
        const settling = raw.state === 'signedOut' && wasSignedIn(service) && Date.now() - started < SIGNED_IN_GRACE_MS
        const result = settling ? { ...raw, state: 'loading' as const } : raw
        if (result.state === 'running') rememberSignedIn(service, true)
        else if (result.state === 'signedOut') rememberSignedIn(service, false)
        // A player already on screen stays (with its page) while the engine wakes from a nap: only
        // signed out, an error or not installed replace it.
        setState(previous => previous === 'running' && result.state === 'loading' ? previous : result.state)
        setMessage(result.message ?? null)
        // Opening, signing in or upgrading: look again until the engine is ready.
        if (result.state === 'loading' || result.state === 'signedOut' || result.state === 'notPremium') {
          // Only opening gives up; the sign-in screen waits for the user.
          if (result.state === 'loading' && ++tries > 120) {
            setState(previous => previous === 'running' ? previous : 'error')
            setMessage(`${adapter.name} did not open. Try again.`)
            return
          }
          timer = window.setTimeout(() => void check(), result.state === 'loading' ? 1000 : 3000)
        }
      } catch (error) {
        report(error, 'NativeMusicRoom.start')
        if (live) { setState('error'); setMessage(`${adapter.name} could not open. Try again.`) }
      }
    }
    void check()
    return () => {
      live = false
      window.clearTimeout(timer)
      void adapter.leave().catch(() => { })
    }
  }, [adapter, service, shown, attempt])

  // The engine can end under a room that is playing (Spotify closed, a web player's page crashed). A room on screen starts
  // it again, on a short leash: twice a minute at most, then it says so instead of fighting someone who closed it on purpose.
  const stateRef = useRef(state)
  stateRef.current = state
  const restarts = useRef<number[]>([])
  useEffect(() => onHostEvent('music.status', data => {
    const status = data as { service?: string; state?: string } | null
    if (!shown || stateRef.current !== 'running' || status?.service !== service) return
    if (status.state !== 'closed' && status.state !== 'error') return
    const now = Date.now()
    restarts.current = restarts.current.filter(at => now - at < 60_000)
    if (restarts.current.length >= 2) {
      setState('error')
      setMessage(`${adapter.name} stopped. Open it again.`)
      return
    }
    restarts.current.push(now)
    setState('loading')
    setAttempt(n => n + 1)
  }), [adapter, service, shown])

  if (state === 'notInstalled') return <MusicRoom service={service} active={active} />
  if (state === 'signedOut' || state === 'notPremium') {
    // Without the subscription: Spotify's button opens Premium; the web services' signs in with another account.
    const upgrade = state === 'notPremium' ? adapter.premiumUrl : undefined
    return <MusicSignIn service={service} name={adapter.name} busy={signingIn} note={message} message={signInMessage} status={signInStatus}
      action={upgrade ? `Get ${adapter.name} Premium` : undefined} onSignIn={() => void (upgrade ? host.openUrl(upgrade) : signIn())} />
  }
  if (state === 'storeEdition') {
    return (
      <main className="exo-hosted-room">
        <div className="exo-hosted-room-state" role="status">
          <p>{switchError ?? message ?? 'Spotify from the Microsoft Store can’t play inside Exo.'}</p>
          <button type="button" className="exo-set-btn" disabled={switching} onClick={() => void switchToClassic()}>
            {switching ? <Spinner label="Switching to Spotify’s app" /> : null}
            Switch to Spotify’s app
          </button>
        </div>
      </main>
    )
  }
  if (state === 'error') {
    return (
      <main className="exo-hosted-room">
        <div className="exo-hosted-room-state" role="alert">
          <p>{message ?? `${adapter.name} could not open.`}</p>
          <button type="button" className="exo-set-btn" onClick={() => setAttempt(n => n + 1)}>Open again</button>
        </div>
      </main>
    )
  }
  // Opening: the room's own frame, shining, the same one the player lies under until its first page is in.
  if (state !== 'running') return <MusicWait name={adapter.name} />
  return <MusicPlayer key={service} adapter={adapter} active={active} nowPlaying={nowPlaying} />
}
