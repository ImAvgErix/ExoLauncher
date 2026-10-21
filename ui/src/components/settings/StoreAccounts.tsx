import { useCallback, useEffect, useState, type ReactNode } from 'react'
import { host, onHostEvent, type AccountStore, type LauncherSettings, type StoreAccounts, type SwitchStore } from '../../lib/host'
import { report } from '../../lib/report'
import { Switch } from '../Switch'
import { Menu, type MenuItem } from '../ui/Menu'
import { ChevronDown } from '../../brand/icons'
import { Row, State } from './parts'

type Result = { ok: boolean; message?: string | null }

/** An action picked from a store's Account menu, waiting for its confirm click. */
type Pending = { id: string; line: string; confirm: string; run: () => Promise<Result> }

/** Reads who each store is signed in as; `reload` after any change. */
export function useStoreAccounts(active: boolean) {
  const [accounts, setAccounts] = useState<StoreAccounts | null>(null)
  const reload = useCallback(async () => {
    try { setAccounts(await host.accountsList()) }
    catch { setAccounts(null) }
  }, [])
  useEffect(() => { if (active) void reload() }, [active, reload])
  return { accounts, reload }
}

/** The signed-in line for a store row, or null when Exo does not sign into that store. */
export function accountLine(store: string, accounts: StoreAccounts | null): string | null {
  if (!accounts) return null
  switch (store) {
    case 'steam': return accounts.steam?.current ? `Signed in as ${accounts.steam.current}` : null
    case 'riot': return accounts.riot?.current ? `Signed in as ${accounts.riot.current}` : null
    case 'epic': return accounts.epic.signedIn ? `Exo uses ${accounts.epic.name ?? 'an Epic account'}` : null
    case 'gog': return accounts.gog.signedIn ? 'Signed in' : null
    case 'amazon': return accounts.amazon.signedIn ? `Signed in as ${accounts.amazon.name ?? 'your Amazon account'}` : null
    default: return null
  }
}

/** The row's line, with Epic's quiet note when its launcher uses another account (not over a pending confirm). */
export function accountDetail(store: string, accounts: StoreAccounts | null, line: string | null | undefined, confirming: boolean): ReactNode {
  if (!line) return undefined
  if (confirming || store !== 'epic' || accounts?.epic.launcherDiffers !== true) return line
  return <>{line}<span className="exo-set-item-aside">The Epic Games Launcher uses a different account</span></>
}

const CLIENT: Record<SwitchStore, string> = { steam: 'Steam', riot: 'Riot Client' }

/** Add account on Steam's and Riot's rows: the client closes and opens its own sign-in. */
function addPending(store: SwitchStore, accounts: StoreAccounts): Pending {
  const kept = store === 'riot' ? accounts.riot?.current : null
  return {
    id: 'add',
    line: kept ? `Riot Client closes and opens its sign-in. Exo keeps ${kept} to switch back` : `${CLIENT[store]} closes and opens its sign-in`,
    confirm: 'Add account',
    run: () => host.accountsAdd(store),
  }
}

function menuFor(store: Exclude<AccountStore, 'steam'>, accounts: StoreAccounts): Array<MenuItem & { pending?: Pending; run?: () => Promise<Result> }> {
  const name = { epic: 'Epic', gog: 'GOG', amazon: 'Amazon' }[store]
  const items: Array<MenuItem & { pending?: Pending; run?: () => Promise<Result> }> = [{
    id: 'switch',
    label: 'Switch account',
    pending: { id: 'switch', line: `Exo signs out of ${name}, then opens its sign-in`, confirm: 'Switch', run: () => host.accountsSwitch(store) },
  }]
  if (store === 'epic' && accounts.epic.canImport) {
    items.push({
      id: 'import',
      label: "Use the Epic Games Launcher's account",
      pending: { id: 'import', line: 'The Epic Games Launcher signs out and Exo uses its account', confirm: 'Use it', run: host.accountsEpicImport },
    })
  }
  if (store === 'epic') items.push({ id: 'add', label: 'Add another account', run: host.accountsEpicAdd })
  const canSignOut = store === 'epic' ? accounts.epic.signedIn : store === 'gog' ? accounts.gog.canSignOut : accounts.amazon.signedIn
  if (canSignOut) {
    items.push({
      id: 'signout',
      label: 'Sign out',
      pending: { id: 'signout', line: `Exo signs out of ${name}`, confirm: 'Sign out', run: () => host.accountsSignOut(store) },
    })
  }
  return items
}

/**
 * The Account menu on a store row, and its confirm step: the row's line says
 * what happens, Confirm or Cancel replace the menu until one is clicked.
 */
export function AccountControl({ store, accounts, busy, onBusy, onLine, onError, onDone }: {
  store: AccountStore | SwitchStore
  accounts: StoreAccounts
  busy: boolean
  onBusy: (busy: boolean) => void
  onLine: (line: string | null) => void
  onError: (error: string | null) => void
  onDone: () => void
}) {
  const [pending, setPending] = useState<Pending | null>(null)
  const [running, setRunning] = useState(false)
  const switches = store === 'steam' || store === 'riot'
  useEffect(() => {
    if (!running || !switches) return
    return onHostEvent('accounts.progress', progress => { if (progress.store === store) onLine(progress.line) })
  }, [running, switches, store, onLine])
  const items = switches ? [] : menuFor(store, accounts)
  if (!switches && items.length === 0) return null

  async function run(action: () => Promise<Result>) {
    onBusy(true)
    setRunning(true)
    onError(null)
    try {
      const result = await action()
      onLine(result.ok ? result.message ?? null : null)
      if (!result.ok) onError(result.message ?? 'That did not finish. Try again.')
    } catch {
      onLine(null)
      onError('That did not finish. Try again.')
    } finally {
      setPending(null)
      setRunning(false)
      onBusy(false)
      onDone()
    }
  }

  if (pending) {
    return (
      <>
        <button type="button" className="exo-set-btn" disabled={busy} onClick={() => { setPending(null); onLine(null) }}>Cancel</button>
        <button type="button" className="exo-set-btn" disabled={busy} onClick={() => void run(pending.run)}>
          {busy ? 'Working…' : pending.confirm}
        </button>
      </>
    )
  }
  if (switches) {
    return (
      <button
        type="button"
        className="exo-set-btn"
        disabled={busy}
        onClick={() => { const next = addPending(store, accounts); onLine(next.line); void run(next.run) }}
      >
        {busy ? 'Working…' : 'Add account'}
      </button>
    )
  }
  return (
    <Menu
      label="Account"
      anchor={`--exo-account-${store}`}
      offset={4}
      triggerClassName="exo-set-btn"
      trigger={<>Account<ChevronDown size={12} /></>}
      items={items}
      disabled={busy}
      onAction={(id) => {
        const item = items.find(entry => entry.id === id)
        if (!item) return
        if (item.run) { void run(item.run); return }
        if (!item.pending) return
        setPending(item.pending)
        onError(null)
        onLine(item.pending.line)
      }}
    />
  )
}

type SwitchAccount = { key: string; name: string; current: boolean; remembered?: boolean; saved?: boolean; lastIn?: string[] }

/** The line under one account: its store, whether it needs Steam's sign-in, and what last played as it. */
function accountNote(store: SwitchStore, account: SwitchAccount): string {
  const label = store === 'steam' ? 'Steam' : 'Riot'
  const facts = [
    store === 'steam' && account.remembered === false ? 'sign in once in Steam' : null,
    ...(account.lastIn ?? []).map(game => `last used in ${game}`),
  ].filter(Boolean)
  return [label, ...facts].join(', ')
}

function AccountRow({ store, account, busy, onBusy, onDone }: {
  store: SwitchStore
  account: SwitchAccount
  busy: boolean
  onBusy: (busy: boolean) => void
  onDone: () => void
}) {
  // Remove waits for a confirm; Switch runs at once, its steps in the row's line.
  const [pending, setPending] = useState(false)
  const [running, setRunning] = useState<'switch' | 'remove' | null>(null)
  const [line, setLine] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const canRemove = !account.current && (store === 'steam' || account.saved === true)
  const confirmLine = store === 'steam' ? `Steam forgets ${account.name} on this PC` : `Exo forgets ${account.name}'s saved sign-in`
  useEffect(() => {
    if (running !== 'switch') return
    return onHostEvent('accounts.progress', progress => { if (progress.store === store) setLine(progress.line) })
  }, [running, store])
  async function run(action: 'switch' | 'remove') {
    onBusy(true)
    setRunning(action)
    setError(null)
    setLine(action === 'switch' ? `${CLIENT[store]} restarts to sign in as ${account.name}…` : null)
    try {
      const result = action === 'switch' ? await host.accountsSwitch(store, account.key) : await host.accountsRemove(store, account.key)
      setLine(result.ok ? result.message ?? null : null)
      if (!result.ok) setError(result.message ?? 'That did not finish. Try again.')
    } catch (cause) {
      report(cause, `StoreAccounts.${action}`)
      setLine(null)
      setError('That did not finish. Try again.')
    } finally {
      setPending(false)
      setRunning(null)
      onBusy(false)
      onDone()
    }
  }
  return (
    <Row label={account.name} detail={pending ? confirmLine : line ?? accountNote(store, account)} error={error}>
      {pending ? (
        <>
          <button type="button" className="exo-set-btn" disabled={busy} onClick={() => setPending(false)}>Cancel</button>
          <button type="button" className="exo-set-btn" disabled={busy} onClick={() => void run('remove')}>
            {running === 'remove' ? 'Working…' : 'Remove'}
          </button>
        </>
      ) : account.current ? (
        <State on>Signed in</State>
      ) : (
        <>
          {canRemove ? <button type="button" className="exo-set-btn" disabled={busy} onClick={() => { setError(null); setPending(true) }}>Remove</button> : null}
          <button type="button" className="exo-set-btn" disabled={busy} onClick={() => void run('switch')}>
            {running === 'switch' ? 'Switching…' : 'Switch'}
          </button>
        </>
      )}
    </Row>
  )
}

/**
 * Steam's remembered accounts, or the Riot accounts Exo saved, under the store's row
 * once there is more than one: the active one, and Switch and Remove for the others.
 */
export function AccountRows({ store, accounts, busy, onBusy, onDone }: {
  store: SwitchStore
  accounts: StoreAccounts | null
  busy: boolean
  onBusy: (busy: boolean) => void
  onDone: () => void
}) {
  const list: SwitchAccount[] = (store === 'steam' ? accounts?.steam?.accounts : accounts?.riot?.accounts) ?? []
  if (list.length < 2) return null
  return list.map(account => <AccountRow key={account.key} store={store} account={account} busy={busy} onBusy={onBusy} onDone={onDone} />)
}

/** The opt-in hours switch and the Epic accounts added for their hours. */
export function AccountHours({ settings, accounts, onSave, onChanged }: {
  settings: LauncherSettings | null
  accounts: StoreAccounts | null
  onSave: (patch: Partial<LauncherSettings>) => Promise<boolean>
  onChanged: () => void
}) {
  const [error, setError] = useState<string | null>(null)
  const [removing, setRemoving] = useState<string | null>(null)
  const [removeError, setRemoveError] = useState<{ key: string; message: string } | null>(null)
  const on = settings?.countHoursAcrossAccounts === true
  async function remove(key: string) {
    setRemoving(key)
    setRemoveError(null)
    const failed = { key, message: 'Could not remove that account. Try again.' }
    try {
      const result = await host.accountsEpicRemove(key)
      if (!result.ok) setRemoveError({ key, message: result.message ?? failed.message })
    } catch {
      setRemoveError(failed)
    } finally {
      setRemoving(null)
      onChanged()
    }
  }
  return (
    <>
      <Row label="Count hours from all my accounts" labelId="set-account-hours" detail="Every Steam and Epic account on this PC" error={error}>
        <Switch
          checked={on}
          disabled={!settings}
          labelledBy="set-account-hours"
          onChange={value => { setError(null); void onSave({ countHoursAcrossAccounts: value }).then(ok => { if (!ok) setError('Could not save. Try again.') }) }}
        />
      </Row>
      {(accounts?.epic.added ?? []).map(account => (
        <Row key={account.key} label={account.name} detail="Epic, for its hours" error={removeError?.key === account.key ? removeError.message : null}>
          <button type="button" className="exo-set-btn" disabled={removing !== null} onClick={() => void remove(account.key)}>
            {removing === account.key ? 'Removing…' : 'Remove'}
          </button>
        </Row>
      ))}
    </>
  )
}
