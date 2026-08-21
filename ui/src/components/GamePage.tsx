import {
  Check,
  Close,
  Download,
  ExternalLink,
  FileText,
  FolderOpen,
  Loader2,
  Play,
  Star,
  StarFilled,
  Stop,
  Trash,
  Wrench,
} from '../brand/icons'
import { useCallback, useEffect, useRef, useState, type CSSProperties } from 'react'
import {
  host,
  hostedBuyUrl,
  onHostEvent,
  primaryCtaLabel,
  resolvePrimaryAction,
  type DlssStatus,
  type Game,
  type GameAchievementsResponse,
  type GameMetadata,
  type InstallProgress,
  type ArtworkMutationResponse,
} from '../lib/host'
import { formatRelativeLastPlayed, formatSize, formatSpeed, storeLabel, transferPercent } from '../lib/utils'
import { isUsefulAchievement } from '../lib/achievements'
import { loadUpscalerStatus, peekUpscalerStatus } from '../lib/upscalerCache'
import { ggDealsUrl } from '../lib/stores'
import { HeroWash } from './CoverArt'
import { UpscalerFiles } from './UpscalerFiles'

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

export type GamePageProps = {
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
}

export function GamePage({
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
}: GamePageProps) {
  const [uninstalling, setUninstalling] = useState(false)
  const [removeArmed, setRemoveArmed] = useState(false)
  const [repairing, setRepairing] = useState(false)
  const [repair, setRepair] = useState<{ can: boolean; label: string } | null>(null)
  const [achievementData, setAchievementData] = useState<GameAchievementsResponse | null>(null)
  const [dlss, setDlss] = useState<DlssStatus | null>(() =>
    selected.installed ? peekUpscalerStatus(selected.id) : null,
  )
  const [metadata, setMetadata] = useState<GameMetadata | null>(null)
  const [artAction, setArtAction] = useState<'replace' | 'reset' | 'refetch' | 'report' | null>(null)
  const [artworkGame, setArtworkGame] = useState<Game | null>(null)
  const selectionKey = JSON.stringify([selected.id, selected.store])
  const selectionKeyRef = useRef(selectionKey)
  selectionKeyRef.current = selectionKey
  const sourceSwitchLocked = busy || repairing || uninstalling || artAction !== null || !!progress?.isActive
  const action = resolvePrimaryAction(selected)
  const buyUrl = hostedBuyUrl(selected)
  const dealsUrl = buyUrl ? ggDealsUrl(selected) : null
  const selectedProgress = progressForGame(progress, selected)
  const progressPercent = transferPercent(selectedProgress)
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
    action === 'none' && buyUrl
      ? selected.entitlementState === 'notOwned' ? 'Buy again' : buyLabel(selected.store)
      : primaryCtaLabel(selected, action)
  const achievement = achievementDisplay(achievementData)
  const sources = selected.variants && selected.variants.length > 1 ? selected.variants : null
  // Source chips name one store. Use that store's hours, not the grouped
  // card total — the default chip matches the card id, so reading
  // selected.playtimeMinutes showed Epic+Steam while Epic looked selected.
  const playtime = formatPlayed(
    sources?.find((variant) => variant.id === selected.id)?.playtimeMinutes
      ?? selected.playtimeMinutes,
  )
  const lastLaunched = formatRelativeLastPlayed(
    sources?.find((variant) => variant.id === selected.id)?.lastPlayedUtc
      ?? selected.lastPlayedUtc,
  )
  const installedActions = selected.installed && !selected.isAddPortable
  const artworkEnabled = !selected.isAddPortable && (!!selected.owned || selected.installed)
  const artworkView = artworkGame
    ? {
        ...selected,
        coverUrl: artworkGame.coverUrl ?? null,
        coverSource: artworkGame.coverSource ?? null,
        artRevision: artworkGame.artRevision,
      }
    : selected
  const artworkLocked = artAction !== null || busy || repairing || uninstalling || !!progress?.isActive

  // Every local result belongs to one exact store entry.
  useEffect(() => {
    setUninstalling(false)
    setRemoveArmed(false)
    setRepairing(false)
    setRepair(null)
    setAchievementData(null)
    setDlss(selected.installed ? peekUpscalerStatus(selected.id) : null)
    setMetadata(null)
    setArtAction(null)
    setArtworkGame(null)
  }, [selectionKey])

  useEffect(() => {
    if (!artworkGame) return
    if ((selected.artRevision ?? 0) >= (artworkGame.artRevision ?? 0)) setArtworkGame(null)
  }, [artworkGame, selected.artRevision])

  const runArtwork = useCallback((
    actionName: 'replace' | 'reset' | 'refetch',
    operation: (id: string) => Promise<ArtworkMutationResponse>,
  ) => {
    if (artAction !== null) return
    setArtAction(actionName)
    void operation(selected.id)
      .then((result) => {
        if (result.cancelled) {
          onStatus(null)
          return
        }
        if (result.ok && result.game) setArtworkGame(result.game)
        onStatus(
          result.message ?? (result.ok ? 'Artwork updated.' : 'Artwork could not be updated.'),
          !result.ok,
        )
      })
      .catch((error: unknown) => {
        onStatus(error instanceof Error ? error.message : 'Artwork could not be updated.', true)
      })
      .finally(() => setArtAction(null))
  }, [artAction, onStatus, selected.id])

  useEffect(() => {
    let active = true
    void host.gameExtras(selected.id).then((extras) => {
      if (!active) return
      setRepair({
        can: !!extras.canRepair,
        label: extras.repairLabel?.trim() || 'Verify files',
      })
    }).catch(() => {
      if (active) setRepair(null)
    })
    return () => {
      active = false
    }
  }, [selected.id, selectionKey])

  // Catalog text is fetched for the opened card only, never per tile.
  useEffect(() => {
    let active = true
    void host.gameMetadata(selected.id).then((result) => {
      if (active && result.metadata) setMetadata(result.metadata)
    }).catch(() => {})
    return () => {
      active = false
    }
  }, [selected.id, selectionKey])

  const refreshDlss = useCallback(() => {
    const requestKey = selectionKey
    void loadUpscalerStatus(selected.id, true)
      .then((status) => {
        if (selectionKeyRef.current === requestKey) setDlss(status)
      })
      .catch(() => {
        if (selectionKeyRef.current === requestKey) setDlss(null)
      })
  }, [selected.id, selectionKey])

  useEffect(() => {
    if (!selected.installed) {
      setDlss(null)
      return
    }
    let active = true
    const cached = peekUpscalerStatus(selected.id)
    if (cached) setDlss(cached)
    void loadUpscalerStatus(selected.id).then((status) => {
      if (active) setDlss(status)
    }).catch(() => {
      if (active) setDlss(null)
    })
    return () => {
      active = false
    }
  }, [selected.id, selected.installed, selectionKey])

  useEffect(() => {
    let active = true
    const requestId = selected.id
    setAchievementData(null)
    const loadAchievements = async () => {
      try {
        try {
          const hostCached = await host.getAchievements(requestId)
          if (
            active &&
            hostCached?.gameId === requestId &&
            (isUsefulAchievement(hostCached) || hostCached.coverage === 'unsupported' || hostCached.ok)
          ) {
            setAchievementData(hostCached)
          }
        } catch {
          /* ignore cache miss */
        }
        const result = await host.refreshAchievements(requestId)
        if (!active || (result?.gameId && result.gameId !== requestId)) return
        const refreshedUseful = isUsefulAchievement(result)
        if (refreshedUseful) {
          setAchievementData(result)
        } else if (result?.coverage === 'unsupported' || result?.ok) {
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
  }, [selected.id, selectionKey])

  useEffect(() => {
    return onHostEvent('achievements.updated', (data) => {
      const snap = data as GameAchievementsResponse
      if (!snap?.ok || !snap.gameId) return
      if (snap.gameId !== selected.id) return
      if (isUsefulAchievement(snap)) {
        setAchievementData(snap)
      }
    })
  }, [selected.id, selectionKey])

  return (
    <article className="exo-game-page" data-controller-scope="game-detail">
      <div className="exo-game-page-wash" aria-hidden>
        <HeroWash game={artworkView} />
      </div>

      {!closeDisabled && (
        <button
          type="button"
          data-controller-target=""
          data-controller-safe=""
          className="exo-game-close"
          onClick={onClose}
          aria-label="Dismiss details"
          aria-keyshortcuts="Escape"
        >
          <Close size={18} />
        </button>
      )}

      <div className="exo-game-page-inner">
      <div className="exo-game-page-body">
        <div className="exo-game-info">
          <p className="exo-game-kicker">
            {[storeLabel(selected.store), metadata?.genre, metadata?.year].filter(Boolean).join(' · ')}
          </p>
          <div className="exo-game-title-row">
            <h1 className="exo-game-title">{selected.title}</h1>
            {installedActions && (
              <button
                type="button"
                className={`exo-game-favorite${selected.isFavorite ? ' is-on' : ''}`}
                aria-label={selected.isFavorite ? `Remove ${selected.title} from favorites` : `Add ${selected.title} to favorites`}
                aria-pressed={selected.isFavorite}
                onClick={() => onToggleFavorite(selected.id)}
              >
                {selected.isFavorite ? <StarFilled size={16} /> : <Star size={16} />}
              </button>
            )}
          </div>
          {sources && onSelectSource && (
            <div className="exo-game-sources" role="group" aria-label="Choose game source">
              {sources.map((variant) => {
                const active = variant.id === selected.id
                return (
                  <button
                    key={variant.id}
                    type="button"
                    disabled={sourceSwitchLocked}
                    onClick={() => onSelectSource(variant.id)}
                    aria-pressed={active}
                    className={`exo-game-source${active ? ' is-on' : ''}`}
                  >
                    {variant.updateAvailable
                      ? `${storeLabel(variant.store)} · Update`
                      : storeLabel(variant.store)}
                  </button>
                )
              })}
            </div>
          )}
          <div className="exo-game-actions">
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
              className={`exo-play exo-primary-action${actionInFlight ? ' is-active' : ''}`}
              aria-label={selectedProgress && selectedProgress.canCancel
                ? `Cancel ${activeLabel.replace('…', '').toLocaleLowerCase()} ${selected.title}${progressPercent == null ? '' : `, ${Math.round(progressPercent)}%`}`
                : selectedProgress
                  ? `${activeLabel.replace('…', '')} ${selected.title}${progressPercent == null ? '' : `, ${Math.round(progressPercent)}%`}`
                    : busy
                      ? `${selected.canStop ? 'Closing' : 'Preparing'} ${selected.title}`
                      : ctaLabel}
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
                  ) : action === 'none' && buyUrl ? (
                    <ExternalLink size={16} className="shrink-0" />
                  ) : action === 'install' || action === 'update' ? (
                    <Download size={16} className="shrink-0" />
                  ) : (
                    <Play size={16} className="shrink-0" />
                  )}
                  <span className="exo-action-copy">{ctaLabel}</span>
                </span>
                <span className="exo-action-content exo-action-active" aria-hidden={!actionInFlight}>
                  <Loader2 size={16} className="shrink-0 animate-spin motion-reduce:animate-none" />
                  <span className="exo-action-copy">
                    <span>
                      {actionStateLabel}
                      {progressPercent == null ? '' : ` ${Math.round(progressPercent)}%`}
                    </span>
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
            </button>

            {selectedProgress && (
              <span
                className="sr-only"
                role="progressbar"
                aria-label={`${selected.title} progress`}
                aria-valuemin={0}
                aria-valuemax={100}
                aria-valuenow={progressPercent == null ? undefined : Math.round(progressPercent)}
                aria-valuetext={[
                  selectedProgress.status || selectedProgress.phase,
                  progressPercent == null ? null : `${Math.round(progressPercent)}%`,
                  selectedProgress.bytesPerSecond == null ? null : formatSpeed(selectedProgress.bytesPerSecond),
                ].filter(Boolean).join(' · ')}
              />
            )}

            {dealsUrl && (
              <button
                type="button"
                className="exo-ghost-btn exo-buy-key"
                aria-label={`Buy cheapest key for ${selected.title} on gg.deals`}
                onClick={(e) => {
                  e.preventDefault()
                  void host.openUrl(dealsUrl)
                }}
              >
                <span className="exo-action-state">
                  <span className="exo-action-content exo-action-idle">
                    <Download size={16} className="shrink-0" />
                    <span className="exo-action-copy">Buy cheapest key</span>
                  </span>
                </span>
              </button>
            )}
          </div>

          <div className="exo-game-stats">
            <Stat label="Time played" value={playtime} />
            <Stat label="Last launched" value={lastLaunched} />
            <Stat label="Size" value={formatSize(selected.sizeBytes)} />
            <Stat label="Achievements" value={achievement.text} />
          </div>

          {(artworkEnabled || installedActions) && (
            <div className="exo-game-tools exo-utility-row" role="group" aria-label="Game utilities">
              {artworkEnabled && (
                <>
                  <button
                    type="button"
                    className="exo-game-tool"
                    disabled={artworkLocked}
                    aria-busy={artAction === 'replace'}
                    onClick={() => runArtwork('replace', host.artReplace)}
                  >
                    <Download size={14} />
                    {artAction === 'replace' ? 'Choosing…' : 'Replace cover'}
                  </button>
                  {artworkView.coverSource === 'custom' ? (
                    <button
                      type="button"
                      className="exo-game-tool"
                      disabled={artworkLocked}
                      aria-busy={artAction === 'reset'}
                      onClick={() => runArtwork('reset', host.artReset)}
                    >
                      <Trash size={14} />
                      {artAction === 'reset' ? 'Resetting…' : 'Reset cover'}
                    </button>
                  ) : (
                    <button
                      type="button"
                      className="exo-game-tool"
                      disabled={artworkLocked}
                      aria-busy={artAction === 'refetch'}
                      onClick={() => runArtwork('refetch', host.artRefetch)}
                    >
                      <Wrench size={14} />
                      {artAction === 'refetch' ? 'Refreshing…' : 'Refetch artwork'}
                    </button>
                  )}
                  <button
                    type="button"
                    className="exo-game-tool"
                    disabled={artworkLocked}
                    aria-busy={artAction === 'report'}
                    onClick={() => {
                      if (artAction !== null) return
                      setArtAction('report')
                      void host.artReport(selected.id, true)
                        .then((result) => onStatus(
                          result.message ?? (result.ok ? 'Artwork details copied.' : 'Artwork report failed.'),
                          !result.ok,
                        ))
                        .catch((error: unknown) => {
                          onStatus(error instanceof Error ? error.message : 'Artwork report failed.', true)
                        })
                        .finally(() => setArtAction(null))
                    }}
                  >
                    <FileText size={14} />
                    {artAction === 'report' ? 'Preparing…' : 'Report wrong art'}
                  </button>
                </>
              )}

              {installedActions && (
                <>
                <button
                  type="button"
                  className="exo-game-tool"
                  onClick={() =>
                    void host.openFolder(selected.id).then((r) => {
                      if (!r.ok) onStatus(r.message ?? 'Folder not found')
                    })
                  }
                >
                  <FolderOpen size={14} />
                  Open folder
                </button>
                {repair?.can && (
                  <button
                    type="button"
                    className="exo-game-tool"
                    disabled={repairing || uninstalling || busy || !!progress?.isActive || !!selected.canStop}
                    onClick={() => {
                      setRepairing(true)
                      onStatus(`${repair.label}…`)
                      void host.repair(selected.id)
                        .then((r) => {
                          onStatus(
                            r.message ?? (r.queued ? 'Queued.' : r.ok ? `${repair.label} started.` : `${repair.label} failed.`),
                            !r.ok,
                          )
                        })
                        .catch((error: unknown) => {
                          onStatus(error instanceof Error ? error.message : `${repair.label} failed.`, true)
                        })
                        .finally(() => setRepairing(false))
                    }}
                  >
                    {repairing ? <Loader2 size={14} className="animate-spin motion-reduce:animate-none" /> : <Wrench size={14} />}
                    {repairing ? `${repair.label}…` : repair.label}
                  </button>
                )}
                <button
                  type="button"
                  disabled={uninstalling || busy || !!progress?.isActive || !!selected.canStop}
                  className={`exo-game-tool${removeArmed ? ' is-armed' : ''}`}
                  onClick={() => {
                    if (!removeArmed) {
                      setRemoveArmed(true)
                      return
                    }
                    setRemoveArmed(false)
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
                  {uninstalling ? <Loader2 size={14} className="animate-spin motion-reduce:animate-none" /> : <Trash size={14} />}
                  {uninstalling ? 'Removing…' : removeArmed ? 'Confirm remove' : 'Remove'}
                </button>
                </>
              )}
            </div>
          )}

          {installedActions && (
            <>
              <UpscalerFiles
                key={selectionKey}
                items={dlss?.items}
                gameId={selected.id}
                antiCheat={dlss?.antiCheatWarning}
                gameRunning={!!selected.canStop}
                installing={!!progress?.isActive}
                onRefresh={refreshDlss}
              />
            </>
          )}

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
            <p className="exo-game-status" aria-live="polite">
              {statusMsg === 'Running' ? <Check size={14} className="shrink-0 text-good" /> : null}
              <span>{statusMsg}</span>
            </p>
          )}
        </div>
      </div>
      </div>
    </article>
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

function formatPlayed(minutes: number | null | undefined): string {
  if (minutes == null || minutes <= 0) return '—'
  if (minutes < 60) return `${minutes} min`
  const hours = Math.floor(minutes / 60)
  const rest = minutes % 60
  return rest > 0 ? `${hours} hr ${rest} min` : `${hours} hr`
}

/** Count carries the numbers; the bar carries the ratio. Never both. */
function achievementDisplay(
  data: GameAchievementsResponse | null,
): { text: string } {
  if (data?.coverage === 'unsupported') return { text: 'Not supported' }
  const summary = data?.summary
  if (!summary) return { text: '—' }
  if (summary.total === 0 && summary.unlocked === 0) return { text: 'None' }
  return { text: `${summary.unlocked} / ${summary.total}` }
}

function buyLabel(store: string): string {
  if (store === 'steam') return 'Buy on Steam'
  if (store === 'gog') return 'Buy on GOG'
  return 'Buy in browser'
}
