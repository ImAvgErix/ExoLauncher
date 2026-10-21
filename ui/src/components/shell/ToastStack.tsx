import { formatTransferPercent } from '../../lib/utils'
import type { CSSProperties } from 'react'
import { Button } from '../ui/Button'
import { Loader2 } from '../../brand/icons'
import type { MissingDependency } from '../../lib/host'
import type { AppUpdate } from './useAppUpdate'
import type { StatusTone } from './useActionStatus'

export type DepPrompt = {
  gameId: string
  action: 'play' | 'install' | 'update'
  deps: MissingDependency[]
  awaitingContinue?: boolean
}

function DependencyToast({ prompt, onSkip, onInstall, onContinue }: {
  prompt: DepPrompt
  onSkip: () => void
  onInstall: () => void
  onContinue: () => void
}) {
  const names = prompt.deps.map((d) => d.name).join(', ')
  return (
    <div className="exo-toast" role="status" aria-label="Missing runtime">
      <p className="min-w-0 flex-1 text-fg">
        {prompt.awaitingContinue ? `Waiting for ${names}` : `Needs ${names}`}
      </p>
      <div className="flex shrink-0 items-center gap-2">
        <Button type="button" variant="ghost" size="sm" className="exo-ghost-btn" onPress={onSkip}>
          Try without runtimes
        </Button>
        {prompt.awaitingContinue ? (
          <Button type="button" size="sm" className="exo-ghost-btn is-primary" onPress={onContinue}>
            Check and retry
          </Button>
        ) : (
          <Button type="button" size="sm" className="exo-ghost-btn is-primary" onPress={onInstall}>
            Install
          </Button>
        )}
      </div>
    </div>
  )
}

function UpdateToast({ update }: { update: AppUpdate }) {
  const percent = typeof update.percent === 'number' && Number.isFinite(update.percent)
    ? Math.min(100, Math.max(0, update.percent)) : null
  const label = `${update.status || 'Installing update…'}${percent == null ? '' : ` ${formatTransferPercent(percent)}%`}`
  return (
    // The banner is the status; the pill's percent below it is not live, or every tick would be read aloud.
    <div className="exo-toast">
      <div className="min-w-0 flex-1">
        <p role="status" className="font-medium text-fg">{update.banner}</p>
      </div>
      <div className="flex shrink-0 items-center gap-2">
        {!update.busy && (
          <Button type="button" variant="ghost" size="sm" className="exo-ghost-btn" onPress={update.dismiss}>
            Later
          </Button>
        )}
        <Button
          type="button"
          className={`exo-ghost-btn is-primary exo-update-action${update.busy ? ' is-active' : ''}`}
          isDisabled={update.busy}
          onPress={() => void update.install()}
        >
          {update.busy && (
            <span className="exo-action-progress" style={{ '--progress': (percent ?? 0) / 100 } as CSSProperties} aria-hidden="true" />
          )}
          <span className="exo-action-state">
            <span className="exo-action-content exo-action-idle" aria-hidden={update.busy}>
              <strong>Install update</strong>
            </span>
            <span className="exo-action-content exo-action-active" aria-hidden={!update.busy}>
              <Loader2 size={16} className="animate-spin motion-reduce:animate-none" />
              <strong>{label}</strong>
            </span>
          </span>
        </Button>
        {update.busy && (
          <>
            <span
              className="sr-only"
              role="progressbar"
              aria-label="App update progress"
              aria-valuemin={0}
              aria-valuemax={100}
              aria-valuenow={percent ?? undefined}
              aria-valuetext={label}
            />
            <span className="sr-only" role="status" aria-live="polite" aria-atomic="true">
              {update.status || 'Installing update…'}
            </span>
          </>
        )}
      </div>
    </div>
  )
}

export type ToastStackProps = {
  depPrompt: DepPrompt | null
  onSkipDeps: () => void
  onInstallDeps: () => void
  onContinueAfterDeps: () => void
  update: AppUpdate
  /** The update banner hides on the Settings room, which has its own control. */
  showUpdate: boolean
  /** A message that belongs to no open sheet. Only failures read red; a sticky one waits for Dismiss. */
  notice: { message: string; tone: StatusTone; sticky: boolean } | null
  onDismissNotice: () => void
  /** A passing shell notice (a sale, a mouse alert), shown beside the status, never over it. */
  passing?: string | null
  transferFailures?: Array<{ gameId: string; title: string; message: string }>
  onDismissTransferFailure?: (gameId: string) => void
}

const noop = () => { }

export function ToastStack({
  depPrompt,
  onSkipDeps,
  onInstallDeps,
  onContinueAfterDeps,
  update,
  showUpdate,
  notice,
  onDismissNotice,
  passing = null,
  transferFailures = [],
  onDismissTransferFailure = noop,
}: ToastStackProps) {
  return (
    <>
      <div className="exo-toast-stack">
        {transferFailures.map(failure => <div key={failure.gameId} role="alert" className="exo-toast is-bad">
          <p className="min-w-0 flex-1">{failure.title}: {failure.message}</p>
          <Button type="button" variant="ghost" size="sm" className="exo-ghost-btn" onPress={() => onDismissTransferFailure(failure.gameId)}>
            Dismiss
          </Button>
        </div>)}
        {depPrompt && (
          <DependencyToast prompt={depPrompt} onSkip={onSkipDeps} onInstall={onInstallDeps} onContinue={onContinueAfterDeps} />
        )}
        {update.banner && showUpdate && <UpdateToast update={update} />}
        {notice && (
          <div role={notice.tone === 'bad' ? 'alert' : 'status'} className={`exo-toast${notice.tone === 'bad' ? ' is-bad' : ''}`}>
            {notice.sticky ? <p className="min-w-0 flex-1">{notice.message}</p> : notice.message}
            {notice.sticky ? (
              <Button type="button" variant="ghost" size="sm" className="exo-ghost-btn" onPress={onDismissNotice}>
                Dismiss
              </Button>
            ) : null}
          </div>
        )}
        {passing && passing !== notice?.message && <div role="status" className="exo-toast">{passing}</div>}
      </div>
    </>
  )
}
