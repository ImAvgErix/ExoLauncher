import { useEffect, useMemo, useState } from 'react'
import type { Game } from '../lib/host'
import { cn, monogram } from '../lib/utils'

/**
 * Cover art: monogram underneath; portrait image after onLoad.
 * Native CoverArtService owns source selection. Wide heroes are rejected —
 * portrait posters only, never letterboxed landscape.
 */

/** Survives pin ↔ grid remounts so covers don’t flash to monogram. */
const loadedUrlByGameId = new Map<string, string>()

/** Official Steam portrait posters (library_600x900 / library_capsule) — never heroes. */
function isOfficialSteamPortraitCdn(url: string): boolean {
  if (!url.startsWith('https://')) return false
  const hostOk =
    url.includes('steamstatic.com/') || url.includes('steamcdn-a.akamaihd.net/')
  if (!hostOk) return false
  if (
    url.includes('library_hero') ||
    url.includes('header.jpg') ||
    url.includes('capsule_231') ||
    url.includes('capsule_184') ||
    url.includes('capsule_616') ||
    url.includes('capsule_sm')
  )
    return false
  return (
    url.includes('library_600x900') ||
    url.includes('library_capsule')
  )
}

export function isSafeCoverUrl(url: string | null | undefined): url is string {
  if (!url) return false
  if (url.startsWith('data:image/')) return true
  if (url.startsWith('https://covers.exo-launcher.local/')) return true
  if (isOfficialSteamPortraitCdn(url)) return true
  if (isOfficialEpicPortraitCdn(url)) return true
  return false
}

function isOfficialEpicPortraitCdn(url: string): boolean {
  try {
    const parsed = new URL(url)
    return parsed.protocol === 'https:' &&
      (parsed.hostname === 'cdn1.epicgames.com' || parsed.hostname === 'cdn2.unrealengine.com')
  } catch {
    return false
  }
}

/** Tiny store capsules / known landscape CDN names — never show on 2:3 tiles. */
function isRejectedCoverUrl(url: string): boolean {
  return (
    url.includes('capsule_231') ||
    url.includes('capsule_184') ||
    url.includes('capsule_sm') ||
    url.includes('capsule_616') ||
    url.includes('library_hero') ||
    url.includes('header.jpg') ||
    url.includes('product_tile') ||
    url.includes('product_card')
  )
}

/** True when the bitmap is a poster large enough for a 172px library tile. */
function isPortraitBitmap(width: number, height: number): boolean {
  if (width < 300 || height < 450) return false
  return width / height <= 1.12
}

export function CoverArt({
  game,
  className,
  large,
}: {
  game: Pick<Game, 'id' | 'title' | 'coverUrl' | 'store' | 'launchTarget'>
  className?: string
  large?: boolean
}) {
  const candidates = useMemo(() => {
    const raw = game.coverUrl
    if (!raw || !isSafeCoverUrl(raw) || isRejectedCoverUrl(raw)) return []
    const list = [raw]
    // Prefer the native 2x Steam poster when a 1x URL was supplied. This is a
    // source-quality choice, not browser upscaling; the 1x poster remains the
    // bounded fallback when that CDN variant is unavailable.
    if (raw.includes('library_600x900') && !raw.includes('library_600x900_2x')) {
      const twoX = raw.replace('library_600x900', 'library_600x900_2x')
      if (twoX !== raw && isSafeCoverUrl(twoX)) list.unshift(twoX)
    } else if (raw.includes('library_600x900_2x')) {
      const oneX = raw.replace('library_600x900_2x', 'library_600x900')
      if (oneX !== raw && isSafeCoverUrl(oneX)) list.push(oneX)
    }
    return list
  }, [game.coverUrl])

  const cached = loadedUrlByGameId.get(game.id)
  const startIdx = cached ? Math.max(0, candidates.indexOf(cached)) : 0
  const [idx, setIdx] = useState(startIdx)
  const safeUrl = candidates[idx] ?? candidates[0] ?? null
  const primary = candidates[0] ?? ''
  const alreadyLoaded = !!(safeUrl && loadedUrlByGameId.get(game.id) === safeUrl)
  const [loaded, setLoaded] = useState(alreadyLoaded)
  const [failed, setFailed] = useState(false)

  // Only reset on game identity / primary URL change — never on pin (favorite) remounts.
  useEffect(() => {
    const hit = loadedUrlByGameId.get(game.id)
    const hitIdx = hit ? candidates.indexOf(hit) : -1
    if (hitIdx >= 0) {
      setIdx(hitIdx)
      setLoaded(true)
      setFailed(false)
      return
    }
    setFailed(false)
    setIdx(0)
    setLoaded(false)
    // eslint-disable-next-line react-hooks/exhaustive-deps -- candidates rebuilt; primary is the identity signal
  }, [game.id, primary])

  useEffect(() => {
    if (safeUrl && loadedUrlByGameId.get(game.id) === safeUrl) {
      setLoaded(true)
      return
    }
    setLoaded(false)
  }, [safeUrl, game.id])

  const showImg = !!safeUrl && !failed

  return (
    <div className={cn('relative overflow-hidden', className)}>
      <div
        className={cn('exo-cover-mono', loaded && showImg && 'is-under')}
        style={{ fontSize: large ? 42 : 28 }}
        aria-hidden
      >
        {monogram(game.title)}
      </div>
      {showImg && (
        <img
          src={safeUrl}
          alt=""
          className={cn(
            'absolute inset-0 h-full w-full object-cover object-center transition-opacity duration-200',
            loaded ? 'opacity-100' : 'opacity-0 pointer-events-none',
          )}
          draggable={false}
          loading={large ? 'eager' : 'lazy'}
          fetchPriority={large ? 'high' : 'auto'}
          decoding="async"
          onLoad={(e) => {
            const el = e.currentTarget
            if (!isPortraitBitmap(el.naturalWidth, el.naturalHeight)) {
              // Wide art — refuse it. Monogram stays; native may warm a real poster later.
              loadedUrlByGameId.delete(game.id)
              setFailed(true)
              setLoaded(false)
              return
            }
            loadedUrlByGameId.set(game.id, safeUrl)
            setLoaded(true)
          }}
          onError={() => {
            if (idx + 1 < candidates.length) {
              setIdx((i) => i + 1)
              setLoaded(false)
            } else {
              setFailed(true)
              setLoaded(false)
              loadedUrlByGameId.delete(game.id)
            }
          }}
        />
      )}
    </div>
  )
}

export function coverBg(game: Pick<Game, 'id' | 'title'>): string {
  const hue = hashHue(game.id + game.title)
  return `linear-gradient(160deg,
    hsl(${hue} 42% 28%) 0%,
    hsl(${(hue + 18) % 360} 38% 18%) 50%,
    #050505 100%)`
}

function hashHue(s: string) {
  let h = 0
  for (let i = 0; i < s.length; i++) h = (h * 31 + s.charCodeAt(i)) >>> 0
  return h % 360
}
