import { clsx, type ClassValue } from 'clsx'
import { twMerge } from 'tailwind-merge'

export function cn(...inputs: ClassValue[]) {
  return twMerge(clsx(inputs))
}

export function formatPlaytime(minutes: number | null | undefined): string {
  if (minutes == null || minutes <= 0) return '—'
  if (minutes < 60) return `${minutes}m`
  const h = Math.floor(minutes / 60)
  const m = minutes % 60
  return m > 0 ? `${h}h ${m}m` : `${h}h`
}

export function formatSize(bytes: number | null | undefined): string {
  if (bytes == null || bytes <= 0) return '—'
  const gb = bytes / (1024 * 1024 * 1024)
  if (gb >= 1) return `${gb < 10 ? gb.toFixed(1) : Math.round(gb)} GB`
  const mb = bytes / (1024 * 1024)
  return `${Math.round(mb)} MB`
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
  }
  return map[store.toLowerCase()] ?? 'var(--store-local)'
}
