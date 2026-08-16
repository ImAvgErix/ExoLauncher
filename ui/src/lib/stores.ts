import type { StoreStatus } from './host'

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

export function canOpenStoreClient(store: StoreStatus): boolean {
  return store.clientPresent === true || (store.clientPresent === undefined && !!store.agentPresent)
}

export function storePresenceLabel(store: StoreStatus): string {
  if (store.detail && store.detail !== 'Not installed') return store.detail
  if (store.signedIn) return 'Signed in'
  if (store.store === 'steam' || store.store === 'riot') return 'Client present'
  return 'Found'
}
