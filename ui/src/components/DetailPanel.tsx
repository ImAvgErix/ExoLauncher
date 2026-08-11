import {
  ArrowLeft,
  Check,
  Download,
  FolderOpen,
  Loader2,
  Play,
  Square,
  Star,
  Trash2,
} from 'lucide-react'
import { useEffect, useState, type CSSProperties } from 'react'
import {
  host,
  resolvePrimaryAction,
  type Game,
  type GameAchievementsResponse,
  type InstallProgress,
} from '../lib/host'
import { formatPlaytime, formatSize, formatSpeed, storeLabel } from '../lib/utils'
import { FadeIn } from '../motion'
import { CoverArt, coverBg } from './CoverArt'

export function DetailPanel({
  selected,
  busy,
  statusMsg,
  progress,
  onPrimary,
  onStop,
  onCancel,
  onClose,
  onToggleFavorite,
  onStatus,
  onUninstalled,
  closeDisabled = false,
}: {
  selected: Game
  busy: boolean
  statusMsg: string | null
  progress: InstallProgress | null
  onPrimary: () => void
  onStop: () => void
  onCancel: () => void
  onClose: () => void
  onToggleFavorite: (id: string) => void
  onStatus: (msg: string | null) => void
  onUninstalled: () => void
  closeDisabled?: boolean
}) {
  const [uninstalling, setUninstalling] = useState(false)
  const [achievementData, setAchievementData] = useState<GameAchievementsResponse | null>(null)
  const [achievementRefreshing, setAchievementRefreshing] = useState(false)
  const action = resolvePrimaryAction(selected)
  const buyUrl = storeBuyUrl(selected)
  const selectedProgress = progress?.isActive && progress.gameId === selected.id ? progress : null
  const progressPercent = selectedProgress?.percent == null
    ? null
    : Math.max(0, Math.min(100, selectedProgress.percent))
  const activeLabel = action === 'update'
    ? 'Updating…'
    : action === 'install'
      ? 'Installing…'
      : 'Working…'
  const actionInFlight = busy || !!selectedProgress
  const actionStateLabel = selectedProgress ? activeLabel : selected.canStop ? 'Closing…' : 'Preparing…'
  const ctaLabel =
    selected.canStop
      ? 'Stop'
      : action === 'play'
      ? 'Play'
      : action === 'install'
        ? 'Install'
        : action === 'update'
          ? 'Update'
          : buyUrl
            ? buyLabel(selected.store)
            : selected.installed
              ? 'Unavailable'
              : 'Not installed'

  useEffect(() => {
    let active = true
    setAchievementData(null)
    setAchievementRefreshing(true)
    const loadAchievements = async () => {
      try {
        // A persisted baseline can belong to the account that was active on a
        // previous run. Display only a fresh, account-scoped provider result.
        const result = await host.refreshAchievements(selected.id)
        if (active) setAchievementData(result)
      } catch {
        if (active) setAchievementData({ ok: false, message: 'Achievement data is unavailable.' })
      } finally {
        if (active) setAchievementRefreshing(false)
      }
    }
    void loadAchievements()
    return () => { active = false }
  }, [selected.id, selected.launchTarget, selected.store])

  return (
    <aside className="flex h-full w-full flex-col" aria-label={`${selected.title} details`}>
      {!closeDisabled && <div className="flex items-center gap-2 border-b border-line-soft px-3 py-2.5 md:hidden">
        <button
          type="button"
          onClick={onClose}
          className="inline-flex items-center gap-1.5 rounded-full px-2 py-1.5 text-sm text-muted hover:bg-hover hover:text-fg"
        >
          <ArrowLeft className="h-4 w-4" />
          Library
        </button>
      </div>}

      {/* Full 2:3 poster — same crop as library cards, no fade overlay. */}
      <div className="shrink-0 px-3.5 pt-3.5">
        <div
          className="exo-detail-cover relative overflow-hidden"
          style={{ background: coverBg(selected) }}
        >
          <CoverArt game={selected} className="absolute inset-0 h-full w-full" large />
        </div>
      </div>

      <div className="shrink-0 px-4 pt-3 pb-1">
        <div className="flex items-start justify-between gap-2">
          <div className="min-w-0">
            <h2 className="line-clamp-2 text-[15px] font-semibold leading-snug tracking-tight text-fg">
              {selected.title}
            </h2>
            <p className="mt-0.5 text-[11px] text-muted">
              {storeLabel(selected.store)}
            </p>
          </div>
          {selected.installed && !selected.isAddPortable && (
            <button
              type="button"
              className="exo-titlebar-button shrink-0"
              title={selected.isFavorite ? 'Unpin' : 'Pin'}
              aria-label={selected.isFavorite ? `Unpin ${selected.title}` : `Pin ${selected.title}`}
              aria-pressed={selected.isFavorite}
              onClick={() => onToggleFavorite(selected.id)}
            >
              <Star
                size={16}
                className={selected.isFavorite ? 'fill-current text-fg' : 'text-muted'}
              />
            </button>
          )}
        </div>
      </div>

      <div className="flex min-h-0 flex-1 flex-col gap-3 overflow-y-auto px-4 py-3 md:overflow-hidden">
        <button
          type="button"
          disabled={
            (busy && !selectedProgress) ||
            (!selected.canStop && !selectedProgress && action === 'none' && !buyUrl) ||
            (!!selectedProgress && !selectedProgress.canCancel)
          }
          onClick={(e) => {
            e.preventDefault()
            if (selectedProgress) {
              if (selectedProgress.canCancel) onCancel()
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
          }}
          className={`exo-cta exo-primary-action w-full${actionInFlight ? ' is-active' : ''}`}
          aria-label={selectedProgress && selectedProgress.canCancel
            ? `Cancel ${activeLabel.replace('…', '').toLocaleLowerCase()} ${selected.title}`
            : selectedProgress
              ? `${activeLabel.replace('…', '')} ${selected.title}`
              : busy
                ? `${selected.canStop ? 'Closing' : 'Preparing'} ${selected.title}`
                : ctaLabel}
          title={selectedProgress?.canCancel ? 'Cancel' : undefined}
        >
          {selectedProgress && progressPercent != null && (
            <span
              className="exo-action-progress"
              style={{ '--progress': progressPercent / 100 } as CSSProperties}
              aria-hidden="true"
            />
          )}
          <span className="exo-action-state">
            <span className="exo-action-content exo-action-idle" aria-hidden={actionInFlight}>
              {selected.canStop ? (
                <Square className="h-4 w-4 shrink-0 fill-current" />
              ) : action === 'install' || action === 'update' || (action === 'none' && buyUrl) ? (
                <Download className="h-4 w-4 shrink-0" />
              ) : (
                <Play className="h-4 w-4 shrink-0 fill-current" />
              )}
              <span className="exo-action-copy"><strong>{ctaLabel}</strong></span>
            </span>
            <span className="exo-action-content exo-action-active" aria-hidden={!actionInFlight}>
              <Loader2 className="h-4 w-4 shrink-0 animate-spin" />
              <span className="exo-action-copy">
                <strong>{actionStateLabel}</strong>
                {selectedProgress && (
                  <small>
                    {[selectedProgress.status || selectedProgress.phase,
                      progressPercent == null ? null : `${Math.round(progressPercent)}%`,
                      selectedProgress.bytesPerSecond == null ? null : formatSpeed(selectedProgress.bytesPerSecond),
                    ].filter(Boolean).join(' · ')}
                  </small>
                )}
              </span>
            </span>
          </span>
          {selectedProgress && progressPercent != null && (
            <span
              className="sr-only"
              role="progressbar"
              aria-label={`${selected.title} progress`}
              aria-valuemin={0}
              aria-valuemax={100}
              aria-valuenow={Math.round(progressPercent)}
            />
          )}
        </button>

        {selectedProgress && (
          <span className="sr-only" role="status" aria-live="polite" aria-atomic="true">
            {[activeLabel, selectedProgress.status || selectedProgress.phase,
              progressPercent == null ? null : `${Math.round(progressPercent)}%`,
              selectedProgress.bytesPerSecond == null ? null : formatSpeed(selectedProgress.bytesPerSecond),
            ].filter(Boolean).join(' · ')}
          </span>
        )}

        {busy && !selectedProgress && (
          <span className="sr-only" role="status" aria-live="polite">
            {selected.canStop ? 'Closing' : 'Preparing'} {selected.title}
          </span>
        )}

        {statusMsg && !selectedProgress && !busy && (
          <FadeIn>
            <div className="exo-action-message" aria-live="polite">
              <div className="flex items-center gap-2 text-xs">
                {statusMsg === 'Running' ? (
                  <Check className="h-3.5 w-3.5 shrink-0 text-good" />
                ) : null}
                <span className="min-w-0 flex-1 text-fg">{statusMsg}</span>
              </div>
            </div>
          </FadeIn>
        )}

        {selected.installed && !selected.isAddPortable && (
          <div className="grid grid-cols-2 gap-2">
            <button
              type="button"
              className="exo-ghost-btn w-full justify-center"
              onClick={() =>
                void host.openFolder(selected.id).then((r) => {
                  if (!r.ok) onStatus(r.message ?? 'Folder not found')
                })
              }
            >
              <FolderOpen className="h-3.5 w-3.5" />
              Folder
            </button>
            <button
              type="button"
              disabled={uninstalling || busy || !!progress?.isActive || !!selected.canStop}
              className="exo-ghost-btn w-full justify-center"
              onClick={() => {
                setUninstalling(true)
                onStatus(`Removing ${selected.title}…`)
                void host.uninstall(selected.id)
                  .then((r) => {
                  onStatus(r.message ?? (r.ok ? 'Uninstalled' : 'Uninstall failed'))
                  if (r.ok) onUninstalled()
                  })
                  .catch((error: unknown) => {
                    onStatus(error instanceof Error ? error.message : 'Uninstall failed')
                  })
                  .finally(() => setUninstalling(false))
              }}
            >
              {uninstalling ? (
                <Loader2 className="h-3.5 w-3.5 animate-spin" />
              ) : (
                <Trash2 className="h-3.5 w-3.5" />
              )}
              {uninstalling ? 'Removing…' : 'Uninstall'}
            </button>
          </div>
        )}

        <div className="space-y-3 text-sm">
          <Row
            label="Playtime"
            value={formatPlaytime(selected.playtimeMinutes, selected.lastPlayedUtc)}
          />
          <Row
            label="Achievements"
            value={achievementRefreshing
              ? 'Updating…'
              : achievementData?.summary
              ? `${achievementData.summary.unlocked} / ${achievementData.summary.total}`
              : achievementData?.coverage === 'unsupported'
                ? 'Not supported'
                : achievementData
                  ? 'Unavailable'
                  : 'Checking…'}
          />
          <Row label="Size" value={formatSize(selected.sizeBytes)} />
        </div>

        {!closeDisabled && <button
          type="button"
          onClick={onClose}
          className="mt-auto hidden text-left text-xs text-fg-subtle hover:text-fg md:block"
        >
          Close
        </button>}
      </div>
    </aside>
  )
}

function Row({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex items-center justify-between border-b border-line-soft/80 pb-3 last:border-0 last:pb-0">
      <span className="text-fg-subtle">{label}</span>
      <span className="tabular-nums text-fg">{value}</span>
    </div>
  )
}

/** Buy via store client / browser — Exo does not host checkout. */
function storeBuyUrl(game: Game): string | null {
  if (game.installed || game.canInstall) return null
  const target = (game.launchTarget || '').trim()
  // Steam: open the desktop client's store page (not the browser).
  if (game.store === 'steam' && /^\d+$/.test(target))
    return `steam://store/${target}`
  if (game.store === 'gog' && target)
    return `https://www.gog.com/en/game/${encodeURIComponent(target)}`
  if (game.id.startsWith('steam:')) {
    const id = game.id.slice('steam:'.length)
    if (/^\d+$/.test(id)) return `steam://store/${id}`
  }
  return null
}

function buyLabel(store: string): string {
  if (store === 'steam') return 'Buy on Steam'
  if (store === 'gog') return 'Buy on GOG'
  return 'Buy in browser'
}
