import { useEffect, useState, type CSSProperties } from 'react'
import { host } from '../../lib/host'
import { report } from '../../lib/report'
import type { DiscordMessage, DiscordPoll } from '../../lib/discord'
import { emojiUrl } from './markdown'
import { Icons } from './icons'
import { ErrorNote } from '../ui/ErrorNote'

/** "3 hours left", "1 day left", or "Poll closed". */
function timeLeft(poll: DiscordPoll, now: number) {
  if (poll.results?.is_finalized) return 'Poll closed'
  const end = poll.expiry ? Date.parse(poll.expiry) : NaN
  if (Number.isNaN(end)) return null
  const left = end - now
  if (left <= 0) return 'Poll closed'
  const hours = Math.round(left / 3_600_000)
  if (hours >= 24) { const days = Math.round(hours / 24); return `${days} ${days === 1 ? 'day' : 'days'} left` }
  if (hours >= 1) return `${hours} ${hours === 1 ? 'hour' : 'hours'} left`
  const minutes = Math.max(1, Math.round(left / 60_000))
  return `${minutes} ${minutes === 1 ? 'minute' : 'minutes'} left`
}

function AnswerEmoji({ emoji }: { emoji?: { id?: string | null; name?: string | null } }) {
  if (!emoji) return null
  if (emoji.id) return <img className="exo-dc-poll-emoji" src={emojiUrl(emoji.id, false, 48)} alt={emoji.name ?? ''} draggable={false} />
  return emoji.name ? <span className="exo-dc-poll-emoji" aria-hidden="true">{emoji.name}</span> : null
}

/**
 * Discord's poll in a message: the question, its answers to pick (one, or several when it allows),
 * Vote; once you voted or it closed, each answer's share as a bar with its count and your own marked.
 * Remove Vote takes yours back while it runs; its author can End Poll.
 */
export function Poll({ message, meId }: { message: DiscordMessage; meId: string | null }) {
  const poll = message.poll!
  const [picked, setPicked] = useState<Set<number>>(new Set())
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  // The clock the poll reads its time left from: each half minute while it runs, and at once when its end moves (End Poll).
  const [now, setNow] = useState(() => Date.now())
  const over = poll.results?.is_finalized === true || (poll.expiry ? Date.parse(poll.expiry) <= now : false)
  useEffect(() => {
    if (over) return
    const tick = () => setNow(Date.now())
    const soon = window.setTimeout(tick, 0)
    const every = window.setInterval(tick, 30_000)
    return () => { window.clearTimeout(soon); window.clearInterval(every) }
  }, [poll.expiry, over])
  const counts = new Map((poll.results?.answer_counts ?? []).map(c => [c.id, c]))
  const total = [...counts.values()].reduce((sum, c) => sum + c.count, 0)
  const voted = [...counts.values()].some(c => c.me_voted)
  const closed = over
  const showing = voted || closed
  const top = Math.max(0, ...[...counts.values()].map(c => c.count))

  function send(answers: number[] | 'end') {
    setBusy(true)
    setError(null)
    host.discordPoll(message.channel_id, message.id, answers)
      .then(result => { if (!result.ok) setError(result.message ?? 'Discord did not take that.'); else setPicked(new Set()) })
      .catch((err: unknown) => { report(err, 'Poll.vote'); setError('Discord did not take that.') })
      .finally(() => setBusy(false))
  }
  function pick(id: number) {
    setPicked(current => {
      const next = new Set(poll.allow_multiselect ? current : [])
      if (current.has(id)) next.delete(id)
      else next.add(id)
      return next
    })
  }

  return (
    <section className="exo-dc-poll" aria-label="Poll">
      <strong className="exo-dc-poll-question">{poll.question.text}</strong>
      <span className="exo-dc-poll-hint">{showing ? null : poll.allow_multiselect ? 'Select one or more answers' : 'Select one answer'}</span>
      <div className="exo-dc-poll-answers" role={showing ? 'list' : 'group'}>
        {poll.answers.map(answer => {
          const count = counts.get(answer.answer_id)
          const share = total ? Math.round((count?.count ?? 0) / total * 100) : 0
          const mine = count?.me_voted === true
          const winner = closed && top > 0 && count?.count === top
          if (showing) {
            return (
              <div key={answer.answer_id} role="listitem" className={`exo-dc-poll-answer is-result${mine ? ' is-mine' : ''}${winner ? ' is-winner' : ''}`}
                style={{ '--share': `${share}%` } as CSSProperties}>
                <AnswerEmoji emoji={answer.poll_media.emoji} />
                <span className="exo-dc-poll-text">{answer.poll_media.text}</span>
                {mine ? <Icons.Check size={14} /> : null}
                <span className="exo-dc-poll-count">{count?.count ?? 0} · {share}%</span>
              </div>
            )
          }
          const on = picked.has(answer.answer_id)
          return (
            <button key={answer.answer_id} type="button" className={`exo-dc-poll-answer${on ? ' is-picked' : ''}`} aria-pressed={on} disabled={busy}
              onClick={() => pick(answer.answer_id)}>
              <AnswerEmoji emoji={answer.poll_media.emoji} />
              <span className="exo-dc-poll-text">{answer.poll_media.text}</span>
              <span className={`exo-dc-poll-mark${poll.allow_multiselect ? ' is-box' : ''}`} aria-hidden="true">{on ? <Icons.Check size={12} /> : null}</span>
            </button>
          )
        })}
      </div>
      <footer className="exo-dc-poll-foot">
        <span>{total} {total === 1 ? 'vote' : 'votes'}{timeLeft(poll, now) ? ` · ${timeLeft(poll, now)}` : ''}</span>
        <span className="exo-dc-poll-actions">
          {!closed && voted ? <button type="button" className="exo-dc-tab" disabled={busy} onClick={() => send([])}>Remove vote</button> : null}
          {!closed && message.author.id === meId ? <button type="button" className="exo-dc-tab" disabled={busy} onClick={() => send('end')}>End poll</button> : null}
          {!showing ? <button type="button" className="exo-dc-tab is-raised" disabled={busy || picked.size === 0} onClick={() => send([...picked])}>Vote</button> : null}
        </span>
      </footer>
      {error ? <ErrorNote className="exo-dc-note" message={error} /> : null}
    </section>
  )
}

/**
 * Discord's line when a poll closes: "Erix's poll <question> has closed", then the winning answer
 * and its votes (or that there was no winner). Clicking it jumps to the poll.
 */
export function PollResultLine({ message, name }: { message: DiscordMessage; name: string }) {
  const fields = new Map((message.embeds[0]?.fields ?? []).map(f => [f.name, f.value]))
  const question = fields.get('poll_question_text') ?? 'A poll'
  const winner = fields.get('victor_answer_text')
  const votes = Number(fields.get('victor_answer_votes') ?? 0)
  const total = Number(fields.get('total_votes') ?? 0)
  const pollId = message.message_reference?.message_id
  return (
    <div className="exo-dc-msg is-system" data-msg-id={message.id}>
      <span className="exo-dc-msg-gutter" aria-hidden="true"><Icons.Poll size={16} /></span>
      <p role={pollId ? 'button' : undefined} tabIndex={pollId ? 0 : undefined} className={pollId ? 'is-clickable' : undefined}
        onClick={() => { if (pollId) window.dispatchEvent(new CustomEvent('exo-dc-jump', { detail: pollId })) }}>
        <strong>{name}</strong>'s poll <strong>{question}</strong> has closed.{' '}
        {winner && votes > 0
          ? <>{winner} won with {votes} of {total} {total === 1 ? 'vote' : 'votes'}.</>
          : <>There was no winner.</>}
      </p>
    </div>
  )
}
