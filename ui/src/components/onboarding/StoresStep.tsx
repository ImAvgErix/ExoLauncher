import { CompanyLogo } from '../../brand/CompanyLogo'
import type { StoreAccounts, StoreStatus } from '../../lib/host'
import { useStoreAccounts } from '../settings/StoreAccounts'
import { Row, State } from '../settings/parts'
import { canConnectStore, canOpenStoreClient, signsInInOwnApp, storeState } from '../../lib/stores'
import type { MissingRuntime, StoreAction, StoreCheckState } from './model'

export interface StoresStepProps {
  storeCheckState: StoreCheckState
  storeRows: StoreStatus[]
  storeBusy: Record<string, StoreAction | undefined>
  storeErrors: Record<string, string | undefined>
  onStoreAction: (store: StoreStatus, action: StoreAction) => void
  missingRuntimes: MissingRuntime[]
  runtimeBusy: boolean
  onInstallRuntimes: () => void
  canAddFolder: boolean
  folderBusy: boolean
  onAddFolder: () => void
}

/** The Steam and Riot accounts this PC already has, by name. */
function foundAccounts(store: string, accounts: StoreAccounts | null): string | null {
  const list = store === 'steam' ? accounts?.steam?.accounts : store === 'riot' ? accounts?.riot?.accounts : null
  return list && list.length > 0 ? list.map(account => account.name).join(', ') : null
}

function StoreRow({ store, busy, error, found, onAction }: {
  store: StoreStatus
  busy: StoreAction | undefined
  error: string | undefined
  found: string | null
  onAction: (action: StoreAction) => void
}) {
  const state = storeState(store)
  const connect = canConnectStore(store)
  const handoff = signsInInOwnApp(store)
  const open = !connect && !handoff && canOpenStoreClient(store) && store.clientPresent === true
  return (
    <Row label={store.displayName} detail={found ?? undefined} icon={<CompanyLogo company={store.store} size={22} />} error={error}>
      <State on={state.on}>{state.label}</State>
      {connect ? (
        <button type="button" className="exo-set-btn" disabled={!!busy} onClick={() => onAction('connect')}>
          {busy === 'connect' ? 'Signing in…' : 'Sign in'}
        </button>
      ) : handoff ? (
        <button type="button" className="exo-set-btn" disabled={!!busy} onClick={() => onAction('connect')}>
          {busy === 'connect' ? 'Opening…' : `Open ${store.displayName}`}
        </button>
      ) : open ? (
        <button type="button" className="exo-set-btn" disabled={!!busy} onClick={() => onAction('open')}>
          {busy === 'open' ? 'Opening…' : 'Open'}
        </button>
      ) : null}
    </Row>
  )
}

export function StoresStep({
  storeCheckState,
  storeRows,
  storeBusy,
  storeErrors,
  onStoreAction,
  missingRuntimes,
  runtimeBusy,
  onInstallRuntimes,
  canAddFolder,
  folderBusy,
  onAddFolder,
}: StoresStepProps) {
  const { accounts } = useStoreAccounts(true)
  return (
    <div className="exo-set-list">
      {storeCheckState === 'checking' && storeRows.length === 0 ? (
        <Row label="Looking for stores on this PC…" />
      ) : storeCheckState === 'error' && storeRows.length === 0 ? (
        <Row label="Could not check for store apps" detail="You can keep going. Exo checks again from Settings, under Game stores." />
      ) : storeRows.length === 0 ? (
        <Row label="No store apps found" detail="Add a game folder below, or install Steam, Epic or another store and Exo picks it up." />
      ) : storeRows.map(store => (
        <StoreRow
          key={store.store}
          store={store}
          busy={storeBusy[store.store]}
          error={storeErrors[store.store]}
          found={foundAccounts(store.store, accounts)}
          onAction={action => onStoreAction(store, action)}
        />
      ))}
      {missingRuntimes.length > 0 ? (
        <Row label="Game runtimes" detail={missingRuntimes.map(item => item.name).join(', ')}>
          <State>{missingRuntimes.length} missing</State>
          <button type="button" className="exo-set-btn" disabled={runtimeBusy} onClick={onInstallRuntimes}>
            {runtimeBusy ? 'Installing…' : 'Install'}
          </button>
        </Row>
      ) : null}
      {canAddFolder ? (
        <Row label="Games outside a store" detail="A folder of games Exo adds to your library">
          <button type="button" className="exo-set-btn" disabled={folderBusy} onClick={onAddFolder}>
            {folderBusy ? 'Choosing…' : 'Add a folder'}
          </button>
        </Row>
      ) : null}
    </div>
  )
}
