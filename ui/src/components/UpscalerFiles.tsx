import { useEffect, useState } from 'react'
import { host, type DlssRunResult } from '../lib/host'
import {
  gateReason,
  upscalerGroup,
  upscalerRows,
  upscalerVisualState,
  type UpscalerAction,
  type UpscalerRow,
  type UpscalerStatusItem,
  type UpscalerVisualState,
} from '../lib/upscalers'

type GroupAction = 'newest' | 'restore'

type RunState =
  | { kind: 'idle' }
  | { kind: 'working'; action: GroupAction }
  | { kind: 'done'; text: string }
  | { kind: 'failed'; action: GroupAction; text: string }

const Idle: RunState = { kind: 'idle' }

export type UpscalerFilesProps = {
  items?: UpscalerStatusItem[] | null
  gameId: string
  antiCheat?: boolean
  gameRunning?: boolean
  installing?: boolean
  /** Re-reads dlss.status so the version column matches disk again. */
  onRefresh: () => void
}

/**
 * Only upscalers that are actually present belong in the game overlay. Keeping
 * the unused destination matrix out of this surface makes the useful controls
 * visible without turning every game into a scrolling settings page.
 */
export function UpscalerFiles({
  items,
  gameId,
  antiCheat,
  gameRunning,
  installing,
  onRefresh,
}: UpscalerFilesProps) {
  const [run, setRun] = useState<RunState>(Idle)

  useEffect(() => {
    setRun(Idle)
  }, [gameId])

  const gate = { antiCheat, gameRunning, installing }
  const rows = upscalerRows(items, gate)
  const visibleRows = rows.filter((row) => row.present)
  const group = upscalerGroup(visibleRows, gate)
  // The native gate still refuses anti-cheat titles. That policy does not need
  // a permanent warning in the game page; transient running/install gates do.
  const visibleGate = antiCheat ? null : gateReason(gate)
  const busy = run.kind === 'working'

  const start = (action: GroupAction) => {
    setRun({ kind: 'working', action })
    const call = action === 'newest' ? host.dlssApply(gameId) : host.dlssRestore(gameId)
    void call
      .then((result) => {
        setRun(result.ok
          ? { kind: 'done', text: runText(action, result) }
          : { kind: 'failed', action, text: trimStop(result.message) || 'Could not apply' })
        onRefresh()
      })
      .catch((error: unknown) => {
        setRun({
          kind: 'failed',
          action,
          text: error instanceof Error ? trimStop(error.message) : 'Could not apply',
        })
      })
  }

  if (visibleRows.length === 0 && run.kind === 'idle') return null

  return (
    <section className="exo-upscaler-files">
      <div className="exo-upscaler-head">
        <div className="exo-upscaler-title">
          <span className="exo-section-label">Upscalers</span>
          <span className="exo-upscaler-count">
            {visibleRows.length} detected
          </span>
        </div>
        <span className="exo-upscaler-actions">
          <GroupButton
            label="Newest"
            action={group.newest}
            busy={busy && run.action === 'newest'}
            disabled={busy}
            retry={run.kind === 'failed' && run.action === 'newest'}
            count={group.updatable}
            hideBlockedReason={antiCheat === true}
            onClick={() => start('newest')}
          />
          <GroupButton
            label="Restore"
            action={group.restore}
            busy={busy && run.action === 'restore'}
            disabled={busy}
            retry={run.kind === 'failed' && run.action === 'restore'}
            count={group.restorable}
            hideBlockedReason={antiCheat === true}
            onClick={() => start('restore')}
          />
        </span>
      </div>

      {run.kind !== 'idle' && (
        <p className={runClass(run)} aria-live="polite">
          {run.kind === 'working' ? 'Working…' : run.text}
        </p>
      )}

      {visibleGate && <p className="exo-upscaler-gate">{visibleGate}</p>}
      <ul className="exo-upscaler-list">
        {visibleRows.map((row) => {
          const state = upscalerVisualState(row)
          return (
            <li
              key={row.fileName}
              className="exo-upscaler-row"
              aria-label={rowLabel(row, state)}
            >
              <span className="exo-upscaler-id">
                <span className="exo-upscaler-name">{row.label}</span>
                {row.api && <span className="exo-upscaler-api">{row.api}</span>}
              </span>
              <span className="exo-upscaler-meta">
                <span className={`exo-upscaler-ver is-${state}`}>
                  <span className={`exo-upscaler-signal is-${state}`} aria-hidden="true" />
                  {row.version}
                </span>
              </span>
            </li>
          )
        })}
      </ul>
    </section>
  )
}

function GroupButton({
  label,
  action,
  busy,
  disabled,
  retry,
  count,
  hideBlockedReason,
  onClick,
}: {
  label: string
  action: UpscalerAction
  busy: boolean
  disabled: boolean
  retry: boolean
  count: number
  hideBlockedReason: boolean
  onClick: () => void
}) {
  const off = disabled || (!action.enabled && !retry)
  return (
    <button
      type="button"
      className="exo-game-tool exo-upscaler-btn"
      disabled={off}
      aria-label={retry
        ? `Retry ${label.toLowerCase()} after the last failure`
        : action.reason
        ? hideBlockedReason
          ? `${label} all upscalers unavailable`
          : `${label} all upscalers: ${action.reason}`
        : `${label} ${count} upscaler ${count === 1 ? 'file' : 'files'}`}
      onClick={onClick}
    >
      {busy ? 'Working' : label}
    </button>
  )
}

function runClass(run: RunState): string {
  if (run.kind === 'done') return 'exo-upscaler-run is-good'
  if (run.kind === 'failed') return 'exo-upscaler-run is-bad'
  return 'exo-upscaler-run'
}

function rowLabel(row: UpscalerRow, state: UpscalerVisualState): string {
  const who = row.api ? `${row.label} ${row.api}` : row.label
  const status = state === 'current'
    ? 'current'
    : state === 'outdated'
      ? 'update available'
      : 'newest version unavailable'
  return `${who}: ${row.version}, ${status}`
}

/** Counts, never a bare "Done" — the user asked what actually changed. */
function runText(action: GroupAction, result: DlssRunResult): string {
  const changed = result.updated ?? 0
  const skipped = result.skipped ?? 0
  const verb = action === 'newest' ? 'Updated' : 'Restored'
  const parts: string[] = []
  if (changed > 0) parts.push(`${verb} ${changed}`)
  if (skipped > 0) parts.push(`${skipped} left alone`)
  if (parts.length === 0) return trimStop(result.message) || 'Nothing to do'
  return parts.join(' · ')
}

function trimStop(text?: string | null): string {
  return (text ?? '').trim().replace(/\.$/, '')
}
