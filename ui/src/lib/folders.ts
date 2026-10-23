import type { FolderSort, Game, LibraryFolder } from './host'
import { sortGames } from './utils'

/** A folder's colors, as the host keeps them (SettingsService.FolderColors); gray first, the default. */
export const FOLDER_COLORS = ['gray', 'red', 'orange', 'yellow', 'green', 'teal', 'blue', 'pink'] as const
export type FolderColor = (typeof FOLDER_COLORS)[number]
export const FOLDER_COLOR_NAMES: Record<FolderColor, string> = {
  gray: 'Gray', red: 'Red', orange: 'Orange', yellow: 'Yellow', green: 'Green', teal: 'Teal', blue: 'Blue', pink: 'Pink',
}
export const folderColor = (color: string | undefined): FolderColor =>
  (FOLDER_COLORS as readonly string[]).includes(color ?? '') ? color as FolderColor : 'gray'

/** A folder as the Library's grid shows it: its games that the library shows, in the folder's order or its sort. */
export type FolderView = { id: string; name: string; color: FolderColor; sort: FolderSort; art: string | null; games: Game[] }

/** A folder's place in the grid: a card of its own, carried as a game so the grid lays it out like one. */
export type FolderTile = Game & { folder: FolderView }

const TILE = 'folder:'

export const isFolderTile = (game: Game): game is FolderTile => 'folder' in game && game.id.startsWith(TILE)

/** The grid's id of a folder's card. */
export const folderTileId = (folderId: string) => TILE + folderId

/**
 * The grid with folders: each folder that holds a game of `all` comes first as one card, in the folders' order,
 * then the games of `games` in no folder, in `games`' order. A game in folders shows inside each of them and not
 * loose. `all` is every game the library shows (a folder keeps a game that sits in Continue, which takes it out of
 * `games`; a hidden game is in neither). A folder's games are in its own order, or its sort.
 */
export function withFolders(games: Game[], all: Game[], folders: LibraryFolder[]): Game[] {
  if (folders.length === 0) return games
  const byId = new Map(all.map(game => [game.id, game]))
  const filed = new Set<string>()
  const tiles: FolderTile[] = []
  for (const folder of folders) {
    const inside = folder.games.map(id => byId.get(id)).filter((game): game is Game => !!game)
    for (const game of inside) filed.add(game.id)
    if (inside.length === 0) continue
    const sort = folder.sort ?? 'folder'
    tiles.push({
      id: folderTileId(folder.id),
      title: folder.name,
      store: 'local',
      installed: true,
      status: '',
      deps: [],
      launchNote: '',
      folder: {
        id: folder.id, name: folder.name, color: folderColor(folder.color), sort, art: folder.art ?? null,
        games: sort === 'folder' ? inside : sortGames(inside, sort),
      },
    })
  }
  return [...tiles, ...games.filter(game => !filed.has(game.id))]
}

/** A new folder's id: unique among these. */
export function newFolderId(folders: LibraryFolder[]): string {
  for (;;) {
    const id = Math.random().toString(36).slice(2, 10)
    if (!folders.some(folder => folder.id === id)) return id
  }
}

/** A name not taken yet: Folder, Folder 2, Folder 3… */
export function freeFolderName(folders: LibraryFolder[]): string {
  const taken = new Set(folders.map(folder => folder.name.toLowerCase()))
  for (let n = 1; ; n++) {
    const name = n === 1 ? 'Folder' : `Folder ${n}`
    if (!taken.has(name.toLowerCase())) return name
  }
}

/**
 * A folder saved from the New folder or Edit folder sheet: a new one goes first (the front of the grid), an edited
 * one keeps its place. Its games stay in any other folder they are in.
 */
export function saveFolder(folders: LibraryFolder[], folder: LibraryFolder): LibraryFolder[] {
  const clean = { ...folder, name: folder.name.trim().slice(0, 40) || freeFolderName(folders), color: folderColor(folder.color) }
  return folders.some(item => item.id === folder.id)
    ? folders.map(item => item.id === folder.id ? clean : item)
    : [clean, ...folders]
}

/** The game added to this folder (last in it); the other folders it is in keep it. */
export function addToFolder(folders: LibraryFolder[], gameId: string, folderId: string): LibraryFolder[] {
  return folders.map(folder => folder.id === folderId && !folder.games.includes(gameId) ? { ...folder, games: [...folder.games, gameId] } : folder)
}

/** The game out of this folder only; an emptied folder goes away. */
export function leaveFolder(folders: LibraryFolder[], gameId: string, folderId: string): LibraryFolder[] {
  return folders
    .map(folder => folder.id === folderId ? { ...folder, games: folder.games.filter(id => id !== gameId) } : folder)
    .filter(folder => folder.games.length > 0)
}

/** The folder's order: its own ('folder') or one of the Library's sorts. */
export function sortFolder(folders: LibraryFolder[], folderId: string, sort: FolderSort): LibraryFolder[] {
  return folders.map(folder => folder.id === folderId ? { ...folder, sort } : folder)
}

export function renameFolder(folders: LibraryFolder[], folderId: string, name: string): LibraryFolder[] {
  const clean = name.trim().slice(0, 40)
  return clean ? folders.map(folder => folder.id === folderId ? { ...folder, name: clean } : folder) : folders
}

/** A folder moved to just before or just after another: where a drag drops it, or its menu's Move earlier and Move later. */
export function moveFolder(folders: LibraryFolder[], folderId: string, toId: string, after: boolean): LibraryFolder[] {
  const moving = folders.find(folder => folder.id === folderId)
  const rest = folders.filter(folder => folder.id !== folderId)
  const at = rest.findIndex(folder => folder.id === toId)
  if (!moving || at < 0) return folders
  rest.splice(after ? at + 1 : at, 0, moving)
  return rest
}

/** The folder gone; its games back in the grid. */
export function deleteFolder(folders: LibraryFolder[], folderId: string): LibraryFolder[] {
  return folders.filter(folder => folder.id !== folderId)
}
