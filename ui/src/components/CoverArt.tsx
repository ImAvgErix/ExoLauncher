import { useEffect, useMemo, useRef, useState } from 'react'
import type { Game } from '../lib/host'
import { cn, monogram } from '../lib/utils'
import {
  isHeroShaped,
  isWashableBitmap,
  isWideBitmap,
  shouldArmCoverTimeout,
  shouldKeepCoverBitmap,
} from '../lib/coverFit'

/**
 * Cover art: monogram underneath; image after onLoad.
 * Native CoverArtService owns source selection. Library tiles stay portrait.
 * Steam app ids still resolve library_600x900 / library_capsule when coverUrl is empty.
 * Wide surfaces take store landscape art — Steam library_hero, or the hero file
 * the native warm caches for Epic / GOG / Riot — and fall back to the portrait
 * cover blurred into a wash rather than stretching a poster across a banner.
 */

/** A URL plus whether it needs the wash (portrait art standing in for a banner). */
type ArtCandidate = { url: string; derived: boolean }

type ArtGame = Pick<Game, 'id' | 'coverUrl' | 'coverSource' | 'launchTarget' | 'variants' | 'artRevision'>
type CoverGame = Pick<Game, 'id' | 'title' | 'coverUrl' | 'coverSource' | 'store' | 'launchTarget' | 'variants' | 'artRevision'>

const COVER_CACHE_ORIGIN = 'https://covers.exo-launcher.local'

/** Survives pin ↔ grid remounts so covers don’t flash to monogram. Key includes fit. */
const loadedUrlByKey = new Map<string, string>()

/**
 * Wide URLs that failed, and when. Keeps a remount off a dead URL without making
 * the miss permanent — the native cover warm may cache real hero art later.
 */
const wideMissAtMs = new Map<string, number>()
const WIDE_MISS_TTL_MS = 10_000

function cacheKey(gameId: string, fit: 'poster' | 'wash', artRevision?: number) {
  return `${gameId}::${fit}::${artRevision ?? 0}`
}

/** Revalidate only Exo-owned cache URLs; official CDN URLs keep their stable identity. */
function withArtRevision(url: string, artRevision?: number): string {
  if (!artRevision || !url.startsWith(`${COVER_CACHE_ORIGIN}/`)) return url
  return `${url}${url.includes('?') ? '&' : '?'}rev=${artRevision}`
}

function isFreshWideMiss(url: string): boolean {
  const at = wideMissAtMs.get(url)
  if (at === undefined) return false
  if (Date.now() - at < WIDE_MISS_TTL_MS) return true
  wideMissAtMs.delete(url)
  return false
}

/** Official Steam portrait posters (library_600x900 / library_capsule) — never heroes. */
function isOfficialSteamPortraitCdn(url: string): boolean {
  const hostOk =
    httpsHostIs(url, 'steamstatic.com') || httpsHostIs(url, 'steamcdn-a.akamaihd.net')
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
    url.includes('library_capsule') ||
    url.includes('portrait.png') ||
    url.includes('library_600x900_schinese')
  )
}

function isOfficialSteamHeroCdn(url: string): boolean {
  const hostOk =
    httpsHostIs(url, 'steamstatic.com') || httpsHostIs(url, 'steamcdn-a.akamaihd.net')
  return hostOk && url.includes('library_hero')
}

export function isSafeCoverUrl(url: string | null | undefined, allowHero = false): url is string {
  if (!url) return false
  if (url.startsWith('data:image/')) return true
  if (url.startsWith(`${COVER_CACHE_ORIGIN}/`)) return true
  if (isOfficialSteamPortraitCdn(url)) return true
  if (allowHero && isOfficialSteamHeroCdn(url)) return true
  if (isOfficialEpicPortraitCdn(url)) return true
  if (httpsHostIs(url, 'images.gog-statics.com') || httpsHostIs(url, 'gog-statics.com')) return true
  if (httpsHostIs(url, 'ddragon.leagueoflegends.com')) return true
  if (httpsHostIs(url, 'riotgames.com') || httpsHostIs(url, 'playvalorant.com') || httpsHostIs(url, 'leagueoflegends.com'))
    return true
  if (httpsHostIs(url, 'store-images.s-microsoft.com') || httpsHostIs(url, 'images-eds-ssl.xboxlive.com'))
    return true
  if (httpsHostIs(url, 'ubisoft.com') || httpsHostIs(url, 'ubi.com')) return true
  if (httpsHostIs(url, 'ea.com') || httpsHostIs(url, 'origin.com')) return true
  if (httpsHostIs(url, 'blizzard.com') || httpsHostIs(url, 'battle.net')) return true
  return false
}

function isIconCover(url: string | null | undefined, coverSource?: string | null): boolean {
  if (coverSource === 'icon') return true
  if (!url) return false
  return /\/icon_[^/?#]+\.png(?:$|[?#])/i.test(url)
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

/** Official Steam portraits. Cached hashed capsules first — classic 600x900 404s on newer apps. */
function steamPortraitUrlsForApp(appId: string, artRevision?: number): string[] {
  const base = `https://cdn.cloudflare.steamstatic.com/steam/apps/${appId}`
  return [
    withArtRevision(`${COVER_CACHE_ORIGIN}/${appId}.jpg`, artRevision),
    withArtRevision(`${COVER_CACHE_ORIGIN}/${appId}_2x.jpg`, artRevision),
    withArtRevision(`${COVER_CACHE_ORIGIN}/steam_${appId}.jpg`, artRevision),
    `${base}/library_600x900.jpg`,
    `${base}/library_capsule.jpg`,
    `${base}/library_600x900_2x.jpg`,
    `${base}/library_capsule_2x.jpg`,
    `${base}/portrait.png`,
  ]
}

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

/**
 * Mirrors CoverArtService.SanitizeId, which maps `char.IsLetterOrDigit` over
 * UTF-16 code units: Unicode letters and decimal digits survive, everything
 * else — including each half of a surrogate pair — becomes one underscore.
 * A looser regex here (`\p{N}`, or `/u` collapsing a surrogate pair to a single
 * replacement) yields a different filename than the host wrote, and the banner
 * silently falls back forever.
 */
function sanitizeCacheId(gameId: string): string {
  let out = ''
  for (let i = 0; i < gameId.length; i += 1) {
    const unit = gameId[i]
    out += /[\p{L}\p{Nd}]/u.test(unit) ? unit : '_'
  }
  return out
}

/**
 * Cache file the native cover warm writes landscape store art into (Epic wide
 * key images, GOG background, Riot splash, Steam hero). Mirrors
 * CoverArtService.WideArtFileName, so no extra bridge field is needed.
 */
function wideCacheUrl(gameId: string, artRevision?: number): string {
  return withArtRevision(`${COVER_CACHE_ORIGIN}/hero_${sanitizeCacheId(gameId)}.jpg`, artRevision)
}

/**
 * Landscape art for one title, best first: the native cache, then Steam's
 * remote heroes, then the portrait cover as a blurred wash.
 * The list is finite, so the chain always reaches a terminal state.
 */
function wideArtCandidates(game: ArtGame): ArtCandidate[] {
  const list: ArtCandidate[] = []
  const push = (url: string | null | undefined, derived: boolean) => {
    if (!url || !isSafeCoverUrl(url, true) || isRejectedCoverUrl(url, true)) return
    if (isIconCover(url, game.coverSource)) return
    if (!derived && isFreshWideMiss(url)) return
    if (list.some((candidate) => candidate.url === url)) return
    list.push({ url, derived })
  }
  push(wideCacheUrl(game.id, game.artRevision), false)
  for (const hero of steamHeroUrls(game)) push(hero, false)
  push(game.coverUrl ? withArtRevision(game.coverUrl, game.artRevision) : null, true)
  return list
}

function httpsHostIs(url: string, host: string): boolean {
  try {
    const parsed = new URL(url)
    return parsed.protocol === 'https:' &&
      (parsed.port === '' || parsed.port === '443') &&
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
  // Hero candidates are allowed only on wide surfaces. This includes the
  // native Exo `hero_<id>.jpg` cache as well as Steam's library_hero CDN art.
  if (allowHero && /(?:^|[/_])hero[_./-]/i.test(url)) return false
  return (
    /(?:^|[/_])hero[_./-]/i.test(url) ||
    /(?:^|[/_])banner[_./-]/i.test(url) ||
    /(?:^|[/_])header(?:\.[a-z0-9]+|$)/i.test(url) ||
    url.includes('capsule_231') ||
    url.includes('capsule_184') ||
    url.includes('capsule_sm') ||
    url.includes('capsule_616') ||
    url.includes('library_hero') ||
    url.includes('product_card') ||
    (url.includes('product_tile') && !/product_tile_256(?!0)/.test(url))
  )
}

function pushUnique(list: ArtCandidate[], url: string | null | undefined, allowHero: boolean) {
  if (!url || list.some((candidate) => candidate.url === url)) return
  if (!isSafeCoverUrl(url, allowHero) || isRejectedCoverUrl(url, allowHero)) return
  list.push({ url, derived: false })
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

function portraitArtCandidates(game: CoverGame): ArtCandidate[] {
  const list: ArtCandidate[] = []
  const raw = game.coverUrl ? withArtRevision(game.coverUrl, game.artRevision) : null
  if (raw && isSafeCoverUrl(raw) && !isRejectedCoverUrl(raw, false)) {
    for (const url of portraitFallbacks(raw)) pushUnique(list, url, false)
  }
  const appId = steamAppId(game)
  if (appId) {
    for (const url of steamPortraitUrlsForApp(appId, game.artRevision)) pushUnique(list, url, false)
  }
  return list
}

function loadBitmap(url: string, timeoutMs: number): Promise<HTMLImageElement | null> {
  return new Promise((resolve) => {
    const image = new Image()
    let settled = false
    const finish = (value: HTMLImageElement | null) => {
      if (settled) return
      settled = true
      window.clearTimeout(timeout)
      resolve(value)
    }
    const timeout = window.setTimeout(() => finish(null), timeoutMs)
    image.decoding = 'async'
    image.onload = () => finish(image)
    image.onerror = () => finish(null)
    image.src = url
  })
}

/** Decode the first screen's posters before the boot mark yields to the shell. */
export async function preloadInitialCoverArt(games: readonly CoverGame[], limit = 10): Promise<void> {
  if (typeof window === 'undefined' || typeof Image === 'undefined') return
  const unique = Array.from(new Map(games.map((game) => [game.id, game])).values()).slice(0, limit)
  await Promise.all(unique.map(async (game) => {
    const candidates = portraitArtCandidates(game)
    for (let index = 0; index < candidates.length; index += 1) {
      const candidate = candidates[index]
      const image = await loadBitmap(candidate.url, candidate.url.startsWith(`${COVER_CACHE_ORIGIN}/`) ? 2200 : 4500)
      if (!image) continue
      const icon = isIconCover(candidate.url, game.coverSource)
      if (!shouldKeepCoverBitmap(image.naturalWidth, image.naturalHeight, {
        icon,
        lastCandidate: index + 1 >= candidates.length,
      })) continue
      loadedUrlByKey.set(cacheKey(game.id, 'poster', game.artRevision), candidate.url)
      return
    }
  }))
}

export function CoverArt({
  game,
  className,
  large,
  preload = false,
}: {
  game: CoverGame
  className?: string
  large?: boolean
  /** Eagerly fetch a small set of likely-next art while its page is hidden. */
  preload?: boolean
}) {
  const key = cacheKey(game.id, 'poster', game.artRevision)
  const candidates = useMemo(() => portraitArtCandidates(game), [game])

  const cached = loadedUrlByKey.get(key)
  const cachedIdx = cached ? candidates.findIndex((c) => c.url === cached) : -1
  const startIdx = cachedIdx >= 0 ? cachedIdx : 0
  const [idx, setIdx] = useState(startIdx)
  const candidate = candidates[idx] ?? candidates[0] ?? null
  const safeUrl = candidate?.url ?? null
  const primary = candidates[0]?.url ?? ''
  const alreadyLoaded = !!(safeUrl && loadedUrlByKey.get(key) === safeUrl)
  const [loaded, setLoaded] = useState(alreadyLoaded)
  const [failed, setFailed] = useState(false)
  const imageRef = useRef<HTMLImageElement>(null)
  const coverRef = useRef<HTMLDivElement>(null)
  const eager = !!(large || preload)
  const [nearViewport, setNearViewport] = useState(eager)

  useEffect(() => {
    if (eager) {
      setNearViewport(true)
      return
    }
    setNearViewport(false)
    const element = coverRef.current
    if (!element || typeof IntersectionObserver === 'undefined') {
      setNearViewport(true)
      return
    }
    const observer = new IntersectionObserver(
      (entries) => {
        if (!entries.some((entry) => entry.isIntersecting)) return
        setNearViewport(true)
        observer.disconnect()
      },
      { rootMargin: '320px 0px' },
    )
    observer.observe(element)
    return () => observer.disconnect()
  }, [eager, game.id])

  useEffect(() => {
    const hit = loadedUrlByKey.get(key)
    const hitIdx = hit ? candidates.findIndex((c) => c.url === hit) : -1
    if (hitIdx >= 0) {
      setIdx(hitIdx)
      setLoaded(true)
      setFailed(false)
      return
    }
    if (hit) loadedUrlByKey.delete(key)
    setFailed(false)
    setIdx(0)
    setLoaded(false)
    // eslint-disable-next-line react-hooks/exhaustive-deps -- candidates rebuilt; primary is the identity signal
  }, [game.id, game.coverUrl, primary])

  useEffect(() => {
    if (safeUrl && loadedUrlByKey.get(key) === safeUrl) {
      setLoaded(true)
    }
  }, [safeUrl, key])

  // A stale native cache URL can stay pending in WebView2 instead of firing
  // error. Do not let that strand a card forever: give Exo's local cache a
  // short head start, then advance to the official CDN candidate. CDN misses
  // get a longer window so normal cold starts are not mistaken for failures.
  useEffect(() => {
    if (
      !safeUrl ||
      loadedUrlByKey.get(key) === safeUrl ||
      !shouldArmCoverTimeout({ eager, visible: nearViewport })
    ) return
    const timeout = window.setTimeout(() => {
      const image = imageRef.current
      if (image?.complete && image.naturalWidth > 0) return
      if (idx + 1 < candidates.length) setIdx((current) => current + 1)
      else setFailed(true)
    }, safeUrl.startsWith(`${COVER_CACHE_ORIGIN}/`) ? 1800 : 4500)
    return () => window.clearTimeout(timeout)
  }, [candidates.length, eager, idx, key, nearViewport, safeUrl])

  const remembered = loadedUrlByKey.get(key)
  const rememberedCandidate = remembered && candidates.some((entry) => entry.url === remembered)
    ? remembered
    : null
  const displayUrl =
    safeUrl && !failed
      ? safeUrl
      : rememberedCandidate
  const showImg = !!displayUrl
  const icon = isIconCover(displayUrl, game.coverSource)

  const acceptCover = (el: HTMLImageElement) => {
    const lastCandidate = idx + 1 >= candidates.length
    const ok = shouldKeepCoverBitmap(el.naturalWidth, el.naturalHeight, {
      icon,
      lastCandidate,
    })
    if (!ok) {
      if (displayUrl === safeUrl && !lastCandidate) {
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
  }

  return (
    <div
      ref={coverRef}
      className={cn(
        'relative overflow-hidden exo-cover',
        icon && 'is-icon exo-icon',
        className,
      )}
    >
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
          className={cn(
            'exo-cover-front',
            'object-cover',
            icon && 'exo-icon',
          )}
          style={{ opacity: loaded ? 1 : 0.02 }}
          draggable={false}
          loading={eager ? 'eager' : 'lazy'}
          fetchPriority={eager ? 'high' : 'low'}
          decoding="async"
          ref={(el) => {
            imageRef.current = el
            if (el && el.complete && el.naturalWidth > 0) acceptCover(el)
          }}
          onLoad={(e) => acceptCover(e.currentTarget)}
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

/**
 * Wide store art behind Now, the details card, and profile banners. Real
 * landscape art wins; a portrait cover is blurred into a wash instead of being
 * stretched; a title with no art at all renders nothing and keeps the monogram
 * surface underneath.
 */
export function HeroWash({
  game,
  className,
}: {
  game: Pick<Game, 'id' | 'coverUrl' | 'coverSource' | 'launchTarget' | 'variants' | 'artRevision'>
  className?: string
}) {
  const key = cacheKey(game.id, 'wash', game.artRevision)
  const candidates = useMemo(() => wideArtCandidates(game), [game])
  const primary = candidates[0]?.url ?? ''
  const cachedHit = loadedUrlByKey.get(key)
  const cachedIdx = cachedHit ? candidates.findIndex((c) => c.url === cachedHit) : -1
  const [idx, setIdx] = useState(cachedIdx >= 0 ? cachedIdx : 0)
  const [ok, setOk] = useState(cachedIdx >= 0)
  const [washed, setWashed] = useState(false)
  const [letterbox, setLetterbox] = useState(false)
  const appliedKey = useRef('')

  useEffect(() => {
    const hit = loadedUrlByKey.get(key)
    const hitIdx = hit ? candidates.findIndex((c) => c.url === hit) : -1
    setIdx(hitIdx >= 0 ? hitIdx : 0)
    setOk(hitIdx >= 0)
    setWashed(false)
    setLetterbox(false)
    appliedKey.current = ''
    // eslint-disable-next-line react-hooks/exhaustive-deps -- candidates rebuilt; primary is the identity signal
  }, [game.id, primary])

  const candidate = candidates[idx]
  if (!candidate) return null

  // Exactly one step per failure, and the list is finite: no retry loop.
  const advance = () => setIdx((i) => i + 1)

  const applyWashMetrics = (img: HTMLImageElement) => {
    const token = `${idx}:${candidate.url}:${img.naturalWidth}x${img.naturalHeight}`
    if (appliedKey.current === token) return
    appliedKey.current = token
    const wide = isWideBitmap(img.naturalWidth, img.naturalHeight)
    const hero = isHeroShaped(img.naturalWidth, img.naturalHeight)
    if (!candidate.derived) {
      // Store art that turned out portrait would stretch here — skip it.
      if (!wide) {
        advance()
        return
      }
      wideMissAtMs.delete(candidate.url)
      loadedUrlByKey.set(key, candidate.url)
      setWashed(false)
      setLetterbox(!hero)
      setOk(true)
      return
    }
    if (!isWashableBitmap(img.naturalWidth, img.naturalHeight)) {
      advance()
      return
    }
    // Hero-only covers are already landscape: real art, worth remembering.
    // A poster only ever gets washed, and is never memoised — the native
    // warm may cache real banner art for this title at any point.
    if (wide) loadedUrlByKey.set(key, candidate.url)
    setWashed(!wide)
    // 16:9 (and any non-hero landscape) still letterboxes; only portraits wash.
    setLetterbox(wide && !hero)
    setOk(true)
  }

  return (
    <>
      {letterbox && ok ? (
        <img
          src={candidate.url}
          alt=""
          className="exo-now-wash-img exo-cover-fill exo-cover-derived is-on"
          draggable={false}
          decoding="async"
          aria-hidden
        />
      ) : null}
      <img
        src={candidate.url}
        alt=""
        className={cn(
          'exo-now-wash-img',
          letterbox && 'is-letterbox',
          washed && 'exo-cover-derived',
          washed && 'is-derived',
          ok && 'is-on',
          className,
        )}
        draggable={false}
        decoding="async"
        ref={(el) => {
          // Cached bitmaps can skip onLoad in WebView2 after a remount.
          // Re-apply after the effect clears letterbox on a cache hit.
          if (el && el.complete && el.naturalWidth > 0) applyWashMetrics(el)
        }}
        onLoad={(e) => applyWashMetrics(e.currentTarget)}
        onError={() => {
          if (!candidate.derived) wideMissAtMs.set(candidate.url, Date.now())
          advance()
        }}
      />
    </>
  )
}
