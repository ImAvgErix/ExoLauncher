import { useEffect, useRef, useState } from 'react'
import { ChevronRight, Close } from '../../brand/icons'
import { host } from '../../lib/host'
import { onMfaAsk, useLayer, type AdminReply, type MfaAsk } from './admin'
import { ModalBack } from './SettingsLayer'

type Waiting = { ask: MfaAsk; done: (reply: AdminReply<unknown>) => void }

// Discord's names for its ways to verify, in its words.
const WAYS: Record<string, string> = {
  totp: 'Use your authenticator app',
  sms: 'Use a code sent to your phone',
  backup: 'Use a backup code',
  password: 'Use your password',
}
const CANCELLED: AdminReply<unknown> = { ok: false, message: 'Multi-factor authentication cancelled' }

/** Discord's "it's really you" check for whatever in the room asked for it: one at a time, over everything. */
export function MfaHost() {
  const [waiting, setWaiting] = useState<Waiting | null>(null)
  const open = useRef<Waiting | null>(null)
  useEffect(() => onMfaAsk((ask, done) => {
    // A newer check replaces one still open, whose request gives up.
    open.current?.done(CANCELLED)
    open.current = { ask, done }
    setWaiting(open.current)
  }), [])
  if (!waiting) return null
  const finish = (reply: AdminReply<unknown>) => { open.current = null; setWaiting(null); waiting.done(reply) }
  return <MfaDialog key={waiting.ask.id} ask={waiting.ask} onDone={finish} />
}

type Slide = 'select' | 'backup_ack' | Way
type Way = 'totp' | 'sms' | 'backup' | 'password'

/**
 * Discord's Multi-Factor Authentication dialog in Exo's look: its first way open, "Verify with something else" for the
 * rest. The host keeps the ticket and the request; a wrong code stays here for another try, anything else ends it.
 */
function MfaDialog({ ask, onDone }: { ask: MfaAsk; onDone: (reply: AdminReply<unknown>) => void }) {
  const open = (way: string): Slide => way === 'backup' ? 'backup_ack' : way as Slide
  const [slide, setSlide] = useState<Slide>(ask.methods[0] ? open(ask.methods[0]) : 'select')
  const box = useRef<HTMLDivElement>(null)
  const cancel = () => { void host.discordAdmin('mfa', { id: ask.id, step: 'cancel' }).catch(() => { }); onDone(CANCELLED) }
  useLayer(box, cancel)
  return (
    <ModalBack top onClose={cancel}>
      <div ref={box} className="exo-dc-modal exo-dc-mfa" role="dialog" aria-modal="true" aria-label="Multi-factor authentication" tabIndex={-1}>
        <header>
          <strong>Multi-factor authentication</strong>
          {slide === 'select' ? <span>This extra step shows that it’s really you.</span> : null}
          <button type="button" className="exo-game-close" aria-label="Close" onClick={cancel}><Close size={16} /></button>
        </header>
        {slide === 'select' ? (
          <div className="exo-dc-mfa-ways">
            {ask.methods.map(method => (
              <button key={method} type="button" className="exo-dc-mfa-way" onClick={() => setSlide(open(method))}>
                <span>{WAYS[method] ?? method}</span><ChevronRight size={20} />
              </button>
            ))}
          </div>
        ) : slide === 'backup_ack'
          ? <BackupWarning onSure={() => setSlide('backup')} />
          : <CodeForm key={slide} ask={ask} way={slide} onElse={ask.methods.length > 1 ? () => setSlide('select') : null} onDone={onDone} />}
      </div>
    </ModalBack>
  )
}

// One way's field (Discord's label, placeholder and length for it), Confirm, and the error Discord gave.
function CodeForm({ ask, way, onElse, onDone }: { ask: MfaAsk; way: Way; onElse: (() => void) | null; onDone: (reply: AdminReply<unknown>) => void }) {
  const [code, setCode] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const digits = way === 'totp' || way === 'sms'
  const data = way === 'backup' ? code.replace(/-/g, '') : code.trim()
  const ready = digits ? data.length === 6 : way === 'backup' ? data.length >= 8 : data.length > 0
  async function submit(event: React.FormEvent) {
    event.preventDefault()
    if (!ready || busy) return
    setBusy(true); setError(null)
    try {
      const reply = await host.discordAdmin('mfa', { id: ask.id, step: 'finish', method: way, code: data }) as AdminReply<unknown>
      // The ask back with the error: Discord didn't take the code and the check is still open.
      if (reply.mfa) { setError(reply.message ?? 'Discord didn’t take that code.'); setBusy(false); return }
      onDone(reply)
    } catch {
      setError('Discord did not answer. Try again.')
      setBusy(false)
    }
  }
  return (
    <form onSubmit={event => void submit(event)}>
      {way === 'sms' ? <SmsSent id={ask.id} /> : null}
      <label className="exo-dc-mfa-field">
        <span>{way === 'password' ? 'Password' : way === 'backup' ? 'Enter Discord Backup Code' : 'Enter Discord Auth Code'}</span>
        <input className="exo-dc-modal-input" value={code} disabled={busy} autoFocus spellCheck={false}
          type={way === 'password' ? 'password' : 'text'} autoComplete={way === 'password' ? 'current-password' : 'one-time-code'}
          inputMode={digits ? 'numeric' : undefined} maxLength={way === 'backup' ? 11 : way === 'password' ? 128 : way === 'sms' ? 10 : 6}
          placeholder={way === 'backup' ? '8-digit backup code' : way === 'password' ? undefined : '6-digit authentication code'}
          onChange={event => { setCode(event.target.value); setError(null) }} />
      </label>
      {error ? <p className="is-bad" role="alert">{error}</p> : null}
      <div className="exo-dc-modal-row">
        {onElse ? <button type="button" className="exo-dc-tab" onClick={onElse}>Verify with something else</button> : null}
        <button type="submit" className="exo-set-btn is-primary" disabled={!ready || busy}>Confirm</button>
      </div>
    </form>
  )
}

// Discord's pause before a backup code: they work once each, so ten seconds to pick another way first.
function BackupWarning({ onSure }: { onSure: () => void }) {
  const [left, setLeft] = useState(10)
  useEffect(() => {
    if (left <= 0) return
    const timer = window.setTimeout(() => setLeft(n => n - 1), 1000)
    return () => window.clearTimeout(timer)
  }, [left])
  return (
    <div className="exo-dc-mfa-warning">
      <strong>Slow down! Are you sure?</strong>
      <p>Backup codes can only be used one time each and are for emergency access to your account!</p>
      <p>{left > 0 ? `If you do not have access to any other methods, the button below will activate in ${left} seconds.` : 'If you do not have access to any other methods, use the other button below to proceed.'}</p>
      <div className="exo-dc-modal-row">
        <button type="button" className="exo-set-btn is-primary" disabled={left > 0} onClick={onSure}>I’m sure, use a backup code</button>
      </div>
    </div>
  )
}

// The SMS way: Discord texts the code when it opens (Resend SMS? sends another), to the number it names.
function SmsSent({ id }: { id: string }) {
  const [line, setLine] = useState('Sending...')
  const send = () => {
    setLine('Sending...')
    void (host.discordAdmin('mfa', { id, step: 'sms' }) as Promise<AdminReply<{ phone?: string }>>)
      .then(reply => setLine(reply.ok ? `We've sent a message to ${reply.data?.phone ?? 'your phone'}. Please enter the code you received.` : reply.message ?? 'Discord did not send it.'))
      .catch(() => setLine('Discord did not answer. Try again.'))
  }
  useEffect(send, [id])
  return (
    <p className="exo-dc-mfa-sms">{line} <button type="button" className="exo-dc-tab" onClick={send}>Resend SMS?</button></p>
  )
}
