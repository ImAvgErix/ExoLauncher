import { useCallback, useEffect, useRef, useState } from 'react'
import { Button } from './ui/Button'
import { CHECK_PATH, Close } from '../brand/icons'
import { hostedBuyUrl, resolvePrimaryAction, type Game, type GameVariant, type InstallProgress } from '../lib/host'
import { transferForGame } from '../lib/library'
import { installProgressLabel, transferPercent } from '../lib/utils'
import { CoverArt, HeroWash } from './CoverArt'
import { GameHeader, GameStats, GameStore } from './game/GameSections'
import { HdrRow } from './game/HdrRow'
import { ArtworkStrip, GameActions } from './game/ManageStrip'
import { PrimaryAction } from './game/PrimaryAction'
import { ShaderPrecompile } from './game/ShaderPrecompile'
import { useGameDetails } from './game/useGameDetails'
import { useJustFinished } from '../lib/useJustFinished'
import { useGlassTone, useLens } from '../lib/glass'
import { UpscalerFiles } from './UpscalerFiles'

function isNoiseStatus(message: string): boolean {
  return /^(canceled\.?|uninstalled\.?|removed\.?|installed\.?|up to date\.?|running|(removing|uninstalling) .+…)$/i.test(message.trim())
}

/** Which sections a card gets. A portable "Add folder" tile gets none of them. */
function pageFlags(selected: Game) {
  const portable = !!selected.isAddPortable
  const storePage = !selected.owned && !selected.installed && !portable
  const installedActions = selected.installed && !portable
  const artworkEnabled = !storePage && !portable && (!!selected.owned || selected.installed)
  return { storePage, installedActions, artworkEnabled }
}

/**
 * A just-mutated cover painted over the card until the host's own revision
 * catches up, then dropped so the library copy is the single source again.
 */
function useArtworkOverride(selected: Game, selectionKey: string): [Game, (game: Game) => void] {
  const [art, setArt] = useState<Game | null>(null)
  useEffect(() => setArt(null), [selectionKey])
  useEffect(() => {
    if (art && (selected.artRevision ?? 0) >= (art.artRevision ?? 0)) setArt(null)
  }, [art, selected.artRevision])
  const view = art
    ? { ...selected, coverUrl: art.coverUrl ?? null, coverSource: art.coverSource ?? null, artRevision: art.artRevision }
    : selected
  return [view, setArt]
}

/** Installed with nothing left to update: where a finished download or update lands. */
function isCurrent(selected: Game): boolean {
  return !!selected.installed && resolvePrimaryAction(selected) !== 'update'
}

function sourceVariants(selected: Game): GameVariant[] | null {
  const sources = selected.variants ?? []
  return sources.length > 1 ? sources : null
}

export type GamePageProps = {
  selected: Game
  busy: boolean
  statusMsg: string | null
  progress: InstallProgress | null
  /** Waiting in Exo's download queue. */
  queued?: boolean
  onPrimary: () => void
  onStop: () => void
  onCancel: () => void
  onDequeue?: () => void
  /** Opens a System page: Graphics (every game's upscalers) or Shaders. */
  onOpenSystem?: (page: 'graphics' | 'shaders') => void
  onClose: () => void
  /** Switches the exact store entry behind a grouped library card. */
  onSelectSource?: (id: string) => void
  onStatus: (msg: string | null, sticky?: boolean) => void
  onUninstalled: () => void
  closeDisabled?: boolean
  /** System > HDR > HDR for HDR games, and turning it on from the HDR row. */
  hdrForGames: boolean
  onHdrForGames: () => Promise<boolean>
  /** Properties over the page (its tool, or the card menu's Properties). */
  propertiesOpen: boolean
  onPropertiesOpen: (open: boolean) => void
}

/** Upscalers and Shaders, one row each, each opening its System page. */
function GameLines({ selected, details, onOpenSystem }: {
  selected: Game
  details: ReturnType<typeof useGameDetails>
  onOpenSystem: NonNullable<GamePageProps['onOpenSystem']>
}) {
  return (
    <div className="exo-game-lines">
      <UpscalerFiles
        key={'upscalers' + details.selectionKey}
        status={details.dlss}
        scanState={!details.dlss ? 'loading' : details.dlss.ok === false ? 'failed' : 'ready'}
        onOpen={() => onOpenSystem('graphics')}
      />
      <ShaderPrecompile key={'shaders' + details.selectionKey} gameId={selected.id} onOpenShaders={() => onOpenSystem('shaders')} />
    </div>
  )
}

/** Live status for screen readers while something is happening to this title. */
function LiveStatus({
  selected,
  busy,
  progress,
  statusMsg,
}: Pick<GamePageProps, 'selected' | 'busy' | 'statusMsg'> & { progress: InstallProgress | null }) {
  if (progress) {
    // Phase and quarter steps only: every byte tick would be read aloud. The
    // pill's progressbar carries the exact figure for anyone who asks.
    const percent = transferPercent(progress)
    return (
      <span className="sr-only" role="status" aria-live="polite" aria-atomic="true">
        {[
          installProgressLabel({ phase: progress.phase, status: progress.status }, 'download'),
          percent == null ? null : `${Math.floor(percent / 25) * 25}%`,
        ].filter(Boolean).join(' ')}
      </span>
    )
  }
  if (busy) {
    return (
      <span className="sr-only" role="status" aria-live="polite">
        {selected.canStop ? 'Closing' : 'Preparing'} {selected.title}
      </span>
    )
  }
  if (!statusMsg || isNoiseStatus(statusMsg)) return null
  // A pill floating at the foot of the page: it fades in over the page and
  // clears on its own, so a message never pushes the page around.
  return (
    <p key={statusMsg} className="exo-game-status exo-motion-switch" aria-live="polite">
      <span>{statusMsg}</span>
    </p>
  )
}

export function GamePage({
  selected,
  busy,
  statusMsg,
  progress,
  queued = false,
  onPrimary,
  onStop,
  onCancel,
  onDequeue,
  onOpenSystem,
  onClose,
  onSelectSource,
  onStatus,
  onUninstalled,
  closeDisabled = false,
  hdrForGames,
  onHdrForGames,
  propertiesOpen,
  onPropertiesOpen,
}: GamePageProps) {
  const { installedActions, artworkEnabled } = pageFlags(selected)
  const details = useGameDetails(selected)
  const [manageBusy, setManageBusy] = useState(false)
  const [artworkView, setArtworkGame] = useArtworkOverride(selected, details.selectionKey)
  const onManageBusy = useCallback((next: boolean) => setManageBusy(next), [])

  const action = resolvePrimaryAction(selected)
  const buyUrl = hostedBuyUrl(selected)
  const selectedProgress = transferForGame(progress, selected)
  const hostLocked = busy || !!selectedProgress?.isActive
  const sources = sourceVariants(selected)
  // Work and Done (DESIGN.md › Motion) on the cover: while it downloads, and when it lands installed.
  const downloading = !!selectedProgress?.isActive
  const finished = useJustFinished(downloading, isCurrent(selected))
  const close = useRef<HTMLButtonElement>(null)
  useLens(close)
  useGlassTone(close, 'game-close')

  return (
    <article className="exo-game-page exo-enter">
      <div className="exo-game-page-wash" aria-hidden>
        <HeroWash game={artworkView} />
      </div>
      {/* Stays put while a launch owns the sheet, so the header never jumps. */}
      <Button
        type="button"
        variant="ghost"
        size="sm"
        isIconOnly
        ref={close}
        className="exo-game-close exo-glass"
        isDisabled={closeDisabled}
        onPress={onClose}
        aria-label="Close"
        aria-keyshortcuts="Escape"
      >
        <Close size={18} />
      </Button>

      <div className="exo-game-page-inner">
        <div className="exo-game-page-layout">
          <div className="exo-game-cover-col">
            <div className="exo-game-detail-cover">
              <CoverArt game={artworkView} large preload />
              {(downloading || finished) && (
                <span className={`exo-game-cover-motion ${downloading ? 'exo-motion-work' : 'exo-motion-done'}`} aria-hidden="true">
                  {finished && <svg className="exo-motion-check" viewBox="0 0 24 24"><path d={CHECK_PATH} pathLength="1" /></svg>}
                </span>
              )}
            </div>
          </div>
          <div className="exo-game-page-body">
            <div className="exo-game-info">
              <GameHeader selected={selected}>
                <GameStore
                  selected={selected}
                  sources={sources}
                  locked={hostLocked || manageBusy}
                  onSelectSource={onSelectSource}
                />
              </GameHeader>
              <div className="exo-game-actions">
                <PrimaryAction
                  selected={selected}
                  action={action}
                  buyUrl={buyUrl}
                  busy={busy}
                  statusMsg={statusMsg}
                  progress={selectedProgress}
                  queued={queued}
                  onPrimary={onPrimary}
                  onStop={onStop}
                  onCancel={onCancel}
                  onDequeue={onDequeue}
                />
                {installedActions && (
                  <GameActions
                    selected={selected}
                    coverSource={artworkView.coverSource}
                    locked={hostLocked}
                    upscalers={details.dlss}
                    onUpscalersChanged={() => void details.refreshDlss(false)}
                    onStatus={onStatus}
                    onArtwork={setArtworkGame}
                    onUninstalled={onUninstalled}
                    onBusyChange={onManageBusy}
                    propertiesOpen={propertiesOpen}
                    onPropertiesOpen={onPropertiesOpen}
                  />
                )}
              </div>

              <GameStats selected={selected} sources={sources} achievements={details.achievements} />
              {artworkEnabled && !installedActions && (
                <ArtworkStrip selected={selected} coverSource={artworkView.coverSource} locked={hostLocked} onArtwork={setArtworkGame} onStatus={onStatus} />
              )}
              {installedActions && onOpenSystem && (
                <GameLines selected={selected} details={details} onOpenSystem={onOpenSystem} />
              )}
              {installedActions && (
                <HdrRow key={'hdr' + details.selectionKey} gameId={selected.id} hdrForGames={hdrForGames} onHdrForGames={onHdrForGames} />
              )}
            </div>
          </div>
        </div>
      </div>
      <LiveStatus selected={selected} busy={busy} progress={selectedProgress} statusMsg={statusMsg} />
    </article>
  )
}
