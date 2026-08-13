import {
  Check,
  Close,
  Download,
  FolderOpen,
  Loader2,
  Play,
  Star,
  StarFilled,
  Stop,
  Trash,
} from '../brand/icons'
import { useEffect, useState, type CSSProperties } from 'react'
import {
  host,
  onHostEvent,
  primaryCtaLabel,
  resolvePrimaryAction,
  type Game,
  type GameAchievementsResponse,
  type InstallProgress,
} from '../lib/host'
import { formatPlaytime, formatRelativeLastPlayed, formatSize, formatSpeed, storeLabel, visibleInstallPercent } from '../lib/utils'
import { FadeIn } from '../motion'
import { achievementCache, isUsefulAchievement } from '../lib/achievements'
import { CoverArt } from './CoverArt'

function progressForGame(progress: InstallProgress | null, game: Game): InstallProgress | null {
  if (!progress?.isActive || !progress.gameId) return null
  if (progress.gameId === game.id) return progress
  if (game.selectedVariantId && progress.gameId === game.selectedVariantId) return progress
  if (game.variants?.some((variant) => variant.id === progress.gameId)) return progress
  const app = progress.gameId.match(/^steam:(\d+)/i)?.[1]
  const own = game.id.match(/^steam:(\d+)/i)?.[1]
  if (app && own && app === own) return progress
  return null
}

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
  onSelectSource,
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
  /** Switches the exact store entry behind a grouped library card. */
  onSelectSource?: (id: string) => void
  onStatus: (msg: string | null, sticky?: boolean) => void
  onUninstalled: () => void
  closeDisabled?: boolean
}) {
  const [uninstalling, setUninstalling] = useState(false)
  const [achievementData, setAchievementData] = useState<GameAchievementsResponse | null>(null)
  const action = resolvePrimaryAction(selected)
  const buyUrl = storeBuyUrl(selected)
  const selectedProgress = progressForGame(progress, selected)
  const progressPercent = visibleInstallPercent(selectedProgress?.percent)
  const activeLabel = action === 'update'
    ? 'Updating…'
    : action === 'install'
      ? 'Installing…'
      : 'Working…'
  const actionInFlight = busy || !!selectedProgress
  const actionStateLabel = selectedProgress
    ? activeLabel
    : statusMsg && /clos/i.test(statusMsg)
      ? 'Closing…'
      : selected.canStop
        ? 'Running…'
        : 'Preparing…'
  const ctaLabel =
    selected.canStop
      ? 'Stop'
      : action === 'none' && buyUrl
        ? buyLabel(selected.store)
        : action === 'none'
          ? selected.installed
            ? 'Unavailable'
            : 'Not installed'
          : primaryCtaLabel(selected, action)
  const achievement = achievementDisplay(achievementData)

  useEffect(() => {
    let active = true
    const requestId = selected.id
    const cached = achievementCache.get(requestId) ?? null
    setAchievementData(cached)
    const loadAchievements = async () => {
      try {
        try {
          const hostCached = await host.getAchievements(requestId)
          if (
            active &&
            hostCached?.gameId === requestId &&
            (isUsefulAchievement(hostCached) || hostCached.coverage === 'unsupported' || hostCached.ok)
          ) {
            achievementCache.set(requestId, hostCached)
            setAchievementData(hostCached)
          }
        } catch {
          /* ignore cache miss */
        }
        const result = await host.refreshAchievements(requestId)
        if (!active || (result?.gameId && result.gameId !== requestId)) return
        const refreshedUseful = isUsefulAchievement(result)
        if (refreshedUseful) {
          achievementCache.set(requestId, result)
          setAchievementData(result)
        } else if (result?.coverage === 'unsupported' || result?.ok) {
          achievementCache.set(requestId, result)
          setAchievementData((prev) =>
            prev?.ok &&
            prev.gameId === requestId &&
            isUsefulAchievement(prev)
              ? prev
              : result ?? { ok: false, gameId: requestId, message: 'Achievement data is unavailable.' },
          )
        } else {
          setAchievementData((prev) =>
            prev?.ok &&
            prev.gameId === requestId &&
            isUsefulAchievement(prev)
              ? prev
              : result ?? { ok: false, gameId: requestId, message: 'Achievement data is unavailable.' },
          )
        }
      } catch {
        if (active) {
          setAchievementData((prev) =>
            prev?.ok && prev.gameId === requestId && prev.summary
              ? prev
              : { ok: false, gameId: requestId, message: 'Achievement data is unavailable.' },
          )
        }
      }
    }
    void loadAchievements()
    return () => { active = false }
  }, [selected.id])

  useEffect(() => {
    return onHostEvent('achievements.updated', (data) => {
      const snap = data as GameAchievementsResponse
      if (!snap?.ok || !snap.gameId) return
      if (snap.gameId !== selected.id) return
      if (isUsefulAchievement(snap)) {
        achievementCache.set(snap.gameId, snap)
        setAchievementData(snap)
      }
    })
  }, [selected.id])

  return (
    <div className="exo-game-page">
      <div className="exo-game-page-inner">
        {!closeDisabled && (
          <button
            type="button"
            onClick={onClose}
            className="exo-game-close"
            aria-label="Close details"
          >
            <Close size={16} />
          </button>
        )}
        <div className="exo-game-page-body">
        <div className="exo-game-poster-col">
          <div className="exo-detail-cover exo-cover group relative overflow-hidden">
            <CoverArt game={selected} className="absolute inset-0 h-full w-full" large />
          </div>
        </div>

        <FadeIn delay={0.08} className="exo-game-info">
          <p className="exo-game-kicker">
            {storeLabel(selected.store).toUpperCase()}
          </p>
          {selected.variants && selected.variants.length > 1 && onSelectSource && (
            <div
              className="mt-2 flex flex-wrap gap-1"
              role="group"
              aria-label="Choose game source"
            >
              {selected.variants.map((variant) => {
                const active = variant.id === selected.id
                return (
                  <button
                    key={variant.id}
                    type="button"
                    disabled={busy}
                    onClick={() => onSelectSource(variant.id)}
                    aria-pressed={active}
                    className={`rounded-full border px-2 py-1 text-[10px] font-medium transition-colors disabled:cursor-not-allowed disabled:opacity-50 ${
                      active
                        ? 'border-fg bg-fg text-black'
                        : 'border-line-soft bg-black text-muted hover:border-muted hover:text-fg'
                    }`}
                  >
                    {variant.updateAvailable
                      ? `${storeLabel(variant.store)} · Update`
                      : storeLabel(variant.store)}
                  </button>
                )
              })}
            </div>
          )}

          <div className="mt-2 flex items-start justify-between gap-3">
            <h2 className="exo-game-title">{selected.title}</h2>
            {selected.installed && !selected.isAddPortable && (
              <button
                type="button"
                className="exo-titlebar-button shrink-0"
                title={selected.isFavorite ? 'Unpin' : 'Pin'}
                aria-label={selected.isFavorite ? `Unpin ${selected.title}` : `Pin ${selected.title}`}
                aria-pressed={selected.isFavorite}
                onClick={() => onToggleFavorite(selected.id)}
              >
                {selected.isFavorite ? (
                  <StarFilled size={16} className="text-fg" />
                ) : (
                  <Star size={16} className="text-muted" />
                )}
              </button>
            )}
          </div>

          <div className="exo-game-stats">
            <Stat label="Time played" value={formatPlaytime(bestPlaytimeMinutes(selected), selected.lastPlayedUtc)} />
            <Stat label="Last launched" value={formatRelativeLastPlayed(selected.lastPlayedUtc)} />
            <Stat label="Size" value={formatSize(selected.sizeBytes)} />
            <div className="exo-game-stat">
              <span className="exo-game-stat-label">Achievements</span>
              <span className="exo-game-stat-value tabular-nums">{achievement.text}</span>
              {achievement.percent != null && (
                <span className="exo-ach-bar" aria-hidden>
                  <span style={{ width: `${achievement.percent}%` } as CSSProperties} />
                </span>
              )}
            </div>
          </div>

          <div className="mt-6 flex flex-wrap items-center gap-2">
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
              className={`exo-cta exo-primary-action min-w-[168px]${actionInFlight ? ' is-active' : ''}`}
              aria-label={selectedProgress && selectedProgress.canCancel
                ? `Cancel ${activeLabel.replace('…', '').toLocaleLowerCase()} ${selected.title}${progressPercent == null ? '' : `, ${Math.round(progressPercent)}%`}`
                : selectedProgress
                  ? `${activeLabel.replace('…', '')} ${selected.title}${progressPercent == null ? '' : `, ${Math.round(progressPercent)}%`}`
                  : busy
                    ? `${selected.canStop ? 'Closing' : 'Preparing'} ${selected.title}`
                    : ctaLabel}
              title={selectedProgress?.canCancel ? 'Cancel' : undefined}
            >
              {selectedProgress && (
                <span
                  className={`exo-action-progress${progressPercent == null ? ' is-unknown' : ''}`}
                  style={progressPercent == null ? undefined : { '--progress': progressPercent / 100 } as CSSProperties}
                  aria-hidden="true"
                />
              )}
              <span className="exo-action-state">
                <span className="exo-action-content exo-action-idle" aria-hidden={actionInFlight}>
                  {selected.canStop ? (
                    <Stop size={16} className="shrink-0" />
                  ) : action === 'install' || action === 'update' || (action === 'none' && buyUrl) ? (
                    <Download size={16} className="shrink-0" />
                  ) : (
                    <Play size={16} className="shrink-0" />
                  )}
                  <span className="exo-action-copy"><strong>{ctaLabel}</strong></span>
                </span>
                <span className="exo-action-content exo-action-active" aria-hidden={!actionInFlight}>
                  <Loader2 size={16} className="shrink-0 animate-spin" />
                  <span className="exo-action-copy">
                    <strong>
                      {actionStateLabel}
                      {progressPercent == null ? '' : ` ${Math.round(progressPercent)}%`}
                    </strong>
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
              {selectedProgress && (
                <span
                  className="sr-only"
                  role="progressbar"
                  aria-label={`${selected.title} progress`}
                  aria-valuemin={0}
                  aria-valuemax={100}
                  aria-valuenow={progressPercent == null ? undefined : Math.round(progressPercent)}
                />
              )}
            </button>

            {selected.installed && !selected.isAddPortable && (
              <>
                <button
                  type="button"
                  disabled={uninstalling || busy || !!progress?.isActive || !!selected.canStop}
                  className="exo-ghost-btn h-11 px-4"
                  onClick={() => {
                    setUninstalling(true)
                    onStatus(`Removing ${selected.title}…`)
                    void host.uninstall(selected.id)
                      .then((r) => {
                      onStatus(r.message ?? (r.ok ? 'Uninstalled' : 'Uninstall failed'), !r.ok)
                      if (r.ok) onUninstalled()
                      })
                      .catch((error: unknown) => {
                        onStatus(error instanceof Error ? error.message : 'Uninstall failed', true)
                      })
                      .finally(() => setUninstalling(false))
                  }}
                >
                  {uninstalling ? (
                    <Loader2 size={16} className="animate-spin" />
                  ) : (
                    <Trash size={16} />
                  )}
                  {uninstalling ? 'Removing…' : 'Uninstall'}
                </button>
                <button
                  type="button"
                  className="exo-ghost-btn h-11 px-4"
                  onClick={() =>
                    void host.openFolder(selected.id).then((r) => {
                      if (!r.ok) onStatus(r.message ?? 'Folder not found')
                    })
                  }
                >
                  <FolderOpen size={16} />
                  Folder
                </button>
              </>
            )}
          </div>

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
              <div className="exo-action-message mt-3" aria-live="polite">
                <div className="flex items-center gap-2 text-xs">
                  {statusMsg === 'Running' ? (
                    <Check size={16} className="shrink-0 text-good" />
                  ) : null}
                  <span className="min-w-0 flex-1 text-fg">{statusMsg}</span>
                </div>
              </div>
            </FadeIn>
          )}
        </FadeIn>
        </div>
      </div>
    </div>
  )
}

function Stat({ label, value }: { label: string; value: string }) {
  return (
    <div className="exo-game-stat">
      <span className="exo-game-stat-label">{label}</span>
      <span className="exo-game-stat-value tabular-nums">{value}</span>
    </div>
  )
}

function achievementDisplay(
  data: GameAchievementsResponse | null,
): { text: string; percent: number | null } {
  if (data?.coverage === 'unsupported') return { text: 'Not supported', percent: null }
  const summary = data?.summary
  if (!summary) return { text: '—', percent: null }
  if (summary.total === 0 && summary.unlocked === 0) return { text: 'None', percent: null }
  const percent = summary.total > 0
    ? Math.round((summary.unlocked / summary.total) * 100)
    : (summary.completionPercent ?? null)
  const count = `${summary.unlocked} / ${summary.total}`
  return {
    text: percent != null ? `${count} (${percent}%)` : count,
    percent: percent != null ? Math.max(0, Math.min(100, percent)) : null,
  }
}

function bestPlaytimeMinutes(game: Game): number | null | undefined {
  const values = [game.playtimeMinutes, ...(game.variants ?? []).map((variant) => variant.playtimeMinutes)]
    .filter((n): n is number => typeof n === 'number' && n > 0)
  if (values.length === 0) return game.playtimeMinutes
  return Math.max(...values)
}

/** Buy via store client / browser — Exo does not host checkout. */
function storeBuyUrl(game: Game): string | null {
  if (game.installed || game.canInstall || game.owned) return null
  const target = (game.launchTarget || '').trim()
  if (game.store === 'steam' && /^\d+$/.test(target))
    return `steam://store/${target}`
  if (game.store === 'gog' && target)
    return `https://www.gog.com/en/game/${encodeURIComponent(target)}`
  if (game.store === 'epic' && game.id.startsWith('epic:catalog:') && target)
    return `https://store.epicgames.com/en-US/p/${encodeURIComponent(target)}`
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
