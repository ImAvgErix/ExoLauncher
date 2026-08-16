/** Typed bridge to the .NET WebView2 host. Falls back to mock data in browser dev. */

export type StoreId =
  | 'steam'
  | 'epic'
  | 'gog'
  | 'riot'
  | 'xbox'
  | 'ea'
  | 'ubisoft'
  | 'battlenet'
  | 'amazon'
  | 'rockstar'

export type PrimaryAction = 'play' | 'install' | 'update' | 'none'
export type SortMode = 'name' | 'recent' | 'played' | 'size' | 'store' | 'favorites'

/** One exact store entry represented by a grouped library card. */
export interface GameVariant {
  id: string
  store: StoreId | string
  installed: boolean
  owned?: boolean
  updateAvailable?: boolean
  canInstall?: boolean
  primaryAction?: PrimaryAction | string
  path?: string | null
  launchTarget?: string | null
  playtimeMinutes?: number | null
  lastPlayedUtc?: string | null
  status: string
  /** Only true when Exo has revalidated this source's game process. */
  isRunning?: boolean
  /** Never offered for launcher, overlay, anti-cheat, or service processes. */
  canStop?: boolean
}

export interface Game {
  id: string
  title: string
  store: StoreId | string
  /** Every store variant represented by this library entry. */
  stores?: Array<StoreId | string>
  /** Opaque display grouping key; never send it back as an action id. */
  canonicalTitleKey?: string | null
  /** Exact source projected into this card before the user chooses another source. */
  selectedVariantId?: string | null
  /** Exact sources available for the grouped card. */
  variants?: GameVariant[]
  installed: boolean
  owned?: boolean
  updateAvailable?: boolean
  canInstall?: boolean
  primaryAction?: PrimaryAction | string
  path?: string | null
  coverUrl?: string | null
  coverSource?: string | null
  playtimeMinutes?: number | null
  sizeBytes?: number | null
  status: string
  deps: string[]
  launchNote: string
  launchTarget?: string | null
  lastPlayedUtc?: string | null
  isFavorite?: boolean
  isAddPortable?: boolean
  /** Only true when Exo has revalidated an in-root game process. */
  isRunning?: boolean
  /** Stop is never offered for a store client, overlay, or anti-cheat process. */
  canStop?: boolean
}

export interface InstallProgress {
  gameId: string
  phase: string
  percent?: number | null
  bytesPerSecond?: number | null
  status: string
  canCancel: boolean
  isActive: boolean
}

export interface DependencyItem {
  id: string
  name: string
  status: string
  detail: string
  canOfferInstall: boolean
  officialUrl?: string | null
}

export interface LauncherSettings {
  appVersion: string
  closeStoreClientsAfterLaunch: boolean
  autoInstallRedistributables: boolean
  minimizeWhilePlaying: boolean
  antiCheatSafeMode: boolean
  theme?: string
  copyPortableIntoLibrary?: boolean
  allowResize?: boolean
  checkForUpdates?: boolean
  sortMode?: SortMode | string
  defaultInstallRoot?: string | null
  favorites?: string[]
  recent?: string[]
  onboardingComplete?: boolean
  trophyNotificationsEnabled?: boolean
  /** Legacy fields retained by the native host while older settings migrate. */
  trophyNotificationPreset?: string
  trophyNotificationPosition?: string
  trophyNotificationPositionX?: number
  trophyNotificationPositionY?: number
  trophyNotificationDurationSeconds?: number
  trophyNotificationSound?: boolean
  trophyNotificationSoundCue?: 'exo' | 'soft' | 'off' | string
}

export type AchievementCoverage = 'unsupported' | 'unavailable' | 'partial' | 'complete'

export interface GameAchievementSummary {
  unlocked: number
  total: number
  completionPercent?: number | null
  perfected: boolean
  observedAt: string
}

export interface GameAchievementEntry {
  id: string
  name: string
  description?: string
  hidden: boolean
  iconUrl?: string | null
  rarityPercent?: number | null
  points?: number | null
  tier?: string | null
  unlocked: boolean
  unlockedAt?: string | null
  progressCurrent?: number | null
  progressTarget?: number | null
}

export interface GameAchievementsResponse {
  ok: boolean
  gameId?: string
  provider?: string | null
  sourceGameId?: string
  coverage?: AchievementCoverage
  capabilities?: {
    progress: boolean
    rarity: boolean
    completeCatalog: boolean
  }
  summary?: GameAchievementSummary | null
  achievements?: GameAchievementEntry[]
  message?: string | null
}

export interface StoreStatus {
  store: string
  displayName: string
  agentPresent: boolean
  /** Whether the vendor's visible desktop client is actually installed. */
  clientPresent?: boolean
  signedIn?: boolean
  detail?: string
}

export interface LibraryResponse {
  games: Game[]
  count: number
  stores: StoreStatus[]
  progress?: InstallProgress
  sortMode?: string
  favorites?: string[]
  recent?: string[]
}

export interface MissingDependency {
  id: string
  name: string
  status?: string
  canOfferInstall?: boolean
  officialUrl?: string | null
}

export interface LaunchResponse {
  ok: boolean
  message: string
  processId?: number | null
  backendStarted?: string | null
  handoffOnly?: boolean
  needsDependencies?: boolean
  missingDependencies?: MissingDependency[]
}

export interface InstallResponse {
  ok: boolean
  message: string
  path?: string | null
  progress?: InstallProgress
  handoffOnly?: boolean
  needsDependencies?: boolean
  missingDependencies?: MissingDependency[]
}

type HostRequest = { id: string; method: string; params?: Record<string, unknown> }
type HostResponse = { id: string; ok: boolean; result?: unknown; error?: string }
type HostEvent = { event: string; data?: unknown }

const pending = new Map<string, { resolve: (v: unknown) => void; reject: (e: Error) => void }>()
const eventHandlers = new Map<string, Set<(data: unknown) => void>>()

function emitHostEvent(event: string, data?: unknown) {
  const set = eventHandlers.get(event)
  if (set) for (const handler of set) handler(data)
}

function isHost(): boolean {
  return typeof window !== 'undefined' && !!(window as unknown as { chrome?: { webview?: unknown } }).chrome?.webview
}

function post(msg: unknown) {
  const wv = (window as unknown as { chrome?: { webview?: { postMessage: (m: unknown) => void } } }).chrome?.webview
  if (!wv) return
  wv.postMessage(typeof msg === 'string' ? msg : JSON.stringify(msg))
}

let hostBridgeReady = false

export function initHostBridge() {
  if (!isHost() || hostBridgeReady) return
  hostBridgeReady = true
  const wv = (window as unknown as {
    chrome: { webview: { addEventListener: (t: string, fn: (e: MessageEvent) => void) => void } }
  }).chrome.webview
  wv.addEventListener('message', (e: MessageEvent) => {
    let data: HostResponse | HostEvent | null = null
    try {
      data =
        typeof e.data === 'string'
          ? (JSON.parse(e.data) as HostResponse | HostEvent)
          : (e.data as HostResponse | HostEvent)
    } catch {
      return
    }
    if (data && typeof data === 'object' && 'event' in data && (data as HostEvent).event) {
      const ev = data as HostEvent
      emitHostEvent(ev.event, ev.data)
      return
    }
    const res = data as HostResponse
    if (!res?.id) return
    const p = pending.get(res.id)
    if (!p) return
    pending.delete(res.id)
    if (res.ok) p.resolve(res.result)
    else p.reject(new Error(res.error || 'host error'))
  })
}

export function onHostEvent(event: string, handler: (data: unknown) => void) {
  let set = eventHandlers.get(event)
  if (!set) {
    set = new Set()
    eventHandlers.set(event, set)
  }
  set.add(handler)
  return () => {
    set!.delete(handler)
  }
}

async function rawCall<T>(method: string, params?: Record<string, unknown>, timeoutMs = 600_000): Promise<T> {
  if (!isHost()) return mockCall<T>(method, params)
  const id = crypto.randomUUID()
  const req: HostRequest = { id, method, params }
  return new Promise<T>((resolve, reject) => {
    let timer: number | undefined
    const clear = () => {
      if (timer !== undefined) window.clearTimeout(timer)
    }
    timer = window.setTimeout(() => {
      pending.delete(id)
      reject(new Error(`Host timeout: ${method}`))
    }, timeoutMs)
    pending.set(id, {
      resolve: (v) => {
        clear()
        resolve(v as T)
      },
      reject: (e) => {
        clear()
        reject(e)
      },
    })
    post(req)
  })
}

const MOCK_GAMES: Game[] = [
  {
    id: 'local:add',
    title: 'Add portable game',
    store: 'local',
    installed: false,
    owned: true,
    canInstall: true,
    primaryAction: 'install',
    status: 'Ready',
    deps: [],
    launchNote: '',
    isAddPortable: true,
  },
  {
    id: 'mock:valorant',
    title: 'VALORANT',
    store: 'riot',
    installed: false,
    owned: true,
    canInstall: true,
    primaryAction: 'install',
    status: 'Demo',
    playtimeMinutes: 0,
    sizeBytes: 30 * 1024 ** 3,
    deps: ['Riot Client', 'Vanguard'],
    launchNote: '',
  },
  {
    id: 'mock:hades',
    title: 'Hades',
    store: 'steam',
    installed: true,
    owned: true,
    primaryAction: 'play',
    status: 'Ready',
    playtimeMinutes: 1240,
    sizeBytes: 15 * 1024 ** 3,
    deps: ['Steam client'],
    launchNote: '',
    isFavorite: true,
  },
  {
    id: 'mock:control',
    title: 'Control',
    store: 'epic',
    installed: false,
    owned: true,
    canInstall: true,
    primaryAction: 'install',
    status: 'Demo',
    playtimeMinutes: 720,
    sizeBytes: 42 * 1024 ** 3,
    deps: [],
    launchNote: '',
  },
  {
    id: 'mock:disco',
    title: 'Disco Elysium',
    store: 'gog',
    installed: false,
    owned: true,
    canInstall: true,
    primaryAction: 'install',
    status: 'Demo',
    playtimeMinutes: 2100,
    sizeBytes: 20 * 1024 ** 3,
    deps: [],
    launchNote: '',
  },
]

const mockSettings: LauncherSettings = {
  onboardingComplete: true,
  appVersion: '1.0.0-dev',
  closeStoreClientsAfterLaunch: true,
  autoInstallRedistributables: false,
  minimizeWhilePlaying: true,
  antiCheatSafeMode: true,
  theme: 'amoled',
  copyPortableIntoLibrary: false,
  allowResize: true,
  checkForUpdates: true,
  sortMode: 'name',
  favorites: ['mock:hades'],
  trophyNotificationsEnabled: true,
  trophyNotificationPreset: 'exo',
  trophyNotificationPosition: 'bottom-right',
  trophyNotificationPositionX: 1,
  trophyNotificationPositionY: 1,
  trophyNotificationDurationSeconds: 5,
  trophyNotificationSound: true,
  trophyNotificationSoundCue: 'exo',
  recent: [],
}

let mockProgress: InstallProgress = {
  gameId: '',
  phase: 'idle',
  status: '',
  canCancel: false,
  isActive: false,
}

async function mockCall<T>(method: string, params?: Record<string, unknown>): Promise<T> {
  await new Promise((r) => setTimeout(r, 40))
  switch (method) {
    case 'library.get':
    case 'library.refresh':
      return {
        games: MOCK_GAMES,
        count: MOCK_GAMES.length,
        stores: [
          { store: 'local', displayName: 'Local', agentPresent: true },
        ],
        progress: mockProgress,
        sortMode: mockSettings.sortMode,
        favorites: mockSettings.favorites,
        recent: mockSettings.recent,
      } as T
    case 'game.get': {
      const id = String(params?.id ?? '')
      const game = MOCK_GAMES.find((g) => g.id === id)
      return (game ? { ok: true, game } : { ok: false, message: 'Game not found.' }) as T
    }
    case 'game.launch':
      return { ok: false, message: 'Browser mock — launch only works inside the WinUI host.' } as T
    case 'game.stop':
      return { ok: false, message: 'Browser mock — Stop only works inside the WinUI host.' } as T
    case 'game.install':
    case 'game.update': {
      const id = String(params?.id ?? '')
      mockProgress = {
        gameId: id,
        phase: 'downloading',
        percent: 42,
        bytesPerSecond: 12.5 * 1024 * 1024,
        status: 'Mock progress (host required for real install)',
        canCancel: true,
        isActive: true,
      }
      emitHostEvent('install.progress', mockProgress)
      return {
        ok: false,
        message: 'Browser mock — install only works inside the WinUI host.',
        progress: mockProgress,
      } as T
    }
    case 'game.uninstall':
      return { ok: false, message: 'Browser mock.' } as T
    case 'game.openFolder':
      return { ok: true } as T
    case 'game.toggleFavorite': {
      const id = String(params?.id ?? '')
      const favs = mockSettings.favorites ?? []
      const i = favs.indexOf(id)
      if (i >= 0) favs.splice(i, 1)
      else favs.unshift(id)
      mockSettings.favorites = favs
      return { ok: true, isFavorite: favs.includes(id), favorites: favs } as T
    }
    case 'game.cancelInstall':
      mockProgress = { ...mockProgress, phase: 'cancelled', isActive: false, canCancel: false, status: 'Cancelled.' }
      return { ok: true, message: 'Cancel requested.' } as T
    case 'game.progress':
      return mockProgress as T
    case 'achievements.get':
    case 'achievements.refresh':
      return {
        ok: true,
        gameId: String(params?.id ?? ''),
        provider: 'epic',
        sourceGameId: 'mock-game',
        coverage: 'complete',
        capabilities: { progress: true, rarity: true, completeCatalog: true },
        summary: { unlocked: 7, total: 18, completionPercent: 38.9, perfected: false, observedAt: new Date().toISOString() },
        achievements: [
          { id: 'first', name: 'First light', description: 'Complete the opening challenge.', hidden: false, unlocked: true, rarityPercent: 9.8, unlockedAt: new Date().toISOString() },
          { id: 'next', name: 'On the way', description: 'Keep exploring.', hidden: false, unlocked: false, progressCurrent: 4, progressTarget: 10 },
        ],
      } as T
    case 'stores.auth':
      return { ok: false, message: 'Browser mock — auth requires host.', requiresUserAction: true } as T
    case 'deps.list':
      return {
        items: [
          { id: 'vcredist', name: 'Visual C++ Redistributable', status: 'Present', detail: 'Mock', canOfferInstall: true },
          { id: 'directx', name: 'DirectX', status: 'Present', detail: 'Mock', canOfferInstall: true },
          { id: 'dotnet', name: '.NET Desktop Runtime', status: 'Present', detail: 'Mock', canOfferInstall: true },
          { id: 'webview2', name: 'WebView2 Runtime', status: 'Present', detail: 'Mock', canOfferInstall: true },
        ],
      } as T
    case 'deps.offerInstall':
      return { ok: true, message: 'Mock: would open official installer page.' } as T
    case 'stores.matrix':
      return [
        { store: 'local', displayName: 'Local', agentPresent: true },
      ] as T
    case 'settings.get':
      return { ...mockSettings } as T
    case 'settings.set':
      Object.assign(mockSettings, params ?? {})
      mockSettings.antiCheatSafeMode = true
      return { ...mockSettings } as T
    case 'trophies.preview':
      return { ok: true } as T
    case 'shell.minimize':
    case 'shell.maximize':
    case 'shell.windowState':
    case 'shell.close':
    case 'shell.openUrl':
    case 'shell.openPath':
    case 'shell.showStore':
      return { ok: true } as T
    case 'shell.pickFolder':
      return { ok: true, cancelled: false, path: 'C:\\Games\\MockPortable' } as T
    case 'app.version':
      return { version: '1.0.0-dev' } as T
    case 'app.checkUpdate':
      return { ok: true, updateAvailable: false, message: 'Up to date.', current: '1.0.0-dev' } as T
    case 'app.installUpdate':
      return {
        ok: true,
        alreadyLatest: true,
        updateAvailable: false,
        message: 'Already up to date.',
        current: '1.0.0-dev',
      } as T
    case 'stores.search': {
      const q = String(params?.query ?? '')
        .trim()
        .toLowerCase()
      const hits = [
        {
          id: 'mock:control',
          title: 'Control',
          store: 'epic',
          owned: true,
          installed: false,
          canInstall: true,
          source: 'owned',
        },
        {
          id: 'mock:disco',
          title: 'Disco Elysium',
          store: 'gog',
          owned: true,
          installed: false,
          canInstall: true,
          source: 'owned',
        },
        {
          id: 'steam:570',
          title: 'Dota 2',
          store: 'steam',
          owned: false,
          installed: false,
          canInstall: true,
          source: 'catalog',
          coverUrl: 'https://cdn.cloudflare.steamstatic.com/steam/apps/570/library_600x900_2x.jpg',
        },
        {
          id: 'mock:valorant',
          title: 'VALORANT',
          store: 'riot',
          owned: true,
          installed: false,
          canInstall: true,
          source: 'owned',
        },
      ].filter((h) => !q || h.title.toLowerCase().includes(q) || h.store.includes(q))
      return { ok: true, query: String(params?.query ?? ''), count: hits.length, results: hits } as T
    }
    default:
      throw new Error(`Unknown mock method: ${method}`)
  }
}

export function resolvePrimaryAction(game: Game): PrimaryAction {
  // The attention badge and primary CTA must never disagree, even when an
  // older/native catalog payload carries a stale explicit action.
  if (game.installed && game.updateAvailable) return 'update'
  if (game.installed && game.variants?.some((variant) => variant.updateAvailable)) return 'update'
  if (game.installed) return 'play'
  if (game.canInstall || game.owned) return 'install'
  if (game.primaryAction === 'play' || game.primaryAction === 'install' || game.primaryAction === 'update' || game.primaryAction === 'none') {
    return game.primaryAction
  }
  return 'none'
}

/** Play | Download | Install | Update | Stop — owned store titles download, they are not Buy. */
export function primaryCtaLabel(game: Game, action = resolvePrimaryAction(game)): string {
  if (game.canStop) return 'Stop'
  if (action === 'update') return 'Update'
  if (action === 'play') return 'Play'
  if (action === 'install') return game.store === 'local' ? 'Install' : 'Download'
  return 'Buy'
}

export const host = {
  getLibrary: (force = false) =>
    rawCall<LibraryResponse>(force ? 'library.refresh' : 'library.get', { force }),
  getGame: (id: string) =>
    rawCall<{ ok: boolean; game?: Game; message?: string }>('game.get', { id }),
  launch: (id: string, opts?: { skipDeps?: boolean }) =>
    rawCall<LaunchResponse>('game.launch', { id, ...(opts?.skipDeps ? { skipDeps: true } : {}) }),
  stop: (id: string) => rawCall<{ ok: boolean; message?: string }>('game.stop', { id }),
  install: (id: string, path?: string, title?: string, opts?: { skipDeps?: boolean }) =>
    rawCall<InstallResponse>('game.install', {
      id,
      ...(path ? { path } : {}),
      ...(title ? { title } : {}),
      ...(opts?.skipDeps ? { skipDeps: true } : {}),
    }),
  update: (id: string, opts?: { skipDeps?: boolean }) =>
    rawCall<InstallResponse>('game.update', {
      id,
      ...(opts?.skipDeps ? { skipDeps: true } : {}),
    }),
  uninstall: (id: string) => rawCall<{ ok: boolean; message?: string }>('game.uninstall', { id }),
  openFolder: (id: string) => rawCall<{ ok: boolean; message?: string; path?: string }>('game.openFolder', { id }),
  toggleFavorite: (id: string) =>
    rawCall<{ ok: boolean; isFavorite?: boolean; favorites?: string[] }>('game.toggleFavorite', { id }),
  cancelInstall: () => rawCall<{ ok: boolean; message?: string }>('game.cancelInstall'),
  progress: (id?: string) =>
    rawCall<InstallProgress>('game.progress', id ? { id } : {}),
  getAchievements: (id: string) =>
    rawCall<GameAchievementsResponse>('achievements.get', { id }),
  refreshAchievements: (id: string) =>
    rawCall<GameAchievementsResponse>('achievements.refresh', { id }),
  storesAuth: (store: string) =>
    rawCall<{ ok: boolean; message?: string; requiresUserAction?: boolean }>('stores.auth', { store }),
  storeSearch: (query: string) =>
    rawCall<{
      ok: boolean
      query?: string
      count?: number
      cancelled?: boolean
      results?: Array<{
        id: string
        title: string
        store: string
        launchTarget?: string | null
        coverUrl?: string | null
        owned?: boolean
        installed?: boolean
        canInstall?: boolean
        source?: string
      }>
    }>('stores.search', { query }),
  listDeps: () => rawCall<{ items: DependencyItem[] }>('deps.list'),
  offerDepInstall: (id: string) =>
    rawCall<{ ok: boolean; message?: string }>('deps.offerInstall', { id }),
  storesMatrix: () => rawCall<StoreStatus[]>('stores.matrix'),
  getSettings: () => rawCall<LauncherSettings>('settings.get'),
  setSettings: (patch: Partial<LauncherSettings>) =>
    rawCall<LauncherSettings>('settings.set', patch as Record<string, unknown>),
  previewTrophy: () => rawCall<{ ok: boolean; message?: string }>('trophies.preview'),
  minimize: () => rawCall<{ ok: boolean }>('shell.minimize'),
  maximize: () => rawCall<{ ok: boolean; maximized?: boolean }>('shell.maximize'),
  windowState: () => rawCall<{ ok: boolean; maximized?: boolean }>('shell.windowState'),
  close: () => rawCall<{ ok: boolean }>('shell.close'),
  openUrl: (url: string) => rawCall<{ ok: boolean }>('shell.openUrl', { url }),
  openPath: (path: string) => rawCall<{ ok: boolean }>('shell.openPath', { path }),
  showStore: (store: string) =>
    rawCall<{ ok: boolean; message?: string }>('shell.showStore', { store }),
  pickFolder: (title?: string) =>
    rawCall<{ ok: boolean; cancelled?: boolean; path?: string; message?: string }>(
      'shell.pickFolder',
      title ? { title } : {},
    ),
  version: () => rawCall<{ version: string }>('app.version'),
  checkUpdate: () =>
    rawCall<{
      ok: boolean
      updateAvailable?: boolean
      latest?: string
      current?: string
      message?: string
      inApp?: boolean
    }>('app.checkUpdate'),
  installUpdate: () =>
    rawCall<{
      ok: boolean
      updateAvailable?: boolean
      alreadyLatest?: boolean
      installed?: boolean
      shouldExit?: boolean
      latest?: string
      current?: string
      message?: string
    }>('app.installUpdate', {}, 30 * 60_000),
}
