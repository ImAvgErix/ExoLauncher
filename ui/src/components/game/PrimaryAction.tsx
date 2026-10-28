import { Button } from '../ui/Button'
import { confirm } from '../ui/Dialog'
import { WorkingWord } from '../ui/WorkingWord'
import { Download, ExternalLink, Play, Stop } from '../../brand/icons'
import { host, primaryCtaLabel, type Game, type InstallProgress, type PrimaryAction as ActionKind } from '../../lib/host'
import { buyLabel } from '../../lib/social'
import { installVerb, transferMetaLine, transferPercent } from '../../lib/utils'

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

/** The button's word while a download runs: the work alone, with moving dots; how far it is, Downloads says. */
function activeLabelFor(progress: InstallProgress | null, action: ActionKind): string {
  if (progress) return installVerb(progress, action === 'update' ? 'update' : 'download')
  if (action === 'update') return 'Updating'
  if (action === 'install') return 'Installing'
  return 'Working'
}

function stateLabelFor(
  selected: Game,
  busy: boolean,
  statusMsg: string | null,
  active: string | null,
  cta: string,
): string {
  if (active) return active
  if (statusMsg && /clos/i.test(statusMsg)) return 'Closing'
  if (selected.canStop) return 'Running'
  return busy ? 'Preparing' : cta
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
  const verb = progress ? installVerb(progress, action === 'update' ? 'update' : 'download') : ''
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

type PillInput = Pick<PrimaryActionProps, 'selected' | 'action' | 'buyUrl' | 'busy' | 'statusMsg' | 'progress'> & {
  waiting: boolean
}

function pillView({ selected, action, buyUrl, busy, statusMsg, progress, waiting }: PillInput) {
  const cta = waiting ? 'Queued' : ctaLabelFor(selected, action, buyUrl)
  const inFlight = busy || !!progress
  const stateLabel = stateLabelFor(selected, busy, statusMsg, progress ? activeLabelFor(progress, action) : null, cta)
  return {
    cta,
    inFlight,
    stateLabel,
    disabled: !waiting && isDisabled(selected, action, buyUrl, busy, progress),
    reason: inFlight || waiting ? null : unavailableReason(selected, action, buyUrl),
    ariaLabel: waiting ? `${cta}: ${selected.title}` : ariaLabelFor(selected, progress, busy, action, cta),
  }
}

/**
 * Play | Install | Update | Stop | Buy — one pill. A download or update carries the Exo bar along its foot; a launch,
 * which has no measure, says Preparing with its dots, then the pill turns to Stop.
 */
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
  const { cta, inFlight, stateLabel, disabled, reason, ariaLabel } =
    pillView({ selected, action, buyUrl, busy, statusMsg, progress, waiting })

  // The pill doubles as the download's Cancel and the queue's Remove: one stray click must not throw either away.
  async function activate() {
    if (progress) {
      if (!progress.canCancel) return
      const update = action === 'update'
      const sure = await confirm({
        title: `Stop ${update ? 'updating' : 'downloading'} ${selected.title}?`,
        body: update ? 'The update stops where it is. You can update it again later.' : 'The download stops where it is. You can install it again later.',
        action: update ? 'Stop update' : 'Stop download',
        cancel: update ? 'Keep updating' : 'Keep downloading',
        danger: true,
      })
      if (sure === true) onCancel()
      return
    }
    if (waiting) {
      const sure = await confirm({ title: 'Remove from the queue?', body: `${selected.title} won't download. Install it again to queue it.`, action: 'Remove' })
      if (sure === true) onDequeue?.()
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
        onPress={() => void activate()}
        className={`exo-play exo-primary-action${inFlight ? ' is-active' : ''}`}
        aria-label={ariaLabel}
      >
        <span className="exo-action-state">
          <span className="exo-action-content exo-action-idle" aria-hidden={inFlight}>
            {waiting ? null : <IdleIcon selected={selected} action={action} buyUrl={buyUrl} />}
            <span className="exo-action-copy">{cta}</span>
          </span>
          <span className="exo-action-content exo-action-active" aria-hidden={!inFlight}>
            <span className="exo-action-copy">
              <span>
                <WorkingWord word={stateLabel} />
              </span>
            </span>
          </span>
        </span>
      </Button>
      {reason ? <p className="exo-action-reason">{reason}</p> : null}
      {/* A button's insides are not read as their own parts: screen readers get the download's figures here. */}
      {progress && (
        <span
          className="sr-only"
          role="progressbar"
          aria-label={`${selected.title} progress`}
          aria-valuemin={0}
          aria-valuemax={100}
          aria-valuenow={percent ?? undefined}
          aria-valuetext={[stateLabel, transferMetaLine(progress, false)].filter(Boolean).join(' · ')}
        />
      )}
    </>
  )
}
