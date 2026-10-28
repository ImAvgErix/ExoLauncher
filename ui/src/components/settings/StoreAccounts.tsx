import { useCallback, useEffect, useState, type ReactNode } from 'react'
import { host, onHostEvent, type AccountStore, type StoreAccounts, type SwitchStore } from '../../lib/host'
import { Menu, type MenuItem } from '../ui/Menu'
import { ChevronDown } from '../../brand/icons'
import { CompanyLogo } from '../../brand/CompanyLogo'
import { confirm } from '../ui/Dialog'
import { Spinner } from '../ui/Spinner'
import { Row } from './parts'

type Result = { ok: boolean; message?: string | null }

/** An action picked from a store's Account menu that asks first: its question, then the row's line while it runs. */
type Pending = { title: string; body: string; action: string; line: string; run: () => Promise<Result> }

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

/** The row's line, with Epic's quiet note when its launcher uses another account (not beside a line of the account's own). */
export function accountDetail(store: string, accounts: StoreAccounts | null, line: string | null | undefined, own: boolean): ReactNode {
  if (!line) return undefined
  if (own || store !== 'epic' || accounts?.epic.launcherDiffers !== true) return line
  return <>{line}<span className="exo-set-item-aside">The Epic Games Launcher uses a different account</span></>
}

const CLIENT: Record<SwitchStore, string> = { steam: 'Steam', riot: 'Riot Client' }

type AccountItem = MenuItem & { pending?: Pending; run?: () => Promise<Result>; line?: string }

/** One of Steam's remembered accounts or the Riot accounts Exo saved. */
type SwitchAccount = { key: string; name: string; current: boolean; saved?: boolean }

/**
 * Steam's and Riot's Account menu, the same menu as every store's: the accounts on this PC (the one in use checked; any
 * other switches to it), Add account, and Forget for the others. Switching runs at once, its steps in the row's line;
 * Forget asks first.
 */
function switchMenuFor(store: SwitchStore, accounts: StoreAccounts): AccountItem[] {
  const list: SwitchAccount[] = (store === 'steam' ? accounts.steam?.accounts : accounts.riot?.accounts) ?? []
  const items: AccountItem[] = list.map(account => ({
    id: `switch:${account.key}`,
    label: account.name,
    section: 'Accounts',
    on: account.current,
    line: account.current ? undefined : `${CLIENT[store]} restarts to sign in as ${account.name}…`,
    run: account.current ? undefined : () => host.accountsSwitch(store, account.key),
  }))
  items.push({ id: 'add', label: 'Add account', line: `${CLIENT[store]} closes and opens its sign-in`, run: () => host.accountsAdd(store) })
  for (const account of list.filter(entry => !entry.current && (store === 'steam' || entry.saved === true))) {
    items.push({
      id: `forget:${account.key}`,
      label: `Forget ${account.name}`,
      section: 'Forget',
      pending: {
        title: `Forget ${account.name}?`,
        body: store === 'steam' ? `Steam forgets ${account.name} on this PC. Signing in again brings it back.` : `Exo forgets ${account.name}'s saved sign-in. Signing in again brings it back.`,
        action: 'Forget', line: `Forgetting ${account.name}`, run: () => host.accountsRemove(store, account.key),
      },
    })
  }
  return items
}

function menuFor(store: Exclude<AccountStore, 'steam'>, accounts: StoreAccounts): AccountItem[] {
  const name = { epic: 'Epic', gog: 'GOG', amazon: 'Amazon' }[store]
  const items: AccountItem[] = [{
    id: 'switch',
    label: 'Switch account',
    pending: {
      title: `Switch ${name} account?`, body: `Exo signs out of ${name}, then opens its sign-in. Installed games stay on this PC.`, action: 'Switch',
      line: `Exo signs out of ${name}, then opens its sign-in`, run: () => host.accountsSwitch(store),
    },
  }]
  if (store === 'epic' && accounts.epic.canImport) {
    items.push({
      id: 'import',
      label: "Use the Epic Games Launcher's account",
      pending: {
        title: "Use the launcher's account?", body: 'The Epic Games Launcher signs out, and Exo signs in with its account.', action: 'Use its account',
        line: 'The Epic Games Launcher signs out and Exo uses its account', run: host.accountsEpicImport,
      },
    })
  }
  if (store === 'epic') items.push({ id: 'add', label: 'Add another account', run: host.accountsEpicAdd })
  if (store === 'epic' && accounts.epic.canSwitchLauncher) {
    items.push({
      id: 'newlauncher',
      label: "Switch to Epic's new launcher",
      pending: {
        title: "Switch to Epic's new launcher?",
        body: "Exo downloads Epic's new launcher from Epic and opens its installer. It moves your games, saves and purchases over and takes out the old launcher. Epic still calls it a preview.",
        action: 'Switch', line: "Downloading Epic's new launcher", run: host.accountsEpicNewLauncher,
      },
    })
  }
  const canSignOut = store === 'epic' ? accounts.epic.signedIn : store === 'gog' ? accounts.gog.canSignOut : accounts.amazon.signedIn
  if (canSignOut) {
    items.push({
      id: 'signout',
      label: 'Sign out',
      pending: {
        title: `Sign out of ${name}?`, body: `Exo signs out of ${name}. Installed games stay on this PC.`, action: 'Sign out',
        line: `Exo signs out of ${name}`, run: () => host.accountsSignOut(store),
      },
    })
  }
  return items
}

/**
 * The Account menu on a store row. What signs out asks first; while it runs, the row's line says what happens.
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
  const [running, setRunning] = useState(false)
  const switches = store === 'steam' || store === 'riot'
  useEffect(() => {
    if (!running || !switches) return
    return onHostEvent('accounts.progress', progress => { if (progress.store === store) onLine(progress.line) })
  }, [running, switches, store, onLine])
  const items = switches ? switchMenuFor(store, accounts) : menuFor(store, accounts)
  if (items.length === 0) return null

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
      setRunning(false)
      onBusy(false)
      onDone()
    }
  }

  async function ask(pending: Pending) {
    if (await confirm({ title: pending.title, body: pending.body, action: pending.action }) !== true) return
    onLine(pending.line)
    await run(pending.run)
  }

  return (
    <Menu
      label="Account"
      anchor={`--exo-account-${store}`}
      offset={4}
      triggerClassName="exo-set-btn"
      trigger={<>{running ? <Spinner label="Changing the account" /> : null}Account<ChevronDown size={12} /></>}
      items={items}
      disabled={busy}
      onAction={(id) => {
        const item = items.find(entry => entry.id === id)
        if (item?.line) onLine(item.line)
        if (item?.run) void run(item.run)
        else if (item?.pending) void ask(item.pending)
      }}
    />
  )
}

/** The Epic accounts added for their hours (hours always count every account on this PC). */
export function AccountHours({ accounts, onChanged }: {
  accounts: StoreAccounts | null
  onChanged: () => void
}) {
  const [removing, setRemoving] = useState<string | null>(null)
  const [removeError, setRemoveError] = useState<{ key: string; message: string } | null>(null)
  async function remove(account: { key: string; name: string }) {
    const sure = await confirm({ title: `Remove ${account.name}?`, body: "Exo stops counting this account's Epic hours. Add it again to count them.", action: 'Remove', danger: true })
    if (sure !== true) return
    const { key } = account
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
      {(accounts?.epic.added ?? []).map(account => (
        <Row key={account.key} label={account.name} icon={<CompanyLogo company="epic" size={22} />} error={removeError?.key === account.key ? removeError.message : null}>
          <button type="button" className="exo-set-btn" disabled={removing !== null} onClick={() => void remove(account)}>
            {removing === account.key ? <Spinner label={`Removing ${account.name}`} /> : null}Remove
          </button>
        </Row>
      ))}
    </>
  )
}
