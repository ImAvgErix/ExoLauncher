import { useCallback, useEffect, useMemo, useRef, useState, useSyncExternalStore, type CSSProperties, type RefObject } from 'react'
import type { Game } from '../lib/host'
import { cn } from '../lib/utils'
import { steamAppId } from '../lib/library'
import { observeNearViewport } from '../lib/nearViewport'
import { coverThumbWidth, dimThumbUrl, isDimThumbUrl, isThumbUrl, thumbUrl, useCoverThumbWidth } from '../lib/coverThumb'
import {
  coverBitmapFit,
  isHeroShaped,
  isWashableBitmap,
  isWideBitmap,
  shouldArmCoverTimeout,
  shouldKeepCoverBitmap,
} from '../lib/coverFit'

/**
 * Cover art: gradient + title underneath; image after onLoad.
 * Native CoverArtService owns source selection. Library tiles stay portrait.
 * Steam app ids still resolve library_600x900 / library_capsule when coverUrl is empty.
 * Wide surfaces take store landscape art — Steam library_hero, or the hero file
 * the native warm caches for Epic / GOG / Riot — and fall back to the portrait
 * cover blurred into a wash rather than stretching a poster across a banner.
 */

/** First-screen posters at the default 1400×900 library grid (~7×3 plus slack). */
export const VISIBLE_COVER_PREFETCH = 14

/** A URL plus whether it needs the wash (portrait art standing in for a banner). */
type ArtCandidate = { url: string; derived: boolean }

type ArtGame = Pick<Game, 'id' | 'coverUrl' | 'coverSource' | 'launchTarget' | 'variants' | 'artRevision'> & { heroUrl?: string | null }
type CoverGame = Pick<Game, 'id' | 'title' | 'coverUrl' | 'coverSource' | 'store' | 'launchTarget' | 'variants' | 'artRevision'>

const COVER_CACHE_ORIGIN = 'https://covers.exo-launcher.local'

/** Survives grid remounts so covers don't flash to the title plate. Key includes fit. */
const loadedUrlByKey = new Map<string, string>()
/** Keys include artRevision, so refetches mint new keys; cap the map so a
 * long session cannot grow it forever. Oldest-inserted entries go first. */
const LOADED_URL_MAX_ENTRIES = 4096

function rememberLoadedUrl(key: string, url: string): void {
  if (!loadedUrlByKey.has(key) && loadedUrlByKey.size >= LOADED_URL_MAX_ENTRIES) {
    const oldest = loadedUrlByKey.keys().next().value
    if (oldest !== undefined) loadedUrlByKey.delete(oldest)
  }
  loadedUrlByKey.set(key, url)
}

/**
 * Wide URLs that failed, and when. Keeps a remount off a dead URL without making
 * the miss permanent — the native cover warm may cache real hero art later.
 */
const wideMissAtMs = new Map<string, number>()
const WIDE_MISS_TTL_MS = 10_000
const WIDE_MISS_MAX_ENTRIES = 1024

function cacheKey(gameId: string, fit: 'poster' | 'full' | 'wash', artRevision?: number) {
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

function isOfficialSteamHeaderCdn(url: string): boolean {
  const hostOk =
    httpsHostIs(url, 'steamstatic.com') || httpsHostIs(url, 'steamcdn-a.akamaihd.net')
  return hostOk && (
    url.includes('/header.jpg') ||
    url.includes('/header_2x.jpg') ||
    url.includes('capsule_616x353')
  )
}

/** Vendor CDNs whose art may be shown as-is (https, default port, host or subdomain). */
const SAFE_COVER_HOSTS: readonly string[] = [
  'gog-statics.com',
  'ddragon.leagueoflegends.com',
  'riotgames.com',
  'playvalorant.com',
  'leagueoflegends.com',
  'store-images.s-microsoft.com',
  'images-eds-ssl.xboxlive.com',
  'ubisoft.com',
  'ubi.com',
  'ea.com',
  'origin.com',
  'blizzard.com',
  'battle.net',
  'images.igdb.com',
  'steamgriddb.com',
  'cdn.akamai.steamstatic.com',
]

function isSafeCoverUrl(url: string | null | undefined, allowHero = false): url is string {
  if (!url) return false
  if (url.startsWith('data:image/') || url.startsWith(`${COVER_CACHE_ORIGIN}/`)) return true
  if (isOfficialSteamPortraitCdn(url) || isOfficialEpicPortraitCdn(url)) return true
  if (allowHero && (isOfficialSteamHeroCdn(url) || isOfficialSteamHeaderCdn(url))) return true
  return SAFE_COVER_HOSTS.some((host) => httpsHostIs(url, host))
}

function isIconCover(url: string | null | undefined, coverSource?: string | null): boolean {
  if (coverSource === 'icon') return true
  if (!url) return false
  return /\/icon_[^/?#]+\.png(?:$|[?#])/i.test(url)
}

/** 1920-wide first: it fills any hero Exo draws and arrives in half the time of the 2x. */
function steamHeroUrlsForApp(appId: string): string[] {
  const base = `https://cdn.cloudflare.steamstatic.com/steam/apps/${appId}`
  return [`${base}/library_hero.jpg`, `${base}/library_hero_2x.jpg`]
}

/** Official Steam portraits. Local cache first — classic 600x900 404s on newer apps. */
function steamPortraitUrlsForApp(appId: string, artRevision?: number): { remote: string[]; cache: string[] } {
  const base = `https://cdn.cloudflare.steamstatic.com/steam/apps/${appId}`
  return {
    remote: [
      `${base}/library_600x900_2x.jpg`,
      `${base}/library_600x900.jpg`,
      `${base}/library_capsule.jpg`,
    ],
    cache: [
      withArtRevision(`${COVER_CACHE_ORIGIN}/${appId}_2x.jpg`, artRevision),
      withArtRevision(`${COVER_CACHE_ORIGIN}/${appId}.jpg`, artRevision),
      withArtRevision(`${COVER_CACHE_ORIGIN}/steam_${appId}.jpg`, artRevision),
    ],
  }
}

function steamHeroUrls(
  game: Pick<Game, 'id' | 'coverUrl' | 'launchTarget' | 'variants'>,
): string[] {
  const appId = steamAppId(game)
  return appId ? steamHeroUrlsForApp(appId) : []
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
  push(game.heroUrl, false)
  const appId = steamAppId(game)
  if (appId) {
    const base = `https://cdn.cloudflare.steamstatic.com/steam/apps/${appId}`
    push(`${base}/header.jpg`, false)
    push(`${base}/capsule_616x353.jpg`, false)
  }
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
  if (allowHero && isOfficialSteamHeaderCdn(url)) return false
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

function isIgdbCoverHost(url: string): boolean {
  return httpsHostIs(url, 'images.igdb.com')
}

function isSteamGridDbHost(url: string): boolean {
  return httpsHostIs(url, 'cdn.steamgriddb.com') || httpsHostIs(url, 'steamgriddb.com')
}

/** Prefer IGDB cover_big_2x when the game already has an IGDB cover URL. */
function igdbCoverBig2xUrl(url: string): string | null {
  if (!isIgdbCoverHost(url)) return null
  try {
    const parsed = new URL(url)
    const nextPath = parsed.pathname.replace(/\/t_[^/]+\//, '/t_cover_big_2x/')
    if (nextPath === parsed.pathname && !/\/t_cover_big_2x\//.test(parsed.pathname)) return url
    parsed.pathname = nextPath
    return parsed.toString()
  } catch {
    return url
  }
}

/** SGDB 600×900 only when the game already has an SGDB URL (no SteamDB scrape). */
function steamGridDbPortraitUrl(url: string): string | null {
  if (!isSteamGridDbHost(url)) return null
  return url
}

/**
 * Grid cards pass a thumbnail width; each of Exo's cached files is then tried
 * as its thumbnail and, right after, as itself. A thumbnail that is slow (its
 * first scale, a busy host) falls back to the same art at full size, never to
 * a smaller poster further down the ladder.
 */
function portraitArtCandidates(game: Pick<CoverGame, 'coverUrl' | 'artRevision'>, appId: string | null, thumb: number | null = null, dim = false): ArtCandidate[] {
  const list: ArtCandidate[] = []
  const raw = game.coverUrl ? withArtRevision(game.coverUrl, game.artRevision) : null
  if (raw && isSafeCoverUrl(raw) && !isRejectedCoverUrl(raw, false)) {
    pushUnique(list, igdbCoverBig2xUrl(raw), false)
    for (const url of portraitFallbacks(raw)) pushUnique(list, url, false)
    pushUnique(list, steamGridDbPortraitUrl(raw), false)
  }
  if (appId) {
    const steam = steamPortraitUrlsForApp(appId, game.artRevision)
    for (const url of steam.cache) pushUnique(list, url, false)
    for (const url of steam.remote) pushUnique(list, url, false)
    const base = `https://cdn.cloudflare.steamstatic.com/steam/apps/${appId}`
    pushSteamStoreBitmap(list, `${base}/header.jpg`)
    pushSteamStoreBitmap(list, `${base}/capsule_616x353.jpg`)
  }
  if (thumb == null) return list
  return list.flatMap((candidate) => {
    const small = thumbUrl(candidate.url, thumb)
    if (small === candidate.url) return [candidate]
    return dim ? [{ ...candidate, url: dimThumbUrl(candidate.url, thumb) }, { ...candidate, url: small }, candidate]
               : [{ ...candidate, url: small }, candidate]
  })
}

/** Landscape store bitmaps only after every portrait URL has been tried. */
function pushSteamStoreBitmap(list: ArtCandidate[], url: string) {
  if (list.some((candidate) => candidate.url === url)) return
  try {
    const parsed = new URL(url)
    const host = parsed.hostname
    if (parsed.protocol !== 'https:') return
    if (!(host.endsWith('steamstatic.com') || host === 'steamcdn-a.akamaihd.net')) return
  } catch {
    return
  }
  list.push({ url, derived: true })
}

function artTimeoutMs(url: string): number {
  // The host answers every thumbnail (404 when the cover is gone); the first
  // request for a cover scales it, so give it time rather than skip to other art.
  if (isThumbUrl(url)) return 2000
  // The full file is usually the fallback for a slow thumbnail: give it time
  // to arrive rather than drop to a smaller poster.
  if (url.startsWith(`${COVER_CACHE_ORIGIN}/`)) return 1500
  if (
    url.includes('library_600x900') ||
    url.includes('library_capsule') ||
    url.includes('portrait.png')
  )
    return 800
  return 1200
}

type BitmapSize = { naturalWidth: number; naturalHeight: number }
const pendingBitmaps = new Map<string, Promise<BitmapSize | null>>()

function loadBitmap(url: string, timeoutMs: number): Promise<BitmapSize | null> {
  const existing = pendingBitmaps.get(url)
  if (existing) return existing
  const loading = new Promise<BitmapSize | null>((resolve) => {
    const image = new Image()
    let settled = false
    const finish = (value: BitmapSize | null) => {
      if (settled) return
      settled = true
      window.clearTimeout(timeout)
      image.onload = null
      image.onerror = null
      if (!value) image.removeAttribute('src')
      resolve(value)
    }
    const timeout = window.setTimeout(() => finish(null), timeoutMs)
    image.decoding = 'async'
    image.onload = () => {
      if (settled) return
      const decoded = typeof image.decode === 'function' ? image.decode() : Promise.resolve()
      void decoded.catch(() => undefined).then(() => finish({ naturalWidth: image.naturalWidth, naturalHeight: image.naturalHeight }))
    }
    image.onerror = () => finish(null)
    image.src = url
  })
  const pending = loading.finally(() => { if (pendingBitmaps.get(url) === pending) pendingBitmaps.delete(url) })
  pendingBitmaps.set(url, pending)
  return pending
}

/** Decode the first screen's posters before the boot mark yields to the shell. */
export async function preloadInitialCoverArt(games: readonly CoverGame[], limit = VISIBLE_COVER_PREFETCH, signal?: AbortSignal): Promise<void> {
  if (typeof window === 'undefined' || typeof Image === 'undefined') return
  const unique = Array.from(new Map(games.map((game) => [game.id, game])).values()).slice(0, limit)
  const thumb = coverThumbWidth()
  await Promise.all(unique.map(async (game) => {
    const candidates = portraitArtCandidates(game, steamAppId(game), thumb)
    if (rememberedIndex(cacheKey(game.id, 'poster', game.artRevision), candidates) >= 0) return
    // Only the card's own art (its thumbnail, then that file). Anything further
    // down is the card's call: a warm that timed out on a busy host used to pin
    // the 300 px poster as this card's remembered art.
    const own = isThumbUrl(candidates[0]?.url ?? '') ? 2 : 1
    for (let index = 0; index < Math.min(own, candidates.length); index += 1) {
      if (signal?.aborted) return
      const candidate = candidates[index]
      const image = await loadBitmap(candidate.url, artTimeoutMs(candidate.url))
      if (!image) continue
      const icon = isIconCover(candidate.url, game.coverSource)
      if (!shouldKeepCoverBitmap(image.naturalWidth, image.naturalHeight, {
        icon,
        derived: candidate.derived,
        lastCandidate: index + 1 >= candidates.length,
      })) continue
      rememberLoadedUrl(cacheKey(game.id, 'poster', game.artRevision), candidate.url)
      return
    }
  }))
}

/** Warm both posters and detail backgrounds for the complete library. */
export async function preloadLibraryArtwork(games: readonly CoverGame[], concurrency = 6, signal?: AbortSignal): Promise<void> {
  const unique = Array.from(new Map(games.map((game) => [game.id, game])).values())
  let cursor = 0
  await Promise.all(Array.from({ length: Math.min(concurrency, unique.length) }, async () => {
    while (cursor < unique.length && !signal?.aborted && !document.hidden) {
      const game = unique[cursor++]
      await Promise.all([
        preloadInitialCoverArt([game], 1, signal),
        (async () => {
          const candidates = wideArtCandidates(game)
          if (rememberedIndex(cacheKey(game.id, 'wash', game.artRevision), candidates) >= 0) return
          for (const candidate of candidates) {
            if (signal?.aborted || document.hidden) return
            const bitmap = await loadBitmap(candidate.url, artTimeoutMs(candidate.url))
            if (!bitmap || (!candidate.derived && !isWideBitmap(bitmap.naturalWidth, bitmap.naturalHeight))) continue
            rememberLoadedUrl(cacheKey(game.id, 'wash', game.artRevision), candidate.url)
            break
          }
        })(),
      ])
    }
  }))
}

/** Lazy tiles only start their fallback ladder once they are near the viewport. */
function useNearViewport(
  ref: RefObject<HTMLElement | null>,
  eager: boolean,
  resetKey: string,
): boolean {
  const [near, setNear] = useState(eager)
  useEffect(() => {
    if (eager) {
      setNear(true)
      return
    }
    setNear(false)
    const element = ref.current
    if (!element || typeof IntersectionObserver === 'undefined') {
      setNear(true)
      return
    }
    return observeNearViewport(element, () => setNear(true))
  }, [eager, ref, resetKey])
  return near
}

function rememberedIndex(key: string, candidates: ArtCandidate[]): number {
  const hit = loadedUrlByKey.get(key)
  return hit ? candidates.findIndex((c) => c.url === hit) : -1
}

/** The remembered URL, but only while it is still one of this card's candidates. */
function rememberedCandidateUrl(key: string, candidates: ArtCandidate[]): string | null {
  const hit = loadedUrlByKey.get(key)
  return hit && candidates.some((entry) => entry.url === hit) ? hit : null
}

/** Keyed on what the art depends on, not the card object a library refresh replaces.
 * The sheet's large cover keeps the full file; everything else draws at card size. */
function usePortraitCandidates(game: CoverGame, large: boolean, dim = false): ArtCandidate[] {
  const gridThumb = useCoverThumbWidth()
  const thumb = large ? null : gridThumb
  const { coverUrl, artRevision } = game
  const appId = steamAppId(game)
  return useMemo(() => portraitArtCandidates({ coverUrl, artRevision }, appId, thumb, dim), [coverUrl, artRevision, appId, thumb, dim])
}

/** Grid cards and the sheet's full-size cover remember their bitmaps separately. */
function posterCacheKey(game: CoverGame, large?: boolean): string {
  return cacheKey(game.id, large ? 'full' : 'poster', game.artRevision)
}

function subscribeVisibility(update: () => void): () => void {
  document.addEventListener('visibilitychange', update)
  return () => document.removeEventListener('visibilitychange', update)
}

/**
 * A minimized window loads nothing (lazy images wait for layout that a hidden
 * page never runs) but its timers still fire, so a fallback timer armed while
 * hidden walked every card down its ladder to the title plate.
 */
function usePageVisible(): boolean {
  return useSyncExternalStore(subscribeVisibility, () => !document.hidden, () => true)
}

/**
 * A card that ran out of art (or stands on a landscape stand-in) tries again,
 * once each time it comes back into view or the window is shown or focused
 * again: a host that was busy or a file mid-rewrite is not a reason to show the
 * title plate or a banner for the rest of the session.
 */
function useRetryOnReturn(armed: boolean, ref: RefObject<HTMLElement | null>, retry: () => void): void {
  const retryLatest = useRef(retry)
  /** Set when the effect is torn down while still armed: the Library room was hidden (Activity). */
  const leftArmed = useRef(false)
  useEffect(() => { retryLatest.current = retry })
  useEffect(() => {
    const element = ref.current
    if (!armed || !element) return
    let inView = false
    // Coming back from another room re-runs this effect; that return counts
    // like scrolling the card back into view.
    let wasOut = leftArmed.current
    leftArmed.current = false
    let done = false
    const fire = () => {
      if (done) return
      done = true
      retryLatest.current()
    }
    const observer = typeof IntersectionObserver === 'undefined' ? null : new IntersectionObserver((entries) => {
      const entry = entries.at(-1)
      if (!entry) return
      inView = entry.isIntersecting
      if (!inView) wasOut = true
      else if (wasOut) fire()
    })
    observer?.observe(element)
    const onShown = () => { if (!document.hidden && inView) fire() }
    document.addEventListener('visibilitychange', onShown)
    window.addEventListener('focus', onShown)
    return () => {
      leftArmed.current = !done
      observer?.disconnect()
      document.removeEventListener('visibilitychange', onShown)
      window.removeEventListener('focus', onShown)
    }
  }, [armed, ref])
}

/** A retry must reach the host again, not a cached miss; only Exo's own origins ignore the query. */
function withAttempt(url: string, attempt: number): string {
  if (!attempt || !(url.startsWith(`${COVER_CACHE_ORIGIN}/`) || isThumbUrl(url))) return url
  return `${url}${url.includes('?') ? '&' : '?'}retry=${attempt}`
}

/** Where this card is on its fallback ladder, for one key and one candidate list. */
type Ladder = { id: string; idx: number; failed: boolean; attempt: number }

/**
 * Starts on the art this card last decoded, unless that was a landscape stand-in:
 * a Steam account switch shows new titles before their posters are cached, and a
 * ladder that started on the header again never reached the poster that landed.
 */
function freshLadder(id: string, key: string, candidates: ArtCandidate[], attempt: number): Ladder {
  const remembered = rememberedIndex(key, candidates)
  return { id, idx: remembered >= 0 && !candidates[remembered].derived ? remembered : 0, failed: false, attempt }
}

function GridBitmap({ url }: { url: string | null }) {
  return url ? <img src={url} alt="" className="exo-cover-front" data-under="" draggable={false} decoding="async" /> : null
}

function isDecoded(el: HTMLImageElement | null): el is HTMLImageElement {
  return !!el && el.complete && el.naturalWidth > 0
}

type CoverFit = { url: string; fit: 'cover' | 'contain' }
function currentCoverFit(metrics: CoverFit | null, url: string | null, icon: boolean) {
  if (icon) return 'contain'
  return metrics?.url === url ? metrics.fit : 'cover'
}

/**
 * New art (a refresh, a sharper thumbnail, a custom cover) restarts the ladder
 * from what this card already decoded. Adjusted during render, so no frame
 * pairs the new list with the old rung.
 */
function useCoverLadder(key: string, candidates: ArtCandidate[]) {
  const ladderId = `${key}\n${candidates[0]?.url ?? ''}`
  const [stored, setLadder] = useState(() => freshLadder(ladderId, key, candidates, 0))
  const ladder = stored.id === ladderId ? stored : freshLadder(ladderId, key, candidates, stored.attempt)
  if (ladder !== stored) setLadder(ladder)
  const count = candidates.length
  const advance = useCallback(() => {
    setLadder((current) => {
      if (current.id !== ladderId || current.failed) return current
      return current.idx + 1 >= count ? { ...current, failed: true } : { ...current, idx: current.idx + 1 }
    })
  }, [count, ladderId])
  const retry = useCallback(() => {
    setLadder((current) => current.id === ladderId ? { ...current, idx: 0, failed: false, attempt: current.attempt + 1 } : current)
  }, [ladderId])
  return { ...ladder, advance, retry }
}

/**
 * A stale native cache URL can stay pending in WebView2 instead of firing
 * error, so a card that has waited long enough moves to the next candidate.
 * Only while the page is shown: a hidden page loads nothing.
 */
function useCoverTimeout(url: string | null, armed: boolean, imageRef: RefObject<HTMLImageElement | null>, advance: () => void): void {
  const pageVisible = usePageVisible()
  useEffect(() => {
    if (!url || !armed || !pageVisible) return
    const timeout = window.setTimeout(() => {
      if (!isDecoded(imageRef.current)) advance()
    }, artTimeoutMs(url))
    return () => window.clearTimeout(timeout)
  }, [advance, armed, imageRef, pageVisible, url])
}

/** What a card draws this frame: the rung it tries, the URL it shows, and the last good bitmap under it. */
function coverFrame({ key, candidates, idx, failed, attempt, loadedUrl, fallbackSrc }: {
  key: string
  candidates: ArtCandidate[]
  idx: number
  failed: boolean
  attempt: number
  loadedUrl: string | null
  fallbackSrc: string | null
}) {
  const safeUrl = (candidates[idx] ?? candidates[0])?.url ?? null
  const displayUrl = safeUrl && !failed ? safeUrl : rememberedCandidateUrl(key, candidates)
  const src = displayUrl ? withAttempt(displayUrl, attempt) : null
  const loaded = !!displayUrl && (loadedUrl === displayUrl || loadedUrlByKey.get(key) === displayUrl)
  const underlay = !loaded && fallbackSrc !== src ? fallbackSrc : null
  return { safeUrl, displayUrl, src, loaded, underlay }
}

export function CoverArt({
  game,
  className,
  large,
  preload = false,
  dim = false,
}: {
  game: CoverGame
  className?: string
  large?: boolean
  /** Eagerly fetch a small set of likely-next art while its page is hidden. */
  preload?: boolean
  /** Not installed: the host-dimmed thumbnail leads, and hover shows the plain one over it. */
  dim?: boolean
}) {
  const key = posterCacheKey(game, large)
  const candidates = usePortraitCandidates(game, !!large, dim)
  const eager = !!(large || preload)
  const { idx, failed, attempt, advance, retry } = useCoverLadder(key, candidates)

  const [loadedUrl, setLoadedUrl] = useState<string | null>(null)
  /** The last bitmap this card decoded; it stays up while new art loads, so a card never blanks. */
  const [shownSrc, setShownSrc] = useState<string | null>(() => loadedUrlByKey.get(key) ?? null)
  const [bitmapFit, setBitmapFit] = useState<CoverFit | null>(null)
  const imageRef = useRef<HTMLImageElement>(null)
  const coverRef = useRef<HTMLDivElement>(null)
  const nearViewport = useNearViewport(coverRef, eager, game.id)
  const lastCandidate = idx + 1 >= candidates.length
  const { safeUrl, displayUrl, src, loaded, underlay } = coverFrame({
    key, candidates, idx, failed, attempt, loadedUrl,
    fallbackSrc: shownSrc ?? (large ? loadedUrlByKey.get(posterCacheKey(game)) ?? null : null),
  })
  const icon = isIconCover(displayUrl, game.coverSource)

  useCoverTimeout(
    safeUrl,
    !failed && loadedUrlByKey.get(key) !== safeUrl && shouldArmCoverTimeout({ eager, visible: nearViewport }),
    imageRef,
    advance,
  )
  // A landscape stand-in is a card that ran out of posters, not its art.
  useRetryOnReturn(failed || (loaded && !!candidates[idx]?.derived), coverRef, retry)

  /** Give up on the current URL: try the next one, or settle on the plate. */
  function rejectCurrent() {
    if (displayUrl !== safeUrl) return
    if (lastCandidate && loadedUrlByKey.get(key) === safeUrl) loadedUrlByKey.delete(key)
    advance()
  }

  const acceptCover = async (el: HTMLImageElement) => {
    const acceptedUrl = displayUrl
    const acceptedSrc = src
    try {
      if (loadedUrlByKey.get(key) !== acceptedUrl) await el.decode()
    } catch { }
    if (imageRef.current !== el || !el.isConnected || !acceptedUrl) return
    const ok = shouldKeepCoverBitmap(el.naturalWidth, el.naturalHeight, {
      icon,
      derived: candidates.find((entry) => entry.url === acceptedUrl)?.derived,
      lastCandidate,
    })
    if (!ok) {
      rejectCurrent()
      return
    }
    rememberLoadedUrl(key, acceptedUrl)
    const fit = coverBitmapFit(el.naturalWidth, el.naturalHeight, icon)
    setBitmapFit(previous => previous?.url === acceptedUrl && previous.fit === fit ? previous : { url: acceptedUrl, fit })
    setLoadedUrl(acceptedUrl)
    setShownSrc(acceptedSrc)
  }
  const acceptLatest = useRef(acceptCover)
  acceptLatest.current = acceptCover
  // Stable, so React calls it when the image mounts, not on every commit.
  // Leaving the Library for another room hides it (Activity) and React detaches
  // refs while the image stays in the page; a bitmap that finished loading then
  // was dropped and the card drew nothing until the room was shown again.
  const attachImage = useCallback((el: HTMLImageElement | null) => {
    if (imageRef.current === el || (!el && imageRef.current?.isConnected)) return
    imageRef.current = el
    if (isDecoded(el)) void acceptLatest.current(el)
  }, [])



  return (
    <div ref={coverRef} className={cn('exo-cover', icon && 'is-icon exo-icon', className)}>
      <div className={cn('exo-cover-mono', (loaded || underlay) && 'is-under', large && 'is-large')} aria-hidden>
        <span>{game.title}</span>
      </div>
      <GridBitmap url={underlay} />
      <CoverFaces
        coverKey={key}
        src={src}
        dim={dim}
        loaded={loaded}
        icon={icon}
        eager={eager}
        fit={currentCoverFit(bitmapFit, displayUrl, icon)}
        rootRef={coverRef}
        attachImage={attachImage}
        onLoad={acceptCover}
        onError={rejectCurrent}
      />
    </div>
  )
}

/**
 * The plain thumbnail over a dim card's, mounted on the card's first hover/focus
 * and kept mounted after, fading in with the same 160ms the filter used. Only
 * loads when the dim thumbnail won: a failed one already drew the plain art under
 * the old CSS filter, whose own hover rule still undims it.
 */
function CoverFaces({ coverKey, src, dim, loaded, icon, eager, fit, rootRef, attachImage, onLoad, onError }: {
  coverKey: string
  src: string | null
  dim: boolean
  loaded: boolean
  icon: boolean
  eager: boolean
  fit: CSSProperties['objectFit']
  rootRef: RefObject<HTMLDivElement | null>
  attachImage: (el: HTMLImageElement | null) => void
  onLoad: (el: HTMLImageElement) => void
  onError: () => void
}) {
  const { undimArmed, undim } = useUndim(dim, rootRef)
  return (
    <>
      {src && (
        <img
          key={`${coverKey}:${src}`}
          src={src}
          alt=""
          className={cn('exo-cover-front', icon && 'exo-icon', dim && !isDimThumbUrl(src) && 'is-dim-fallback')}
          style={{ opacity: loaded ? 1 : 0, objectFit: fit }}
          draggable={false}
          loading={eager ? 'eager' : 'lazy'}
          fetchPriority={eager ? 'high' : 'low'}
          decoding="async"
          ref={attachImage}
          onLoad={(e) => onLoad(e.currentTarget)}
          onError={onError}
        />
      )}
      <UndimCover src={src} undimArmed={undimArmed} undim={undim} fit={fit} />
    </>
  )
}

function UndimCover({ src, undimArmed, undim, fit }: {
  src: string | null
  undimArmed: boolean
  undim: boolean
  fit: CSSProperties['objectFit']
}) {
  if (!undimArmed || !src || !isDimThumbUrl(src)) return null
  return (
    <img
      src={src.replace('&dim=1', '')}
      alt=""
      className={cn('exo-cover-front exo-cover-undim', undim && 'is-on')}
      style={{ objectFit: fit }}
      draggable={false}
      decoding="async"
      aria-hidden="true"
    />
  )
}

/** Hover/focus state of the card the cover sits in, armed once and remembered. */
function useUndim(dim: boolean, coverRef: RefObject<HTMLDivElement | null>) {
  const [armed, setArmed] = useState(false)
  const [on, setOn] = useState(false)
  useEffect(() => {
    if (!dim) return
    const hit = coverRef.current?.closest('.exo-tile-hit')
    if (!hit) return
    const enter = () => { setArmed(true); setOn(true) }
    const leave = () => setOn(false)
    hit.addEventListener('pointerenter', enter)
    hit.addEventListener('pointerleave', leave)
    hit.addEventListener('focusin', enter)
    hit.addEventListener('focusout', leave)
    return () => {
      hit.removeEventListener('pointerenter', enter)
      hit.removeEventListener('pointerleave', leave)
      hit.removeEventListener('focusin', enter)
      hit.removeEventListener('focusout', leave)
    }
  }, [dim, coverRef])
  return { undimArmed: armed, undim: on }
}

/**
 * Wide store art behind the game sheet. Real
 * landscape art wins; a portrait cover is blurred into a wash instead of being
 * stretched; a title with no art at all renders nothing and keeps the monogram
 * surface underneath.
 */
export function HeroWash({
  game,
  className,
}: {
  game: ArtGame
  className?: string
}) {
  const key = cacheKey(game.id, 'wash', game.artRevision)
  const candidates = useMemo(() => wideArtCandidates(game), [game])
  const primary = candidates[0]?.url ?? ''

  // Reset before the image mounts. A cached 404 can fire before effects, so a
  // mount effect that resets idx to zero would erase its fallback advancement.
  return <HeroWashImage key={`${key}:${primary}`} imageCacheKey={key} candidates={candidates} className={className} />
}

function HeroWashImage({ imageCacheKey: key, candidates, className }: {
  imageCacheKey: string
  candidates: ArtCandidate[]
  className?: string
}) {
  const cachedHit = loadedUrlByKey.get(key)
  const cachedIdx = cachedHit ? candidates.findIndex((c) => c.url === cachedHit) : -1
  const [idx, setIdx] = useState(cachedIdx >= 0 ? cachedIdx : 0)
  const [ok, setOk] = useState(cachedIdx >= 0)
  const [washed, setWashed] = useState(false)
  const [letterbox, setLetterbox] = useState(false)
  const appliedKey = useRef('')

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
      rememberLoadedUrl(key, candidate.url)
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
    if (wide) rememberLoadedUrl(key, candidate.url)
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
          if (!candidate.derived) {
            if (wideMissAtMs.size >= WIDE_MISS_MAX_ENTRIES && !wideMissAtMs.has(candidate.url)) {
              const oldest = wideMissAtMs.keys().next().value
              if (oldest !== undefined) wideMissAtMs.delete(oldest)
            }
            wideMissAtMs.set(candidate.url, Date.now())
          }
          advance()
        }}
      />
    </>
  )
}
