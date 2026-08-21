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

export function settingsStoreRows(stores: StoreStatus[]): StoreStatus[] {
  const present = presentStoreRows(stores)
  const extra = stores.filter(
    (store) =>
      AGENT_BACKED.has(store.store) &&
      store.agentPresent &&
      !present.some((row) => row.store === store.store),
  )
  return [...present, ...extra]
}

export function canConnectStore(store: StoreStatus): boolean {
  return AGENT_BACKED.has(store.store) && !!store.agentPresent && !store.signedIn
}

export function canOpenStoreClient(store: StoreStatus): boolean {
  return store.clientPresent === true || (store.clientPresent === undefined && !!store.agentPresent)
}

export function storePresenceLabel(store: StoreStatus): string {
  if (store.detail && store.detail !== 'Not installed') return store.detail
  if (store.signedIn) return 'Signed in'
  if (store.store === 'steam' || store.store === 'riot') return 'Client present'
  return 'Found'
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
