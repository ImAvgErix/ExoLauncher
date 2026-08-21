import type { Game, StoreStatus } from './host'

/** Visible official client or a proven sign-in. Bundled legendary/gogdl alone is not a row. */
export function isPresentStore(store: StoreStatus): boolean {
  if (store.store === 'local') return false
  if (store.signedIn) return true
  if (store.clientPresent === true) return true
  if (store.clientPresent === undefined && store.agentPresent) return true
  return false
}

export function presentStoreRows(stores: StoreStatus[]): StoreStatus[] {
  return stores.filter(isPresentStore)
}

/**
 * Stores whose session lives in an official CLI Exo ships against, not in the
 * vendor's desktop client: Epic through Legendary, GOG through gogdl, Amazon
 * through Nile. These can sign in with the client absent.
 */
const AGENT_BACKED = new Set(['epic', 'gog', 'amazon'])

/** Every launcher Exo knows, including ones that are not on this PC yet. */
export const ALL_LAUNCHER_STORES: ReadonlyArray<{ store: string; displayName: string }> = [
  { store: 'steam', displayName: 'Steam' },
  { store: 'epic', displayName: 'Epic' },
  { store: 'gog', displayName: 'GOG' },
  { store: 'riot', displayName: 'Riot' },
  { store: 'xbox', displayName: 'Xbox' },
  { store: 'ea', displayName: 'EA' },
  { store: 'ubisoft', displayName: 'Ubisoft' },
  { store: 'battlenet', displayName: 'Battle.net' },
  { store: 'amazon', displayName: 'Amazon' },
  { store: 'rockstar', displayName: 'Rockstar' },
  { store: 'itch', displayName: 'itch' },
  { store: 'minecraft', displayName: 'Minecraft' },
  { store: 'roblox', displayName: 'Roblox' },
  { store: 'paradox', displayName: 'Paradox' },
  { store: 'wargaming', displayName: 'Wargaming' },
]

function placeholderStore(row: { store: string; displayName: string }): StoreStatus {
  return {
    store: row.store,
    displayName: row.displayName,
    signedIn: false,
    clientPresent: false,
    agentPresent: false,
    detail: 'Not installed',
    layers: {
      login: 'none',
      owned: 'none',
      covers: 'none',
      downloads: 'none',
      social: 'none',
    },
  }
}

export function settingsStoreRows(stores: StoreStatus[]): StoreStatus[] {
  const byId = new Map(
    stores.filter((store) => store.store !== 'local').map((store) => [store.store, store] as const),
  )
  const known = ALL_LAUNCHER_STORES.map((row) => byId.get(row.store) ?? placeholderStore(row))
  const extra = stores.filter(
    (store) =>
      store.store !== 'local' &&
      !ALL_LAUNCHER_STORES.some((row) => row.store === store.store),
  )
  return [...known, ...extra]
}

/** Official client download when the vendor app is not on this PC. */
export function storeClientDownloadUrl(store: string): string | null {
  switch (store) {
    case 'steam':
      return 'https://store.steampowered.com/about/'
    case 'epic':
      return 'https://store.epicgames.com/download'
    case 'gog':
      return 'https://www.gog.com/galaxy'
    case 'riot':
      return 'https://www.riotgames.com/en/download'
    case 'xbox':
      return 'ms-windows-store://pdp/?ProductId=9MV0B5HZVK9Z'
    case 'ea':
      return 'https://www.ea.com/ea-app'
    case 'ubisoft':
      return 'https://ubisoftconnect.com/'
    case 'battlenet':
      return 'https://www.battle.net/download'
    case 'amazon':
      return 'https://www.amazongames.com/'
    case 'rockstar':
      return 'https://www.rockstargames.com/newswire'
    case 'itch':
      return 'https://itch.io/app'
    case 'minecraft':
      return 'https://www.minecraft.net/get-minecraft'
    case 'roblox':
      return 'https://www.roblox.com/download'
    case 'paradox':
      return 'https://play.paradoxplaza.com/'
    case 'wargaming':
      return 'https://wargaming.com/en/game-center/'
    default:
      return null
  }
}

export function canConnectStore(store: StoreStatus): boolean {
  return AGENT_BACKED.has(store.store) && !!store.agentPresent && !store.signedIn
}

export function canOpenStoreClient(store: StoreStatus): boolean {
  return store.clientPresent === true || (store.clientPresent === undefined && !!store.agentPresent)
}

export function storePresenceLabel(store: StoreStatus): string {
  if (store.signedIn) return 'Signed in'
  if (store.clientPresent === true || (store.clientPresent === undefined && store.agentPresent)) {
    if (store.detail && store.detail !== 'Not installed') return store.detail
    if (store.store === 'steam' || store.store === 'riot') return 'Client present'
    return 'Found'
  }
  if (store.detail && store.detail !== 'Not installed') return store.detail
  return 'Not installed'
}

/** Onboarding prefers a usable next step over mere presence detection. */
export function onboardingStoreLabel(store: StoreStatus): string {
  if (store.signedIn) return 'Signed in'
  if (canConnectStore(store)) return 'Ready to sign in'
  if ((store.store === 'steam' || store.store === 'riot') && store.clientPresent === true) {
    return 'Client present — sign-in stays in the official app'
  }
  if (store.detail && store.detail !== 'Not installed') return store.detail
  if (store.clientPresent === true) return 'Client present'
  return 'Found'
}

type KeyShopGame = Pick<Game, 'id' | 'title' | 'store' | 'launchTarget'>

/**
 * gg.deals in the system browser. Steam uses the documented appid redirect;
 * other stores use a title search. There is deliberately no affiliate query.
 */
export function ggDealsUrl(game: KeyShopGame): string | null {
  const appId = steamAppId(game)
  if (appId) return `https://gg.deals/steam/app/${appId}/`
  return ggDealsTitleUrl(game.title)
}

/** Safe title-search fallback for store presence that has no local library match. */
export function ggDealsTitleUrl(value: string): string | null {
  const title = (value || '').trim()
  if (!/\p{Letter}|\p{Number}/u.test(title)) return null
  return `https://gg.deals/games/?title=${encodeURIComponent(title)}`
}

function steamAppId(game: KeyShopGame): string | null {
  const target = (game.launchTarget || '').trim()
  if (game.store === 'steam' && /^\d+$/.test(target)) return target
  if (!game.id.startsWith('steam:')) return null
  const id = game.id.slice('steam:'.length)
  return /^\d+$/.test(id) ? id : null
}
