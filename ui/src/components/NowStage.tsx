import { useState, type CSSProperties } from 'react'
import { Download, Loader2, Play, Stop } from '../brand/icons'
import {
  primaryCtaLabel,
  resolvePrimaryAction,
  type Game,
  type InstallProgress,
} from '../lib/host'
import { nowKicker, type NowKind } from '../lib/now'
import { formatPlaytime, formatRelativeLastPlayed, formatSpeed, storeLabel, visibleInstallPercent } from '../lib/utils'
import { CoverArt, steamHeroUrls } from './CoverArt'

function NowWash({ game }: { game: Game }) {
  const urls = steamHeroUrls(game)
  const [idx, setIdx] = useState(0)
  const [ok, setOk] = useState(false)
  const url = urls[idx]
  if (!url) return null
  return (
    <img
      src={url}
      alt=""
      className={`exo-now-wash-img${ok ? ' is-on' : ''}`}
      draggable={false}
      decoding="async"
      onLoad={(e) => {
        const img = e.currentTarget
        if (img.naturalWidth < 400 || img.naturalWidth / img.naturalHeight < 1.2) {
          if (idx + 1 < urls.length) setIdx((i) => i + 1)
          return
        }
        setOk(true)
      }}
      onError={() => {
        if (idx + 1 < urls.length) setIdx((i) => i + 1)
      }}
    />
  )
}

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
  const percent = transferring ? visibleInstallPercent(progress?.percent) : null
  const cta = playing
    ? 'Stop'
    : transferring
      ? percent == null ? 'Downloading' : `${Math.round(percent)}%`
      : primaryCtaLabel(game, action)
  const meta = [
    storeLabel(game.store),
    transferring && progress?.status ? progress.status : null,
    transferring && progress?.bytesPerSecond ? formatSpeed(progress.bytesPerSecond) : null,
    !transferring && formatPlaytime(game.playtimeMinutes),
    !transferring && game.lastPlayedUtc ? formatRelativeLastPlayed(game.lastPlayedUtc) : null,
  ].filter((part) => part && part !== '—')

  return (
    <section className="exo-now-wrap">
      <article
        className="exo-now"
        onClick={() => {
          if (!disabled) onOpen()
        }}
      >
        <div className="exo-now-wash" aria-hidden>
          <NowWash game={game} />
          <span className="exo-now-veil" />
        </div>
        <div className="exo-now-body">
          <button
            type="button"
            className="exo-now-open"
            disabled={disabled}
            onMouseDown={(event) => {
              if (event.button === 0) event.preventDefault()
            }}
            onClick={(event) => {
              event.stopPropagation()
              onOpen()
            }}
            aria-label={`${game.title} details`}
          >
            <div className="exo-now-poster">
              <CoverArt game={game} className="absolute inset-0 h-full w-full" large />
            </div>
            <div className="exo-now-copy">
              <p className="exo-now-kicker">{nowKicker(kind)}</p>
              <h2 className="exo-now-title">{game.title}</h2>
              {meta.length > 0 && <p className="exo-now-meta">{meta.join(' · ')}</p>}
            </div>
          </button>
          <button
            type="button"
            className={`exo-cta exo-now-cta min-w-[148px]${transferring || playing ? ' is-active' : ''}`}
            disabled={disabled}
            onClick={(event) => {
              event.stopPropagation()
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
                <Loader2 size={16} className="animate-spin" />
              ) : playing ? (
                <Stop size={16} />
              ) : action === 'install' || action === 'update' ? (
                <Download size={16} />
              ) : (
                <Play size={16} />
              )}
              <strong>{cta}</strong>
            </span>
          </button>
        </div>
        {transferring && (
          <span
            className={`exo-now-meter${percent == null ? ' is-unknown' : ''}`}
            style={percent == null ? undefined : { '--progress': percent / 100 } as CSSProperties}
            aria-hidden
          />
        )}
      </article>
    </section>
  )
}
