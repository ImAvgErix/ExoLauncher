import type { StoreStatus } from './host'

/** Visible official client or a proven sign-in. Bundled legendary/gogdl alone is not a row. */
function isPresentStore(store: StoreStatus): boolean {
  if (store.store === 'local') return false
  if (store.signedIn) return true
  if (store.clientPresent === true) return true
  if (store.clientPresent === undefined && store.agentPresent) return true
  return false
}

/**
 * Stores whose session lives in an official CLI Exo ships against, not in the
 * vendor's desktop client: Epic through Legendary, GOG through gogdl, Amazon
 * through Nile. These can sign in with the client absent.
 */
const AGENT_BACKED = new Set(['epic', 'gog', 'amazon'])
const SESSION_BACKED = new Set([
  'riot',
  'battlenet',
  'ea',
  'ubisoft',
  'xbox',
  'minecraft',
  'paradox',
  'rockstar',
])

/** Play / Install depth. Everything else is detect + open vendor. */
const FIRST_CLASS_STORES = new Set(['steam', 'epic', 'gog', 'riot'])

/** Every launcher Exo knows, including ones that are not on this PC yet. */
const ALL_LAUNCHER_STORES: ReadonlyArray<{ store: string; displayName: string }> = [
  { store: 'steam', displayName: 'Steam' },
  { store: 'epic', displayName: 'Epic' },
  { store: 'gog', displayName: 'GOG' },
  { store: 'riot', displayName: 'Riot' },
  { store: 'xbox', displayName: 'Xbox' },
  { store: 'microsoft-store', displayName: 'Microsoft Store' },
  { store: 'ea', displayName: 'EA' },
  { store: 'ubisoft', displayName: 'Ubisoft' },
  { store: 'battlenet', displayName: 'Battle.net' },
  { store: 'amazon', displayName: 'Amazon' },
  { store: 'rockstar', displayName: 'Rockstar' },
  { store: 'itch', displayName: 'itch.io' },
  { store: 'paradox', displayName: 'Paradox' },
  { store: 'wargaming', displayName: 'Wargaming' },
]

function isFirstClassStore(store: string): boolean {
  return FIRST_CLASS_STORES.has(store)
}

function placeholderStore(row: { store: string; displayName: string }): StoreStatus {
  return {
    store: row.store,
    displayName: row.displayName,
    signedIn: false,
    clientPresent: false,
    agentPresent: false,
    detail: 'Not installed',
  }
}

export function settingsStoreRows(stores: StoreStatus[]): StoreStatus[] {
  const byId = new Map(
    stores.filter((store) => store.store !== 'local').map((store) => [store.store, store] as const),
  )
  const known = ALL_LAUNCHER_STORES.map((row) => byId.get(row.store) ?? placeholderStore(row))
  const first = known.filter((store) => isFirstClassStore(store.store) && isPresentStore(store))
  const vendors = known.filter((store) => !isFirstClassStore(store.store) && isPresentStore(store))
  const extra = stores.filter(
    (store) =>
      !['local', 'minecraft', 'roblox'].includes(store.store) &&
      !ALL_LAUNCHER_STORES.some((row) => row.store === store.store) &&
      isPresentStore(store),
  )
  return [...first, ...vendors, ...extra]
}

export function availableStoreRows(stores: StoreStatus[]): StoreStatus[] {
  const visible = new Set(settingsStoreRows(stores).map(store => store.store))
  const byId = new Map(stores.map(store => [store.store, store]))
  return ALL_LAUNCHER_STORES.filter(row => !visible.has(row.store))
    .map(row => byId.get(row.store) ?? placeholderStore(row))
}

/** Official client download when the vendor app is not on this PC. */
export function storeClientDownloadUrl(store: string): string | null {
  switch (store) {
    case 'microsoft-store':
      return 'ms-windows-store://home'
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
      return 'https://download.amazongames.com/AmazonGamesSetup.exe'
    case 'rockstar':
      return 'https://socialclub.rockstargames.com/rockstar-games-launcher'
    case 'itch':
      return 'https://itch.io/app'
    case 'minecraft':
      return 'https://www.minecraft.net/en-us/download'
    case 'roblox':
      return 'ms-windows-store://pdp/?ProductId=9PMF91N3LZ3M'
    case 'paradox':
      return 'https://play.paradoxplaza.com/'
    case 'wargaming':
      return 'https://wargaming.com/en/game-center/'
    default:
      return null
  }
}

/** Exo runs the sign-in itself (Epic through Legendary, GOG through gogdl). */
export function canConnectStore(store: StoreStatus): boolean {
  return isFirstClassStore(store.store) && !store.signedIn && AGENT_BACKED.has(store.store)
}

export function canOpenStoreClient(store: StoreStatus): boolean {
  return store.clientPresent === true || (store.clientPresent === undefined && !!store.agentPresent)
}

/**
 * Stores that sign in only inside their own app (Steam, Riot). Exo can open
 * that app; `stores.auth` answers with `handoff` once it is open.
 */
export function signsInInOwnApp(store: StoreStatus): boolean {
  if (!isFirstClassStore(store.store) || store.signedIn || AGENT_BACKED.has(store.store)) return false
  return store.store === 'steam' || SESSION_BACKED.has(store.store) ? canOpenStoreClient(store) : false
}

/** One status word for a store row, shared by setup and Settings. `on` is true only when Exo can act for the account. */
export function storeState(store: StoreStatus): { label: string; on: boolean } {
  if (store.signedIn) return { label: 'Connected', on: true }
  if (canConnectStore(store) || signsInInOwnApp(store)) return { label: 'Not signed in', on: false }
  if (store.clientPresent === true || store.agentPresent) return { label: 'Found', on: false }
  return { label: 'Not installed', on: false }
}
