import { useEffect, useRef, useState } from 'react'
import { ExoMark } from '../brand/ExoMark'
import {
  host,
  type AccountState,
  type LauncherSettings,
  type ProfileImageKind,
  type ProfilePatch,
  type ProfileResponse,
  type StoreStatus,
} from '../lib/host'
import { PROFILE_LIMITS } from '../lib/social'
import {
  canConnectStore,
  canOpenStoreClient,
  onboardingStoreLabel,
  settingsStoreRows,
} from '../lib/stores'
import { AccountPanel } from './AccountPanel'
import { WindowChrome } from './WindowChrome'

const STEAM_WEB_API_KEY_URL = 'https://steamcommunity.com/dev/apikey'

const STEPS = [
  { id: 'stores', label: 'Stores' },
  { id: 'account', label: 'Account' },
  { id: 'profile', label: 'Make it yours' },
] as const

type StepId = (typeof STEPS)[number]['id']
type StoreAction = 'connect' | 'open'
type ProfileLoadState = 'idle' | 'loading' | 'ready' | 'error'
type StoreCheckState = 'checking' | 'ready' | 'error'
type Feedback = { message: string; error?: boolean }

const UNAVAILABLE_ACCOUNT: AccountState = {
  ok: false,
  signedIn: false,
  configured: false,
  providers: [],
  roles: [],
  canManageBadges: false,
  badges: [],
}

let onboardingStoreCheck: Promise<StoreStatus[]> | null = null

function checkOnboardingStoresOnce(): Promise<StoreStatus[]> {
  onboardingStoreCheck ??= host.storesCheck().then((result) => {
    if (result.state === 'failed') throw new Error('Local store check failed.')
    return host.storesMatrix()
  })
  return onboardingStoreCheck
}

function isAccountServiceUnavailable(account: AccountState | null): boolean {
  if (!account) return false
  if (!account.configured) return true
  if (account.signedIn) return false
  return account.providers.length === 0
}

export interface OnboardingPanelProps {
  stores: StoreStatus[]
  message: string | null
  onSettings: (next: LauncherSettings) => void
  onStores: (next: StoreStatus[]) => void
  onComplete: (refreshLibrary: boolean) => Promise<void>
  /** Return false when the picker was cancelled. Void and true mean the library changed. */
  onAddFolder?: () => boolean | void | Promise<boolean | void>
}

export function OnboardingPanel({
  stores,
  message,
  onSettings,
  onStores,
  onComplete,
  onAddFolder,
}: OnboardingPanelProps) {
  const [step, setStep] = useState<StepId>('stores')
  const [feedback, setFeedback] = useState<Feedback | null>(null)
  const [refreshLibrary, setRefreshLibrary] = useState(false)
  const [storeCheckState, setStoreCheckState] = useState<StoreCheckState>('checking')
  const [storeBusy, setStoreBusy] = useState<Record<string, StoreAction | undefined>>({})
  const [folderBusy, setFolderBusy] = useState(false)
  const [accountState, setAccountState] = useState<AccountState | null>(null)
  const [profileLoad, setProfileLoad] = useState<ProfileLoadState>('idle')
  const [profile, setProfile] = useState<ProfileResponse | null>(null)
  const [profileName, setProfileName] = useState('')
  const [profileBusy, setProfileBusy] = useState<'save' | ProfileImageKind | null>(null)
  const [steamKeySet, setSteamKeySet] = useState(false)
  const [steamKeyDraft, setSteamKeyDraft] = useState('')
  const [steamKeyBusy, setSteamKeyBusy] = useState(false)
  const [completing, setCompleting] = useState(false)
  const profileLoadStartedRef = useRef(false)
  const completingRef = useRef(false)
  const stepHeadingRef = useRef<HTMLHeadingElement>(null)
  const stepFocusPendingRef = useRef(false)

  const stepIndex = STEPS.findIndex((item) => item.id === step)
  const serviceUnavailable = isAccountServiceUnavailable(accountState)
  const accountReady =
    serviceUnavailable || (!!accountState?.signedIn && !!accountState.handle)
  const storeRows = settingsStoreRows(stores)
  const steamStore = storeRows.find((store) => store.store === 'steam')
  const trimmedName = profileName.trim()
  const profileChanged =
    profileLoad === 'ready' &&
    trimmedName !== (profile?.name?.trim() ?? '')

  useEffect(() => {
    let active = true
    void checkOnboardingStoresOnce().then(
      (next) => {
        if (!active) return
        onStores(next)
        setStoreCheckState('ready')
      },
      () => {
        if (active) setStoreCheckState('error')
      },
    )
    return () => {
      active = false
    }
  }, [onStores])

  useEffect(() => {
    let active = true
    void host.accountGet().then(
      (next) => {
        if (!active) return
        setAccountState(next.ok ? next : { ...UNAVAILABLE_ACCOUNT, message: next.message })
      },
      () => {
        if (active) setAccountState(UNAVAILABLE_ACCOUNT)
      },
    )
    void host.getSettings().then(
      (next) => {
        if (!active) return
        setSteamKeySet(next.steamWebApiKeySet === true)
        onSettings(next)
      },
      () => {},
    )
    return () => {
      active = false
    }
  }, [onSettings])

  useEffect(() => {
    if (!stepFocusPendingRef.current) return
    stepFocusPendingRef.current = false
    const frame = window.requestAnimationFrame(() => stepHeadingRef.current?.focus())
    return () => window.cancelAnimationFrame(frame)
  }, [step])

  useEffect(() => {
    if (!feedback) return
    const timer = window.setTimeout(() => setFeedback(null), feedback.error ? 6000 : 3600)
    return () => window.clearTimeout(timer)
  }, [feedback])

  function applyProfile(next: ProfileResponse) {
    setProfile(next)
    setProfileName(next.name ?? '')
    setProfileLoad('ready')
  }

  useEffect(() => {
    if (step !== 'profile' || profileLoad !== 'idle' || profileLoadStartedRef.current) return

    profileLoadStartedRef.current = true
    setProfileLoad('loading')
    void host.profileGet().then(
      (next) => {
        if (!next.ok) {
          setProfileLoad('error')
          setFeedback({ message: 'The local profile could not be read.', error: true })
          return
        }
        applyProfile(next)
      },
      (cause: unknown) => {
        setProfileLoad('error')
        setFeedback({
          message: cause instanceof Error ? cause.message : 'The local profile could not be read.',
          error: true,
        })
      },
    )
  }, [profileLoad, step])

  function chooseStep(next: StepId) {
    if (next === step) return
    setFeedback(null)
    stepFocusPendingRef.current = true
    setStep(next)
  }

  function clearStoreBusy(store: string) {
    setStoreBusy((current) => {
      const next = { ...current }
      delete next[store]
      return next
    })
  }

  async function runStoreAction(store: StoreStatus, action: StoreAction) {
    if (storeBusy[store.store]) return

    setStoreBusy((current) => ({ ...current, [store.store]: action }))
    setFeedback(null)
    try {
      const result =
        action === 'connect'
          ? await host.storesAuth(store.store)
          : await host.showStore(store.store)

      if (!result.ok) {
        setFeedback({
          message:
            result.message ??
            (action === 'connect'
              ? `Could not sign in to ${store.displayName}.`
              : `Could not open ${store.displayName}.`),
          error: true,
        })
        return
      }

      setRefreshLibrary(true)
      let refreshFailed = false
      try {
        onStores(await host.storesMatrix())
      } catch {
        refreshFailed = true
      }
      setFeedback({
        message: `${result.message ?? (action === 'connect' ? `${store.displayName} sign-in opened.` : `${store.displayName} opened.`)}${
          refreshFailed ? ' Store status could not be refreshed yet.' : ''
        }`,
        error: refreshFailed,
      })
    } catch (cause) {
      setFeedback({
        message:
          cause instanceof Error
            ? cause.message
            : action === 'connect'
              ? `Could not sign in to ${store.displayName}.`
              : `Could not open ${store.displayName}.`,
        error: true,
      })
    } finally {
      clearStoreBusy(store.store)
    }
  }

  async function addFolder() {
    if (!onAddFolder || folderBusy) return
    setFolderBusy(true)
    setFeedback(null)
    try {
      const result = await onAddFolder()
      if (result !== false) {
        setRefreshLibrary(true)
        setFeedback({ message: 'Folder added. Exo will refresh the library when setup finishes.' })
      }
    } catch (cause) {
      setFeedback({
        message: cause instanceof Error ? cause.message : 'The folder could not be added.',
        error: true,
      })
    } finally {
      setFolderBusy(false)
    }
  }

  async function persistSteamKey(value: string) {
    if (steamKeyBusy) return
    setSteamKeyBusy(true)
    setFeedback(null)
    try {
      const pending = host.setSettings({ steamWebApiKey: value.trim() })
      setSteamKeyDraft('')
      const next = await pending
      setSteamKeySet(next.steamWebApiKeySet === true)
      onSettings(next)
      setFeedback({
        message: value.trim()
          ? 'Steam Web API key saved on this PC. Live friend status can use it.'
          : 'Steam Web API key cleared.',
      })
    } catch (cause) {
      setFeedback({
        message: cause instanceof Error ? cause.message : 'The Steam Web API key could not be saved.',
        error: true,
      })
    } finally {
      setSteamKeyBusy(false)
    }
  }

  async function saveProfile(): Promise<boolean> {
    if (profileLoad !== 'ready' || profileBusy) return false

    const patch: ProfilePatch = {}
    if (trimmedName !== (profile?.name?.trim() ?? '')) patch.name = trimmedName
    if (Object.keys(patch).length === 0) return true

    setProfileBusy('save')
    setFeedback(null)
    try {
      const next = await host.profileSet(patch)
      if (!next.ok) {
        setFeedback({ message: 'The local profile could not be saved.', error: true })
        return false
      }
      applyProfile(next)
      if (accountState?.signedIn && accountState.handle) {
        const synced = await host.accountSetProfile()
        setFeedback({ message: synced.ok ? 'Profile auto-saved to Exo.' : synced.message ?? 'Profile saved on this PC; Exo sync will retry.', error: !synced.ok })
      } else {
        setFeedback({ message: 'Profile saved on this PC.' })
      }
      return true
    } catch (cause) {
      setFeedback({
        message: cause instanceof Error ? cause.message : 'The local profile could not be saved.',
        error: true,
      })
      return false
    } finally {
      setProfileBusy(null)
    }
  }

  async function pickProfileImage(kind: ProfileImageKind) {
    if (profileBusy) return

    setProfileBusy(kind)
    setFeedback(null)
    try {
      const result = await host.profilePickImage(kind)
      if (result.profile) applyProfile(result.profile)
      if (!result.cancelled) {
        let onlineMessage: string | null = null
        if (result.ok && accountState?.signedIn && accountState.handle) {
          const uploaded = await host.onlineUploadMedia(kind)
          onlineMessage = uploaded.ok ? ' Auto-saved to Exo.' : ' Saved on this PC; online media will retry.'
        }
        setFeedback({
          message: result.ok
            ? `${kind === 'avatar' ? 'Avatar' : 'Banner'} stored.${onlineMessage ?? ''}`
            : result.message ?? 'That picture could not be used.',
          error: !result.ok,
        })
      }
    } catch (cause) {
      setFeedback({
        message: cause instanceof Error ? cause.message : 'That picture could not be used.',
        error: true,
      })
    } finally {
      setProfileBusy(null)
    }
  }

  async function finish() {
    if (completingRef.current) return
    if (!accountReady) {
      chooseStep('account')
      setFeedback({
        message:
          accountState?.signedIn && !accountState.handle
            ? 'Choose a handle once.'
            : 'Create or sign in to your Exo account.',
        error: true,
      })
      return
    }
    if (profileChanged && !(await saveProfile())) return
    completingRef.current = true
    setCompleting(true)
    setFeedback(null)
    try {
      await onComplete(refreshLibrary)
    } catch (cause) {
      completingRef.current = false
      setCompleting(false)
      setFeedback({
        message: cause instanceof Error ? cause.message : 'Setup could not be completed.',
        error: true,
      })
    }
  }

  const visibleFeedback = feedback?.message ?? message
  const canOpenOfficialClient = (store: StoreStatus) =>
    canOpenStoreClient(store) && store.clientPresent === true

  return (
    <div className="exo-app exo-onboarding">
      <header className="exo-titlebar exo-onboarding-titlebar">
        <div className="exo-onboarding-brand exo-no-drag">
          <ExoMark size={26} />
          <span className="exo-onboarding-brand-name">Exo</span>
        </div>
        <div className="exo-titlebar-actions exo-no-drag">
          <WindowChrome />
        </div>
      </header>

      <div className="exo-onboarding-shell">
        <aside className="exo-onboarding-rail" aria-label="Setup progress">
          <div className="exo-onboarding-rail-copy">
            <p className="exo-onboarding-kicker">First run</p>
            <p className="exo-onboarding-rail-title">Set up this PC</p>
          </div>
          <ol className="exo-onboarding-steps">
            {STEPS.map((item, index) => {
              const current = item.id === step
              return (
                <li key={item.id}>
                  <button
                    type="button"
                    className={`exo-onboarding-step-button${current ? ' is-current' : ''}${
                      index < stepIndex ? ' is-complete' : ''
                    }`}
                    aria-current={current ? 'step' : undefined}
                    onClick={() => chooseStep(item.id)}
                  >
                    <span className="exo-onboarding-step-index" aria-hidden="true">
                      {String(index + 1).padStart(2, '0')}
                    </span>
                    <span className="exo-onboarding-step-label">{item.label}</span>
                  </button>
                </li>
              )
            })}
          </ol>
          <p className="exo-onboarding-local-note">Your Exo account keeps profile and friends together. The library still opens offline.</p>
        </aside>

        <main className="exo-onboarding-main">
          <div
            className="exo-onboarding-content"
            role="region"
            aria-live="polite"
            aria-labelledby={`exo-onboarding-${step}-title`}
          >
            {step === 'stores' ? (
              <section
                key="stores"
                className="exo-onboarding-step"
                aria-labelledby="exo-onboarding-stores-title"
              >
                <div className="exo-onboarding-step-head">
                  <p className="exo-onboarding-eyebrow">Stores on this PC</p>
                  <h1
                    ref={stepHeadingRef}
                    id="exo-onboarding-stores-title"
                    className="exo-onboarding-title"
                    tabIndex={-1}
                  >
                    Bring in the games Exo can see
                  </h1>
                  <p className="exo-onboarding-copy">
                    Sign in here only when Exo can complete it (Epic, GOG, Amazon). Steam and Riot stay in their official clients. A standard Steam user Web API key can add ownership, achievements, and public friend presence; never paste a publisher key.
                  </p>
                  <p className="exo-onboarding-copy" role="status" aria-live="polite">
                    {storeCheckState === 'checking'
                      ? 'Checking local capabilities…'
                      : storeCheckState === 'error'
                        ? 'Local check unavailable. Setup can continue.'
                        : 'Local capabilities checked.'}
                  </p>
                </div>

                <div className="exo-onboarding-store-body">
                  {storeRows.length > 0 ? (
                    <ul className="exo-onboarding-store-grid">
                      {storeRows.map((store) => {
                        const action = storeBusy[store.store]
                        const connect = canConnectStore(store)
                        const openClient = canOpenOfficialClient(store)
                        return (
                          <li
                            key={store.store}
                            className="exo-onboarding-store"
                            aria-busy={action ? true : undefined}
                          >
                            <div className="exo-onboarding-store-info">
                              <span className="exo-onboarding-store-name">{store.displayName}</span>
                              <span className="exo-onboarding-store-state">{onboardingStoreLabel(store)}</span>
                            </div>
                            <div className="exo-onboarding-store-actions">
                              {connect ? (
                                <button
                                  type="button"
                                  className="exo-onboarding-secondary"
                                  disabled={!!action}
                                  onClick={() => void runStoreAction(store, 'connect')}
                                >
                                  {action === 'connect' ? 'Signing in…' : 'Sign in'}
                                </button>
                              ) : null}
                              {openClient ? (
                                <button
                                  type="button"
                                  className="exo-onboarding-secondary"
                                  disabled={!!action}
                                  onClick={() => void runStoreAction(store, 'open')}
                                >
                                  {action === 'open' ? 'Opening…' : 'Open client'}
                                </button>
                              ) : null}
                            </div>
                          </li>
                        )
                      })}
                    </ul>
                  ) : (
                    <div className="exo-onboarding-empty">
                      <p>
                        {storeCheckState === 'checking'
                          ? 'Checking capabilities…'
                          : 'No supported store clients were found.'}
                      </p>
                      <span>You can still add a game folder or continue with an empty library.</span>
                    </div>
                  )}
                </div>

                <form
                  className="exo-onboarding-key-card"
                  onSubmit={(event) => {
                    event.preventDefault()
                    if (steamKeyDraft.trim()) void persistSteamKey(steamKeyDraft)
                  }}
                >
                  <label className="exo-onboarding-field-label" htmlFor="exo-onboarding-steam-key">
                    Steam Web API key
                  </label>
                  <p className="exo-onboarding-field-hint">
                    {steamStore
                      ? 'Optional standard user key for Steam ownership refresh, achievements, and public friend presence. DPAPI-protected on this PC; never synced to Exo. Do not enter a publisher key.'
                      : 'Optional. Steam was not found. You may save a standard user key for later, but never enter a publisher key.'}
                  </p>
                  <input
                    id="exo-onboarding-steam-key"
                    type="password"
                    className="exo-onboarding-field"
                    value={steamKeyDraft}
                    autoComplete="off"
                    spellCheck={false}
                    placeholder={steamKeySet ? 'Key saved on this PC' : 'Paste a key from Steam'}
                    aria-label="Steam Web API key"
                    onChange={(event) => setSteamKeyDraft(event.target.value)}
                  />
                  <div className="exo-onboarding-inline-actions">
                    <button
                      type="submit"
                      className="exo-onboarding-secondary"
                      disabled={steamKeyBusy || steamKeyDraft.trim().length === 0}
                    >
                      {steamKeyBusy ? 'Saving…' : 'Save key'}
                    </button>
                    {steamKeySet ? (
                      <button
                        type="button"
                        className="exo-onboarding-secondary"
                        disabled={steamKeyBusy}
                        onClick={() => void persistSteamKey('')}
                      >
                        Clear
                      </button>
                    ) : null}
                    <button
                      type="button"
                      className="exo-onboarding-link"
                      disabled={steamKeyBusy}
                      onClick={() => void host.openUrl(STEAM_WEB_API_KEY_URL)}
                    >
                      Get a key
                    </button>
                  </div>
                </form>

                {onAddFolder ? (
                  <button
                    type="button"
                    className="exo-onboarding-add-folder"
                    disabled={folderBusy}
                    onClick={() => void addFolder()}
                  >
                    {folderBusy ? 'Choosing folder…' : 'Add a game folder'}
                  </button>
                ) : null}
              </section>
            ) : null}

            {step === 'account' ? (
              <section
                key="account"
                className="exo-onboarding-step"
                aria-labelledby="exo-onboarding-account-title"
              >
                <div className="exo-onboarding-step-head">
                  <p className="exo-onboarding-eyebrow">One identity</p>
                  <h1
                    ref={stepHeadingRef}
                    id="exo-onboarding-account-title"
                    className="exo-onboarding-title"
                    tabIndex={-1}
                  >
                    Create or sign in to your Exo account
                  </h1>
                  <p className="exo-onboarding-copy">
                    {serviceUnavailable
                      ? 'Exo account service is unavailable. Continue setup; the library still works. Sign in later from Settings → Account.'
                      : 'Email and password create the account friends will use. Choose a handle once. Profile changes auto-save afterward. Privacy stays in Settings.'}
                  </p>
                </div>

                <div className="exo-onboarding-account-wrap">
                  <AccountPanel
                    heading="Your Exo account"
                    onProfile={applyProfile}
                    onSettings={onSettings}
                    onAccountState={setAccountState}
                  />
                </div>
              </section>
            ) : null}

            {step === 'profile' ? (
              <section
                key="profile"
                className="exo-onboarding-step"
                aria-labelledby="exo-onboarding-profile-title"
              >
                <div className="exo-onboarding-step-head">
                  <p className="exo-onboarding-eyebrow">Finishing touch</p>
                  <h1
                    ref={stepHeadingRef}
                    id="exo-onboarding-profile-title"
                    className="exo-onboarding-title"
                    tabIndex={-1}
                  >
                    Make Exo feel like yours
                  </h1>
                  <p className="exo-onboarding-copy">
                    Add a display name and media now, or finish and shape the full profile later. Your account handle is already the identity friends use.
                  </p>
                </div>

                {profileLoad === 'loading' ? (
                  <div className="exo-onboarding-loading" role="status">Reading the profile on this PC…</div>
                ) : null}
                {profileLoad === 'error' ? (
                  <div className="exo-onboarding-error" role="alert">
                    <span>The local profile could not be read.</span>
                    <button
                      type="button"
                      className="exo-onboarding-secondary"
                      onClick={() => {
                        setFeedback(null)
                        profileLoadStartedRef.current = false
                        setProfileLoad('idle')
                      }}
                    >
                      Try again
                    </button>
                  </div>
                ) : null}
                {profileLoad === 'ready' ? (
                  <form
                    className="exo-onboarding-profile-form"
                    onSubmit={(event) => {
                      event.preventDefault()
                      void saveProfile()
                    }}
                  >
                    <div className="exo-onboarding-profile-grid is-single">
                      <label className="exo-onboarding-field-group" htmlFor="exo-onboarding-profile-name">
                        <span className="exo-onboarding-field-label">Display name</span>
                        <input
                          id="exo-onboarding-profile-name"
                          className="exo-onboarding-field"
                          value={profileName}
                          maxLength={PROFILE_LIMITS.name}
                          placeholder={accountState?.handle ? `@${accountState.handle}` : 'How friends see you'}
                          autoComplete="off"
                          onChange={(event) => setProfileName(event.target.value)}
                          onBlur={() => {
                            if (profileChanged) void saveProfile()
                          }}
                        />
                      </label>
                    </div>
                    <p className="exo-onboarding-field-hint">One display name here; one account handle everywhere else. Changes auto-save.</p>
                    <div className="exo-onboarding-profile-actions">
                      <button
                        type="button"
                        className="exo-onboarding-secondary"
                        disabled={!!profileBusy}
                        onClick={() => void pickProfileImage('avatar')}
                      >
                        {profileBusy === 'avatar' ? 'Choosing avatar…' : 'Choose avatar'}
                      </button>
                      <button
                        type="button"
                        className="exo-onboarding-secondary"
                        disabled={!!profileBusy}
                        onClick={() => void pickProfileImage('banner')}
                      >
                        {profileBusy === 'banner' ? 'Choosing banner…' : 'Choose banner'}
                      </button>
                    </div>
                  </form>
                ) : null}
              </section>
            ) : null}
          </div>

          <footer className="exo-onboarding-footer">
            <p
              className={`exo-onboarding-status${feedback?.error ? ' is-error' : ''}`}
              role={feedback?.error ? 'alert' : 'status'}
              aria-live={feedback?.error ? 'assertive' : 'polite'}
            >
              {visibleFeedback ?? '\u00a0'}
            </p>
            <div className="exo-onboarding-footer-actions">
              <button
                type="button"
                className="exo-onboarding-back"
                disabled={stepIndex === 0 || completing}
                onClick={() => chooseStep(STEPS[stepIndex - 1].id)}
              >
                Back
              </button>
              {stepIndex < STEPS.length - 1 ? (
                <button
                  type="button"
                  className="exo-onboarding-primary"
                  disabled={completing || (step === 'account' && !accountReady)}
                  onClick={() => chooseStep(STEPS[stepIndex + 1].id)}
                >
                  Continue
                </button>
              ) : (
                <button
                  type="button"
                  className="exo-onboarding-primary"
                  disabled={completing || !accountReady}
                  onClick={() => void finish()}
                >
                  {completing ? 'Finishing…' : 'Finish setup'}
                </button>
              )}
            </div>
          </footer>
        </main>
      </div>
    </div>
  )
}
