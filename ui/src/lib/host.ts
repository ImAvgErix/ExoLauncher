/** Typed bridge to the .NET WebView2 host. Falls back to mock data in browser dev. */

import {
  blockedEntitlementLabel,
  canExposeBuyUrl,
  resolveEntitlementPrimaryAction,
  type EntitlementPrimaryAction,
  type EntitlementState as GameEntitlementState,
} from './entitlementActions'

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

export type PrimaryAction = EntitlementPrimaryAction
export type EntitlementState = GameEntitlementState
export type SortMode = 'name' | 'recent' | 'played' | 'size' | 'store' | 'favorites'

export type DlssStatusItem = {
  fileName?: string
  displayName?: string
  present?: boolean
  eligible?: boolean
  currentVersion?: string | null
  fileVersion?: string | null
  currentDisplayVersion?: string | null
  /** Newest file Exo can put here, or null when it has none. */
  packVersion?: string | null
  packDisplayVersion?: string | null
  /** A shipped copy is on disk, so Restore has something to put back. */
  canRestore?: boolean
  /** Set when this GPU cannot run the destination, e.g. FSR 4 off RDNA 4. */
  unsupportedReason?: string | null
  skipReason?: string | null
}

export type DlssStatus = {
  ok?: boolean
  message?: string | null
  latestDisplayVersion?: string | null
  antiCheatWarning?: boolean
  items?: DlssStatusItem[]
}

/** What one Newest / Restore press did at a single destination. */
export type DlssFileOutcome = {
  fileName?: string
  state?: 'updated' | 'skipped' | 'failed' | 'restored'
  version?: string | null
  displayVersion?: string | null
  message?: string
}

export type DlssRunResult = {
  ok: boolean
  updated?: number
  skipped?: number
  failed?: number
  message?: string
  latestDisplayVersion?: string | null
  files?: DlssFileOutcome[]
}

/** One exact store entry represented by a grouped library card. */
export interface GameVariant {
  id: string
  store: StoreId | string
  installed: boolean
  owned?: boolean
  entitlementState?: EntitlementState
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
  entitlementState?: EntitlementState
  updateAvailable?: boolean
  canInstall?: boolean
  primaryAction?: PrimaryAction | string
  path?: string | null
  coverUrl?: string | null
  coverSource?: string | null
  /** Process-local cache generation. Changes force WebView image revalidation. */
  artRevision?: number
  playtimeMinutes?: number | null
  sizeBytes?: number | null
  status: string
  deps: string[]
  launchNote: string
  launchTarget?: string | null
  /** Official storefront from the host. Absent when Buy must not appear. */
  buyUrl?: string | null
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
  bytesDownloaded?: number | null
  bytesToDownload?: number | null
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
  accountSetupComplete?: boolean
  trophyNotificationsEnabled?: boolean
  /** Legacy fields retained by the native host while older settings migrate. */
  trophyNotificationPreset?: string
  trophyNotificationPosition?: string
  trophyNotificationPositionX?: number
  trophyNotificationPositionY?: number
  trophyNotificationDurationSeconds?: number
  trophyNotificationSound?: boolean
  trophyNotificationSoundCue?: 'exo' | 'soft' | 'off' | string
  /** True when a Steam Web API key is saved on this PC. The key itself never arrives. */
  steamWebApiKeySet?: boolean
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

/** Per-capability honesty: only 'wired' may read as done. */
export type StoreLayerState = 'checking' | 'wired' | 'partial' | 'none'

export interface StoreLayers {
  login?: StoreLayerState
  owned?: StoreLayerState
  covers?: StoreLayerState
  downloads?: StoreLayerState
  social?: StoreLayerState
}

export interface StoreStatus {
  store: string
  displayName: string
  agentPresent: boolean
  /** Whether the vendor's visible desktop client is actually installed. */
  clientPresent?: boolean
  /** Whether Exo's local command backend (Legendary, gogdl, Nile, or IPC) exists. */
  backendPresent?: boolean
  signedIn?: boolean
  cachePresent?: boolean
  detail?: string
  checkCode?: string
  checkedAtUtc?: string
  /** Which parts of the integration are real. Absent until the host reports it. */
  layers?: StoreLayers
}

export type StoreCheckState = 'complete' | 'partial' | 'failed'
export type StoreProbeState = 'present' | 'missing' | 'unavailable'
export type StoreReadiness = 'ready' | 'limited' | 'not_detected' | 'unknown'

export interface StoreLocalCheckItem {
  store: string
  client: StoreProbeState
  backend: StoreProbeState
  session: StoreProbeState
  cache: StoreProbeState
  readiness: StoreReadiness
  code: string
}

export interface StoreLocalCheck {
  state: StoreCheckState
  checkedAtUtc: string
  code: string
  stores: StoreLocalCheckItem[]
}

/** Catalog text from the store, keyed by product id. Absent until the store answers. */
export interface GameMetadata {
  genre?: string | null
  year?: number | null
  description?: string | null
}

export type FriendPresence = 'ingame' | 'online' | 'away' | 'dnd' | 'offline' | 'unknown'

export interface HostFriend {
  id: string
  name: string
  avatarUrl?: string | null
  source?: string
  /** Only present when a store session reported it. */
  status?: FriendPresence
  statusText?: string | null
  playingId?: string | null
  playingTitle?: string | null
  /** When the store last saw them. A timestamp, never a live state. */
  lastSeenUtc?: string | null
  /** Per-row live. Galaxy last-known is never this. */
  live?: boolean
  /** steam | galaxy — where the presence claim came from. */
  presenceFrom?: string | null
}

/** What one store contributed, and why it contributed nothing when it did not. */
export interface FriendSource {
  store: string
  live: boolean
  count: number
  note: string
}

export interface FriendsResponse {
  ok: boolean
  source?: string | null
  /** True only when presence is live. A names-only cache must report false. */
  live?: boolean
  note?: string | null
  friends?: HostFriend[]
  sources?: FriendSource[]
  /** Everyone Exo can read across every store. */
  count?: number
  /** People who are not offline. Cached names and unknown rows count for nothing. */
  activeCount?: number
}

/** A store account the user says belongs to someone on their Exo list. */
export interface PersonLink {
  id: string
  store: string
  /** The store's name for them, when that store can still be read. */
  name?: string | null
}

/** One person the user added on Exo. Local list — no presence, no directory. */
export interface ExoPerson {
  id: string
  handle: string
  name?: string | null
  note?: string | null
  addedUtc?: string | null
  links?: PersonLink[]
}

export interface RosterResponse {
  ok: boolean
  message?: string | null
  /** False for the local fallback; accepted Exo friends use the online namespace. */
  live?: boolean
  note?: string | null
  people?: ExoPerson[]
}

/** A store session Exo could read a name from. The store's identity, not Exo's. */
export interface StoreAccount {
  store: string
  displayName: string
  accountName?: string | null
}

export type ProfileLayout = 'left' | 'center'
export type ProfileBannerHeight = 'short' | 'standard' | 'tall'
export type ProfileShowcaseStyle = 'grid' | 'rows'
export type ProfileAccent = 'ash' | 'steel' | 'sand' | 'clay' | 'sage' | 'rose'

/** Which slot an uploaded picture fills. */
export type ProfileGalleryKind = 'gallery0' | 'gallery1' | 'gallery2' | 'gallery3' | 'gallery4' | 'gallery5'
export type ProfileImageKind = 'avatar' | 'banner' | ProfileGalleryKind

/**
 * One pinned game with what the host actually recorded for it. An absent number
 * was never observed, so the room prints a dash rather than a confident zero.
 */
export interface ProfileShowcaseEntry {
  id: string
  title: string
  store: string
  installed?: boolean
  playtimeMinutes?: number | null
  lastPlayedUtc?: string | null
  achievementsUnlocked?: number | null
  achievementsTotal?: number | null
}

/**
 * The Exo profile. Every authored field comes from the user and is persisted by
 * the host; every number is counted from the real library or the store matrix.
 */
export interface ProfileResponse {
  ok: boolean
  name?: string | null
  handle?: string | null
  /** Reserved account handle while signed in; local is the offline fallback. */
  handleSource?: 'server' | 'local'
  pronouns?: string | null
  statusText?: string | null
  bio?: string | null
  accent?: string | null
  /** Library id whose cover art stands in for an avatar. */
  avatarGameId?: string | null
  bannerGameId?: string | null
  /**
   * Cover-cache URL for a picture the user uploaded from this PC. It outranks
   * the library pick, and it never leaves the machine.
   */
  avatarImageUrl?: string | null
  bannerImageUrl?: string | null
  galleryImages?: Array<{ slot: ProfileGalleryKind; url: string }>
  layout?: ProfileLayout | string
  bannerHeight?: ProfileBannerHeight | string
  showcaseStyle?: ProfileShowcaseStyle | string
  /** Whether the public-facing profile header should print the handle. */
  showHandle?: boolean
  /** Section keys in the user's order. Every known key is present. */
  sections?: string[]
  hiddenSections?: string[]
  playingId?: string | null
  playingTitle?: string | null
  gameCount?: number
  installedCount?: number
  playtimeMinutes?: number | null
  unlockedCount?: number | null
  storesConnected?: number
  rosterCount?: number
  showcase?: string[]
  showcaseEntries?: ProfileShowcaseEntry[]
  storeAccounts?: StoreAccount[]
  stores?: StoreStatus[]
}

/** Absent fields are left alone by the host; an empty string clears one. */
export interface ProfilePatch {
  name?: string
  handle?: string
  pronouns?: string
  statusText?: string
  bio?: string
  accent?: string
  avatarGameId?: string
  bannerGameId?: string
}

/** Absent fields keep their saved choice. Unknown values fall back host-side. */
export interface ProfileLookPatch {
  layout?: ProfileLayout
  bannerHeight?: ProfileBannerHeight
  showcaseStyle?: ProfileShowcaseStyle
  showHandle?: boolean
  sections?: string[]
  hiddenSections?: string[]
}

/** The host owns the file dialog; the UI only ever names the slot. */
export interface ProfileImageResponse {
  ok: boolean
  cancelled?: boolean
  message?: string | null
  profile?: ProfileResponse
}

export type AccountProvider = 'google' | 'email' | 'password'
export type OnlineStaffRole = 'owner' | 'admin' | 'developer'
export type OnlineBadgeKey =
  | 'founder'
  | 'ceo'
  | 'developer'
  | 'moderator'
  | 'contributor'
  | 'early_supporter'
export type OnlineBadgeTone = 'founder' | 'leadership' | 'staff' | 'community' | 'supporter'

export interface OnlineProfileBadge {
  key: OnlineBadgeKey
  label: string
  description: string
  tone: OnlineBadgeTone
  grantedAt?: string | null
}

/** Capability and identity snapshot returned by account.get. */
export interface AccountState {
  ok: boolean
  signedIn: boolean
  configured: boolean
  providers: AccountProvider[]
  message?: string | null
  id?: string | null
  handle?: string | null
  email?: string | null
  provider?: AccountProvider | null
  roles: OnlineStaffRole[]
  canManageBadges: boolean
  badges: OnlineProfileBadge[]
}

export interface AccountOperationResponse {
  ok: boolean
  signedIn?: boolean
  code?: string | null
  message?: string | null
}

export interface AccountPortableProfileResponse extends AccountOperationResponse {
  profile?: OnlineProfileValues | null
  preferences?: OnlinePortablePreferences | null
}

export type OnlineSource = 'live' | 'cache' | 'unavailable'

export interface OnlineError {
  code: string
  message: string
}

export interface OnlineDiagnostics {
  configured: boolean
  signedIn: boolean | null
  source: OnlineSource
  lastSuccessfulSync?: string | null
  retryable: boolean
  error?: OnlineError | null
}

export interface OnlineResult<T> {
  ok: boolean
  value?: T | null
  diagnostics: OnlineDiagnostics
  queued?: boolean
}

export interface OnlineCapabilities {
  providers: { google: boolean; email: boolean; password: boolean }
  profiles: boolean
  friends: boolean
  media: boolean
  presence: boolean
}

export interface OnlineHealth {
  ok: boolean
  service: string
  capabilities: OnlineCapabilities
}

export interface OnlineHandle {
  display: string
  normalized: string
  claimedAt?: string | null
  changedAt?: string | null
}

export interface OnlineFriend {
  userId: string
  handle?: OnlineHandle | null
  avatarUrl?: string | null
  sources: string[]
  connectedAt?: string | null
}

export interface OnlineFriendPage {
  friends: OnlineFriend[]
  nextCursor?: string | null
}

export interface OnlineFriendRequest {
  id: string
  direction: 'incoming' | 'outgoing'
  user: { userId: string; handle?: OnlineHandle | null }
  status: 'pending' | 'accepted' | 'declined'
  createdAt?: string | null
  updatedAt?: string | null
}

export interface OnlineFriendRequestPage {
  incoming: OnlineFriendRequest[]
  outgoing: OnlineFriendRequest[]
  nextIncomingCursor?: string | null
  nextOutgoingCursor?: string | null
}

export interface OnlineBlock {
  userId: string
  handle?: OnlineHandle | null
  createdAt?: string | null
}

export interface OnlineBlockPage {
  blocks: OnlineBlock[]
  nextCursor?: string | null
}

export interface OnlinePrivacy {
  profileVisibility: 'public' | 'friends' | 'private'
  searchable: boolean
  requestPolicy: 'anyone' | 'none'
  activityVisibility: 'friends' | 'private'
  updatedAt?: string | null
}

export interface OnlineProfileMedia {
  available: boolean
  url?: string | null
  contentType?: string | null
  size?: number | null
  source: OnlineSource
  updatedAt?: string | null
}

export type OnlineProfileSection = 'facts' | 'about' | 'showcase' | 'stores'

/** Exact portable values accepted by exo-id. Unknown profile keys never cross the bridge. */
export interface OnlineProfileValues {
  displayName?: string | null
  pronouns?: string | null
  statusText?: string | null
  bio?: string | null
  accent?: ProfileAccent | null
  layout?: ProfileLayout | null
  bannerHeight?: ProfileBannerHeight | null
  showcaseStyle?: ProfileShowcaseStyle | null
  sections?: OnlineProfileSection[] | null
  hiddenSections?: OnlineProfileSection[] | null
  showcase?: string[] | null
  avatarGameId?: string | null
  bannerGameId?: string | null
  [key: string]: string | string[] | null | undefined
}

/** The deny-by-default settings subset that may follow an Exo account. */
export interface OnlinePortablePreferences {
  sortMode?: string | null
  trophyNotificationsEnabled?: boolean | null
  trophyNotificationPosition?: string | null
  trophyNotificationPreset?: string | null
  trophyNotificationSound?: boolean | null
  trophyNotificationSoundCue?: string | null
  [key: string]: string | boolean | null | undefined
}

export interface OnlinePublicProfile {
  userId: string
  handle?: OnlineHandle | null
  profile: OnlineProfileValues
  badges: OnlineProfileBadge[]
  media: {
    avatar?: OnlineProfileMedia | null
    banner?: OnlineProfileMedia | null
    gallery0?: OnlineProfileMedia | null
    gallery1?: OnlineProfileMedia | null
    gallery2?: OnlineProfileMedia | null
    gallery3?: OnlineProfileMedia | null
    gallery4?: OnlineProfileMedia | null
    gallery5?: OnlineProfileMedia | null
  }
}

export interface OnlineAdminBadgeState {
  handle: OnlineHandle
  badges: OnlineProfileBadge[]
}

export interface OnlinePublicProfilePage {
  profiles: Array<{
    userId: string
    handle?: OnlineHandle | null
    profile: OnlineProfileValues
  }>
  nextCursor?: string | null
}

export interface OnlineLinkState {
  discovery: { enabled: boolean; updatedAt?: string | null }
  links: Array<{ store: 'steam' | 'epic' | 'gog'; verified: boolean; verifiedAt?: string | null }>
  connections: Array<{
    userId: string
    handle?: OnlineHandle | null
    store: 'steam' | 'epic' | 'gog'
    createdAt?: string | null
  }>
}

export interface OnlineSession {
  id: string
  current: boolean
  createdAt?: string | null
  updatedAt?: string | null
  expiresAt?: string | null
  userAgent?: string | null
}

export interface OnlineSessionPage {
  sessions: OnlineSession[]
}

export interface OnlinePresenceEntry {
  userId: string
  status: 'unknown' | 'offline' | 'online' | 'away' | 'ingame'
  gameId?: string | null
  gameTitle?: string | null
  lastSeen?: string | null
  available: boolean
}

export interface OnlinePresenceRoster {
  friends: OnlinePresenceEntry[]
  unavailable: boolean
}

export interface OnlinePresenceEvent {
  kind: 'ready' | 'ack' | 'presence' | 'error' | 'transportError'
  scope?: 'user' | 'roster' | null
  presence?: OnlinePresenceEntry | null
  error?: OnlineError | null
  receivedAt?: string
}

export interface LibraryResponse {
  games: Game[]
  count: number
  stores: StoreStatus[]
  progress?: InstallProgress
  queuedGameIds?: string[]
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
  queued?: boolean
  queuedGameIds?: string[]
  handoffOnly?: boolean
  needsDependencies?: boolean
  missingDependencies?: MissingDependency[]
}

export interface ArtworkMutationResponse {
  ok: boolean
  cancelled?: boolean
  message?: string | null
  game?: Game | null
  artRevision?: number
}

export interface ArtworkReportResponse {
  ok: boolean
  copied?: boolean
  issueOpened?: boolean
  message?: string | null
}

type HostRequest = { id: string; method: string; params?: Record<string, unknown> }
type HostResponse = { id: string; ok: boolean; result?: unknown; error?: string }
type HostEvent = { event: string; data?: unknown }

const pending = new Map<string, { resolve: (v: unknown) => void; reject: (e: Error) => void }>()
const eventHandlers = new Map<string, Set<(data: unknown) => void>>()

type BuyUrlCarrier = { id?: string; buyUrl?: string | null }

/** Catalog cards drop extra payload fields; keep the host URL by id. */
const buyUrlById = new Map<string, string>()

function rememberBuyUrls(rows: Array<BuyUrlCarrier | null | undefined> | undefined) {
  if (!rows) return
  for (const row of rows) {
    if (!row?.id) continue
    const url = row.buyUrl?.trim()
    if (url) buyUrlById.set(row.id, url)
    else buyUrlById.delete(row.id)
  }
}

function rememberEventBuyUrls(event: string, data?: unknown) {
  if (!data || typeof data !== 'object') return
  const payload = data as { games?: BuyUrlCarrier[]; results?: BuyUrlCarrier[] }
  if (event === 'library.updated') rememberBuyUrls(payload.games)
  if (event === 'stores.search.partial') rememberBuyUrls(payload.results)
}

function emitHostEvent(event: string, data?: unknown) {
  rememberEventBuyUrls(event, data)
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

export interface IdentityHostEventMap {
  'account.updated': AccountState
  'profile.updated': ProfileResponse
  'online.presence': OnlinePresenceEvent
}

export function onHostEvent<K extends keyof IdentityHostEventMap>(
  event: K,
  handler: (data: IdentityHostEventMap[K]) => void,
): () => void
export function onHostEvent(event: string, handler: (data: unknown) => void): () => void
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

const inflightReads = new Map<string, Promise<unknown>>()

/** Reads that are safe to share while an identical call is already on the wire. */
const COALESCE_READS = new Set([
  'library.get',
  'account.get',
  'account.getProfile',
  'online.health',
  'online.profiles.get',
  'online.profiles.search',
  'online.privacy.get',
  'online.friends.list',
  'online.friends.requests',
  'online.blocks.list',
  'online.links.get',
  'online.sessions.list',
  'online.media.download',
  'online.presence.get',
  'profile.get',
  'settings.get',
  'stores.matrix',
  'friends.list',
  'friends.roster',
  'deps.list',
  'game.get',
  'game.metadata',
  'dlss.status',
  'game.extras',
  'game.progress',
  'achievements.get',
  'dlss.status',
  'app.version',
  'app.checkUpdate',
  'shell.windowState',
])

async function rawCall<T>(method: string, params?: Record<string, unknown>, timeoutMs = 600_000): Promise<T> {
  if (!isHost()) return mockCall<T>(method, params)
  const coalesce = COALESCE_READS.has(method)
  const key = coalesce ? `${method}:${JSON.stringify(params ?? {})}` : ''
  if (coalesce) {
    const existing = inflightReads.get(key)
    if (existing) return existing as Promise<T>
  }
  const id = crypto.randomUUID()
  const req: HostRequest = { id, method, params }
  const work = new Promise<T>((resolve, reject) => {
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
  if (coalesce) {
    inflightReads.set(key, work)
    const clearInflight = () => {
      if (inflightReads.get(key) === work) inflightReads.delete(key)
    }
    void work.then(clearInflight, clearInflight)
  }
  return work
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
  steamWebApiKeySet: false,
  recent: [],
}

const MOCK_ROSTER_NOTE =
  'Browser mock — local handles live in memory. Sign in to use Exo friend requests and presence.'

/** Nothing authored, nothing counted. The mock must not invent an identity. */
const mockProfile: ProfileResponse = {
  ok: true,
  accent: 'ash',
  layout: 'left',
  bannerHeight: 'standard',
  showcaseStyle: 'grid',
  showHandle: true,
  sections: ['facts', 'about', 'showcase', 'stores'],
  hiddenSections: [],
  gameCount: MOCK_GAMES.length,
  installedCount: MOCK_GAMES.filter((game) => game.installed).length,
  playtimeMinutes: null,
  unlockedCount: null,
  storesConnected: 0,
  rosterCount: 0,
  showcase: [],
  showcaseEntries: [],
  storeAccounts: [],
  stores: [],
}

const mockAccount: AccountState = {
  ok: true,
  signedIn: false,
  configured: false,
  providers: [],
  roles: [],
  canManageBadges: false,
  badges: [],
}

const mockOnlineDiagnostics: OnlineDiagnostics = {
  configured: false,
  signedIn: false,
  source: 'unavailable',
  lastSuccessfulSync: null,
  retryable: false,
  error: { code: 'NOT_CONFIGURED', message: 'Browser mock — online services are not configured.' },
}

function mockOnlineUnavailable<T>(): OnlineResult<T> {
  return { ok: false, value: null, diagnostics: { ...mockOnlineDiagnostics } }
}

const MOCK_ACCOUNT_UNAVAILABLE = 'Browser mock — Exo accounts are not configured.'

/** The browser mock has no file dialog and no cover cache to copy a picture into. */
const MOCK_NO_PICKER = 'Browser mock — picking a picture needs the Exo host.'

let mockRoster: ExoPerson[] = []

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
    case 'art.replace':
      return { ok: false, cancelled: false, message: MOCK_NO_PICKER } as T
    case 'art.reset':
    case 'art.refetch': {
      const id = String(params?.id ?? '')
      const index = MOCK_GAMES.findIndex((game) =>
        game.id === id || game.variants?.some((variant) => variant.id === id),
      )
      if (index < 0) return { ok: false, message: 'Game not found.' } as T
      const current = MOCK_GAMES[index]
      const next: Game = {
        ...current,
        ...(method === 'art.reset' ? { coverUrl: null, coverSource: null } : {}),
        artRevision: (current.artRevision ?? 0) + 1,
      }
      MOCK_GAMES[index] = next
      emitHostEvent('library.updated', { games: [...MOCK_GAMES], count: MOCK_GAMES.length })
      return {
        ok: true,
        message: method === 'art.reset' ? 'Cover reset.' : 'Artwork refreshed.',
        game: next,
        artRevision: next.artRevision,
      } as T
    }
    case 'art.report':
      return {
        ok: false,
        copied: false,
        issueOpened: false,
        message: 'Browser mock — copying artwork diagnostics needs the Exo host.',
      } as T
    case 'game.repair':
      return { ok: true, queued: false, message: 'Mock: would verify files.' } as T
    case 'game.extras':
      return { ok: true, canRepair: true, repairLabel: 'Verify files' } as T
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
    case 'friends.list':
      return {
        ok: true,
        source: null,
        live: false,
        note: 'Browser mock — no store client to read a friends cache from.',
        friends: [],
        sources: [],
        count: 0,
        activeCount: 0,
      } as T
    case 'friends.roster':
      return { ok: true, live: false, note: MOCK_ROSTER_NOTE, people: [...mockRoster] } as T
    case 'friends.add': {
      const handle = String(params?.handle ?? '')
        .toLowerCase()
        .replace(/[^a-z0-9_]/g, '')
      if (handle.length < 2) {
        return { ok: false, message: 'Handles are lowercase letters, numbers, and underscore.', live: false, note: MOCK_ROSTER_NOTE, people: [...mockRoster] } as T
      }
      if (mockRoster.some((person) => person.handle === handle)) {
        return { ok: false, message: `@${handle} is already on your list.`, live: false, note: MOCK_ROSTER_NOTE, people: [...mockRoster] } as T
      }
      mockRoster.push({
        id: `exo:${handle}`,
        handle,
        name: String(params?.name ?? '').trim() || null,
        note: String(params?.note ?? '').trim() || null,
        addedUtc: new Date().toISOString(),
      })
      return { ok: true, live: false, note: MOCK_ROSTER_NOTE, people: [...mockRoster] } as T
    }
    case 'friends.remove': {
      const id = String(params?.id ?? '')
      mockRoster = mockRoster.filter((person) => person.id !== id)
      return { ok: true, live: false, note: MOCK_ROSTER_NOTE, people: [...mockRoster] } as T
    }
    case 'friends.setNote': {
      const id = String(params?.id ?? '')
      const note = String(params?.note ?? '').trim()
      mockRoster = mockRoster.map((person) =>
        person.id === id ? { ...person, note: note || null } : person,
      )
      return { ok: true, live: false, note: MOCK_ROSTER_NOTE, people: [...mockRoster] } as T
    }
    // The mock has no store list, so there is never a real row to claim.
    case 'friends.link':
    case 'friends.unlink':
      return {
        ok: false,
        message: 'Browser mock — linking a store account needs the Exo host.',
        live: false,
        note: MOCK_ROSTER_NOTE,
        people: [...mockRoster],
      } as T
    case 'account.get':
      return { ...mockAccount, providers: [...mockAccount.providers] } as T
    case 'account.signIn':
    case 'account.createPassword':
    case 'account.signInPassword':
    case 'account.reserveHandle':
    case 'account.setProfile':
      return { ok: false, signedIn: false, message: MOCK_ACCOUNT_UNAVAILABLE } as T
    case 'account.signOut':
      return { ok: true, signedIn: false, message: 'Signed out.' } as T
    case 'account.getProfile':
      return { ok: true, signedIn: false, profile: null, preferences: null } as T
    case 'online.health':
    case 'online.profiles.get':
    case 'online.profiles.search':
    case 'online.badges.get':
    case 'online.badges.grant':
    case 'online.badges.revoke':
    case 'online.privacy.get':
    case 'online.privacy.set':
    case 'online.friends.list':
    case 'online.friends.requests':
    case 'online.friends.request':
    case 'online.friends.accept':
    case 'online.friends.decline':
    case 'online.friends.remove':
    case 'online.blocks.list':
    case 'online.blocks.block':
    case 'online.blocks.unblock':
    case 'online.links.get':
    case 'online.links.discovery':
    case 'online.links.link':
    case 'online.links.unlink':
    case 'online.links.match':
    case 'online.sessions.list':
    case 'online.sessions.revoke':
    case 'online.sessions.revokeAll':
    case 'online.media.upload':
    case 'online.media.delete':
    case 'online.media.download':
    case 'online.presence.get':
      return mockOnlineUnavailable<unknown>() as T
    case 'online.profiles.share':
      return { ok: false, message: MOCK_ACCOUNT_UNAVAILABLE } as T
    case 'online.account.export':
      return {
        ok: false,
        cancelled: false,
        message: MOCK_ACCOUNT_UNAVAILABLE,
        diagnostics: { ...mockOnlineDiagnostics },
      } as T
    case 'online.account.delete':
      return mockOnlineUnavailable<unknown>() as T
    case 'profile.get':
      return { ...mockProfile, rosterCount: mockRoster.length } as T
    case 'profile.set': {
      for (const [key, value] of Object.entries(params ?? {})) {
        if (typeof value !== 'string') continue
        // Same contract as the host: an empty string clears the field.
        Object.assign(mockProfile, { [key]: value.length > 0 ? value : null })
      }
      return { ...mockProfile, rosterCount: mockRoster.length } as T
    }
    case 'profile.setLook': {
      const patch = (params ?? {}) as ProfileLookPatch
      if (patch.layout) mockProfile.layout = patch.layout
      if (patch.bannerHeight) mockProfile.bannerHeight = patch.bannerHeight
      if (patch.showcaseStyle) mockProfile.showcaseStyle = patch.showcaseStyle
      if (patch.showHandle !== undefined) mockProfile.showHandle = patch.showHandle
      if (patch.sections) mockProfile.sections = [...patch.sections]
      if (patch.hiddenSections) mockProfile.hiddenSections = [...patch.hiddenSections]
      return { ...mockProfile, rosterCount: mockRoster.length } as T
    }
    case 'profile.setShowcase': {
      const ids = Array.isArray(params?.ids) ? (params.ids as unknown[]) : []
      mockProfile.showcase = ids.filter((id): id is string => typeof id === 'string').slice(0, 6)
      mockProfile.showcaseEntries = mockProfile.showcase.flatMap((id) => {
        const game = MOCK_GAMES.find((entry) => entry.id === id)
        return game
          ? [
              {
                id: game.id,
                title: game.title,
                store: game.store,
                installed: game.installed,
                playtimeMinutes: game.playtimeMinutes ?? null,
              },
            ]
          : []
      })
      return { ...mockProfile, rosterCount: mockRoster.length } as T
    }
    case 'profile.pickImage':
      return { ok: false, cancelled: false, message: MOCK_NO_PICKER } as T
    case 'profile.clearImage':
      return { ok: true, cancelled: false, profile: { ...mockProfile } } as T
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
    case 'stores.check':
      return {
        state: 'complete',
        checkedAtUtc: new Date().toISOString(),
        code: 'local_check_complete',
        stores: [
          {
            store: 'local',
            client: 'missing',
            backend: 'present',
            session: 'present',
            cache: 'present',
            readiness: 'ready',
            code: 'ready',
          },
        ],
      } as T
    case 'stores.matrix':
      return [
        {
          store: 'local',
          displayName: 'Local',
          agentPresent: true,
          backendPresent: true,
          signedIn: true,
          cachePresent: true,
          detail: 'Ready',
          checkCode: 'checked',
          checkedAtUtc: new Date().toISOString(),
          layers: {
            login: 'wired',
            owned: 'wired',
            covers: 'partial',
            downloads: 'wired',
            social: 'none',
          },
        },
      ] as T
    case 'settings.get':
      return { ...mockSettings } as T
    case 'settings.set': {
      const next = { ...(params ?? {}) } as Record<string, unknown>
      if (typeof next.steamWebApiKey === 'string') {
        mockSettings.steamWebApiKeySet = next.steamWebApiKey.trim().length > 0
        delete next.steamWebApiKey
      }
      Object.assign(mockSettings, next)
      mockSettings.antiCheatSafeMode = true
      delete (mockSettings as { steamWebApiKey?: string }).steamWebApiKey
      return { ...mockSettings } as T
    }
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
    case 'dlss.status':
      return { ok: true, items: [] } as T
    case 'dlss.updateAll':
    case 'dlss.restore':
      return { ok: false, message: 'Mock: no upscaler files.' } as T
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
  return resolveEntitlementPrimaryAction(game)
}

/** Play | Download | Install | Update | Stop. No action is not a purchase. */
export function primaryCtaLabel(game: Game, action = resolvePrimaryAction(game)): string {
  if (game.canStop) return 'Stop'
  const blockedLabel = blockedEntitlementLabel(game, action)
  if (blockedLabel) return blockedLabel
  if (action === 'update') return 'Update'
  if (action === 'play') return 'Play'
  if (action === 'install') return game.store === 'local' ? 'Install' : 'Download'
  if (game.installed) return 'Unavailable'
  return 'Not installed'
}

/**
 * Storefront URL the host already computed. Normal owned/installed titles use
 * Play or Install; an explicitly revoked title is the sole installed exception
 * and may offer Buy again. Unverified ownership never becomes a purchase claim.
 */
export function hostedBuyUrl(game: Game): string | null {
  if (!canExposeBuyUrl(game)) return null
  const fromGame = game.buyUrl?.trim()
  if (fromGame) return fromGame
  return buyUrlById.get(game.id) ?? null
}

export const host = {
  getLibrary: (force = false) =>
    rawCall<LibraryResponse>(force ? 'library.refresh' : 'library.get', { force }).then((res) => {
      rememberBuyUrls(res.games)
      return res
    }),
  getGame: (id: string) =>
    rawCall<{ ok: boolean; game?: Game; message?: string; metadata?: GameMetadata | null }>('game.get', { id }).then((res) => {
      if (res.game) rememberBuyUrls([res.game])
      return res
    }),
  /** Details-only catalog lookup. Never call this per tile. */
  gameMetadata: (id: string) =>
    rawCall<{ ok: boolean; metadata?: GameMetadata | null }>('game.metadata', { id }),
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
  uninstall: (id: string) => rawCall<{ ok: boolean; message?: string; queued?: boolean }>('game.uninstall', { id }),
  repair: (id: string) =>
    rawCall<InstallResponse>('game.repair', { id }),
  gameExtras: (id: string) =>
    rawCall<{ ok: boolean; canRepair?: boolean; repairLabel?: string; message?: string }>('game.extras', { id }),
  openFolder: (id: string) => rawCall<{ ok: boolean; message?: string; path?: string }>('game.openFolder', { id }),
  toggleFavorite: (id: string) =>
    rawCall<{ ok: boolean; isFavorite?: boolean; favorites?: string[] }>('game.toggleFavorite', { id }),
  /** Opens the native image picker. The UI never supplies a filesystem path. */
  artReplace: (id: string) => rawCall<ArtworkMutationResponse>('art.replace', { id }),
  artReset: (id: string) => rawCall<ArtworkMutationResponse>('art.reset', { id }),
  artRefetch: (id: string) => rawCall<ArtworkMutationResponse>('art.refetch', { id }),
  artReport: (id: string, openIssue = false) =>
    rawCall<ArtworkReportResponse>('art.report', { id, openIssue }),
  cancelInstall: () => rawCall<{ ok: boolean; message?: string }>('game.cancelInstall'),
  progress: (id?: string) =>
    rawCall<InstallProgress>('game.progress', id ? { id } : {}),
  getAchievements: (id: string) =>
    rawCall<GameAchievementsResponse>('achievements.get', { id }),
  refreshAchievements: (id: string) =>
    rawCall<GameAchievementsResponse>('achievements.refresh', { id }),
  storesAuth: (store: string) =>
    rawCall<{ ok: boolean; message?: string; requiresUserAction?: boolean }>('stores.auth', { store }),
  friendsList: (live = false) =>
    rawCall<FriendsResponse>('friends.list', live ? { live: true } : {}),
  friendsRoster: () => rawCall<RosterResponse>('friends.roster'),
  friendsAdd: (handle: string, name?: string, note?: string) =>
    rawCall<RosterResponse>('friends.add', { handle, name: name ?? '', note: note ?? '' }),
  friendsRemove: (id: string) => rawCall<RosterResponse>('friends.remove', { id }),
  friendsSetNote: (id: string, note: string) =>
    rawCall<RosterResponse>('friends.setNote', { id, note }),
  friendsLink: (id: string, friendId: string) =>
    rawCall<RosterResponse>('friends.link', { id, friendId }),
  friendsUnlink: (id: string, friendId: string) =>
    rawCall<RosterResponse>('friends.unlink', { id, friendId }),
  accountGet: () => rawCall<AccountState>('account.get'),
  accountSignIn: (provider: Exclude<AccountProvider, 'password'>, email?: string) =>
    rawCall<AccountOperationResponse>('account.signIn', {
      provider,
      ...(email ? { email } : {}),
    }),
  accountCreatePassword: (name: string, email: string, password: string) =>
    rawCall<AccountOperationResponse>('account.createPassword', { name, email, password }),
  accountPasswordSignIn: (email: string, password: string) =>
    rawCall<AccountOperationResponse>('account.signInPassword', { email, password }),
  accountSignOut: () => rawCall<AccountOperationResponse>('account.signOut'),
  accountReserveHandle: (handle: string) =>
    rawCall<AccountOperationResponse>('account.reserveHandle', { handle }),
  accountGetProfile: () => rawCall<AccountPortableProfileResponse>('account.getProfile'),
  accountSetProfile: () => rawCall<AccountOperationResponse>('account.setProfile'),
  onlineHealth: () => rawCall<OnlineResult<OnlineHealth>>('online.health'),
  onlineProfile: (handle: string, userId?: string) =>
    rawCall<OnlineResult<OnlinePublicProfile>>('online.profiles.get', {
      handle,
      ...(userId ? { userId } : {}),
    }),
  onlineProfileSearch: (query: string, limit = 20, cursor?: string) =>
    rawCall<OnlineResult<OnlinePublicProfilePage>>('online.profiles.search', {
      query,
      limit,
      ...(cursor ? { cursor } : {}),
    }),
  onlineProfileShare: (handle: string, action: 'copy' | 'open' = 'copy') =>
    rawCall<{ ok: boolean; message?: string }>('online.profiles.share', { handle, action }),
  onlineBadgesGet: (handle: string) =>
    rawCall<OnlineResult<OnlineAdminBadgeState>>('online.badges.get', { handle }),
  onlineBadgesGrant: (handle: string, badge: OnlineBadgeKey) =>
    rawCall<OnlineResult<OnlineAdminBadgeState>>('online.badges.grant', { handle, badge }),
  onlineBadgesRevoke: (handle: string, badge: OnlineBadgeKey) =>
    rawCall<OnlineResult<OnlineAdminBadgeState>>('online.badges.revoke', { handle, badge }),
  onlinePrivacy: () => rawCall<OnlineResult<OnlinePrivacy>>('online.privacy.get'),
  onlineSetPrivacy: (privacy: OnlinePrivacy) =>
    rawCall<OnlineResult<OnlinePrivacy>>('online.privacy.set', {
      profileVisibility: privacy.profileVisibility,
      searchable: privacy.searchable,
      requestPolicy: privacy.requestPolicy,
      activityVisibility: privacy.activityVisibility,
    }),
  onlineFriends: (limit = 50, cursor?: string) =>
    rawCall<OnlineResult<OnlineFriendPage>>('online.friends.list', {
      limit,
      ...(cursor ? { cursor } : {}),
    }),
  onlineFriendRequests: (limit = 20, incomingCursor?: string, outgoingCursor?: string) =>
    rawCall<OnlineResult<OnlineFriendRequestPage>>('online.friends.requests', {
      limit,
      ...(incomingCursor ? { incomingCursor } : {}),
      ...(outgoingCursor ? { outgoingCursor } : {}),
    }),
  onlineFriendRequest: (handle: string) =>
    rawCall<OnlineResult<OnlineFriendRequest>>('online.friends.request', { handle }),
  onlineFriendAccept: (requestId: string) =>
    rawCall<OnlineResult<OnlineFriendRequest>>('online.friends.accept', { requestId }),
  onlineFriendDecline: (requestId: string) =>
    rawCall<OnlineResult<OnlineFriendRequest>>('online.friends.decline', { requestId }),
  onlineFriendRemove: (userId: string) =>
    rawCall<OnlineResult<{ ok: boolean }>>('online.friends.remove', { userId }),
  onlineBlocks: (limit = 20, cursor?: string) =>
    rawCall<OnlineResult<OnlineBlockPage>>('online.blocks.list', {
      limit,
      ...(cursor ? { cursor } : {}),
    }),
  onlineBlock: (userId: string) =>
    rawCall<OnlineResult<OnlineBlock>>('online.blocks.block', { userId }),
  onlineUnblock: (userId: string) =>
    rawCall<OnlineResult<{ ok: boolean }>>('online.blocks.unblock', { userId }),
  onlineLinks: () => rawCall<OnlineResult<OnlineLinkState>>('online.links.get'),
  onlineSetDiscovery: (enabled: boolean) =>
    rawCall<OnlineResult<{ enabled: boolean; updatedAt?: string | null }>>(
      'online.links.discovery',
      { enabled },
    ),
  onlineLinkStore: (store: 'steam' | 'epic' | 'gog') =>
    rawCall<OnlineResult<OnlineLinkState | { store: string; verified: boolean; verifiedAt?: string | null }>>(
      'online.links.link',
      { store },
    ),
  onlineUnlinkStore: (store: 'steam' | 'epic' | 'gog') =>
    rawCall<OnlineResult<{ ok: boolean }>>('online.links.unlink', { store }),
  onlineMatchStore: (store: 'steam' | 'epic' | 'gog') =>
    rawCall<OnlineResult<{ matches: OnlineLinkState['connections'] }>>('online.links.match', { store }),
  onlineSessions: () => rawCall<OnlineResult<OnlineSessionPage>>('online.sessions.list'),
  onlineRevokeSession: (sessionId: string) =>
    rawCall<OnlineResult<{ ok: boolean }>>('online.sessions.revoke', { sessionId }),
  onlineRevokeAllSessions: () =>
    rawCall<OnlineResult<{ ok: boolean }>>('online.sessions.revokeAll'),
  onlineExportAccount: () =>
    rawCall<{
      ok: boolean
      cancelled?: boolean
      message?: string | null
      diagnostics: OnlineDiagnostics
    }>('online.account.export'),
  onlineDeleteAccount: () =>
    rawCall<OnlineResult<{ ok: boolean; handleHeldUntil?: string | null }>>('online.account.delete'),
  onlineUploadMedia: (kind: ProfileImageKind) =>
    rawCall<OnlineResult<{ kind: ProfileImageKind; updatedAt?: string | null }>>(
      'online.media.upload',
      { kind },
    ),
  onlineDeleteMedia: (kind: ProfileImageKind) =>
    rawCall<OnlineResult<{ ok: boolean }>>('online.media.delete', { kind }),
  onlineDownloadMedia: (userId: string, kind: ProfileImageKind) =>
    rawCall<OnlineResult<{ url: string; contentType: string; size: number; sha256: string }>>(
      'online.media.download',
      { userId, kind },
    ),
  onlinePresence: (limit = 50) =>
    rawCall<OnlineResult<OnlinePresenceRoster>>('online.presence.get', { limit }),
  profileGet: () => rawCall<ProfileResponse>('profile.get'),
  profileSet: (patch: ProfilePatch) => rawCall<ProfileResponse>('profile.set', { ...patch }),
  profileSetLook: (patch: ProfileLookPatch) =>
    rawCall<ProfileResponse>('profile.setLook', { ...patch }),
  profileSetShowcase: (ids: string[]) => rawCall<ProfileResponse>('profile.setShowcase', { ids }),
  /** Opens the host's file picker, then stores the picture inside Exo. */
  profilePickImage: (kind: ProfileImageKind) =>
    rawCall<ProfileImageResponse>('profile.pickImage', { kind }),
  profileClearImage: (kind: ProfileImageKind) =>
    rawCall<ProfileImageResponse>('profile.clearImage', { kind }),
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
        buyUrl?: string | null
      }>
    }>('stores.search', { query }).then((res) => {
      rememberBuyUrls(res.results)
      return res
    }),
  listDeps: () => rawCall<{ items: DependencyItem[] }>('deps.list'),
  offerDepInstall: (id: string) =>
    rawCall<{ ok: boolean; message?: string }>('deps.offerInstall', { id }),
  storesCheck: () => rawCall<StoreLocalCheck>('stores.check'),
  storesMatrix: () => rawCall<StoreStatus[]>('stores.matrix'),
  getSettings: () => rawCall<LauncherSettings>('settings.get'),
  setSettings: (patch: Partial<LauncherSettings> & { steamWebApiKey?: string }) =>
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
  dlssStatus: (id: string) =>
    rawCall<DlssStatus>('dlss.status', { id }),
  /** Newest for every destination this game ships, in one host pass. */
  dlssApply: (id: string) =>
    rawCall<DlssRunResult>('dlss.updateAll', { id }, 10 * 60_000),
  dlssRestore: (id: string) =>
    rawCall<DlssRunResult>('dlss.restore', { id }),
}
