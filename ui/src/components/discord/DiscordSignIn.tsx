import { useEffect, useState } from 'react'
import { Qr } from '../ui/Qr'
import { host, onHostEvent } from '../../lib/host'
import { report } from '../../lib/report'
import type { DiscordSignInStep } from '../../lib/discord'
import { ErrorNote } from '../ui/ErrorNote'

/** Discord's phone sign-in: a QR code while it waits, then what the phone did; New code when it ended. The room and first run share it. */
export function DiscordQrSignIn({ note = null }: { note?: string | null }) {
  const [step, setStep] = useState<DiscordSignInStep | null>(null)
  const [attempt, setAttempt] = useState(0)

  useEffect(() => {
    const off = onHostEvent('discord.signin', setStep)
    setStep(null)
    void host.discordSignIn().catch((error: unknown) => {
      report(error, 'NativeDiscordRoom.signIn')
      setStep({ state: 'error', message: "Discord's sign-in couldn't start." })
    })
    return () => { off(); void host.discordSignIn(true).catch(() => { }) }
  }, [attempt])

  const ended = step && ['error', 'expired', 'cancelled'].includes(step.state)
  return (
    <>
      <div className="exo-dc-qr" aria-live="polite">
        {step?.state === 'waiting' && step.url ? <Qr text={step.url} label="Discord sign-in code" className="exo-dc-qr-code" />
          : step?.state === 'scanned' ? <p>Confirm on your phone{step.user ? `, ${step.user}` : ''}.</p>
          : step?.state === 'done' ? <p>Signed in.</p>
          : ended ? <ErrorNote message={step.message ?? 'The code expired.'} />
          : <span className="exo-dc-qr-code exo-skeleton exo-shine" role="status" aria-label="Getting a sign-in code" />}
      </div>
      {ended ? <button type="button" className="exo-set-btn is-primary" onClick={() => setAttempt(n => n + 1)}>New code</button> : null}
      {note && !step ? <ErrorNote className="exo-dc-signin-note" message={note} /> : null}
    </>
  )
}

