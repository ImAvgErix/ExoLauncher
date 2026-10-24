import { AppleMusic, Spotify, YouTube } from '../../brand/icons'
import { Button } from '../ui/Button'

const marks = { spotify: Spotify, youtube: YouTube, apple: AppleMusic }

/**
 * A signed-out service, or one signed in without the subscription the room
 * plays on, in Exo's look: its mark, why (note) and one button. The button
 * starts the service's own sign-in (Spotify's, in the default browser; Google's
 * sign-in page or Apple's sign-in window); the user signs in there, never in
 * Exo. action: the button's other job, named (Spotify Free: Get Spotify Premium).
 */
export function MusicSignIn({ service, name, busy, note, message, status = null, action, onSignIn }: {
  service: keyof typeof marks
  name: string
  busy: boolean
  /** Why this account can't play yet (no subscription or Premium), from the service's own status. */
  note?: string | null
  message: string | null
  /** A step that is going well (Spotify's sign-in waiting in the browser), muted. */
  status?: string | null
  action?: string
  onSignIn: () => void
}) {
  const Mark = marks[service]
  return (
    <main className="exo-music-signin" aria-label={name}>
      <div className="exo-music-signin-body exo-motion-arrive">
        <Mark size={56} />
        {note ? <p className="exo-music-signin-note">{note}</p> : null}
        <Button variant="primary" className="exo-music-signin-button" isDisabled={busy} onPress={onSignIn}>
          {action ?? `Sign in to ${name}`}
        </Button>
        {message ? <p className="exo-music-signin-error" role="alert">{message}</p>
          : status ? <p className="exo-music-signin-note" role="status">{status}</p> : null}
      </div>
    </main>
  )
}
