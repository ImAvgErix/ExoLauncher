import { memo, useCallback, useEffect, useState } from 'react'
import { CompanyLogo } from '../../brand/CompanyLogo'
import { host, onHostEvent, type AccountStore, type StoreAccounts, type StoreStatus, type SwitchStore } from '../../lib/host'
import { AccountControl, AccountHours, accountDetail, accountLine, useStoreAccounts } from './StoreAccounts'
import {
  availableStoreRows,
  canConnectStore,
  canOpenStoreClient,
  settingsStoreRows,
  signsInInOwnApp,
  storeClientDownloadUrl,
  storeState,
} from '../../lib/stores'
import { Spinner } from '../ui/Spinner'
import { Skeleton } from '../ui/Skeleton'
import { Group, Row, State } from './parts'
import { ErrorNote } from '../ui/ErrorNote'

export type StoreAction = 'connect' | 'open'
/** A store row's last result: a plain line, or an error under the row. */
export type StoreNote = { text: string; error: boolean }

const ACCOUNT_STORES = new Set<string>(['steam', 'riot', 'epic', 'gog', 'amazon'])

function InstalledStore({ store, busy, note, accounts, onConnect, onOpen, onAccountsChanged }: {
  store: StoreStatus
  busy: StoreAction | undefined
  note: StoreNote | undefined
  accounts: StoreAccounts | null
  onConnect: (store: StoreStatus) => Promise<void>
  onOpen: (store: StoreStatus) => Promise<void>
  onAccountsChanged: () => void
}) {
  const [accountBusy, setAccountBusy] = useState(false)
  const [accountNote, setAccountNote] = useState<string | null>(null)
  const [accountError, setAccountError] = useState<string | null>(null)
  const state = storeState(store)
  const connect = canConnectStore(store)
  const handoff = signsInInOwnApp(store)
  const open = !handoff && canOpenStoreClient(store)
  const signedInAs = accountLine(store.store, accounts)
  const showAccount = accounts !== null && ACCOUNT_STORES.has(store.store) && (signedInAs !== null || store.store === 'steam' || store.store === 'riot')
  return (
    <>
      <Row
        label={store.displayName}
        icon={<CompanyLogo company={store.store} size={22} />}
        error={accountError ?? noteError(note)}
        detail={accountDetail(store.store, accounts, accountNote ?? noteLine(note) ?? signedInAs, accountNote !== null)}
      >
        <State on={state.on}>{state.label}</State>
        {connect ? (
          <button type="button" className="exo-set-btn" disabled={!!busy} onClick={() => void onConnect(store)}>
            {busy === 'connect' ? <Spinner label="Signing in" /> : null}Sign in
          </button>
        ) : null}
        {handoff ? (
          <button type="button" className="exo-set-btn" disabled={!!busy} onClick={() => void onConnect(store)}>
            {busy === 'connect' ? <Spinner label={`Opening ${store.displayName}`} /> : null}{`Open ${store.displayName}`}
          </button>
        ) : null}
        {open ? (
          <button type="button" className="exo-set-btn" disabled={!!busy} onClick={() => void onOpen(store)}>
            {busy === 'open' ? <Spinner label={`Opening ${store.displayName}`} /> : null}Open
          </button>
        ) : null}
        <StoreSignIn store={store.store} onLine={setAccountNote} onError={setAccountError} />
        {showAccount && accounts ? (
          <AccountControl
            store={store.store as AccountStore | SwitchStore}
            accounts={accounts}
            busy={accountBusy || !!busy}
            onBusy={setAccountBusy}
            onLine={setAccountNote}
            onError={setAccountError}
            onDone={onAccountsChanged}
          />
        ) : null}
      </Row>
    </>
  )
}

const noteError = (note: StoreNote | undefined) => (note?.error ? note.text : null)
const noteLine = (note: StoreNote | undefined) => (note && !note.error ? note.text : null)

function MissingStore({ store, onError }: { store: StoreStatus; onError: (error: string | null) => void }) {
  const [busy, setBusy] = useState(false)
  const url = storeClientDownloadUrl(store.store)
  if (!url) return null
  async function get() {
    if (!url) return
    setBusy(true)
    onError(null)
    try {
      const result = await host.openUrl(url)
      if (!result.ok) onError(`Could not open the ${store.displayName} download.`)
    } catch {
      onError(`Could not open the ${store.displayName} download.`)
    } finally {
      setBusy(false)
    }
  }
  return (
    <li>
      <button type="button" className="exo-set-btn" disabled={busy} onClick={() => void get()}>
        <CompanyLogo company={store.store} size={16} />
        {store.displayName}
      </button>
    </li>
  )
}

/**
 * Steam's sign-in, as Epic's goes through Legendary: Valve's own sign-in page in Exo's window, once, kept until Sign
 * out. It is what Steam's website needs (the Library's gifts); Exo reads the rest from the Steam app. On the Steam row,
 * in Settings and in setup.
 */
export function SteamSignIn({ onLine, onError }: { onLine: (line: string | null) => void; onError: (error: string | null) => void }) {
  const [signedIn, setSignedIn] = useState<boolean | null>(null)
  const [busy, setBusy] = useState(false)
  useEffect(() => {
    host.giftsList().then(result => setSignedIn(result.steamSignedIn)).catch(() => setSignedIn(false))
    return onHostEvent('gifts.changed', result => setSignedIn(result.steamSignedIn))
  }, [])
  const run = useCallback(async (signIn: boolean) => {
    setBusy(true)
    onError(null)
    try {
      const result = await (signIn ? host.giftsSteamSignIn() : host.giftsSteamSignOut())
      if (result.ok) { setSignedIn(signIn); onLine(result.message ?? null) }
      else onError(result.message ?? 'That did not finish. Try again.')
    } catch {
      onError('That did not finish. Try again.')
    } finally {
      setBusy(false)
    }
  }, [onLine, onError])
  if (signedIn === null) return null
  return (
    <button type="button" className="exo-set-btn" disabled={busy} onClick={() => void run(!signedIn)}>
      {busy ? <Spinner label={signedIn ? 'Signing out' : 'Signing in'} /> : null}{signedIn ? 'Sign out' : 'Sign in'}
    </button>
  )
}

/** The row's own sign-in where the store needs one beyond its app (Steam's website). */
function StoreSignIn({ store, onLine, onError }: { store: string; onLine: (line: string | null) => void; onError: (error: string | null) => void }) {
  return store === 'steam' ? <SteamSignIn onLine={onLine} onError={onError} /> : null
}

export const StoresSection = memo(function StoresSection({
  stores,
  storeBusy,
  storeCheckBusy,
  storeCheckCopy,
  storeNotes,
  onCheckStores,
  onConnect,
  onOpen,
  active,
}: {
  active: boolean
  stores: StoreStatus[]
  storeBusy: Record<string, StoreAction | undefined>
  storeCheckBusy: boolean
  storeCheckCopy: string | null
  storeNotes: Record<string, StoreNote | undefined>
  onCheckStores: () => Promise<void>
  onConnect: (store: StoreStatus) => Promise<void>
  onOpen: (store: StoreStatus) => Promise<void>
}) {
  const installed = settingsStoreRows(stores)
  const missing = availableStoreRows(stores).filter(store => storeClientDownloadUrl(store.store))
  const { accounts, reload } = useStoreAccounts(active)
  const changed = useCallback(() => { void reload(); void onCheckStores() }, [reload, onCheckStores])
  const hasAccounts = installed.some(store => store.store === 'steam' || store.store === 'epic')
  const [getError, setGetError] = useState<string | null>(null)
  return (
    <Group
      id="stores"
      title="Game stores"
      action={
        <button type="button" className="exo-set-btn" disabled={storeCheckBusy} onClick={() => void onCheckStores()}>
          {storeCheckBusy ? <Spinner label="Looking for stores" /> : null}Check again
        </button>
      }
    >
      {installed.length === 0 ? (
        storeCheckBusy ? <Skeleton label="Looking for stores" avatar /> : <Row label="No store apps found on this PC" error={storeCheckCopy} />
      ) : (
        installed.map(store => (
          <InstalledStore
            key={store.store}
            store={store}
            busy={storeBusy[store.store]}
            note={storeNotes[store.store]}
            accounts={accounts}
            onConnect={onConnect}
            onOpen={onOpen}
            onAccountsChanged={changed}
          />
        ))
      )}
      {installed.length > 0 && storeCheckCopy ? (
        <div className="exo-set-item"><ErrorNote className="exo-set-error" message={storeCheckCopy} /></div>
      ) : null}
      {hasAccounts ? <AccountHours accounts={accounts} onChanged={changed} /> : null}
      {missing.length > 0 ? (
        <div className="exo-set-item">
          <span className="exo-set-item-label">Get another store</span>
          <ul className="exo-set-chips" aria-label="Stores not on this PC">
            {missing.map(store => (
              <MissingStore key={store.store} store={store} onError={setGetError} />
            ))}
          </ul>
          {getError ? <ErrorNote className="exo-set-error" message={getError} /> : null}
        </div>
      ) : null}
    </Group>
  )
})
