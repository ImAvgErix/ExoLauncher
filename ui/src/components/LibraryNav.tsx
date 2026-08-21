import { resolvePrimaryAction, type Game } from '../lib/host'
import { storeLabel } from '../lib/utils'

export type CollectionId = 'all' | 'pinned' | 'updates' | `store:${string}`

export function isShelfGame(game: Game): boolean {
  return !game.isAddPortable && game.id !== 'local:add'
}

export function collectionLabel(id: CollectionId): string {
  if (id === 'all') return 'All'
  if (id === 'pinned') return 'Pinned'
  if (id === 'updates') return 'Updates'
  return storeLabel(id.slice('store:'.length))
}

export function gameStores(game: Game): string[] {
  const stores = new Set<string>()
  const push = (value?: string | null) => {
    const store = value?.trim().toLowerCase()
    if (store) stores.add(store)
  }
  push(game.store)
  for (const store of game.stores ?? []) push(store)
  for (const variant of game.variants ?? []) push(variant.store)
  return [...stores]
}

export function hasLibraryUpdate(game: Game): boolean {
  return (
    resolvePrimaryAction(game) === 'update' ||
    !!game.updateAvailable ||
    !!game.variants?.some((variant) => variant.updateAvailable)
  )
}

export function gameBelongsToCollection(game: Game, id: CollectionId): boolean {
  if (!isShelfGame(game)) return false
  if (id === 'all') return true
  if (id === 'pinned') return !!game.isFavorite
  if (id === 'updates') return hasLibraryUpdate(game)
  return gameStores(game).includes(id.slice('store:'.length).toLowerCase())
}

export function filterGamesByCollection(games: Game[], id: CollectionId): Game[] {
  return games.filter((game) => gameBelongsToCollection(game, id))
}
