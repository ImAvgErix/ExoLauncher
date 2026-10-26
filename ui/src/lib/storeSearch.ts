/**
 * A search handed to the Store room from elsewhere (a friend is playing a game you don't have): kept until the room, which
 * may not be built yet, takes it, or told to it at once when it is already there.
 */
let pending: string | null = null
const listeners = new Set<(query: string) => void>()

export function requestStoreSearch(query: string) {
  pending = query
  for (const listener of listeners) listener(query)
}

/** The waiting search, once. */
export function takeStoreSearch(): string | null {
  const query = pending
  pending = null
  return query
}

export function onStoreSearch(listener: (query: string) => void) {
  listeners.add(listener)
  return () => { listeners.delete(listener) }
}
