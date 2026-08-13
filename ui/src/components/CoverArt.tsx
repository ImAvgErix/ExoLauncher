import { useEffect, useMemo, useState } from 'react'
import type { Game } from '../lib/host'
import { cn, monogram } from '../lib/utils'

/**
 * Cover art: monogram underneath; image after onLoad.
 * Native CoverArtService owns source selection. Library tiles stay portrait.
 * Steam app ids still resolve library_600x900 / library_capsule when coverUrl is empty.
 * fit="banner" keeps Steam library_hero for any wide strip that still asks for it.
 */

type CoverFit = 'poster' | 'wide' | 'banner'

/** Survives pin ↔ grid remounts so covers don’t flash to monogram. Key includes fit. */
const loadedUrlByKey = new Map<string, string>()

function cacheKey(gameId: string, fit: CoverFit) {
  return `${gameId}::${fit}`
}

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

function isOfficialSteamHeroCdn(url: string): boolean {
  if (!url.startsWith('https://')) return false
  const hostOk =
    url.includes('steamstatic.com/') || url.includes('steamcdn-a.akamaihd.net/')
  return hostOk && url.includes('library_hero')
}

export function isSafeCoverUrl(url: string | null | undefined, allowHero = false): url is string {
  if (!url) return false
  if (url.startsWith('data:image/')) return true
  if (url.startsWith('https://covers.exo-launcher.local/')) return true
  if (isOfficialSteamPortraitCdn(url)) return true
  if (allowHero && isOfficialSteamHeroCdn(url)) return true
  if (isOfficialEpicPortraitCdn(url)) return true
  if (httpsHostIs(url, 'images.gog-statics.com') || httpsHostIs(url, 'gog-statics.com')) return true
  if (httpsHostIs(url, 'ddragon.leagueoflegends.com')) return true
  return false
}

function steamAppIdFromText(value: string | null | undefined): string | null {
  if (!value) return null
  const fromId = value.match(/^steam:(\d+)/i)
  if (fromId) return fromId[1]
  const fromApps = value.match(/\/(?:steam\/)?apps\/(\d+)\//)
  if (fromApps) return fromApps[1]
  const fromProtocol = value.match(/steam:\/\/(?:rungameid|launch|install)\/(\d+)/i)
  if (fromProtocol) return fromProtocol[1]
  return null
}

/** Steam app id from the card id, cover CDN path, launch target, or a variant. */
export function steamAppId(
  game: Pick<Game, 'id' | 'coverUrl' | 'launchTarget' | 'variants'>,
): string | null {
  return (
    steamAppIdFromText(game.id) ||
    steamAppIdFromText(game.coverUrl) ||
    steamAppIdFromText(game.launchTarget) ||
    game.variants?.reduce<string | null>((found, variant) => {
      if (found) return found
      return steamAppIdFromText(variant.id) || steamAppIdFromText(variant.launchTarget)
    }, null) ||
    null
  )
}

function steamHeroUrlsForApp(appId: string): string[] {
  const base = `https://cdn.cloudflare.steamstatic.com/steam/apps/${appId}`
  return [`${base}/library_hero_2x.jpg`, `${base}/library_hero.jpg`]
}

/** Official Steam portraits. library_capsule is ~374×448 — still a poster. */
function steamPortraitUrlsForApp(appId: string): string[] {
  const base = `https://cdn.cloudflare.steamstatic.com/steam/apps/${appId}`
  return [
    `${base}/library_600x900.jpg`,
    `${base}/library_capsule.jpg`,
    `${base}/library_600x900_2x.jpg`,
    `${base}/library_capsule_2x.jpg`,
  ]
}

/** Steam library_hero URLs, 2x first. Cards never use these. */
export function steamHeroUrls(
  game: Pick<Game, 'id' | 'coverUrl' | 'launchTarget' | 'variants'>,
): string[] {
  const appId = steamAppId(game)
  return appId ? steamHeroUrlsForApp(appId) : []
}

/** Steam library_hero derived from a portrait CDN URL. Cards never use this. */
export function steamHeroUrl(coverUrl: string | null | undefined): string | null {
  const appId = steamAppIdFromText(coverUrl)
  return appId ? steamHeroUrlsForApp(appId)[1] ?? null : null
}

function httpsHostIs(url: string, host: string): boolean {
  try {
    const parsed = new URL(url)
    return parsed.protocol === 'https:' &&
      (parsed.hostname === host || parsed.hostname.endsWith(`.${host}`))
  } catch {
    return false
  }
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
function isRejectedCoverUrl(url: string, allowHero: boolean): boolean {
  if (allowHero && url.includes('library_hero')) return false
  return (
    url.includes('capsule_231') ||
    url.includes('capsule_184') ||
    url.includes('capsule_sm') ||
    url.includes('capsule_616') ||
    url.includes('library_hero') ||
    url.includes('header.jpg') ||
    url.includes('product_card') ||
    (url.includes('product_tile') && !/product_tile_256(?!0)/.test(url))
  )
}

/** True when the bitmap is a poster large enough for a library tile. */
function isPortraitBitmap(width: number, height: number): boolean {
  if (width < 240 || height < 360) return false
  return width / height <= 1.12
}

function isWideBitmap(width: number, height: number): boolean {
  if (width < 400 || height < 140) return false
  return width / height >= 1.2
}

function pushUnique(list: string[], url: string | null | undefined, allowHero: boolean) {
  if (!url || list.includes(url)) return
  if (!isSafeCoverUrl(url, allowHero) || isRejectedCoverUrl(url, allowHero)) return
  list.push(url)
}

function portraitFallbacks(raw: string | null | undefined): string[] {
  if (!raw) return []
  const list: string[] = []
  list.push(raw)
  if (raw.includes('library_600x900_2x')) {
    list.push(raw.replace('library_600x900_2x', 'library_600x900'))
  }
  return list
}

export function CoverArt({
  game,
  className,
  large,
  fit = 'poster',
}: {
  game: Pick<Game, 'id' | 'title' | 'coverUrl' | 'store' | 'launchTarget' | 'variants'>
  className?: string
  large?: boolean
  fit?: CoverFit
}) {
  const allowHero = fit === 'wide' || fit === 'banner'
  const key = cacheKey(game.id, fit)
  const candidates = useMemo(() => {
    const list: string[] = []
    if (fit === 'banner' || fit === 'wide') {
      for (const hero of steamHeroUrls(game)) pushUnique(list, hero, true)
    }
    if (fit === 'banner') return list
    const appId = steamAppId(game)
    if (fit === 'poster' && appId) {
      for (const url of steamPortraitUrlsForApp(appId)) pushUnique(list, url, false)
    }
    const raw = game.coverUrl
    if (raw && isSafeCoverUrl(raw, allowHero) && !isRejectedCoverUrl(raw, allowHero)) {
      if (fit === 'poster') {
        for (const url of portraitFallbacks(raw)) pushUnique(list, url, false)
      } else {
        pushUnique(list, raw, allowHero)
      }
    }
    return list
  }, [game, allowHero, fit])

  const cached = loadedUrlByKey.get(key)
  const startIdx = cached ? Math.max(0, candidates.indexOf(cached)) : 0
  const [idx, setIdx] = useState(startIdx)
  const safeUrl = candidates[idx] ?? candidates[0] ?? null
  const primary = candidates[0] ?? ''
  const alreadyLoaded = !!(safeUrl && loadedUrlByKey.get(key) === safeUrl)
  const [loaded, setLoaded] = useState(alreadyLoaded)
  const [failed, setFailed] = useState(false)

  useEffect(() => {
    const hit = loadedUrlByKey.get(key)
    const hitIdx = hit ? candidates.indexOf(hit) : -1
    if (hitIdx >= 0) {
      setIdx(hitIdx)
      setLoaded(true)
      setFailed(false)
      return
    }
    setFailed(false)
    setIdx(0)
    setLoaded(!!loadedUrlByKey.get(key))
    // eslint-disable-next-line react-hooks/exhaustive-deps -- candidates rebuilt; primary is the identity signal
  }, [game.id, primary, fit])

  useEffect(() => {
    if (safeUrl && loadedUrlByKey.get(key) === safeUrl) {
      setLoaded(true)
    }
  }, [safeUrl, key])

  const displayUrl =
    safeUrl && !failed
      ? safeUrl
      : loadedUrlByKey.get(key) ?? null
  const showImg = !!displayUrl

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
          src={displayUrl}
          alt=""
          className="absolute inset-0 h-full w-full object-cover object-center"
          style={{ transform: 'translateZ(0)', opacity: loaded ? 1 : 0.02 }}
          draggable={false}
          loading={large ? 'eager' : 'lazy'}
          fetchPriority={large ? 'high' : 'low'}
          decoding="async"
          onLoad={(e) => {
            const el = e.currentTarget
            const ok =
              fit === 'banner' || fit === 'wide'
                ? isWideBitmap(el.naturalWidth, el.naturalHeight)
                : isPortraitBitmap(el.naturalWidth, el.naturalHeight)
            if (!ok) {
              if (displayUrl === safeUrl && idx + 1 < candidates.length) {
                setIdx((i) => i + 1)
                return
              }
              if (displayUrl === safeUrl) {
                loadedUrlByKey.delete(key)
                setFailed(true)
                setLoaded(false)
              }
              return
            }
            loadedUrlByKey.set(key, displayUrl!)
            setLoaded(true)
          }}
          onError={() => {
            if (safeUrl && displayUrl === safeUrl && idx + 1 < candidates.length) {
              setIdx((i) => i + 1)
              return
            }
            if (displayUrl === safeUrl) {
              setFailed(true)
              setLoaded(!!loadedUrlByKey.get(key) && loadedUrlByKey.get(key) !== safeUrl)
              if (loadedUrlByKey.get(key) === safeUrl) loadedUrlByKey.delete(key)
            }
          }}
        />
      )}
    </div>
  )
}

/** Stable tile under art. Never a per-title hue — those read as random green/purple. */
export function coverBg(): string {
  return '#050505'
}
