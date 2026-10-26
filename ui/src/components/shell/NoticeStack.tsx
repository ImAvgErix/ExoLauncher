import { memo, useEffect, useRef, useState } from 'react'
import { Alert, Bell, Check, Close } from '../../brand/icons'
import { dismissNotice, isSticky, NOTICE_STAY_MS, renewNotices, useNotices, type ShownNotice } from '../../lib/notify'
import { cn } from '../../lib/utils'
import { Progress } from '../ui/Progress'
import { Spinner } from '../ui/Spinner'

/**
 * Every notice in Exo (lib/notify.ts), in one stack at the top right under the Bar, newest on top: large glass, its
 * glyph or picture, what happened, its buttons, a wait along its foot. One that passes goes after its time, paused
 * while the pointer or focus is on the stack; a failure, a question in buttons or a wait stays until it's answered or
 * dismissed. Pressing one that leads somewhere opens it.
 */
export const NoticeStack = memo(function NoticeStack() {
  const notices = useNotices()
  const [held, setHeld] = useState(false)
  const live = notices.filter(notice => !notice.leaving)
  // A notice that went while the pointer was on it never says it left: nothing is held with none on screen.
  useEffect(() => { if (live.length === 0) setHeld(false) }, [live.length])

  // Each passing notice goes its time after it last changed; none goes while the stack is held.
  const passing = live.filter(notice => !isSticky(notice))
  const nextDue = held || !passing.length ? null : Math.min(...passing.map(due))
  useEffect(() => {
    if (nextDue === null) return
    // A timer can fire a millisecond or two early; a notice that close is due, or nothing would re-arm and it would stay.
    const timer = window.setTimeout(() => {
      for (const notice of passing) if (due(notice) <= Date.now() + 10) dismissNotice(notice.id)
    }, Math.max(0, nextDue - Date.now()))
    return () => window.clearTimeout(timer)
  }, [nextDue, passing])

  // Coming back from a hold gives every notice its full time again.
  const wasHeld = useRef(false)
  useEffect(() => {
    if (wasHeld.current && !held) renewNotices()
    wasHeld.current = held
  }, [held])

  if (!notices.length) return null
  return (
    <section className="exo-notices" aria-label="Notifications"
      onPointerEnter={() => setHeld(true)} onPointerLeave={() => setHeld(false)}
      onFocus={() => setHeld(true)} onBlur={event => { if (!event.currentTarget.contains(event.relatedTarget)) setHeld(false) }}>
      {notices.map(notice => <NoticeCard key={notice.key} notice={notice} />)}
    </section>
  )
})

const due = (notice: ShownNotice) => notice.at + (notice.stay ?? NOTICE_STAY_MS)

function NoticeCard({ notice }: { notice: ShownNotice }) {
  const sticky = isSticky(notice)
  const close = () => { notice.onDismiss?.(); dismissNotice(notice.id) }
  const open = notice.onOpen ? () => { notice.onOpen?.(); dismissNotice(notice.id) } : null
  const glyph = notice.icon ?? <span className="exo-notice-glyph" aria-hidden>
    {notice.tone === 'bad' ? <Alert size={18} /> : notice.tone === 'good' ? <Check size={18} /> : <Bell size={18} />}
  </span>
  const words = <>
    {glyph}
    <span className="exo-notice-text">
      {notice.title ? <span className="exo-notice-head">
        <strong>{notice.title}</strong>
        {notice.count && notice.count > 1 ? <span className="exo-notice-count">{notice.count} new</span> : null}
      </span> : null}
      {notice.where ? <span className="exo-notice-where">{notice.where}</span> : null}
      <span className="exo-notice-body">{notice.text}</span>
    </span>
  </>
  return (
    <div className={cn('exo-notice-slot', notice.leaving && 'is-leaving')} inert={notice.leaving}>
      <div className={cn('exo-notice exo-glass is-panel', notice.tone && `is-${notice.tone}`, sticky && 'is-sticky')}
        role={notice.tone === 'bad' ? 'alert' : 'status'}>
        {open
          ? <button type="button" className="exo-notice-main" onClick={open}
              aria-label={`${notice.title ? `${notice.title}${notice.where ? `, ${notice.where}` : ''}: ` : ''}${notice.text}. Open.`}>{words}</button>
          : <div className="exo-notice-main">{words}</div>}
        {notice.actions?.length ? <div className="exo-notice-actions">
          {notice.actions.map(action => (
            <button key={action.label} type="button" disabled={action.disabled || action.busy}
              className={cn('exo-set-btn is-compact', action.primary && 'is-primary', action.danger && 'is-danger')}
              onClick={action.onPress}>
              {action.busy ? <Spinner label={action.label} /> : null}{action.label}
            </button>
          ))}
        </div> : null}
        {notice.lasting ? null : <button type="button" className="exo-notice-close" aria-label="Dismiss" onClick={close}>
          <Close size={14} />
        </button>}
        {notice.progress !== undefined ? <Progress edge value={notice.progress} label={notice.text} /> : null}
      </div>
    </div>
  )
}
