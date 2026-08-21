import {
  useCallback,
  useEffect,
  useRef,
  useState,
  type CSSProperties,
  type KeyboardEvent,
  type ReactNode,
} from 'react'
import { Check, Coffee, ExternalLink, FileText } from '../brand/icons'
import {
  host,
  type AccountState,
  type DependencyItem,
  type LauncherSettings,
  type OnlineAdminBadgeState,
  type OnlineBadgeKey,
  type OnlineLinkState,
  type OnlinePrivacy,
  type ProfileResponse,
  type SortMode,
  type StoreLayerState,
  type StoreLayers,
  type StoreStatus,
} from '../lib/host'
import { addPortableFolder } from '../lib/portable'
import { canConnectStore, canOpenStoreClient, settingsStoreRows, storeClientDownloadUrl, storePresenceLabel } from '../lib/stores'
import { TrophyNotificationSettings } from './TrophyNotificationSettings'
import { AccountPanel } from './AccountPanel'

const STEAM_WEB_API_KEY = 'https://steamcommunity.com/dev/apikey'
const BUY_ME_A_COFFEE = 'https://www.buymeacoffee.com/UhhErix'
const RELEASES = 'https://github.com/ImAvgErix/ExoLauncher/releases/latest'
const ISSUES = 'https://github.com/ImAvgErix/ExoLauncher/issues'
const PRIVACY = 'https://github.com/ImAvgErix/ExoLauncher/blob/main/PRIVACY.md'
const DEFAULT_INSTALL = 'LocalAppData\\ExoLauncher\\Games'

const GRANTABLE_BADGES: ReadonlyArray<{ key: OnlineBadgeKey; label: string }> = [
  { key: 'developer', label: 'Developer' },
  { key: 'moderator', label: 'Moderator' },
  { key: 'contributor', label: 'Contributor' },
  { key: 'early_supporter', label: 'Early Supporter' },
]
const GRANTABLE_BADGE_KEYS = new Set<OnlineBadgeKey>(GRANTABLE_BADGES.map((badge) => badge.key))

const SECTIONS = [
  { id: 'library', label: 'Library', title: 'Library', blurb: 'How Exo orders your games and where new ones land.' },
  { id: 'stores', label: 'Stores', title: 'Stores on this PC', blurb: 'What Exo can do with each store today.' },
  { id: 'account', label: 'Account', title: 'Account', blurb: 'Identity, privacy, and verified store discovery.' },
  { id: 'unlocks', label: 'Unlocks', title: 'Unlocks', blurb: 'The card Exo shows when an achievement lands.' },
  { id: 'about', label: 'About', title: 'About', blurb: 'Version, updates, and where to send things.' },
] as const

type SectionId = (typeof SECTIONS)[number]['id']

const SORTS: { value: SortMode; label: string }[] = [
  { value: 'name', label: 'Name' },
  { value: 'recent', label: 'Last launched' },
  { value: 'played', label: 'Time played' },
  { value: 'size', label: 'Size' },
  { value: 'store', label: 'Store' },
  { value: 'favorites', label: 'Pinned first' },
]

/** The host clamps to 3–12 seconds; these are the four the product offers. */
const HOLD_SECONDS = [3, 5, 8, 12]

/** One store is five backends. Exo reports each one separately. */
const LAYERS: { key: keyof StoreLayers; label: string }[] = [
  { key: 'login', label: 'Sign-in' },
  { key: 'owned', label: 'Owned list' },
  { key: 'covers', label: 'Cover art' },
  { key: 'downloads', label: 'Downloads' },
  { key: 'social', label: 'Friends' },
]

/** Only 'wired' may read as done. 'none' never gets a checkmark. */
const LAYER_WORD: Record<StoreLayerState, string> = {
  checking: 'Checking',
  wired: 'Wired',
  partial: 'Partial',
  none: 'Not yet',
}

/** The host sends a per-store note alongside the layers; host.ts types only the states. */
type LayerMatrix = StoreLayers & { note?: string }
type StoreAction = 'connect' | 'open'

function storeAccess(store: StoreStatus): { signIn: boolean; note: string } {
  const signIn = canConnectStore(store)
  switch (store.store) {
    case 'epic':
      return {
        signIn,
        note: signIn
          ? 'Sign in opens Epic in your browser. Legendary keeps the token from there.'
          : 'Legendary holds the Epic token and pulls official chunks.',
      }
    case 'gog':
      return {
        signIn,
        note: signIn
          ? 'Sign in opens GOG in a window. gogdl exchanges the code and keeps the token.'
          : 'gogdl holds the GOG token and pulls official installers.',
      }
    case 'steam':
      return {
        signIn: false,
        note: 'Steam has no Exo sign-in. Sign in inside Steam once — Exo reads that account and commands the client.',
      }
    case 'riot':
      return {
        signIn: false,
        note: 'Riot has no Exo sign-in. Riot Client owns the session, patching, and Vanguard.',
      }
    default:
      return {
        signIn: false,
        note: 'Exo cannot connect this store yet. It lists and launches what the official client installed.',
      }
  }
}

function Group({ kicker, children }: { kicker?: string; children: ReactNode }) {
  return (
    <div className="exo-set-group">
      {kicker ? <div className="exo-set-kicker">{kicker}</div> : null}
      <div className="exo-set-rows">{children}</div>
    </div>
  )
}

function Row({
  label,
  hint,
  stack,
  children,
}: {
  label: string
  hint?: string
  stack?: boolean
  children?: ReactNode
}) {
  return (
    <div className={`exo-set-row${stack ? ' is-stacked' : ''}`}>
      <div className="exo-set-row-copy">
        <span className="exo-set-row-label">{label}</span>
        {hint ? <p className="exo-set-row-hint">{hint}</p> : null}
      </div>
      {children ? <div className="exo-set-row-control">{children}</div> : null}
    </div>
  )
}

function SteamWebApiKeyRow({
  keySet,
  onSaved,
}: {
  keySet: boolean
  onSaved: (next: LauncherSettings) => void
}) {
  const [draft, setDraft] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function persist(value: string) {
    setBusy(true)
    setError(null)
    try {
      const pending = host.setSettings({ steamWebApiKey: value.trim() })
      setDraft('')
      onSaved(await pending)
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'The Steam Web API key could not be saved.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <Group kicker="Steam presence">
      <Row
        stack
        label="Steam Web API key"
        hint="Optional standard user key. Exo uses it from this PC for Steam ownership refresh, achievement details, and public friend presence. It is DPAPI-protected for your Windows account and is never synced to Exo. Do not enter a publisher key. The rest of Exo works without it."
      >
        <input
          type="password"
          className="exo-field"
          value={draft}
          autoComplete="off"
          spellCheck={false}
          placeholder={keySet ? 'Key saved on this PC' : 'Paste a key from Steam'}
          aria-label="Steam Web API key"
          onChange={(event) => setDraft(event.target.value)}
        />
        <div className="exo-set-buttons mt-2">
          <button
            type="button"
            className="exo-set-btn"
            disabled={busy || draft.trim().length === 0}
            onClick={() => void persist(draft)}
          >
            Save
          </button>
          {keySet ? (
            <button
              type="button"
              className="exo-set-btn"
              disabled={busy}
              onClick={() => void persist('')}
            >
              Clear
            </button>
          ) : null}
          <button
            type="button"
            className="exo-set-btn"
            onClick={() => void host.openUrl(STEAM_WEB_API_KEY)}
          >
            Get a key
          </button>
        </div>
        {error ? <p className="exo-set-error" role="alert">{error}</p> : null}
      </Row>
    </Group>
  )
}

function Segmented<T extends string | number>({
  label,
  value,
  options,
  disabled,
  onPick,
}: {
  label: string
  value: T
  options: { value: T; label: string }[]
  disabled?: boolean
  onPick: (next: T) => void
}) {
  const buttonRefs = useRef<Array<HTMLButtonElement | null>>([])

  function moveRadio(event: KeyboardEvent<HTMLButtonElement>, index: number) {
    if (disabled || options.length === 0) return
    let target: number
    switch (event.key) {
      case 'ArrowLeft':
      case 'ArrowUp':
        target = (index - 1 + options.length) % options.length
        break
      case 'ArrowRight':
      case 'ArrowDown':
        target = (index + 1) % options.length
        break
      case 'Home':
        target = 0
        break
      case 'End':
        target = options.length - 1
        break
      default:
        return
    }
    event.preventDefault()
    onPick(options[target].value)
    queueMicrotask(() => buttonRefs.current[target]?.focus())
  }

  return (
    <div className="exo-set-seg" role="radiogroup" aria-label={label}>
      {options.map((option, index) => {
        const on = option.value === value
        return (
          <button
            key={String(option.value)}
            ref={(node) => { buttonRefs.current[index] = node }}
            type="button"
            data-controller-target=""
            data-controller-safe=""
            role="radio"
            aria-checked={on}
            tabIndex={on ? 0 : -1}
            disabled={disabled}
            className={`exo-set-seg-btn${on ? ' is-on' : ''}`}
            onClick={() => onPick(option.value)}
            onKeyDown={(event) => moveRadio(event, index)}
          >
            {option.label}
          </button>
        )
      })}
    </div>
  )
}

function LayerList({ layers }: { layers?: StoreLayers }) {
  return (
    <ul className="exo-set-layers">
      {LAYERS.map((layer) => {
        const state = layers?.[layer.key] ?? 'checking'
        return (
          <li key={layer.key} className={`exo-set-layer is-${state}`}>
            <span className="exo-set-layer-name">{layer.label}</span>
            <span className="exo-set-layer-state">
              {state === 'wired' ? <Check size={11} /> : null}
              {LAYER_WORD[state]}
            </span>
          </li>
        )
      })}
    </ul>
  )
}

export function SettingsPanel({
  settings,
  stores,
  message,
  updateBusy,
  updatePercent,
  updateAvailable,
  onCheckUpdate,
  onInstallUpdate,
  onSettings,
  onStores,
}: {
  settings: LauncherSettings | null
  stores: StoreStatus[]
  message: string | null
  updateBusy: boolean
  updatePercent: number
  updateAvailable: boolean
  onCheckUpdate: () => void
  onInstallUpdate: () => void
  onSettings: (next: LauncherSettings) => void
  onStores?: (next: StoreStatus[]) => void | Promise<void>
  onClose?: () => void
}) {
  const [section, setSection] = useState<SectionId>('library')
  const [status, setStatus] = useState<string | null>(null)
  const [storeBusy, setStoreBusy] = useState<Record<string, StoreAction | undefined>>({})
  const [awaitingParent, setAwaitingParent] = useState(false)
  const [deps, setDeps] = useState<DependencyItem[] | null>(null)
  const [depsNote, setDepsNote] = useState<string | null>(null)
  const [depBusy, setDepBusy] = useState<string | null>(null)
  const [checkedStores, setCheckedStores] = useState<StoreStatus[] | null>(null)
  const [storeCheckBusy, setStoreCheckBusy] = useState(false)
  const [storeCheckCopy, setStoreCheckCopy] = useState<string | null>(null)
  const [accountState, setAccountState] = useState<AccountState | null>(null)
  const [badgeHandle, setBadgeHandle] = useState('')
  const [badgeSelection, setBadgeSelection] = useState<OnlineBadgeKey>('contributor')
  const [managedBadges, setManagedBadges] = useState<OnlineAdminBadgeState | null>(null)
  const [badgeBusy, setBadgeBusy] = useState<'load' | 'grant' | 'revoke' | null>(null)
  const [badgeNote, setBadgeNote] = useState<string | null>(null)
  const [privacy, setPrivacy] = useState<OnlinePrivacy | null>(null)
  const [links, setLinks] = useState<OnlineLinkState | null>(null)
  const [onlineBusy, setOnlineBusy] = useState<string | null>(null)
  const [onlineNote, setOnlineNote] = useState<string | null>(null)
  const [confirmAccountDelete, setConfirmAccountDelete] = useState(false)
  const storeBusyRef = useRef<Record<string, StoreAction | undefined>>({})
  const tabRefs = useRef<Partial<Record<SectionId, HTMLButtonElement | null>>>({})

  const visibleStores = checkedStores ?? stores
  const storeRows = settingsStoreRows(visibleStores)
  const sortMode = (settings?.sortMode ?? 'name') as SortMode
  const installPath = settings?.defaultInstallRoot?.trim() || ''
  const holdSeconds = settings?.trophyNotificationDurationSeconds ?? 5
  const current = SECTIONS.find((item) => item.id === section) ?? SECTIONS[0]
  const cachedStoreCheck = visibleStores
    .map((store) => store.checkedAtUtc)
    .filter((value): value is string => !!value)
    .sort()
    .at(-1)
  const missingDependency = deps?.find((item) => item.status.toLowerCase() !== 'present' && item.canOfferInstall) ?? null

  useEffect(() => {
    if (!awaitingParent || !message) return
    setStatus(message)
    setAwaitingParent(false)
  }, [awaitingParent, message])

  useEffect(() => {
    if (!status) return
    const timer = window.setTimeout(() => setStatus(null), 4200)
    return () => window.clearTimeout(timer)
  }, [status])

  useEffect(() => {
    if (!badgeNote) return
    const timer = window.setTimeout(() => setBadgeNote(null), 4200)
    return () => window.clearTimeout(timer)
  }, [badgeNote])

  useEffect(() => {
    setStatus(null)
    setOnlineNote(null)
    setBadgeNote(null)
  }, [section])

  useEffect(() => {
    if (deps !== null) return
    let live = true
    void host
      .listDeps()
      .then((result) => {
        if (live) setDeps(result.items ?? [])
      })
      .catch((error: unknown) => {
        if (!live) return
        setDeps([])
        setDepsNote(error instanceof Error ? error.message : 'Runtimes could not be read.')
      })
    return () => {
      live = false
    }
  }, [deps])

  // Settings is mounted off-screen from the first shell paint. Resolve the
  // account state now so opening Account does not wait for its first RPC.
  useEffect(() => {
    let live = true
    void host.accountGet()
      .then((next) => {
        if (live) setAccountState(next)
      })
      .catch(() => {})
    return () => {
      live = false
    }
  }, [])

  const loadOnlineAccount = useCallback(async () => {
    const [privacyResult, linksResult] = await Promise.all([
      host.onlinePrivacy(),
      host.onlineLinks(),
    ])
    if (privacyResult.value) setPrivacy(privacyResult.value)
    if (linksResult.value) setLinks(linksResult.value)
    const problem = privacyResult.diagnostics.error?.message ?? linksResult.diagnostics.error?.message
    setOnlineNote(problem ?? null)
  }, [])

  useEffect(() => {
    if (!accountState?.signedIn) return
    void loadOnlineAccount().catch((error: unknown) => {
      setOnlineNote(error instanceof Error ? error.message : 'Online account details are unavailable.')
    })
  }, [accountState?.signedIn, loadOnlineAccount])

  useEffect(() => {
    if (accountState?.canManageBadges) return
    setBadgeHandle('')
    setManagedBadges(null)
    setBadgeNote(null)
  }, [accountState?.canManageBadges])

  async function loadManagedBadges() {
    const handle = badgeHandle.trim()
    if (badgeBusy || !/^(?=.*[A-Za-z])[A-Za-z0-9_]{3,24}$/.test(handle)) {
      setBadgeNote('Enter an exact Exo handle.')
      return
    }
    setBadgeBusy('load')
    setBadgeNote(null)
    try {
      const result = await host.onlineBadgesGet(handle)
      if (!result.ok || !result.value) {
        setManagedBadges(null)
        setBadgeNote(result.diagnostics.error?.message ?? 'That profile could not be loaded.')
        return
      }
      setManagedBadges(result.value)
      setBadgeHandle(result.value.handle.display)
      setBadgeNote(null)
    } catch (error) {
      setManagedBadges(null)
      setBadgeNote(error instanceof Error ? error.message : 'That profile could not be loaded.')
    } finally {
      setBadgeBusy(null)
    }
  }

  async function changeManagedBadge(action: 'grant' | 'revoke', badge: OnlineBadgeKey) {
    if (badgeBusy || !managedBadges || !GRANTABLE_BADGE_KEYS.has(badge)) return
    setBadgeBusy(action)
    setBadgeNote(null)
    try {
      const handle = managedBadges.handle.display
      const result = action === 'grant'
        ? await host.onlineBadgesGrant(handle, badge)
        : await host.onlineBadgesRevoke(handle, badge)
      if (!result.ok || !result.value) {
        setBadgeNote(result.diagnostics.error?.message ?? `Badge could not be ${action === 'grant' ? 'granted' : 'revoked'}.`)
        return
      }
      setManagedBadges(result.value)
      setBadgeNote(action === 'grant' ? 'Badge granted.' : 'Badge revoked.')
    } catch (error) {
      setBadgeNote(error instanceof Error ? error.message : 'The badge change did not complete.')
    } finally {
      setBadgeBusy(null)
    }
  }

  const save = useCallback(
    async (patch: Partial<LauncherSettings>, fail = 'Could not save'): Promise<boolean> => {
      if (!settings) return false
      onSettings({ ...settings, ...patch })
      try {
        onSettings(await host.setSettings(patch))
        return true
      } catch (error) {
        onSettings(settings)
        setStatus(error instanceof Error ? error.message : fail)
        return false
      }
    },
    [onSettings, settings],
  )

  async function updatePrivacy(patch: Partial<OnlinePrivacy>) {
    if (!privacy || onlineBusy) return
    const next = { ...privacy, ...patch }
    setPrivacy(next)
    setOnlineBusy('privacy')
    setOnlineNote(null)
    try {
      const result = await host.onlineSetPrivacy(next)
      if (result.value) setPrivacy(result.value)
      if (!result.ok) setOnlineNote(result.diagnostics.error?.message ?? 'Privacy could not be saved.')
    } catch (error) {
      setPrivacy(privacy)
      setOnlineNote(error instanceof Error ? error.message : 'Privacy could not be saved.')
    } finally {
      setOnlineBusy(null)
    }
  }

  async function toggleDiscovery(enabled: boolean) {
    if (!links || onlineBusy) return
    setOnlineBusy('discovery')
    setOnlineNote(null)
    try {
      const result = await host.onlineSetDiscovery(enabled)
      if (!result.ok) {
        setOnlineNote(result.diagnostics.error?.message ?? 'Store discovery could not be changed.')
        return
      }
      await loadOnlineAccount()
      if (enabled && links.links.some((link) => link.store === 'steam' && link.verified)) {
        const matched = await host.onlineMatchStore('steam')
        setOnlineNote(matched.ok ? 'Steam mutual friends refreshed.' : matched.diagnostics.error?.message ?? 'Steam matching is unavailable.')
      }
    } catch (error) {
      setOnlineNote(error instanceof Error ? error.message : 'Store discovery could not be changed.')
    } finally {
      setOnlineBusy(null)
    }
  }

  async function changeStoreLink(store: 'steam' | 'epic' | 'gog', linked: boolean) {
    if (onlineBusy) return
    setOnlineBusy(store)
    setOnlineNote(null)
    try {
      const result = linked ? await host.onlineUnlinkStore(store) : await host.onlineLinkStore(store)
      if (!result.ok) {
        setOnlineNote(result.diagnostics.error?.message ?? `${store} could not be ${linked ? 'unlinked' : 'verified'}.`)
        return
      }
      await loadOnlineAccount()
      if (!linked && store === 'steam' && links?.discovery.enabled) {
        const matched = await host.onlineMatchStore('steam')
        setOnlineNote(matched.ok ? 'Steam verified and mutual friends refreshed.' : matched.diagnostics.error?.message ?? 'Steam verified; matching is unavailable.')
      } else {
        setOnlineNote(`${store === 'gog' ? 'GOG' : store[0].toUpperCase() + store.slice(1)} ${linked ? 'unlinked' : 'verified'}.`)
      }
    } catch (error) {
      setOnlineNote(error instanceof Error ? error.message : 'That store-link action did not complete.')
    } finally {
      setOnlineBusy(null)
    }
  }

  async function refreshSteamMatches() {
    if (onlineBusy) return
    setOnlineBusy('match')
    setOnlineNote(null)
    try {
      const result = await host.onlineMatchStore('steam')
      setOnlineNote(result.ok ? 'Steam mutual friends refreshed.' : result.diagnostics.error?.message ?? 'Steam matching is unavailable.')
      await loadOnlineAccount()
    } catch (error) {
      setOnlineNote(error instanceof Error ? error.message : 'Steam matching is unavailable.')
    } finally {
      setOnlineBusy(null)
    }
  }

  function startStoreAction(store: string, action: StoreAction): boolean {
    if (storeBusyRef.current[store]) return false
    const next = { ...storeBusyRef.current, [store]: action }
    storeBusyRef.current = next
    setStoreBusy(next)
    return true
  }

  function finishStoreAction(store: string, action: StoreAction) {
    if (storeBusyRef.current[store] !== action) return
    const next = { ...storeBusyRef.current }
    delete next[store]
    storeBusyRef.current = next
    setStoreBusy(next)
  }

  async function connectStore(store: StoreStatus) {
    if (!startStoreAction(store.store, 'connect')) return
    setStatus(`Signing in to ${store.displayName}…`)
    try {
      const result = await host.storesAuth(store.store)
      setStatus(result.message ?? (result.ok ? `${store.displayName} connected.` : `Could not sign in to ${store.displayName}`))
      if (result.ok) {
        try {
          const next = await host.storesMatrix()
          setCheckedStores(next)
          await onStores?.(next)
        } catch {
          /* keep the last matrix rather than blanking the rows */
        }
      }
    } catch (error) {
      setStatus(error instanceof Error ? error.message : `Could not sign in to ${store.displayName}`)
    } finally {
      finishStoreAction(store.store, 'connect')
    }
  }

  async function checkStores() {
    if (storeCheckBusy) return
    setStoreCheckBusy(true)
    setStoreCheckCopy('Checking local capabilities…')
    try {
      const check = await host.storesCheck()
      if (check.state === 'failed') {
        setStoreCheckCopy('Local check failed. Showing the last known results.')
        return
      }
      const next = await host.storesMatrix()
      setCheckedStores(next)
      setStoreCheckCopy(
        check.state === 'partial'
          ? 'Checked locally. Some capabilities could not be read.'
          : 'Checked locally just now.',
      )
    } catch {
      setStoreCheckCopy('Local check failed. Showing the last known results.')
    } finally {
      setStoreCheckBusy(false)
    }
  }

  async function openStore(store: StoreStatus) {
    if (!startStoreAction(store.store, 'open')) return
    setStatus(`Opening ${store.displayName}…`)
    try {
      const result = await host.showStore(store.store)
      setStatus(result.message ?? (result.ok ? `Opened ${store.displayName}.` : `Could not open ${store.displayName}`))
    } catch (error) {
      setStatus(error instanceof Error ? error.message : `Could not open ${store.displayName}`)
    } finally {
      finishStoreAction(store.store, 'open')
    }
  }

  const getDependency = useCallback(async (item: DependencyItem) => {
    setDepBusy(item.id)
    setStatus(`Opening the official ${item.name} installer…`)
    try {
      const result = await host.offerDepInstall(item.id)
      setStatus(result.message ?? (result.ok ? `Official ${item.name} page opened.` : `Could not open ${item.name}`))
    } catch (error) {
      setStatus(error instanceof Error ? error.message : `Could not open ${item.name}`)
    } finally {
      setDepBusy(null)
    }
  }, [])

  function goSection(id: SectionId, focus = false) {
    setSection(id)
    if (focus) queueMicrotask(() => tabRefs.current[id]?.focus())
  }

  function onRailKey(event: KeyboardEvent<HTMLDivElement>) {
    const index = SECTIONS.findIndex((item) => item.id === section)
    if (event.key === 'ArrowDown' || event.key === 'ArrowRight') {
      event.preventDefault()
      goSection(SECTIONS[(index + 1) % SECTIONS.length].id, true)
    } else if (event.key === 'ArrowUp' || event.key === 'ArrowLeft') {
      event.preventDefault()
      goSection(SECTIONS[(index - 1 + SECTIONS.length) % SECTIONS.length].id, true)
    } else if (event.key === 'Home') {
      event.preventDefault()
      goSection(SECTIONS[0].id, true)
    } else if (event.key === 'End') {
      event.preventDefault()
      goSection(SECTIONS[SECTIONS.length - 1].id, true)
    }
  }

  return (
    <main className="exo-set" data-controller-scope="settings">
      <aside className="exo-set-rail">
        {/* No Done button: the brand mark in the titlebar already goes home. */}
        <div className="exo-set-rail-head">
          <span className="exo-set-rail-title">Settings</span>
        </div>
        <div className="exo-set-rail-nav" role="tablist" aria-orientation="vertical" aria-label="Settings" onKeyDown={onRailKey}>
          {SECTIONS.map((item) => {
            const on = section === item.id
            return (
              <button
                key={item.id}
                ref={(node) => {
                  tabRefs.current[item.id] = node
                }}
                type="button"
                data-controller-target=""
                data-controller-safe=""
                role="tab"
                id={`set-tab-${item.id}`}
                aria-selected={on}
                aria-controls={`set-pane-${item.id}`}
                tabIndex={on ? 0 : -1}
                className={`exo-set-rail-item${on ? ' is-on' : ''}`}
                onClick={() => goSection(item.id)}
              >
                {item.label}
              </button>
            )
          })}
        </div>
      </aside>

      <div className="exo-set-body">
        <header className="exo-set-head">
          <h2 className="exo-set-title">{current.title}</h2>
          <p className="exo-set-blurb">{current.blurb}</p>
          <p className="exo-set-status" role="status" aria-live="polite">
            {status}
          </p>
        </header>

        <div className="exo-set-scroll">
          {section === 'library' && (
            <section className="exo-set-pane" role="tabpanel" id="set-pane-library" aria-labelledby="set-tab-library">
              <Group>
                <Row label="Sort" hint="The order the library grid uses." stack>
                  <Segmented
                    label="Sort"
                    value={sortMode}
                    options={SORTS}
                    disabled={!settings}
                    onPick={(next) => void save({ sortMode: next })}
                  />
                </Row>
              </Group>

              <Group kicker="Files">
                <Row label="Install folder" hint={installPath || `Default — ${DEFAULT_INSTALL}`} stack>
                  <div className="exo-set-buttons">
                    <button
                      type="button"
                      className="exo-set-btn"
                      onClick={() =>
                        void (async () => {
                          const picked = await host.pickFolder('Install folder')
                          if (picked.cancelled || !picked.path) return
                          if (await save({ defaultInstallRoot: picked.path })) setStatus('Install folder set.')
                        })()
                      }
                    >
                      Choose
                    </button>
                    {installPath ? (
                      <button
                        type="button"
                        className="exo-set-btn"
                        onClick={() =>
                          void (async () => {
                            if (await save({ defaultInstallRoot: '' })) setStatus('Install folder reset.')
                          })()
                        }
                      >
                        Use default
                      </button>
                    ) : null}
                  </div>
                </Row>

                <Row label="Add a folder" hint="A game that did not come from a store.">
                  <button
                    type="button"
                    className="exo-set-btn"
                    onClick={() =>
                      void (async () => {
                        const result = await addPortableFolder()
                        if (result.cancelled) return
                        setStatus(result.message)
                      })()
                    }
                  >
                    Add folder
                  </button>
                </Row>

              </Group>
            </section>
          )}

          {section === 'stores' && (
            <section className="exo-set-pane" role="tabpanel" id="set-pane-stores" aria-labelledby="set-tab-stores">
              <Group kicker="Local check">
                <Row
                  label="Capabilities"
                  hint={
                    storeCheckCopy ??
                    (cachedStoreCheck
                      ? 'Showing cached local results. Check again to refresh.'
                      : 'Check this PC for clients, backends, sessions, and local caches.')
                  }
                >
                  <button
                    type="button"
                    className="exo-set-btn"
                    disabled={storeCheckBusy}
                    onClick={() => void checkStores()}
                  >
                    {storeCheckBusy ? 'Checking' : 'Check again'}
                  </button>
                </Row>
              </Group>
              {storeRows.length === 0 ? (
                <p className="exo-set-empty">
                  {storeCheckBusy ? 'Checking capabilities…' : 'No store apps were found in the last local check.'}
                </p>
              ) : (
                <ul className="exo-set-cards">
                  {storeRows.map((store) => {
                    const layers: LayerMatrix | undefined = store.layers
                    const access = storeAccess(store)
                    const action = storeBusy[store.store]
                    const rowBusy = !!action
                    return (
                      <li key={store.store} className="exo-set-card" aria-busy={rowBusy || undefined}>
                        <div className="exo-set-card-head">
                          <div className="min-w-0">
                            <div className="exo-set-card-name">{store.displayName}</div>
                            <div className={`exo-set-card-state${store.signedIn ? ' is-on' : ''}`}>
                              {storePresenceLabel(store)}
                            </div>
                          </div>
                          <div className="exo-set-buttons">
                            {access.signIn && (
                              <button
                                type="button"
                                className="exo-set-btn"
                                disabled={rowBusy}
                                onClick={() => void connectStore(store)}
                              >
                                {action === 'connect' ? 'Signing in' : 'Sign in'}
                              </button>
                            )}
                            {canOpenStoreClient(store) && (
                              <button
                                type="button"
                                className="exo-set-btn"
                                disabled={rowBusy}
                                onClick={() => void openStore(store)}
                              >
                                {action === 'open' ? 'Opening' : 'Open'}
                              </button>
                            )}
                            {!canOpenStoreClient(store) && storeClientDownloadUrl(store.store) ? (
                              <button
                                type="button"
                                className="exo-set-btn"
                                disabled={rowBusy}
                                onClick={() =>
                                  void host.openUrl(storeClientDownloadUrl(store.store)!).then(
                                    (result) =>
                                      setStatus(
                                        result.ok
                                          ? `Opened the ${store.displayName} download page.`
                                          : `Could not open the ${store.displayName} download page.`,
                                      ),
                                    (error: unknown) =>
                                      setStatus(
                                        error instanceof Error
                                          ? error.message
                                          : `Could not open the ${store.displayName} download page.`,
                                      ),
                                  )
                                }
                              >
                                Get
                              </button>
                            ) : null}
                          </div>
                        </div>
                        <LayerList layers={layers} />
                        <p className="exo-set-card-note">{layers?.note ?? access.note}</p>
                        {access.signIn && layers?.note ? <p className="exo-set-card-note">{access.note}</p> : null}
                      </li>
                    )
                  })}
                </ul>
              )}
              <SteamWebApiKeyRow
                keySet={settings?.steamWebApiKeySet === true}
                onSaved={onSettings}
              />
            </section>
          )}

          {section === 'account' && (
            <section className="exo-set-pane" role="tabpanel" id="set-pane-account" aria-labelledby="set-tab-account">
              <AccountPanel
                heading="Your Exo account"
                onProfile={(_next: ProfileResponse) => {}}
                onSettings={onSettings}
                initialState={accountState}
                onAccountState={setAccountState}
              />

              {accountState?.signedIn && accountState.canManageBadges ? (
                <Group kicker="Community badges">
                  <Row
                    stack
                    label="Profile"
                    hint="Enter an exact Exo handle. Badge authority is checked again by Exo ID for every change."
                  >
                    <input
                      type="text"
                      className="exo-field"
                      value={badgeHandle}
                      maxLength={24}
                      autoComplete="off"
                      autoCapitalize="none"
                      spellCheck={false}
                      placeholder="Exact handle"
                      aria-label="Badge profile handle"
                      disabled={badgeBusy !== null}
                      onChange={(event) => {
                        setBadgeHandle(event.target.value)
                        setManagedBadges(null)
                        setBadgeNote(null)
                      }}
                      onKeyDown={(event) => {
                        if (event.key !== 'Enter') return
                        event.preventDefault()
                        void loadManagedBadges()
                      }}
                    />
                    <div className="exo-set-buttons mt-2">
                      <button
                        type="button"
                        className="exo-set-btn"
                        disabled={badgeBusy !== null || badgeHandle.trim().length < 3}
                        onClick={() => void loadManagedBadges()}
                      >
                        {badgeBusy === 'load' ? 'Loading' : 'Load profile'}
                      </button>
                    </div>
                  </Row>

                  {managedBadges ? (
                    <>
                      <Row label="Grant badge" hint={`Managing ${managedBadges.handle.display}`}>
                        <div className="exo-set-buttons">
                          <select
                            className="exo-field"
                            value={badgeSelection}
                            aria-label="Community badge"
                            disabled={badgeBusy !== null}
                            onChange={(event) => setBadgeSelection(event.target.value as OnlineBadgeKey)}
                          >
                            {GRANTABLE_BADGES.map((badge) => (
                              <option key={badge.key} value={badge.key}>{badge.label}</option>
                            ))}
                          </select>
                          <button
                            type="button"
                            className="exo-set-btn"
                            disabled={badgeBusy !== null || managedBadges.badges.some((badge) => badge.key === badgeSelection)}
                            onClick={() => void changeManagedBadge('grant', badgeSelection)}
                          >
                            {badgeBusy === 'grant' ? 'Granting' : 'Grant'}
                          </button>
                        </div>
                      </Row>
                      {managedBadges.badges
                        .filter((badge) => GRANTABLE_BADGE_KEYS.has(badge.key))
                        .map((badge) => (
                          <Row key={badge.key} label={badge.label} hint={badge.description}>
                            <button
                              type="button"
                              className="exo-set-btn"
                              disabled={badgeBusy !== null}
                              onClick={() => void changeManagedBadge('revoke', badge.key)}
                            >
                              {badgeBusy === 'revoke' ? 'Working' : 'Revoke'}
                            </button>
                          </Row>
                        ))}
                    </>
                  ) : null}
                  {badgeNote ? <p className="exo-set-status" role="status" aria-live="polite">{badgeNote}</p> : null}
                </Group>
              ) : null}

              {accountState?.signedIn && privacy ? (
                <Group kicker="Profile privacy">
                  <Row label="Who can see your profile" hint="This also controls uploaded profile media.">
                    <Segmented
                      label="Profile visibility"
                      value={privacy.profileVisibility}
                      options={[
                        { value: 'public', label: 'Public' },
                        { value: 'friends', label: 'Friends' },
                        { value: 'private', label: 'Private' },
                      ]}
                      disabled={onlineBusy !== null}
                      onPick={(value) => void updatePrivacy({ profileVisibility: value })}
                    />
                  </Row>
                  <Row label="Handle search" hint="Let people find your exact Exo handle.">
                    <button
                      type="button"
                      role="switch"
                      aria-checked={privacy.searchable}
                      className={`exo-set-switch${privacy.searchable ? ' is-on' : ''}`}
                      disabled={onlineBusy !== null}
                      onClick={() => void updatePrivacy({ searchable: !privacy.searchable })}
                    >
                      <span aria-hidden />
                    </button>
                  </Row>
                  <Row label="Friend requests">
                    <Segmented
                      label="Friend requests"
                      value={privacy.requestPolicy}
                      options={[
                        { value: 'anyone', label: 'Anyone' },
                        { value: 'none', label: 'Nobody' },
                      ]}
                      disabled={onlineBusy !== null}
                      onPick={(value) => void updatePrivacy({ requestPolicy: value })}
                    />
                  </Row>
                  <Row label="Game activity" hint="Private hides game titles without pretending you are offline.">
                    <Segmented
                      label="Game activity"
                      value={privacy.activityVisibility}
                      options={[
                        { value: 'friends', label: 'Friends' },
                        { value: 'private', label: 'Private' },
                      ]}
                      disabled={onlineBusy !== null}
                      onPick={(value) => void updatePrivacy({ activityVisibility: value })}
                    />
                  </Row>
                </Group>
              ) : null}

              {accountState?.signedIn && links ? (
                <Group kicker="Store discovery">
                  <Row
                    label="Find mutual store friends"
                    hint="Verified accounts are unique to this Exo profile. Steam matches only when both people opt in."
                  >
                    <button
                      type="button"
                      role="switch"
                      aria-checked={links.discovery.enabled}
                      className={`exo-set-switch${links.discovery.enabled ? ' is-on' : ''}`}
                      disabled={onlineBusy !== null}
                      onClick={() => void toggleDiscovery(!links.discovery.enabled)}
                    >
                      <span aria-hidden />
                    </button>
                  </Row>
                  {(['steam', 'epic', 'gog'] as const).map((store) => {
                    const linked = links.links.some((item) => item.store === store && item.verified)
                    const label = store === 'gog' ? 'GOG' : store[0].toUpperCase() + store.slice(1)
                    return (
                      <Row
                        key={store}
                        label={label}
                        hint={store === 'steam' ? 'Verified with Steam OpenID. Refresh checks real mutual friends.' : 'Verification works now; automatic mutual matching is coming soon.'}
                      >
                        <div className="exo-set-buttons">
                          {linked && store === 'steam' ? (
                            <button type="button" className="exo-set-btn" disabled={onlineBusy !== null} onClick={() => void refreshSteamMatches()}>
                              {onlineBusy === 'match' ? 'Refreshing' : 'Refresh matches'}
                            </button>
                          ) : null}
                          {linked && store !== 'steam' ? <span className="exo-set-coming">Coming soon</span> : null}
                          <button
                            type="button"
                            className="exo-set-btn"
                            disabled={onlineBusy !== null}
                            onClick={() => void changeStoreLink(store, linked)}
                          >
                            {onlineBusy === store ? 'Working' : linked ? 'Unlink' : 'Verify'}
                          </button>
                        </div>
                      </Row>
                    )
                  })}
                </Group>
              ) : null}

              {onlineNote ? <p className="exo-set-status" role="status" aria-live="polite">{onlineNote}</p> : null}
              {accountState?.signedIn ? (
                <Group kicker="Account data">
                  <Row label="Export account" hint="Save a readable copy of your Exo profile, privacy, links, friends, and sessions.">
                    <button type="button" className="exo-set-btn" disabled={onlineBusy !== null} onClick={async () => {
                      setOnlineBusy('export')
                      try {
                        const result = await host.onlineExportAccount()
                        setOnlineNote(result.ok ? 'Account export saved.' : result.diagnostics.error?.message ?? 'Account export failed.')
                      } finally {
                        setOnlineBusy(null)
                      }
                    }}>
                      {onlineBusy === 'export' ? 'Exporting' : 'Export'}
                    </button>
                  </Row>
                  <Row label="Delete Exo account" hint="Requires a recent sign-in. Local library and files stay on this PC.">
                    <button
                      type="button"
                      className={`exo-set-btn${confirmAccountDelete ? ' is-danger' : ''}`}
                      disabled={onlineBusy !== null}
                      onClick={async () => {
                        if (!confirmAccountDelete) {
                          setConfirmAccountDelete(true)
                          return
                        }
                        setOnlineBusy('delete')
                        try {
                          const result = await host.onlineDeleteAccount()
                          setOnlineNote(result.ok ? 'Exo account deleted.' : result.diagnostics.error?.message ?? 'Account deletion failed.')
                          if (result.ok) setAccountState(null)
                        } finally {
                          setOnlineBusy(null)
                          setConfirmAccountDelete(false)
                        }
                      }}
                    >
                      {onlineBusy === 'delete' ? 'Deleting' : confirmAccountDelete ? 'Confirm delete' : 'Delete account'}
                    </button>
                  </Row>
                </Group>
              ) : null}
            </section>
          )}

          {section === 'unlocks' && (
            <section className="exo-set-pane" role="tabpanel" id="set-pane-unlocks" aria-labelledby="set-tab-unlocks">
              <div className="exo-set-trophy">
                <TrophyNotificationSettings
                  settings={settings}
                  onSettings={onSettings}
                  onSave={async (patch) => {
                    await save(patch)
                  }}
                />
              </div>

              <Group kicker="Card">
                <Row label="How long it stays" hint="Measured from the moment the card finishes arriving." stack>
                  <Segmented
                    label="How long it stays"
                    value={holdSeconds}
                    options={HOLD_SECONDS.map((value) => ({ value, label: `${value}s` }))}
                    disabled={!settings || settings.trophyNotificationsEnabled === false}
                    onPick={(next) => void save({ trophyNotificationDurationSeconds: next })}
                  />
                </Row>
                <Row label="More Exo unlock themes" hint="New schemes will use the same store-backed achievements and accessibility rules.">
                  <span className="exo-set-coming">Coming soon</span>
                </Row>
              </Group>
            </section>
          )}

          {section === 'about' && (
            <section className="exo-set-pane" role="tabpanel" id="set-pane-about" aria-labelledby="set-tab-about">
              <Group kicker="Dependencies">
                <Row
                  label="Runtime check"
                  hint={
                    deps === null
                      ? 'Checking the runtimes Exo can verify…'
                      : deps.length === 0
                        ? depsNote ?? 'No dependency results were returned.'
                        : (() => {
                            const missing = deps.filter((item) => item.status.toLowerCase() !== 'present')
                            return missing.length === 0
                              ? `${deps.length} checked · all present`
                              : `${deps.length - missing.length} present · ${missing.length} missing`
                          })()
                  }
                >
                  <div className="exo-set-buttons">
                    <span className={`exo-set-state${deps !== null && deps.length > 0 && deps.every((item) => item.status.toLowerCase() === 'present') ? ' is-on' : ''}`}>
                      {deps === null ? 'Checking' : deps.length > 0 && deps.every((item) => item.status.toLowerCase() === 'present') ? 'Ready' : 'Review'}
                    </span>
                    <button
                      type="button"
                      className="exo-set-btn"
                      disabled={deps === null}
                      onClick={() => {
                        setDeps(null)
                        setDepsNote(null)
                      }}
                    >
                      Check
                    </button>
                    {missingDependency ? (
                      <button
                        type="button"
                        className="exo-set-btn"
                        disabled={depBusy === missingDependency.id}
                        onClick={() => void getDependency(missingDependency)}
                      >
                        {depBusy === missingDependency.id ? 'Opening' : 'Get missing'}
                      </button>
                    ) : null}
                  </div>
                </Row>
              </Group>
              <Group>
                <Row label="Exo Launcher" hint="Windows 11 x64.">
                  <span className="exo-set-state">{settings?.appVersion ?? 'Unknown'}</span>
                </Row>
                <Row label="Update" hint="Released builds come from the GitHub releases page.">
                  <div className="exo-set-buttons">
                    <button
                      type="button"
                      className="exo-set-btn"
                      disabled={updateBusy}
                      onClick={() => {
                        setAwaitingParent(true)
                        setStatus('Checking…')
                        onCheckUpdate()
                      }}
                    >
                      Check for update
                    </button>
                    {(updateAvailable || updateBusy) && (
                      <button
                        type="button"
                        className={`exo-cta exo-update-action exo-set-install${updateBusy ? ' is-active' : ''}`}
                        disabled={updateBusy}
                        onClick={() => {
                          setStatus('Installing…')
                          onInstallUpdate()
                        }}
                      >
                        {updateBusy && (
                          <span
                            className="exo-action-progress"
                            style={{ '--progress': Math.max(0, Math.min(100, updatePercent)) / 100 } as CSSProperties}
                            aria-hidden
                          />
                        )}
                        <span className="relative z-[1]">{updateBusy ? `${Math.round(updatePercent)}%` : 'Install'}</span>
                      </button>
                    )}
                  </div>
                </Row>
              </Group>

              <div className="exo-set-help">
                <button type="button" className="exo-set-link" onClick={() => void host.openUrl(ISSUES)}>
                  <FileText size={16} />
                  Report issue
                </button>
                <button type="button" className="exo-set-link" onClick={() => void host.openUrl(PRIVACY)}>
                  <FileText size={16} />
                  Privacy
                </button>
                <button type="button" className="exo-set-link" onClick={() => void host.openUrl(RELEASES)}>
                  <ExternalLink size={16} />
                  Releases
                </button>
                <button type="button" className="exo-set-link" onClick={() => void host.openUrl(BUY_ME_A_COFFEE)}>
                  <Coffee size={16} />
                  Buy me a coffee
                </button>
              </div>
            </section>
          )}
        </div>
      </div>
    </main>
  )
}
