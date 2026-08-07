/** Typed bridge to the .NET WebView2 host. Falls back to mock data in browser dev. */

export type StoreId =
  | 'local'
  | 'steam'
  | 'epic'
  | 'gog'
  | 'riot'
  | 'xbox'
  | 'ea'
  | 'ubisoft'
  | 'battlenet'

export interface Game {
  id: string
  title: string
  store: StoreId | string
  installed: boolean
  path?: string | null
  coverUrl?: string | null
  playtimeMinutes?: number | null
  sizeBytes?: number | null
  status: string
  deps: string[]
  launchNote: string
  launchTarget?: string | null
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
}

export interface StoreStatus {
  store: string
  displayName: string
  agentPresent: boolean
}

export interface LibraryResponse {
  games: Game[]
  count: number
  stores: StoreStatus[]
}

export interface LaunchResponse {
  ok: boolean
  message: string
  processId?: number | null
  backendStarted?: string | null
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

async function rawCall<T>(method: string, params?: Record<string, unknown>, timeoutMs = 60_000): Promise<T> {
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
    id: 'mock:valorant',
    title: 'VALORANT',
    store: 'riot',
    installed: false,
    status: 'Demo',
    playtimeMinutes: 0,
    sizeBytes: 30 * 1024 ** 3,
    deps: ['Riot Client', 'Vanguard'],
    launchNote: 'Demo entry. Real VALORANT needs Riot Client + Vanguard on disk.',
  },
  {
    id: 'mock:hades',
    title: 'Hades',
    store: 'steam',
    installed: false,
    status: 'Demo',
    playtimeMinutes: 1240,
    sizeBytes: 15 * 1024 ** 3,
    deps: ['Steam client'],
    launchNote: 'Demo entry. Real Steam titles launch via steam://run.',
  },
  {
    id: 'mock:celeste',
    title: 'Celeste',
    store: 'local',
    installed: false,
    status: 'Demo',
    playtimeMinutes: 380,
    sizeBytes: 1200 * 1024 ** 2,
    deps: [],
    launchNote: 'Demo entry. Local/DRM-free titles launch the exe directly.',
  },
  {
    id: 'mock:control',
    title: 'Control',
    store: 'epic',
    installed: false,
    status: 'Demo',
    playtimeMinutes: 720,
    sizeBytes: 42 * 1024 ** 3,
    deps: ['Legendary or Epic Launcher'],
    launchNote: 'Demo entry. Epic prefers Legendary when present.',
  },
  {
    id: 'mock:disco',
    title: 'Disco Elysium',
    store: 'gog',
    installed: false,
    status: 'Demo',
    playtimeMinutes: 2100,
    sizeBytes: 20 * 1024 ** 3,
    deps: ['GOG Galaxy (optional offline)'],
    launchNote: 'Demo entry. GOG offline builds are first-class local launches.',
  },
  {
    id: 'mock:forza',
    title: 'Forza Horizon',
    store: 'xbox',
    installed: false,
    status: 'Demo',
    playtimeMinutes: 540,
    sizeBytes: 100 * 1024 ** 3,
    deps: ['Gaming Services'],
    launchNote: 'Demo entry. Xbox titles keep Gaming Services as backend.',
  },
]

const mockSettings: LauncherSettings = {
  appVersion: '0.1.0-dev',
  closeStoreClientsAfterLaunch: true,
  autoInstallRedistributables: false,
  minimizeWhilePlaying: true,
  antiCheatSafeMode: true,
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
          { store: 'steam', displayName: 'Steam', agentPresent: false },
          { store: 'epic', displayName: 'Epic', agentPresent: false },
          { store: 'riot', displayName: 'Riot', agentPresent: false },
        ],
      } as T
    case 'game.launch':
      return {
        ok: false,
        message: 'Browser mock — launch only works inside the WinUI host.',
      } as T
    case 'deps.list':
      return {
        items: [
          {
            id: 'vcredist',
            name: 'Visual C++ Redistributable',
            status: 'Present',
            detail: 'Mock',
            canOfferInstall: true,
          },
          {
            id: 'directx',
            name: 'DirectX',
            status: 'Present',
            detail: 'Mock',
            canOfferInstall: true,
          },
          {
            id: 'dotnet',
            name: '.NET Desktop Runtime',
            status: 'Present',
            detail: 'Mock',
            canOfferInstall: true,
          },
          {
            id: 'webview2',
            name: 'WebView2 Runtime',
            status: 'Present',
            detail: 'Mock',
            canOfferInstall: true,
          },
        ],
      } as T
    case 'deps.offerInstall':
      return { ok: true, message: 'Mock: would open official installer page.' } as T
    case 'settings.get':
      return { ...mockSettings } as T
    case 'settings.set':
      Object.assign(mockSettings, params ?? {})
      mockSettings.antiCheatSafeMode = true
      return { ...mockSettings } as T
    case 'shell.minimize':
    case 'shell.close':
      return { ok: true } as T
    case 'app.version':
      return { version: '0.1.0-dev' } as T
    default:
      throw new Error(`Unknown mock method: ${method}`)
  }
}

export const host = {
  getLibrary: (force = false) =>
    rawCall<LibraryResponse>(force ? 'library.refresh' : 'library.get', { force }),
  launch: (id: string) => rawCall<LaunchResponse>('game.launch', { id }),
  listDeps: () => rawCall<{ items: DependencyItem[] }>('deps.list'),
  offerDepInstall: (id: string) =>
    rawCall<{ ok: boolean; message?: string }>('deps.offerInstall', { id }),
  getSettings: () => rawCall<LauncherSettings>('settings.get'),
  setSettings: (patch: Partial<LauncherSettings>) =>
    rawCall<LauncherSettings>('settings.set', patch as Record<string, unknown>),
  minimize: () => rawCall<{ ok: boolean }>('shell.minimize'),
  close: () => rawCall<{ ok: boolean }>('shell.close'),
  openUrl: (url: string) => rawCall<{ ok: boolean }>('shell.openUrl', { url }),
}
