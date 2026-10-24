import { useEffect, useRef, useState } from 'react'
import { host } from '../../lib/host'
import { report } from '../../lib/report'
import { Switch } from '../Switch'
import { Choice } from '../settings/Choice'
import { Close } from '../../brand/icons'
import { Icons } from './icons'
import { ModalBack } from './SettingsLayer'
import { useDismiss } from '../../lib/dismiss'

const LENGTHS = [
  { hours: 1, label: '1 hour' }, { hours: 4, label: '4 hours' }, { hours: 8, label: '8 hours' }, { hours: 24, label: '24 hours' },
  { hours: 72, label: '3 days' }, { hours: 168, label: '1 week' }, { hours: 336, label: '2 weeks' },
]

/**
 * Discord's Create Poll: a question, two to ten answers (Add another answer), how long it runs and
 * whether people may pick several; Post sends it to this channel.
 */
export function PollCreator({ channelId, onClose }: { channelId: string; onClose: () => void }) {
  const [question, setQuestion] = useState('')
  const [answers, setAnswers] = useState(['', ''])
  const [hours, setHours] = useState(24)
  const [multiple, setMultiple] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  useEffect(() => {
    const escape = (event: KeyboardEvent) => { if (event.key === 'Escape') { event.stopPropagation(); onClose() } }
    window.addEventListener('keydown', escape, true)
    return () => window.removeEventListener('keydown', escape, true)
  }, [onClose])
  const filled = answers.map(a => a.trim()).filter(Boolean)
  const ready = question.trim().length > 0 && filled.length >= 2 && !busy

  function post() {
    if (!ready) return
    setBusy(true)
    setError(null)
    host.discordPollCreate(channelId, { question: question.trim(), texts: filled, hours, multiple })
      .then(result => { if (result.ok) onClose(); else setError(result.message ?? 'Discord did not take that.') })
      .catch((err: unknown) => { report(err, 'PollCreator.post'); setError('Discord did not take that.') })
      .finally(() => setBusy(false))
  }

  return (
    <ModalBack onClose={onClose}>
      <div className="exo-dc-modal exo-dc-poll-new" role="dialog" aria-label="Create a poll" onClick={event => event.stopPropagation()}>
        <header className="exo-dc-poll-new-head">
          <h2>Create a poll</h2>
          <button type="button" className="exo-dc-icon-btn" aria-label="Close" onClick={onClose}><Close size={16} /></button>
        </header>
        <label className="exo-dc-poll-new-field">
          <span>Question</span>
          <input className="exo-dc-find" value={question} maxLength={300} autoFocus placeholder="What question do you want to ask?" onChange={event => setQuestion(event.target.value)} />
        </label>
        <div className="exo-dc-poll-new-field">
          <span>Answers</span>
          {answers.map((answer, i) => (
            <span key={i} className="exo-dc-poll-new-answer">
              <input className="exo-dc-find" value={answer} maxLength={55} placeholder="Type your answer" aria-label={`Answer ${i + 1}`}
                onChange={event => setAnswers(list => list.map((a, j) => j === i ? event.target.value : a))} />
              {answers.length > 2 ? (
                <button type="button" className="exo-dc-icon-btn" aria-label={`Remove answer ${i + 1}`} onClick={() => setAnswers(list => list.filter((_, j) => j !== i))}>
                  <Close size={14} />
                </button>
              ) : null}
            </span>
          ))}
          {answers.length < 10 ? <button type="button" className="exo-dc-tab exo-dc-poll-new-add" onClick={() => setAnswers(list => [...list, ''])}>Add another answer</button> : null}
        </div>
        <footer className="exo-dc-poll-new-foot">
          <span className="exo-dc-poll-new-length">
            <span>Duration</span>
            <Choice label="Duration" value={String(hours)} choices={LENGTHS.map(length => ({ value: String(length.hours), label: length.label }))}
              onChange={value => setHours(Number(value))} />
          </span>
          <span className="exo-dc-poll-new-multi">
            <Switch checked={multiple} label="Allow multiple answers" onChange={setMultiple} />
            Allow multiple answers
          </span>
          <button type="button" className="exo-set-btn is-primary" disabled={!ready} onClick={post}>{busy ? 'Posting…' : 'Post'}</button>
        </footer>
        {error ? <p className="exo-dc-note" role="alert">{error}</p> : null}
      </div>
    </ModalBack>
  )
}

/** Discord's + beside the message box: Upload a File, or Create Poll. */
// text: what's in the message box, which Upload your message as a file turns into message.txt (offered when there's some).
export function PlusMenu({ channelId, disabled, text, onFiles, onTextFile }: { channelId: string; disabled: boolean; text: string; onFiles: () => void; onTextFile: () => void }) {
  const [open, setOpen] = useState(false)
  const [poll, setPoll] = useState(false)
  const menu = useRef<HTMLDivElement>(null)
  useDismiss(menu, () => setOpen(false), { on: open })
  useEffect(() => {
    if (!open) return
    const escape = (event: KeyboardEvent) => { if (event.key === 'Escape') setOpen(false) }
    document.addEventListener('keydown', escape)
    return () => document.removeEventListener('keydown', escape)
  }, [open])
  return (
    <>
      <button type="button" className="exo-dc-input-btn" aria-label="Upload a file or create a poll" aria-haspopup="menu" aria-expanded={open} disabled={disabled}
        onClick={() => setOpen(value => !value)}><Icons.Add size={22} /></button>
      {open ? (
        <div ref={menu} className="exo-dc-menu exo-dc-plus-menu" role="menu">
          <button type="button" role="menuitem" onClick={() => { setOpen(false); onFiles() }}><Icons.Upload size={16} />Upload a file</button>
          {text.trim() ? <button type="button" role="menuitem" onClick={() => { setOpen(false); onTextFile() }}><Icons.Upload size={16} />Upload your message as a file</button> : null}
          <button type="button" role="menuitem" onClick={() => { setOpen(false); setPoll(true) }}><Icons.Poll size={16} />Create poll</button>
        </div>
      ) : null}
      {poll ? <PollCreator channelId={channelId} onClose={() => setPoll(false)} /> : null}
    </>
  )
}
