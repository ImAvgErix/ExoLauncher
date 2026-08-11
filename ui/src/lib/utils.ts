import { clsx, type ClassValue } from 'clsx'
import { twMerge } from 'tailwind-merge'
import type { Game, SortMode } from './host'

export function cn(...inputs: ClassValue[]) {
  return twMerge(clsx(inputs))
}

/**
 * Search normalization deliberately treats punctuation, spacing, and accents as
 * cosmetic. Keep this aligned with StoreSearchService so an installed result
 * does not disappear while the catalog request is still in flight.
 */
export function normalizeSearchText(value: string): string {
  const decomposed = value.normalize('NFD').toLowerCase()
  let output = ''
  let lastWasSpace = true
  for (const char of decomposed) {
    if (/\p{Mark}/u.test(char)) continue
    if (/\p{Letter}|\p{Number}/u.test(char)) {
      output += char
      lastWasSpace = false
    } else if (!lastWasSpace) {
      output += ' '
      lastWasSpace = true
    }
  }
  return output.trim()
}

function searchTokens(value: string): string[] {
  return normalizeSearchText(value).split(' ').filter(Boolean)
}

function allowedSearchEditDistance(length: number): number {
  if (length <= 4) return 1
  if (length <= 7) return 1
  return 2
}

function boundedDamerauLevenshtein(left: string, right: string, max: number): number {
  if (Math.abs(left.length - right.length) > max) return max + 1
  const previousPrevious = new Array<number>(right.length + 1).fill(0)
  let previous = Array.from({ length: right.length + 1 }, (_, index) => index)
  let current = new Array<number>(right.length + 1).fill(0)

  for (let i = 1; i <= left.length; i += 1) {
    current[0] = i
    let rowMin = current[0]
    for (let j = 1; j <= right.length; j += 1) {
      const cost = left[i - 1] === right[j - 1] ? 0 : 1
      let value = Math.min(previous[j] + 1, current[j - 1] + 1, previous[j - 1] + cost)
      if (i > 1 && j > 1 && left[i - 1] === right[j - 2] && left[i - 2] === right[j - 1]) {
        value = Math.min(value, previousPrevious[j - 2] + 1)
      }
      current[j] = value
      rowMin = Math.min(rowMin, value)
    }
    if (rowMin > max) return max + 1
    for (let j = 0; j <= right.length; j += 1) previousPrevious[j] = previous[j]
    ;[previous, current] = [current, previous]
  }
  return previous[right.length]
}

function tokenMatchQuality(titleToken: string, queryToken: string): number {
  if (titleToken === queryToken) return 3
  if (
    queryToken.length >= 3 &&
    (titleToken.startsWith(queryToken) || queryToken.startsWith(titleToken))
  ) {
    return 2
  }
  if (titleToken.length < 4 || queryToken.length < 4) return 0
  const max = allowedSearchEditDistance(Math.max(titleToken.length, queryToken.length))
  return boundedDamerauLevenshtein(titleToken, queryToken, max) <= max ? 1 : 0
}

/**
 * Returns a deterministic relevance score, or -1 when a title is not a safe
 * match. Exact/prefix matches win; fuzzy matching needs strong token coverage
 * so a typo does not turn the library into a broad, unrelated list.
 */
export function smartSearchScore(title: string, query: string): number {
  const normalizedTitle = normalizeSearchText(title)
  const normalizedQuery = normalizeSearchText(query)
  if (!normalizedTitle || !normalizedQuery) return -1
  if (normalizedTitle === normalizedQuery) return 1200
  if (normalizedTitle.startsWith(normalizedQuery)) return 1050
  if (
    normalizedTitle.includes(` ${normalizedQuery} `) ||
    normalizedTitle.startsWith(`${normalizedQuery} `) ||
    normalizedTitle.endsWith(` ${normalizedQuery}`)
  ) {
    return 900
  }

  const titleTokens = searchTokens(normalizedTitle)
  const queryTokens = searchTokens(normalizedQuery)
  const usedTitleTokens = new Array<boolean>(titleTokens.length).fill(false)
  let matched = 0
  let exact = 0
  let prefixes = 0
  let fuzzy = 0
  let inOrder = true
  let lastTitleIndex = -1
  let unmatchedAreOnlyNumbers = true

  for (const queryToken of queryTokens) {
    let bestIndex = -1
    let bestQuality = 0
    for (let index = 0; index < titleTokens.length; index += 1) {
      if (usedTitleTokens[index]) continue
      const quality = tokenMatchQuality(titleTokens[index], queryToken)
      if (quality <= bestQuality) continue
      bestIndex = index
      bestQuality = quality
    }
    if (bestIndex < 0) {
      // Permit one accidental sequel marker ("2"), not a year/code that would
      // make a broad title search look precise.
      unmatchedAreOnlyNumbers &&= /^\d$/u.test(queryToken)
      continue
    }
    usedTitleTokens[bestIndex] = true
    matched += 1
    if (bestIndex < lastTitleIndex) inOrder = false
    lastTitleIndex = bestIndex
    if (bestQuality === 3) exact += 1
    else if (bestQuality === 2) prefixes += 1
    else fuzzy += 1
  }

  const nonNumericQueryCount = queryTokens.filter((token) => !/^\d+$/u.test(token)).length
  const allMatched = matched === queryTokens.length
  const strongPartial = unmatchedAreOnlyNumbers && matched >= 2 && nonNumericQueryCount >= 2
  const singleStrongToken =
    queryTokens.length === 1 &&
    matched === 1 &&
    (exact === 1 || (prefixes === 1 && queryTokens[0].length >= 3))
  if (!allMatched && !strongPartial && !singleStrongToken) return -1
  if (queryTokens.length === 1 && fuzzy === 1 && queryTokens[0].length < 5) return -1

  let score = 620 + exact * 95 + prefixes * 55 + fuzzy * 24
  score += Math.min(80, matched * 18)
  if (inOrder) score += 30
  if (strongPartial) score -= 85
  score -= fuzzy * 12
  return score
}

export function formatPlaytime(
  minutes: number | null | undefined,
  lastPlayedUtc?: string | null,
): string {
  if (minutes != null && minutes > 0) {
    if (minutes < 60) return `${minutes}m`
    const h = Math.floor(minutes / 60)
    const m = minutes % 60
    return m > 0 ? `${h}h ${m}m` : `${h}h`
  }
  // Riot/Epic often have last-played without lifetime minutes.
  if (lastPlayedUtc) {
    const t = Date.parse(lastPlayedUtc)
    if (!Number.isNaN(t)) {
      return `Last played ${new Date(t).toLocaleDateString(undefined, {
        month: 'short',
        day: 'numeric',
      })}`
    }
  }
  return '—'
}

export function formatSize(bytes: number | null | undefined): string {
  if (bytes == null || bytes <= 0) return '—'
  const gb = bytes / (1024 * 1024 * 1024)
  if (gb >= 1) return `${gb < 10 ? gb.toFixed(1) : Math.round(gb)} GB`
  const mb = bytes / (1024 * 1024)
  return `${Math.round(mb)} MB`
}

export function formatSpeed(bps: number | null | undefined): string {
  if (bps == null || bps <= 0) return ''
  const mb = bps / (1024 * 1024)
  if (mb >= 1) return `${mb.toFixed(1)} MB/s`
  const kb = bps / 1024
  return `${Math.round(kb)} KB/s`
}

export function storeLabel(store: string): string {
  const map: Record<string, string> = {
    local: 'Local',
    steam: 'Steam',
    epic: 'Epic',
    gog: 'GOG',
    riot: 'Riot',
    xbox: 'Xbox',
    ea: 'EA',
    ubisoft: 'Ubisoft',
    battlenet: 'Battle.net',
    amazon: 'Amazon',
    rockstar: 'Rockstar',
  }
  return map[store.toLowerCase()] ?? store
}

export function storeDotColor(store: string): string {
  const map: Record<string, string> = {
    local: 'var(--store-local)',
    steam: 'var(--store-steam)',
    epic: 'var(--store-epic)',
    gog: 'var(--store-gog)',
    riot: 'var(--store-riot)',
    xbox: 'var(--store-xbox)',
    ea: 'var(--store-ea)',
    ubisoft: 'var(--store-ubisoft)',
    battlenet: 'var(--store-battlenet)',
    amazon: 'var(--store-amazon)',
  }
  return map[store.toLowerCase()] ?? 'var(--store-local)'
}

export function monogram(title: string): string {
  const clean = title.replace(/[^a-zA-Z0-9 ]/g, ' ').trim()
  if (!clean) return 'Ex'
  const parts = clean.split(/\s+/).filter(Boolean)
  if (parts.length === 1) return parts[0].slice(0, 2).toUpperCase()
  return (parts[0][0] + parts[1][0]).toUpperCase()
}

export function sortGames(games: Game[], mode: SortMode | string, recent: string[] = []): Game[] {
  const rest = games.filter((g) => !g.isAddPortable && g.id !== 'local:add')
  const cmpTitle = (a: Game, b: Game) => a.title.localeCompare(b.title, undefined, { sensitivity: 'base' })

  let ordered: Game[]
  switch (mode) {
    case 'recent': {
      const rank = new Map(recent.map((id, i) => [id.toLowerCase(), i]))
      ordered = [...rest].sort((a, b) => {
        const ra = rank.has(a.id.toLowerCase()) ? rank.get(a.id.toLowerCase())! : 9999
        const rb = rank.has(b.id.toLowerCase()) ? rank.get(b.id.toLowerCase())! : 9999
        if (ra !== rb) return ra - rb
        const la = a.lastPlayedUtc ? Date.parse(a.lastPlayedUtc) : 0
        const lb = b.lastPlayedUtc ? Date.parse(b.lastPlayedUtc) : 0
        if (la !== lb) return lb - la
        return cmpTitle(a, b)
      })
      break
    }
    case 'size':
      ordered = [...rest].sort((a, b) => (b.sizeBytes ?? 0) - (a.sizeBytes ?? 0) || cmpTitle(a, b))
      break
    case 'store':
      ordered = [...rest].sort((a, b) => a.store.localeCompare(b.store) || cmpTitle(a, b))
      break
    case 'favorites':
      ordered = [...rest].sort((a, b) => Number(!!b.isFavorite) - Number(!!a.isFavorite) || cmpTitle(a, b))
      break
    case 'name':
    default:
      ordered = [...rest].sort(cmpTitle)
      break
  }
  return ordered
}
