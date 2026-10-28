import { useEffect, useMemo, useRef, useState } from 'react'
import { Check } from '../../brand/icons'
import { host, type AppFind, type AppLeftover, type InstalledApp, type JunkItem, type LeftoverSummary } from '../../lib/host'
import { notify } from '../../lib/notify'
import { report } from '../../lib/report'
import { Spinner } from '../ui/Spinner'
import { Skeleton } from '../ui/Skeleton'
import { Row } from '../settings/parts'

type Sort = 'size' | 'name' | 'newest' | 'unused'
type Busy = { id: string; what: 'uninstall' | 'scan' | 'clean' | 'replaced'; likely?: boolean }
type Outcome = { find?: AppFind; cleared?: LeftoverSummary; message?: string | null; error?: string | null }

const SORTS: { id: Sort; label: string }[] = [{ id: 'size', label: 'Size' }, { id: 'name', label: 'Name' }, { id: 'newest', label: 'Newest' }, { id: 'unused', label: 'Unused' }]

const KIND: Record<AppLeftover['kind'], string> = {
  folder: 'Folder', file: 'File', shortcut: 'Shortcut', registry: 'Registry', service: 'Service', task: 'Task',
}

function size(bytes: number | null | undefined): string {
  if (!bytes || bytes <= 0) return ''
  if (bytes >= 1e9) return `${(bytes / 1e9).toFixed(1)} GB`
  if (bytes >= 1e6) return `${Math.round(bytes / 1e6)} MB`
  return `${Math.max(1, Math.round(bytes / 1e3))} KB`
}

/** "3 folders (1.2 GB), 9 registry entries and 2 shortcuts": what an app left, or what was cleared of it. */
function said(left: LeftoverSummary): string {
  const count = (n: number, one: string, many: string) => `${n} ${n === 1 ? one : many}`
  const parts: string[] = []
  if (left.folders > 0) parts.push(count(left.folders, 'folder', 'folders'))
  if (left.files > 0) parts.push(count(left.files, 'file', 'files'))
  const amount = size(left.bytes)
  if (amount && parts.length > 0) parts[parts.length - 1] += ` (${amount})`
  if (left.registry > 0) parts.push(count(left.registry, 'registry entry', 'registry entries'))
  if (left.shortcuts > 0) parts.push(count(left.shortcuts, 'shortcut', 'shortcuts'))
  return parts.length < 2 ? parts.join('') : `${parts.slice(0, -1).join(', ')} and ${parts[parts.length - 1]}`
}

const day = new Intl.DateTimeFormat(undefined, { month: 'short', day: 'numeric', year: 'numeric' })

/**
 * PC › Apps, Exo's uninstaller, as Revo Uninstaller works: every program on this PC with its size and date; Uninstall
 * runs its own uninstaller (asked in Exo's window), waits it out, then shows what it left behind before anything goes:
 * the sure finds (what points into its folders, its own entries) on one press, and what only has its name apart, on its
 * own press. A program whose uninstaller is gone offers to clear what is left. The apps Exo does the job of lead, removed
 * with Exo's own clean-up; what Exo has cleared stays listed at the foot. An app leaves the list the moment it's gone:
 * what it left waits under Just removed at the top until it's deleted or left, and an app that left nothing only says so.
 * Select picks several: asked once, their uninstallers run one after another, each one's leftovers found as it goes.
 * Junk (temp files, crash reports, the Recycle Bin) leads; bloatware is grouped with Remove all; Unused sorts by when Windows
 * last saw each app opened, and an app not opened in two months says since when.
 */
export function AppsPanel({ active }: { active: boolean }) {
  const [programs, setPrograms] = useState<InstalledApp[] | null>(null)
  const [removed, setRemoved] = useState<InstalledApp[]>([])
  const [cleared, setCleared] = useState<{ name: string; summary: LeftoverSummary }[]>([])
  const [edgeRemoved, setEdgeRemoved] = useState(false)
  const [icons, setIcons] = useState<Record<string, string | null>>({})
  const [query, setQuery] = useState('')
  const [sort, setSort] = useState<Sort>('size')
  const [busy, setBusy] = useState<Busy | null>(null)
  const [outcomes, setOutcomes] = useState<Record<string, Outcome>>({})
  // Select: the apps picked to uninstall together (null: not selecting), and how far a run of them has got.
  const [picked, setPicked] = useState<Set<string> | null>(null)
  const [run, setRun] = useState<{ at: number; of: number } | null>(null)
  const read = useRef(false)

  async function load() {
    try {
      const result = await host.appsList()
      setPrograms(result.programs)
      setCleared(result.cleared)
      setEdgeRemoved(Boolean(result.edgeRemoved))
      const missing = result.programs.map(app => app.id)
      for (let at = 0; at < missing.length; at += 100) {
        const chunk = await host.appsIcons(missing.slice(at, at + 100))
        setIcons(current => ({ ...current, ...chunk.icons }))
      }
    } catch (error) {
      report(error, 'AppsPanel.list')
      setPrograms(current => current ?? [])
    }
  }

  useEffect(() => {
    if (!active || read.current) return
    read.current = true
    void load()
  }, [active])

  const shown = useMemo(() => {
    if (!programs) return []
    const words = query.trim().toLowerCase()
    const list = words ? programs.filter(app => `${app.name} ${app.publisher ?? ''}`.toLowerCase().includes(words)) : [...programs]
    if (sort === 'size') list.sort((a, b) => (b.bytes ?? 0) - (a.bytes ?? 0))
    else if (sort === 'newest') list.sort((a, b) => (b.installedOn ?? '').localeCompare(a.installedOn ?? ''))
    // Longest unopened first; apps Windows has no record of opening (started by a launcher, at sign-in) after them.
    else if (sort === 'unused') list.sort((a, b) => (a.lastOpened ?? '9999').localeCompare(b.lastOpened ?? '9999') || (b.bytes ?? 0) - (a.bytes ?? 0))
    else list.sort((a, b) => a.name.localeCompare(b.name))
    return list
  }, [programs, query, sort])

  function settle(id: string, outcome: Outcome) {
    setOutcomes(current => ({ ...current, [id]: { ...current[id], ...outcome } }))
  }

  /** Off the list: what it left waits under Just removed; with nothing left, a notice says it's gone. */
  function gone(app: InstalledApp, outcome: Outcome) {
    if (outcome.find && outcome.find.items.length > 0) {
      setRemoved(current => [app, ...current.filter(other => other.id !== app.id)])
      settle(app.id, outcome)
      return
    }
    const cleared = outcome.cleared ? ` Cleared ${said(outcome.cleared)}.` : outcome.find ? ' It left nothing behind.' : ''
    notify({ text: `${app.name} is gone.${cleared}`, tone: 'good' })
  }

  function leave(app: InstalledApp) {
    setRemoved(current => current.filter(other => other.id !== app.id))
  }

  async function uninstall(app: InstalledApp, pass?: string) {
    if (busy && !pass) return
    setBusy({ id: app.id, what: app.replaces ? 'replaced' : app.uninstaller ? 'uninstall' : 'scan' })
    settle(app.id, { error: null, message: null })
    try {
      let outcome: Outcome = {}
      if (app.replaces) {
        const result = await host.replacedRemove(app.replaces)
        const mine = result.apps.find(replaced => replaced.id === app.replaces)
        if (!result.declined) outcome = !result.ok ? { error: result.message ?? `${app.name} wasn't removed.` } : { cleared: mine?.cleared ?? undefined }
      } else {
        const result = app.uninstaller ? await host.appsUninstall(app.id, pass) : await host.appsScan(app.id)
        if (result.ok) outcome = { find: result }
        else if (!('declined' in result && result.declined)) outcome = { error: result.message ?? `${app.name} wasn't removed.` }
      }
      const next = await host.appsList()
      setPrograms(next.programs)
      setCleared(next.cleared)
      setEdgeRemoved(Boolean(next.edgeRemoved))
      if (next.programs.some(now => now.id === app.id)) settle(app.id, outcome)
      else gone(app, outcome)
    } catch (error) {
      report(error, 'AppsPanel.uninstall')
      settle(app.id, { error: `${app.name} wasn't removed.` })
    } finally {
      setBusy(null)
    }
  }

  /** Several apps (the picked ones, or all the bloatware), asked once in Exo's window, then each uninstalled in turn. */
  async function uninstallMany(ids: string[]) {
    if (ids.length === 0 || busy || run || !programs) return
    try {
      const agreed = await host.appsConfirm(ids)
      if (!agreed.ok) {
        if (!agreed.declined) notify({ text: agreed.message ?? 'Nothing was removed.', tone: 'bad' })
        return
      }
      setPicked(null)
      const apps = agreed.ids.map(id => programs.find(app => app.id === id)).filter((app): app is InstalledApp => !!app)
      for (const [at, app] of apps.entries()) {
        setRun({ at: at + 1, of: apps.length })
        await uninstall(app, agreed.token)
      }
    } catch (error) {
      report(error, 'AppsPanel.uninstallMany')
      notify({ text: 'The apps weren’t removed.', tone: 'bad' })
    } finally {
      setRun(null)
    }
  }

  function pick(app: InstalledApp) {
    setPicked(current => {
      const next = new Set(current)
      if (!next.delete(app.id)) next.add(app.id)
      return next
    })
  }

  async function clean(app: InstalledApp, find: AppFind, likely: boolean) {
    if (busy) return
    setBusy({ id: app.id, what: 'clean', likely })
    try {
      const ids = find.items.filter(item => item.likely === likely).map(item => item.id)
      const result = await host.appsClean(find.token, ids)
      if (result.cleared) {
        const kept = find.items.filter(item => item.likely !== likely)
        if (kept.length === 0 && removed.some(other => other.id === app.id)) {
          leave(app)
          notify({ text: `Cleared ${said(result.cleared)} that ${app.name} left.`, tone: 'good' })
        }
        settle(app.id, { cleared: result.cleared, find: kept.length > 0 ? { ...find, items: kept, sure: likely ? find.sure : emptySummary(), likely: likely ? emptySummary() : find.likely } : undefined, error: result.message ?? null })
        setCleared((await host.appsList()).cleared)
      } else if (!result.ok && !result.declined) settle(app.id, { error: result.message ?? 'Nothing was deleted.' })
    } catch (error) {
      report(error, 'AppsPanel.clean')
      settle(app.id, { error: 'Nothing was deleted.' })
    } finally {
      setBusy(null)
    }
  }

  if (!programs) return <Skeleton label="Reading this PC's apps" rows={8} avatar />
  const replaced = shown.filter(app => app.replaces)
  const bloat = shown.filter(app => !app.replaces && app.bloat)
  const rest = shown.filter(app => !app.replaces && !app.bloat)

  const row = (app: InstalledApp, after?: () => void) => (
    <AppRow key={app.id} app={app} icon={icons[app.id] ?? null} busy={busy?.id === app.id ? busy.what : null} cleaning={busy?.id === app.id && busy.what === 'clean' ? (busy.likely ? 'likely' : 'sure') : null} anyBusy={busy !== null || run !== null}
      outcome={outcomes[app.id]} onUninstall={() => void uninstall(app)} onClean={(find, likely) => void clean(app, find, likely)} onLeave={after}
      picked={picked && !after && pickable(app) ? picked.has(app.id) : null} onPick={() => pick(app)} />
  )

  return (
    <div className="exo-apps">
      <div className="exo-apps-tools">
        <input type="search" className="exo-field" value={query} placeholder="Search apps" aria-label="Search apps" onChange={event => setQuery(event.target.value)} />
        <div className="exo-shelf-chips" role="group" aria-label="Sort apps">
          {/* Unused only where Windows keeps a record of what was opened (its tracking can be off). */}
          {SORTS.filter(choice => choice.id !== 'unused' || programs.some(app => app.lastOpened)).map(choice => (
            <button key={choice.id} type="button" className="exo-shelf-chip" aria-pressed={sort === choice.id} onClick={() => setSort(choice.id)}>{choice.label}</button>
          ))}
        </div>
        <div className="exo-apps-select">
          {run ? <span className="exo-apps-run" role="status"><Spinner label={`Uninstalling ${run.at} of ${run.of}`} />{run.at} of {run.of}</span> : null}
          {picked && picked.size > 0 ? (
            <button type="button" className="exo-set-btn is-danger" disabled={busy !== null} onClick={() => void uninstallMany([...picked])}>Uninstall {picked.size}</button>
          ) : null}
          <button type="button" className="exo-set-btn" aria-pressed={picked !== null} disabled={run !== null} onClick={() => setPicked(current => current ? null : new Set())}>{picked ? 'Cancel' : 'Select'}</button>
        </div>
      </div>
      {/* The list scrolls under the search and fades into the panel's foot (PC › Apps). */}
      <div className="exo-apps-list">
      <JunkSection active={active} />
      {removed.length > 0 ? (
        <section aria-label="Just removed">
          <h3 className="exo-apps-group">Just removed</h3>
          <div className="exo-set-list">{removed.map(app => row(app, () => leave(app)))}</div>
        </section>
      ) : null}
      {replaced.length > 0 ? (
        <section aria-label="Exo does their job">
          <h3 className="exo-apps-group">Exo does their job</h3>
          <div className="exo-set-list">{replaced.map(app => row(app))}</div>
        </section>
      ) : null}
      <BloatSection apps={bloat} disabled={busy !== null || run !== null} onRemoveAll={ids => void uninstallMany(ids)}>{bloat.map(app => row(app))}</BloatSection>
      <section aria-label="All apps">
        {replaced.length > 0 || bloat.length > 0 ? <h3 className="exo-apps-group">All apps</h3> : null}
        <div className="exo-set-list">{rest.length > 0 ? rest.map(app => row(app)) : <Row label="No apps match" />}</div>
      </section>
      {cleared.length > 0 || edgeRemoved ? (
        <section aria-label="Cleared by Exo">
          <h3 className="exo-apps-group">Cleared by Exo</h3>
          <div className="exo-set-list">
            {edgeRemoved ? <EdgeRow busy={busy !== null} onAllowed={() => setEdgeRemoved(false)} /> : null}
            {cleared.map(entry => <Row key={entry.name} label={entry.name} detail={`Cleared ${said(entry.summary)}`} />)}
          </div>
        </section>
      ) : null}
      </div>
    </div>
  )
}

/** Bloatware, grouped: Remove all uninstalls every one that can go, asked once. */
function BloatSection({ apps, disabled, onRemoveAll, children }: { apps: InstalledApp[]; disabled: boolean; onRemoveAll: (ids: string[]) => void; children: React.ReactNode }) {
  if (apps.length === 0) return null
  const removable = apps.filter(pickable).map(app => app.id)
  return (
    <section aria-label="Bloatware">
      <div className="exo-apps-group-head">
        <h3 className="exo-apps-group">Bloatware</h3>
        {removable.length > 1 ? <button type="button" className="exo-set-btn is-danger" disabled={disabled} onClick={() => onRemoveAll(removable)}>Remove all {removable.length}</button> : null}
      </div>
      <div className="exo-set-list">{children}</div>
    </section>
  )
}

/**
 * Junk on this PC, by kind, with its size (junk.scan): each cleaned on its own press or all at once, asked in Exo's
 * window. Read when the panel first shows, and again after a clean.
 */
function JunkSection({ active }: { active: boolean }) {
  const [items, setItems] = useState<JunkItem[] | null>(null)
  const [busy, setBusy] = useState<string | null>(null)
  const read = useRef(false)
  useEffect(() => {
    if (!active || read.current) return
    read.current = true
    host.junkScan().then(result => setItems(result.items), (error: unknown) => { report(error, 'AppsPanel.junk'); setItems([]) })
  }, [active])
  async function clean(ids: string[], label: string) {
    setBusy(label)
    try {
      const result = await host.junkClean(ids)
      if (result.ok) {
        notify({ text: result.files > 0 ? `Deleted ${size(result.bytes) || 'a little'} of junk.` : 'Nothing could be deleted: those files are in use.', tone: result.files > 0 ? 'good' : 'bad' })
        setItems((await host.junkScan()).items)
      } else if (!result.declined) notify({ text: result.message ?? 'Nothing was deleted.', tone: 'bad' })
    } catch (error) {
      report(error, 'AppsPanel.junkClean')
      notify({ text: 'Nothing was deleted.', tone: 'bad' })
    } finally {
      setBusy(null)
    }
  }
  if (!items || items.length === 0) return null
  const total = items.reduce((sum, item) => sum + item.bytes, 0)
  return (
    <section aria-label="Junk">
      <div className="exo-apps-group-head">
        <h3 className="exo-apps-group">Junk</h3>
        {items.length > 1 ? (
          <button type="button" className="exo-set-btn is-danger" disabled={busy !== null} onClick={() => void clean(items.map(item => item.id), 'all')}>
            {busy === 'all' ? <Spinner label="Deleting junk" /> : null}Clean {size(total)}
          </button>
        ) : null}
      </div>
      <div className="exo-set-list">
        {items.map(item => (
          <Row key={item.id} label={item.name} detail={size(item.bytes)}>
            <button type="button" className="exo-set-btn" disabled={busy !== null} onClick={() => void clean([item.id], item.id)}>
              {busy === item.id ? <Spinner label={`Deleting ${item.name.toLowerCase()}`} /> : null}Clean
            </button>
          </Row>
        ))}
      </div>
    </section>
  )
}

/** Edge, removed here: Edge Update is told not to install it again until Allow Edge again. */
function EdgeRow({ busy, onAllowed }: { busy: boolean; onAllowed: () => void }) {
  const [asking, setAsking] = useState(false)
  async function allow() {
    setAsking(true)
    try {
      const result = await host.appsAllowEdge()
      if (result.ok) onAllowed()
      else if (result.message && !result.declined) notify({ text: result.message, tone: 'bad' })
    } catch (error) {
      report(error, 'AppsPanel.allowEdge')
    } finally {
      setAsking(false)
    }
  }
  return (
    <Row label="Microsoft Edge">
      <button type="button" className="exo-set-btn" disabled={busy || asking} onClick={() => void allow()}>
        {asking ? <Spinner label="Allow Edge again" /> : null}Allow Edge again
      </button>
    </Row>
  )
}

function emptySummary(): LeftoverSummary {
  return { folders: 0, files: 0, registry: 0, shortcuts: 0, bytes: 0 }
}

/** Its facts in one line: publisher, size, the day it was installed, when it was last opened. */
function facts(app: InstalledApp): string {
  const date = app.installedOn ? day.format(new Date(`${app.installedOn}T12:00:00`)) : null
  const line = [app.publisher, size(app.bytes), date, stale(app.lastOpened)].filter(Boolean).join(' · ')
  return app.uninstaller ? line : `Its uninstaller is gone${line ? ` · ${line}` : ''}`
}

const WORKING: Record<Busy['what'], (name: string) => string> = {
  uninstall: name => `Uninstalling ${name}: finish in its window`,
  replaced: name => `Removing ${name}`,
  scan: name => `Looking for what ${name} left`,
  clean: () => 'Deleting',
}

const month = new Intl.DateTimeFormat(undefined, { month: 'short', year: 'numeric' })

/** "Not opened since Mar 2025" for an app last opened over two months ago; nothing when it's in use or Windows doesn't know. */
function stale(lastOpened: string | null | undefined): string | null {
  if (!lastOpened) return null
  const at = new Date(`${lastOpened}T12:00:00`)
  return Date.now() - at.getTime() > 60 * 86_400_000 ? `Not opened since ${month.format(at)}` : null
}

/** What Select can pick: an app with its uninstaller, that can go, and isn't one Exo does the job of (those go one by one). */
const pickable = (app: InstalledApp) => app.removable && app.uninstaller && !app.replaces

function AppRow({ app, icon, busy, cleaning, anyBusy, outcome, onUninstall, onClean, onLeave, picked, onPick }: {
  app: InstalledApp
  icon: string | null
  busy: Busy['what'] | null
  /** Which of its leftovers are being deleted. */
  cleaning: 'sure' | 'likely' | null
  anyBusy: boolean
  outcome: Outcome | undefined
  onUninstall: () => void
  onClean: (find: AppFind, likely: boolean) => void
  /** Removed: Leave them takes it off Just removed with its leftovers kept. */
  onLeave?: () => void
  /** Selecting: whether it's picked (null: not pickable, or not selecting). */
  picked: boolean | null
  onPick: () => void
}) {
  const done = outcome && (outcome.find || outcome.cleared || outcome.message) ? outcome : null
  const working = busy ? WORKING[busy](app.name) : null
  const action = app.replaces ? 'Remove' : app.uninstaller ? 'Uninstall' : 'Clear what’s left'
  return (
    <>
      <Row label={app.name} detail={working ?? (onLeave ? undefined : facts(app))} icon={<AppIcon name={app.name} icon={icon} />} error={outcome?.error ?? undefined}>
        {picked !== null ? (
          <button type="button" className={picked ? 'exo-apps-pick is-picked' : 'exo-apps-pick'} aria-pressed={picked} aria-label={`Pick ${app.name}`} onClick={onPick}>
            {picked ? <Check size={12} /> : null}
          </button>
        ) : !done && app.removable ? <button type="button" className="exo-set-btn is-danger" disabled={anyBusy} onClick={onUninstall}>{working && busy !== 'clean' ? <Spinner label={working} /> : null}{action}</button> : null}
      </Row>
      {done ? <Review app={app} outcome={done} busy={cleaning} anyBusy={anyBusy} onClean={onClean} onLeave={onLeave} /> : null}
    </>
  )
}

function AppIcon({ name, icon }: { name: string; icon: string | null }) {
  return <span className="exo-apps-icon">{icon ? <img src={icon} alt="" draggable={false} /> : <span aria-hidden="true">{name.slice(0, 1)}</span>}</span>
}

function Review({ app, outcome, busy, anyBusy, onClean, onLeave }: {
  app: InstalledApp
  outcome: Outcome
  busy: 'sure' | 'likely' | null
  anyBusy: boolean
  onClean: (find: AppFind, likely: boolean) => void
  onLeave?: () => void
}) {
  const [open, setOpen] = useState(false)
  const find = outcome.find
  const sure = find?.items.filter(item => !item.likely) ?? []
  const likely = find?.items.filter(item => item.likely) ?? []
  const toggle = <button type="button" className="exo-set-btn" aria-expanded={open} onClick={() => setOpen(value => !value)}>{open ? 'Hide' : 'Show what'}</button>
  const gone = app.uninstaller && !outcome.cleared ? `${app.name} is gone. ` : ''
  return (
    <div className="exo-apps-review" role="status">
      <ReviewWords app={app} outcome={outcome} empty={!!find && sure.length === 0 && likely.length === 0} />
      {find && sure.length > 0 ? (
        <Found words={`${gone}It left ${said(find.sure)} behind.`}>
          <button type="button" className="exo-set-btn is-danger" disabled={anyBusy} onClick={() => onClean(find, false)}>
            {busy === 'sure' ? <Spinner label={`Deleting what ${app.name} left`} /> : null}Delete leftovers
          </button>
          {toggle}
        </Found>
      ) : null}
      {find && likely.length > 0 ? (
        <Found words={`Found by name, maybe ${app.name}’s too: ${said(find.likely)}.`} muted>
          <button type="button" className="exo-set-btn" disabled={anyBusy} onClick={() => onClean(find, true)}>
            {busy === 'likely' ? <Spinner label={`Deleting what ${app.name} may have left`} /> : null}Delete these too
          </button>
          {sure.length === 0 ? toggle : null}
        </Found>
      ) : null}
      {open && find ? <Items items={[...sure, ...likely]} /> : null}
      {onLeave ? <div className="exo-apps-actions"><button type="button" className="exo-set-btn" disabled={anyBusy} onClick={onLeave}>Leave them</button></div> : null}
    </div>
  )
}

function ReviewWords({ app, outcome, empty }: { app: InstalledApp; outcome: Outcome; empty: boolean }) {
  if (outcome.cleared) return <p className="exo-apps-said is-done">Cleared {said(outcome.cleared)}.</p>
  if (outcome.message) return <p className="exo-apps-said">{outcome.message}</p>
  return empty ? <p className="exo-apps-said">{app.name} is gone, and left nothing behind.</p> : null
}

function Found({ words, muted, children }: { words: string; muted?: boolean; children: React.ReactNode }) {
  return (
    <div className="exo-apps-found">
      <p className={muted ? 'exo-apps-said is-muted' : 'exo-apps-said'}>{words}</p>
      <div className="exo-apps-actions">{children}</div>
    </div>
  )
}

function Items({ items }: { items: AppLeftover[] }) {
  return (
    <ul className="exo-apps-items">
      {items.map(item => (
        <li key={item.id} className={item.likely ? 'is-likely' : undefined}>
          <span className="exo-apps-kind">{KIND[item.kind]}</span>
          <span className="exo-apps-path">{item.path}</span>
          <span className="exo-apps-size">{size(item.bytes)}</span>
        </li>
      ))}
    </ul>
  )
}
