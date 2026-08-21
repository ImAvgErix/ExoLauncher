import { useCallback, useEffect, useId, useRef, useState } from 'react'
import {
  host,
  onHostEvent,
  type AccountOperationResponse,
  type AccountState,
  type LauncherSettings,
  type OnlineHealth,
  type OnlineResult,
  type ProfileResponse,
} from '../lib/host'

type PasswordMode = 'signin' | 'create'
type BusyAction = 'signin' | 'create' | 'handle' | 'google' | 'email' | 'signout' | 'sync'
type Feedback = { message: string; error?: boolean }

const PASSWORD_MIN_LENGTH = 12
const PASSWORD_MAX_LENGTH = 128
const NAME_MAX_LENGTH = 80

export interface AccountPanelProps {
  onProfile: (next: ProfileResponse) => void
  onSettings: (next: LauncherSettings) => void
  onAccountState?: (next: AccountState | null) => void
  initialState?: AccountState | null
  heading?: string
}

function passwordProblem(value: string): string | null {
  return value.length < PASSWORD_MIN_LENGTH || value.length > PASSWORD_MAX_LENGTH
    ? 'Use 12–128 characters.'
    : null
}

function nameProblem(value: string): string | null {
  const trimmed = value.trim()
  if (!trimmed) return 'Enter the name people should see.'
  if (trimmed.length > NAME_MAX_LENGTH || /[\u0000-\u001F\u007F-\u009F]/u.test(trimmed)) {
    return 'Use 1–80 characters without control characters.'
  }
  return null
}

function handleProblem(value: string): string | null {
  const handle = value.trim()
  if (!/^[a-z0-9_]{3,24}$/.test(handle) || !/[a-z]/.test(handle)) {
    return 'Use 3–24 lowercase letters, numbers, or underscore.'
  }
  return null
}

function suggestedHandle(name: string): string {
  let handle = name
    .normalize('NFKD')
    .replace(/[\u0300-\u036f]/g, '')
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '_')
    .replace(/^_+|_+$/g, '')
    .slice(0, 24)
  if (!/[a-z]/.test(handle)) handle = `exo_${handle}`.slice(0, 24)
  if (handle.length < 3) handle = `${handle}_exo`.replace(/^_+/, '').slice(0, 24)
  return handle
}

function failureMessage(result: AccountOperationResponse, fallback: string): string {
  if (result.code === 'INVALID_CREDENTIALS') return 'The email or password is incorrect.'
  if (result.code === 'INVALID_PASSWORD') return 'Use a password between 12 and 128 characters.'
  if (result.code === 'RATE_LIMITED') return 'Too many attempts. Wait a moment, then try again.'
  return result.message?.trim() || fallback
}

export function AccountPanel({
  onProfile,
  onSettings,
  onAccountState,
  initialState = null,
  heading = 'Exo account',
}: AccountPanelProps) {
  const id = useId()
  const [account, setAccount] = useState<AccountState | null>(initialState)
  const [health, setHealth] = useState<OnlineResult<OnlineHealth> | null>(null)
  const [loading, setLoading] = useState(!initialState)
  const [mode, setMode] = useState<PasswordMode>('create')
  const [busy, setBusy] = useState<BusyAction | null>(null)
  const [feedback, setFeedback] = useState<Feedback | null>(null)
  const [name, setName] = useState('')
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [confirmPassword, setConfirmPassword] = useState('')
  const [handle, setHandle] = useState('')
  const [magicEmail, setMagicEmail] = useState('')
  const busyRef = useRef(false)
  const autoSavedRef = useRef('')

  const publishAccount = useCallback((next: AccountState | null) => {
    setAccount(next)
    onAccountState?.(next)
  }, [onAccountState])

  const clearPasswordFields = useCallback(() => {
    setPassword('')
    setConfirmPassword('')
  }, [])

  const load = useCallback(async () => {
    // Account status is the first-paint dependency. Provider readiness is
    // independent and must not hold the account panel behind another read.
    const healthPromise = host.onlineHealth().catch(() => null)
    const next = await host.accountGet()
    if (!next.ok) {
      publishAccount({
        ok: false,
        signedIn: false,
        configured: false,
        providers: [],
        roles: [],
        canManageBadges: false,
        badges: [],
        message: next.message,
      })
      throw new Error(next.message ?? 'Exo account status is unavailable.')
    }
    publishAccount(next)
    setLoading(false)
    const nextHealth = await healthPromise
    if (nextHealth) setHealth(nextHealth)
    return next
  }, [publishAccount])

  const reloadLocal = useCallback(async () => {
    const [nextProfile, nextSettings] = await Promise.all([host.profileGet(), host.getSettings()])
    if (nextProfile.ok) onProfile(nextProfile)
    onSettings(nextSettings)
  }, [onProfile, onSettings])

  useEffect(() => {
    let active = true
    void load()
      .catch((error: unknown) => {
        if (!active) return
        publishAccount({
          ok: false,
          signedIn: false,
          configured: false,
          providers: [],
          roles: [],
          canManageBadges: false,
          badges: [],
        })
        setFeedback({
          message: error instanceof Error ? error.message : 'Exo account status is unavailable.',
          error: true,
        })
      })
      .finally(() => {
        if (active) setLoading(false)
      })
    const unsubscribe = onHostEvent('account.updated', (next) => {
      clearPasswordFields()
      publishAccount(next)
      void load().catch(() => {})
    })
    return () => {
      active = false
      clearPasswordFields()
      unsubscribe()
    }
  }, [clearPasswordFields, load, publishAccount])

  useEffect(() => {
    if (!feedback) return
    const timer = window.setTimeout(() => setFeedback(null), feedback.error ? 5200 : 3000)
    return () => window.clearTimeout(timer)
  }, [feedback])

  useEffect(() => {
    const key = account?.signedIn && account.handle
      ? `${account.id ?? ''}:${account.handle}`
      : ''
    if (!key || key === autoSavedRef.current || busyRef.current) return
    autoSavedRef.current = key
    busyRef.current = true
    setBusy('sync')
    void host.accountSetProfile()
      .then(async (result) => {
        if (!result.ok) {
          autoSavedRef.current = ''
          setFeedback({ message: result.message ?? 'Exo could not auto-save this profile.', error: true })
          return
        }
        await reloadLocal()
        setFeedback({ message: 'Auto-saved to Exo.' })
      })
      .catch(() => {
        autoSavedRef.current = ''
        setFeedback({ message: 'Exo could not auto-save this profile.', error: true })
      })
      .finally(() => {
        busyRef.current = false
        setBusy(null)
      })
  }, [account?.handle, account?.id, account?.signedIn, reloadLocal])

  async function run(action: BusyAction, operation: () => Promise<AccountOperationResponse>) {
    if (busyRef.current) return null
    busyRef.current = true
    setBusy(action)
    setFeedback(null)
    try {
      return await operation()
    } catch (error) {
      setFeedback({
        message: error instanceof Error ? error.message : 'That account action did not complete.',
        error: true,
      })
      return null
    } finally {
      busyRef.current = false
      setBusy(null)
    }
  }

  async function signIn() {
    const address = email.trim()
    if (!address || passwordProblem(password)) return
    const submittedPassword = password
    clearPasswordFields()
    const result = await run('signin', () => host.accountPasswordSignIn(address, submittedPassword))
    if (!result) return
    if (!result.ok) {
      setFeedback({ message: failureMessage(result, 'Sign-in could not be completed.'), error: true })
      return
    }
    await reloadLocal()
    await load()
    setFeedback({ message: 'Signed in. Your profile will stay in sync.' })
  }

  async function createAccount() {
    const displayName = name.trim()
    const address = email.trim()
    const proposedHandle = suggestedHandle(displayName)
    if (!address || nameProblem(displayName) || passwordProblem(password) || password !== confirmPassword) return
    const submittedPassword = password
    clearPasswordFields()
    const result = await run('create', () => host.accountCreatePassword(displayName, address, submittedPassword))
    if (!result) return
    if (!result.ok) {
      setFeedback({ message: failureMessage(result, 'The account could not be created.'), error: true })
      return
    }

    let handleReady = false
    if (!handleProblem(proposedHandle)) {
      const handleResult = await host.accountReserveHandle(proposedHandle)
      handleReady = handleResult.ok
    }
    await reloadLocal()
    await load()
    setFeedback({
      message: handleReady
        ? `Account ready as @${proposedHandle}. Auto-saved to Exo.`
        : 'Account ready. Choose an available handle once.',
    })
  }

  async function reserveHandle() {
    const requested = handle.trim()
    if (handleProblem(requested)) return
    const result = await run('handle', () => host.accountReserveHandle(requested))
    if (!result) return
    if (!result.ok) {
      setFeedback({ message: result.message ?? 'That handle is not available.', error: true })
      return
    }
    setHandle('')
    await reloadLocal()
    await load()
    setFeedback({ message: `Connected as @${requested}. Auto-saved to Exo.` })
  }

  async function signOut() {
    const result = await run('signout', () => host.accountSignOut())
    if (!result) return
    autoSavedRef.current = ''
    await load()
    setFeedback({ message: result.ok ? 'Signed out.' : result.message ?? 'Sign-out did not complete.', error: !result.ok })
  }

  const capabilities = health?.value?.capabilities
  const supportsPassword =
    account?.providers.includes('password') === true && capabilities?.providers.password !== false
  const supportsGoogle = capabilities?.providers.google === true && account?.providers.includes('google')
  const supportsEmail = capabilities?.providers.email === true && account?.providers.includes('email')
  const noSignInMethod = !!account?.configured && !account.signedIn && !supportsPassword && !supportsGoogle && !supportsEmail
  const missingOptionalMethods = !loading && account?.configured && capabilities
    ? [
        !supportsGoogle ? 'Google OAuth' : null,
        !supportsEmail ? 'email links' : null,
      ].filter((method): method is string => method !== null)
    : []
  const proposedHandle = suggestedHandle(name)
  const createProblem = name.length > 0 ? nameProblem(name) : null
  const passwordIssue = password.length > 0 ? passwordProblem(password) : null
  const confirmationIssue = confirmPassword.length > 0 && password !== confirmPassword

  return (
    <section className="exo-account-panel" aria-labelledby={`${id}-title`}>
      <div className="exo-account-head">
        <div>
          <p className="exo-account-kicker">Online identity</p>
          <h2 id={`${id}-title`}>{heading}</h2>
        </div>
        {account?.signedIn ? <span className="exo-account-connected">Connected</span> : null}
      </div>

      {loading ? <div className="exo-account-state" role="status">Connecting to Exo…</div> : null}
      {!loading && account && !account.configured ? (
        <div className="exo-account-state is-error" role="alert">
          Exo account service is unavailable. Continue setup; the library still works.
        </div>
      ) : null}

      {!loading && noSignInMethod ? (
        <div className="exo-account-state" role="status">
          No sign-in method is available. Continue setup; the library still works.
        </div>
      ) : null}

      {!loading && account?.configured && !account.signedIn && !noSignInMethod ? (
        <div className="exo-account-auth-stack">
          <div className="exo-account-mode-switch" role="group" aria-label="Account mode">
            <button
              type="button"
              className={`exo-account-button${mode === 'create' ? ' is-selected' : ''}`}
              aria-pressed={mode === 'create'}
              onClick={() => { clearPasswordFields(); setMode('create') }}
            >
              Create account
            </button>
            <button
              type="button"
              className={`exo-account-button${mode === 'signin' ? ' is-selected' : ''}`}
              aria-pressed={mode === 'signin'}
              onClick={() => { clearPasswordFields(); setMode('signin') }}
            >
              Sign in
            </button>
          </div>

          <form
            className="exo-account-email-form exo-account-password-form"
            aria-label={mode === 'create' ? 'Create an Exo account' : 'Sign in to Exo'}
            onSubmit={(event) => {
              event.preventDefault()
              void (mode === 'create' ? createAccount() : signIn())
            }}
          >
            {mode === 'create' ? (
              <>
                <label className="exo-account-label" htmlFor={`${id}-name`}>Profile name</label>
                <input
                  id={`${id}-name`}
                  className="exo-account-input"
                  value={name}
                  maxLength={NAME_MAX_LENGTH}
                  autoComplete="name"
                  aria-invalid={createProblem ? true : undefined}
                  onChange={(event) => setName(event.target.value)}
                />
                <p className={`exo-account-hint${createProblem ? ' is-error' : ''}`}>
                  {createProblem ?? `Your handle will start as @${proposedHandle || 'player'}.`}
                </p>
              </>
            ) : null}

            <label className="exo-account-label" htmlFor={`${id}-email`}>Email</label>
            <input
              id={`${id}-email`}
              className="exo-account-input"
              type="email"
              value={email}
              maxLength={254}
              autoComplete="username"
              required
              onChange={(event) => setEmail(event.target.value)}
            />
            <label className="exo-account-label" htmlFor={`${id}-password`}>Password</label>
            <input
              id={`${id}-password`}
              className="exo-account-input"
              type="password"
              value={password}
              minLength={PASSWORD_MIN_LENGTH}
              maxLength={PASSWORD_MAX_LENGTH}
              autoComplete={mode === 'create' ? 'new-password' : 'current-password'}
              required
              aria-invalid={passwordIssue ? true : undefined}
              onChange={(event) => setPassword(event.target.value)}
            />
            {mode === 'create' ? (
              <>
                <label className="exo-account-label" htmlFor={`${id}-confirm`}>Confirm password</label>
                <input
                  id={`${id}-confirm`}
                  className="exo-account-input"
                  type="password"
                  value={confirmPassword}
                  minLength={PASSWORD_MIN_LENGTH}
                  maxLength={PASSWORD_MAX_LENGTH}
                  autoComplete="new-password"
                  required
                  aria-invalid={confirmationIssue ? true : undefined}
                  onChange={(event) => setConfirmPassword(event.target.value)}
                />
              </>
            ) : null}
            <p className={`exo-account-hint${passwordIssue || confirmationIssue ? ' is-error' : ''}`}>
              {confirmationIssue ? 'Passwords must match.' : passwordIssue ?? 'Use 12–128 characters.'}
            </p>
            <button
              type="submit"
              className="exo-account-button is-primary"
              disabled={
                busy !== null || !supportsPassword || !email.trim() || !!passwordProblem(password) ||
                (mode === 'create' && (!!nameProblem(name) || password !== confirmPassword))
              }
            >
              {busy === 'create' ? 'Creating account…' : busy === 'signin' ? 'Signing in…' : mode === 'create' ? 'Create account' : 'Sign in'}
            </button>
          </form>

          {supportsGoogle ? (
            <button type="button" className="exo-account-button" disabled={busy !== null} onClick={async () => {
              const result = await run('google', () => host.accountSignIn('google'))
              if (result?.ok) { await reloadLocal(); await load() }
            }}>
              Sign in with Google
            </button>
          ) : null}

          {supportsEmail ? (
            <form className="exo-account-email-form" onSubmit={async (event) => {
              event.preventDefault()
              const result = await run('email', () => host.accountSignIn('email', magicEmail.trim()))
              setFeedback({ message: result?.message ?? (result?.ok ? 'Check your email.' : 'Email sign-in is unavailable.'), error: !result?.ok })
            }}>
              <label className="exo-account-label" htmlFor={`${id}-magic`}>Email link</label>
              <input id={`${id}-magic`} className="exo-account-input" type="email" value={magicEmail} onChange={(event) => setMagicEmail(event.target.value)} />
              <button type="submit" className="exo-account-button" disabled={busy !== null || !magicEmail.trim()}>Send link</button>
            </form>
          ) : null}

          {missingOptionalMethods.length > 0 ? (
            <p className="exo-account-hint" role="status">
              Optional methods not enabled on this deployment: {missingOptionalMethods.join(' and ')}. Configure their server credentials to show them here.
            </p>
          ) : null}
          <p className="exo-account-hint">Email verification and password recovery are not available yet.</p>
        </div>
      ) : null}

      {!loading && account?.signedIn ? (
        <div className="exo-account-signed-in">
          <div className="exo-account-identity">
            <div>
              <strong>{account.handle ? `@${account.handle}` : 'Choose your handle'}</strong>
              <span>{busy === 'sync' ? 'Saving…' : 'Auto-saved to Exo'}</span>
            </div>
          </div>
          {!account.handle ? (
            <form className="exo-account-handle-form" onSubmit={(event) => { event.preventDefault(); void reserveHandle() }}>
              <label className="exo-account-label" htmlFor={`${id}-handle`}>Handle</label>
              <input
                id={`${id}-handle`}
                className="exo-account-input"
                value={handle}
                maxLength={24}
                autoComplete="off"
                spellCheck={false}
                placeholder="your_handle"
                onChange={(event) => setHandle(event.target.value.toLowerCase().replace(/[^a-z0-9_]/g, ''))}
              />
              <button type="submit" className="exo-account-button is-primary" disabled={busy !== null || !!handleProblem(handle)}>
                {busy === 'handle' ? 'Connecting…' : 'Connect handle'}
              </button>
              <p className={`exo-account-hint${handle && handleProblem(handle) ? ' is-error' : ''}`}>
                {handle && handleProblem(handle) ? handleProblem(handle) : 'This is the one name friends use to find you.'}
              </p>
            </form>
          ) : null}
          <button type="button" className="exo-account-button" disabled={busy !== null} onClick={() => void signOut()}>
            {busy === 'signout' ? 'Logging out…' : 'Log out'}
          </button>
        </div>
      ) : null}

      {feedback ? (
        <p className={`exo-account-feedback${feedback.error ? ' is-error' : ''}`} role={feedback.error ? 'alert' : 'status'} aria-live="polite">
          {feedback.message}
        </p>
      ) : null}
    </section>
  )
}
