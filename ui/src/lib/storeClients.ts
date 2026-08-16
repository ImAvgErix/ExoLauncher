export interface StoreClientRow {
  store: string
  clientPresent?: boolean
}

/** Visible store-app rows. Missing clients stay gone — agent-only is not a row. */
export function presentStoreClients<T extends StoreClientRow>(stores: readonly T[]): T[] {
  return stores.filter((store) => store.store !== 'local' && store.clientPresent === true)
}
