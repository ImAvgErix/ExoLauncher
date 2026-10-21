import type { Game, LibraryFolder } from './host'

/** A folder's colors, as the host keeps them (SettingsService.FolderColors); gray first, the default. */
export const FOLDER_COLORS = ['gray', 'red', 'orange', 'yellow', 'green', 'teal', 'blue', 'pink'] as const
export type FolderColor = (typeof FOLDER_COLORS)[number]
export const FOLDER_COLOR_NAMES: Record<FolderColor, string> = {
  gray: 'Gray', red: 'Red', orange: 'Orange', yellow: 'Yellow', green: 'Green', teal: 'Teal', blue: 'Blue', pink: 'Pink',
}
export const folderColor = (color: string | undefined): FolderColor =>
  (FOLDER_COLORS as readonly string[]).includes(color ?? '') ? color as FolderColor : 'gray'

/** A folder as the Library's grid shows it: its games that the library has, in the folder's order. */
export type FolderView = { id: string; name: string; color: FolderColor; games: Game[] }

/** A folder's place in the grid: a card of its own, carried as a game so the grid lays it out like one. */
export type FolderTile = Game & { folder: FolderView }

const TILE = 'folder:'

export const isFolderTile = (game: Game): game is FolderTile => 'folder' in game && game.id.startsWith(TILE)

/** The grid's id of a folder's card. */
export const folderTileId = (folderId: string) => TILE + folderId

/** The folder holding this game, if any. */
export const folderOf = (folders: LibraryFolder[], gameId: string): LibraryFolder | undefined =>
  folders.find(folder => folder.games.includes(gameId))

/**
 * The grid with folders: each folder that holds a game of `all` comes first as one card, in the folders' order,
 * then the games of `games` in no folder, in `games`' order. `all` is every game the library shows (a folder keeps
 * a game that sits in Continue or Favorites, which take it out of `games`).
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
    tiles.push({
      id: folderTileId(folder.id),
      title: folder.name,
      store: 'local',
      installed: true,
      status: '',
      deps: [],
      launchNote: '',
      folder: { id: folder.id, name: folder.name, color: folderColor(folder.color), games: inside },
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

const without = (folders: LibraryFolder[], gameIds: string[]) => {
  const leaving = new Set(gameIds)
  return folders.map(folder => folder.games.some(id => leaving.has(id)) ? { ...folder, games: folder.games.filter(id => !leaving.has(id)) } : folder)
}

/**
 * A folder saved from the New folder or Edit folder sheet: a new one goes first (the front of the grid), an edited
 * one keeps its place; its games leave any other folder, and a folder that empties goes away.
 */
export function saveFolder(folders: LibraryFolder[], folder: LibraryFolder): LibraryFolder[] {
  const clean = { ...folder, name: folder.name.trim().slice(0, 40) || freeFolderName(folders), color: folderColor(folder.color) }
  const others = without(folders.filter(item => item.id !== folder.id), clean.games)
  const at = folders.findIndex(item => item.id === folder.id)
  const next = at < 0 ? [clean, ...others] : [...others.slice(0, at), clean, ...others.slice(at)]
  return next.filter(item => item.games.length > 0)
}

/** The game moved into this folder (last in it); an emptied folder goes away. */
export function moveToFolder(folders: LibraryFolder[], gameId: string, folderId: string): LibraryFolder[] {
  return without(folders, [gameId])
    .map(folder => folder.id === folderId ? { ...folder, games: [...folder.games, gameId] } : folder)
    .filter(folder => folder.games.length > 0)
}

/** The game back in the grid; an emptied folder goes away. */
export function leaveFolder(folders: LibraryFolder[], gameId: string): LibraryFolder[] {
  return without(folders, [gameId]).filter(folder => folder.games.length > 0)
}

export function renameFolder(folders: LibraryFolder[], folderId: string, name: string): LibraryFolder[] {
  const clean = name.trim().slice(0, 40)
  return clean ? folders.map(folder => folder.id === folderId ? { ...folder, name: clean } : folder) : folders
}

/** The folder gone; its games back in the grid. */
export function deleteFolder(folders: LibraryFolder[], folderId: string): LibraryFolder[] {
  return folders.filter(folder => folder.id !== folderId)
}
