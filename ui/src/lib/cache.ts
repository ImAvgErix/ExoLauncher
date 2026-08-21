/**
 * Last-good answers for host reads that a view needs on mount.
 *
 * Switching to Friends or Profile used to await a fresh round trip before it
 * could paint anything, so every visit looked like a cold start. These helpers
 * hand back the previous answer immediately and refresh behind it, which is why
 * a second visit is instant. Nothing here is persisted: it lives for the life of
 * the window, so a restart still reads the host. The shell writes profile, friends
 * and library as soon as those reads land. library.updated replaces the library
 * entry; profile.updated replaces the profile. Playtime, install state and
 * running flags still come from those host payloads — this cache never invents
 * them.
 */

type Entry<T> = { value: T }

const store = new Map<string, Entry<unknown>>()

/** Cached value for this key, or undefined when nothing has landed yet. */
export function peekCache<T>(key: string): T | undefined {
  return (store.get(key) as Entry<T> | undefined)?.value
}

export function writeCache<T>(key: string, value: T): void {
  store.set(key, { value })
}

export const CACHE_KEYS = {
  profile: 'profile.get',
  friends: 'friends.list',
  roster: 'friends.roster',
  library: 'library.get',
} as const
