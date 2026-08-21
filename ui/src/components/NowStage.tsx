import type { CSSProperties } from 'react'
import { Download, Loader2, Play, Stop } from '../brand/icons'
import {
  primaryCtaLabel,
  resolvePrimaryAction,
  type Game,
  type InstallProgress,
} from '../lib/host'
import { nowKicker, type NowKind } from '../lib/now'
import { formatPlaytime, formatSpeed, storeLabel, transferPercent } from '../lib/utils'
import { CoverArt } from './CoverArt'

export function NowStage({
  game,
  kind,
  progress,
  disabled,
  onOpen,
  onPrimary,
  onStop,
}: {
  game: Game
  kind: NowKind
  progress: InstallProgress | null
  disabled?: boolean
  onOpen: () => void
  onPrimary: () => void
  onStop: () => void
}) {
  const transferring = kind === 'download'
  const playing = kind === 'playing' || !!game.canStop
  const action = resolvePrimaryAction(game)
  const percent = transferring ? transferPercent(progress) : null
  const cta = playing
    ? 'Stop'
    : transferring
      ? percent == null ? 'Downloading' : `${Math.round(percent)}%`
      : primaryCtaLabel(game, action)
  const transferMeta = transferring
    ? [progress?.status || progress?.phase, progress?.bytesPerSecond == null ? null : formatSpeed(progress.bytesPerSecond)]
        .filter(Boolean)
        .join(' · ')
    : null
  const contextMeta = [
    storeLabel(game.store),
    (game.playtimeMinutes ?? 0) > 0 ? `${formatPlaytime(game.playtimeMinutes)} played` : null,
    transferMeta,
    kind === 'update' ? 'Update ready' : null,
  ].filter(Boolean).join(' · ')
  return (
    <section className="exo-now-wrap">
      <article className={`exo-now is-${kind}`}>
        <div className="exo-now-body">
          <button
            type="button"
            className="exo-now-open"
            disabled={disabled}
            onClick={onOpen}
            aria-label={`${game.title} details`}
          >
            <span className="exo-now-poster" aria-hidden="true">
              <CoverArt game={game} preload className="h-full w-full" />
            </span>
            <span className="exo-now-copy">
              <p className="exo-now-kicker">{nowKicker(kind)}</p>
              <h2 className="exo-now-title">{game.title}</h2>
              {contextMeta ? <p className="exo-now-meta">{contextMeta}</p> : null}
            </span>
          </button>
          <button
            type="button"
            className="exo-play exo-now-cta"
            disabled={disabled}
            onClick={() => {
              if (playing) onStop()
              else if (transferring) onOpen()
              else onPrimary()
            }}
          >
            {transferring && (
              <span
                className={`exo-action-progress${percent == null ? ' is-unknown' : ''}`}
                style={percent == null ? undefined : { '--progress': percent / 100 } as CSSProperties}
                aria-hidden
              />
            )}
            <span className="relative z-[1] inline-flex items-center gap-2">
              {transferring ? (
                <Loader2 size={16} className="animate-spin motion-reduce:animate-none" />
              ) : playing ? (
                <Stop size={16} />
              ) : action === 'install' || action === 'update' ? (
                <Download size={16} />
              ) : (
                <Play size={16} />
              )}
              <span>{cta}</span>
            </span>
          </button>
        </div>
      </article>
    </section>
  )
}
