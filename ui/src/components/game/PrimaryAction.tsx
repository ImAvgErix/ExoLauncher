import { useEffect, useRef, useState, type CSSProperties } from 'react'
import { Button } from '../ui/Button'
import { Download, ExternalLink, Loader2, Play, Stop } from '../../brand/icons'
import { host, primaryCtaLabel, type Game, type InstallProgress, type PrimaryAction as ActionKind } from '../../lib/host'
import { buyLabel } from '../../lib/social'
import { formatSpeed, installProgressLabel, transferMetaLine, transferPercent, formatTransferPercent } from '../../lib/utils'

export type PrimaryActionProps = {
  selected: Game
  action: ActionKind
  buyUrl: string | null
  busy: boolean
  statusMsg: string | null
  progress: InstallProgress | null
  /** Waiting in Exo's download queue behind another job. */
  queued?: boolean
  onPrimary: () => void
  onStop: () => void
  onCancel: () => void
  onDequeue?: () => void
}

function ctaLabelFor(selected: Game, action: ActionKind, buyUrl: string | null): string {
  if (action !== 'none' || !buyUrl) return primaryCtaLabel(selected, action)
  return selected.entitlementState === 'notOwned' ? 'Buy again' : buyLabel(selected.store)
}

function activeLabelFor(progress: InstallProgress | null, action: ActionKind): string {
  if (progress) return installProgressLabel(progress, action === 'update' ? 'update' : 'download')
  if (action === 'update') return 'Updating…'
  if (action === 'install') return 'Installing…'
  return 'Working…'
}

function stateLabelFor(
  selected: Game,
  busy: boolean,
  statusMsg: string | null,
  active: string | null,
  cta: string,
): string {
  if (active) return active
  if (statusMsg && /clos/i.test(statusMsg)) return 'Closing…'
  if (selected.canStop) return 'Running…'
  return busy ? 'Preparing…' : cta
}

/**
 * The pill's name holds still while bytes tick: a focused control that renames
 * itself every second is read aloud every second. The progressbar beside it
 * carries the figures.
 */
function ariaLabelFor(
  selected: Game,
  progress: InstallProgress | null,
  busy: boolean,
  action: ActionKind,
  cta: string,
): string {
  const verb = progress
    ? installProgressLabel({ phase: progress.phase, status: progress.status }, action === 'update' ? 'update' : 'download').replace('…', '')
    : ''
  if (progress?.canCancel) return `Cancel ${verb.toLocaleLowerCase()} ${selected.title}`
  if (progress) return `${verb} ${selected.title}`
  if (busy) return `${selected.canStop ? 'Closing' : 'Preparing'} ${selected.title}`
  return cta
}

function isDisabled(selected: Game, action: ActionKind, buyUrl: string | null, busy: boolean, progress: InstallProgress | null): boolean {
  if (progress) return !progress.canCancel
  if (busy) return true
  return !selected.canStop && action === 'none' && !buyUrl
}

/** Why an owned title has no action, in the user's words. Null when the pill does something. */
function unavailableReason(selected: Game, action: ActionKind, buyUrl: string | null): string | null {
  if (selected.canStop || action !== 'none' || buyUrl) return null
  if (selected.entitlementState === 'unverified') return 'Exo could not confirm you own this game. Open it from its store app.'
  if (selected.installed) return 'Exo cannot launch this copy. Open it from its store app.'
  return 'Exo cannot install this game yet. Install it from its store app.'
}

function IdleIcon({ selected, action, buyUrl }: Pick<PrimaryActionProps, 'selected' | 'action' | 'buyUrl'>) {
  if (selected.canStop) return <Stop size={16} className="shrink-0" />
  if (action === 'none' && buyUrl) return <ExternalLink size={16} className="shrink-0" />
  if (action === 'install') return <Download size={16} className="shrink-0" />
  if (action === 'update') return null
  return <Play size={16} className="shrink-0" />
}

/**
 * The pill doubles as the download's Cancel and the queue's Remove. One stray
 * click must not throw away a large download, so the first press only asks.
 */
function useSecondPress(enabled: boolean, resetKey: string) {
  const [armed, setArmed] = useState(false)
  const disarm = useRef<number | undefined>(undefined)
  useEffect(() => {
    window.clearTimeout(disarm.current)
    setArmed(false)
  }, [enabled, resetKey])
  useEffect(() => () => window.clearTimeout(disarm.current), [])
  const asking = armed && enabled
  function confirmThen(run: () => void) {
    window.clearTimeout(disarm.current)
    setArmed(!asking)
    if (asking) run()
    else disarm.current = window.setTimeout(() => setArmed(false), 4000)
  }
  return { asking, confirmThen }
}

type PillInput = Pick<PrimaryActionProps, 'selected' | 'action' | 'buyUrl' | 'busy' | 'statusMsg' | 'progress'> & {
  percent: number | null
  waiting: boolean
  asking: boolean
}

function pillView({ selected, action, buyUrl, busy, statusMsg, progress, percent, waiting, asking }: PillInput) {
  const cta = waiting
    ? asking ? 'Press again to remove' : 'Queued'
    : ctaLabelFor(selected, action, buyUrl)
  const inFlight = busy || !!progress
  const stateLabel = stateLabelFor(selected, busy, statusMsg, progress ? activeLabelFor(progress, action) : null, cta)
  return {
    cta,
    inFlight,
    activeText: asking ? 'Press again to cancel'
      : percent == null ? stateLabel : `${stateLabel} ${formatTransferPercent(percent)}%`,
    disabled: !waiting && isDisabled(selected, action, buyUrl, busy, progress),
    reason: inFlight || waiting ? null : unavailableReason(selected, action, buyUrl),
    ariaLabel: waiting ? `${cta}: ${selected.title}`
      : asking ? `Press again to cancel: ${selected.title}`
      : ariaLabelFor(selected, progress, busy, action, cta),
  }
}

/** Play | Install | Update | Stop | Buy — one pill, progress painted inside it. */
export function PrimaryAction({
  selected,
  action,
  buyUrl,
  busy,
  statusMsg,
  progress,
  queued = false,
  onPrimary,
  onStop,
  onCancel,
  onDequeue,
}: PrimaryActionProps) {
  const percent = transferPercent(progress)
  const waiting = queued && !progress && !busy && !!onDequeue
  const { asking, confirmThen } = useSecondPress(!!progress?.canCancel || waiting, selected.id)
  const { cta, inFlight, activeText, disabled, reason, ariaLabel } =
    pillView({ selected, action, buyUrl, busy, statusMsg, progress, percent, waiting, asking })

  function activate() {
    if (progress) {
      if (progress.canCancel) confirmThen(onCancel)
      return
    }
    if (waiting) {
      confirmThen(onDequeue)
      return
    }
    if (selected.canStop) {
      onStop()
      return
    }
    if (action === 'none' && buyUrl) {
      void host.openUrl(buyUrl)
      return
    }
    onPrimary()
  }

  return (
    <>
      <Button
        type="button"
        isDisabled={disabled}
        onPress={activate}
        className={`exo-play exo-primary-action${inFlight ? ' is-active' : ''}`}
        aria-label={ariaLabel}
      >
        {progress && (
          <span
            className={`exo-action-progress${percent == null ? ' is-unknown' : ''}`}
            style={percent == null ? undefined : { '--progress': percent / 100 } as CSSProperties}
            aria-hidden="true"
          />
        )}
        <span className="exo-action-state">
          <span className="exo-action-content exo-action-idle" aria-hidden={inFlight}>
            {waiting ? null : <IdleIcon selected={selected} action={action} buyUrl={buyUrl} />}
            <span className="exo-action-copy">{cta}</span>
          </span>
          <span className="exo-action-content exo-action-active" aria-hidden={!inFlight}>
            <Loader2 size={16} className="shrink-0 animate-spin motion-reduce:animate-none" />
            <span className="exo-action-copy">
              <span>
                {activeText}
              </span>
              {progress && <small>{transferMetaLine(progress, false)}</small>}
            </span>
          </span>
        </span>
      </Button>
      {reason ? <p className="exo-action-reason">{reason}</p> : null}
      {progress && (
        <span
          className="sr-only"
          role="progressbar"
          aria-label={`${selected.title} progress`}
          aria-valuemin={0}
          aria-valuemax={100}
          aria-valuenow={percent ?? undefined}
          aria-valuetext={[
            progress.status || progress.phase,
            percent == null ? null : `${formatTransferPercent(percent)}%`,
            progress.bytesPerSecond == null ? null : formatSpeed(progress.bytesPerSecond),
          ].filter(Boolean).join(' · ')}
        />
      )}
    </>
  )
}
