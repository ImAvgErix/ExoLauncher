import { useEffect, useRef, useState } from 'react'
import { host, onHostEvent, type RuntimeItem, type RuntimeResult, type RuntimesProgress } from '../../lib/host'
import { report } from '../../lib/report'
import { confirm } from '../ui/Dialog'
import { CopyLogs } from '../ui/ErrorNote'
import { Progress } from '../ui/Progress'
import { Skeleton } from '../ui/Skeleton'
import { Row, State } from '../settings/parts'

type Mode = 'update' | 'repair'
type Run = { mode: Mode; progress: RuntimesProgress | null }

const GROUPS: { id: RuntimeItem['group']; title: string }[] = [
  { id: 'vc', title: 'Visual C++' },
  { id: 'directx', title: 'DirectX' },
  { id: 'dotnet', title: '.NET' },
  { id: 'other', title: 'Other' },
]

/** For Ctrl+K: the panel and each of its groups (`runtimes-<group>` is the group's element id). */
export const RUNTIMES_SEARCH: { id: string; label: string; keywords: string[] }[] = [
  {
    id: 'runtimes', label: 'Game runtimes',
    keywords: ['runtimes', 'redistributables', 'dependencies', 'install runtimes', 'update runtimes', 'repair visual c++', 'missing dll', 'msvcp140', 'vcruntime140'],
  },
  { id: 'runtimes-vc', label: 'Visual C++', keywords: ['visual c++', 'vc++', 'vcredist', 'vc_redist', 'msvc', '2005', '2008', '2010', '2012', '2013', '2015', '2017', '2019', '2022', '2026'] },
  { id: 'runtimes-directx', label: 'DirectX', keywords: ['directx', 'directx 12', 'dx12', 'd3dx9', 'xinput', 'xaudio', 'gameinput', 'directstorage', 'agility sdk', 'feature level', 'shader model'] },
  { id: 'runtimes-dotnet', label: '.NET', keywords: ['.net', 'dotnet', '.net framework', '3.5', '4.8.1', 'desktop runtime', 'net 8', 'net 9', 'net 10'] },
  { id: 'runtimes-other', label: 'XNA, OpenAL and PhysX', keywords: ['xna', 'openal', 'physx', 'webview2', 'vulkan'] },
]

const STATE: Record<RuntimeItem['state'], string> = { current: 'Up to date', outdated: 'Update', missing: 'Missing', info: '' }
const OUTCOME: Record<RuntimeResult['outcome'], string> = { installed: 'Installed', newer: 'Already newer', restart: 'Restart to finish', failed: 'Failed' }

function mb(bytes: number) {
  return bytes >= 1e9 ? `${(bytes / 1e9).toFixed(1)} GB` : `${Math.max(1, Math.round(bytes / 1e6))} MB`
}

/** What one press does: what isn't here (but what Microsoft no longer supports), and every older version. */
function todo(items: RuntimeItem[]) {
  return {
    missing: items.filter(item => item.state === 'missing' && item.bulk),
    outdated: items.filter(item => item.state === 'outdated'),
  }
}

/** "3 to install, 2 to update" or "All 29 runtimes are up to date". */
function status(items: RuntimeItem[]) {
  const { missing, outdated } = todo(items)
  const counted = items.filter(item => item.state !== 'info' && (item.state !== 'missing' || item.bulk)).length
  if (missing.length === 0 && outdated.length === 0) return `All ${counted} runtimes are up to date`
  return [missing.length > 0 && `${missing.length} to install`, outdated.length > 0 && `${outdated.length} to update`].filter(Boolean).join(', ')
}

function working(progress: RuntimesProgress | null, mode: Mode) {
  if (!progress) return mode === 'repair' ? 'Getting Visual C++ ready' : 'Getting ready'
  const of = `${progress.index} of ${progress.count}`
  if (progress.phase === 'download') return `Downloading ${progress.name} · ${of} · ${mb(progress.doneBytes)} of ${mb(progress.allBytes)}`
  if (progress.phase === 'verify') return `Checking ${progress.name}`
  if (progress.phase === 'install') return `Installing ${progress.name} · ${of}`
  return 'Checking what installed'
}

/** "5 installed, 1 needs a restart, 1 failed". */
function summary(results: RuntimeResult[]) {
  const count = (outcome: RuntimeResult['outcome']) => results.filter(result => result.outcome === outcome).length
  const parts = [
    count('installed') > 0 && `${count('installed')} installed`,
    count('newer') > 0 && `${count('newer')} already newer`,
    count('restart') > 0 && `${count('restart')} ${count('restart') === 1 ? 'needs' : 'need'} a restart`,
    count('failed') > 0 && `${count('failed')} failed`,
  ].filter(Boolean)
  return parts.length > 0 ? `${parts.join(', ')}.` : 'Nothing needed installing.'
}

/**
 * Game runtimes, as the VisualCppRedist AIO covers them and more, from their makers' own installers: every Visual C++,
 * DirectX's legacy runtime and GameInput, .NET, XNA, OpenAL and PhysX, with what this PC has of what games bring
 * themselves (Direct3D 12, DirectStorage, Vulkan). One press installs what's missing and updates what's older, asked
 * first in Exo's dialog; the downloads carry the Exo bar (each and all), Windows asks once, then each row says how it went.
 * Repair all runs every Visual C++ installer again. Nothing is ever uninstalled.
 */
export function RuntimesPanel({ active = true }: { active?: boolean }) {
  const [items, setItems] = useState<RuntimeItem[] | null>(null)
  const [run, setRun] = useState<Run | null>(null)
  const [results, setResults] = useState<Record<string, RuntimeResult>>({})
  const [note, setNote] = useState<{ text: string; error: boolean } | null>(null)
  const [canceling, setCanceling] = useState(false)
  const read = useRef(false)

  useEffect(() => onHostEvent('runtimes.progress', progress => setRun(current => current && { ...current, progress })), [])

  useEffect(() => {
    if (!active || read.current) return
    read.current = true
    host.runtimesList()
      .then(result => setItems(result.items))
      .catch((error: unknown) => {
        report(error, 'RuntimesPanel.list')
        setItems(current => current ?? [])
        setNote({ text: 'Exo couldn’t read this PC’s runtimes.', error: true })
      })
  }, [active])

  async function start(mode: Mode) {
    if (!items || run) return
    const { missing, outdated } = todo(items)
    const plan = mode === 'repair' ? items.filter(item => item.group === 'vc') : [...missing, ...outdated]
    if (plan.length === 0) return
    const answer = await ask(mode, plan, missing.length > 0)
    if (answer !== true) return
    setRun({ mode, progress: null })
    setResults({})
    setNote(null)
    try {
      const result = await host.runtimesInstall(mode)
      if (result.items) setItems(result.items)
      setResults(Object.fromEntries(result.results.map(item => [item.id, item])))
      if (result.declined || result.canceled || result.results.length === 0) setNote(result.message ? { text: result.message, error: !result.ok && !result.canceled } : null)
      else setNote({ text: summary(result.results), error: result.results.some(item => item.outcome === 'failed') })
    } catch (error) {
      report(error, 'RuntimesPanel.install')
      setNote({ text: 'The runtimes couldn’t be installed. Try again.', error: true })
    } finally {
      setRun(null)
      setCanceling(false)
    }
  }

  async function cancel() {
    setCanceling(true)
    try {
      const result = await host.runtimesCancel()
      if (!result.ok && result.message) setNote({ text: result.message, error: false })
    } catch (error) {
      report(error, 'RuntimesPanel.cancel')
    }
  }

  if (!items) return <Skeleton label="Reading this PC's runtimes" rows={8} />

  return (
    <div className="exo-runtimes">
      <Head items={items} run={run} note={note} canceling={canceling} onStart={mode => void start(mode)} onCancel={() => void cancel()} />
      {/* The groups scroll under the head and fade into the panel's foot (PC › Apps). */}
      <div className="exo-runtimes-list">
      {GROUPS.map(group => {
        const rows = items.filter(item => item.group === group.id)
        if (rows.length === 0) return null
        return (
          <section key={group.id} id={`runtimes-${group.id}`} aria-label={group.title}>
            <h3 className="exo-runtimes-group">{group.title}</h3>
            <div className="exo-set-list">
              {rows.map(item => <RuntimeRow key={item.id} item={item} result={results[item.id]} progress={run?.progress?.id === item.id ? run.progress : null} />)}
            </div>
          </section>
        )
      })}
      </div>
    </div>
  )
}

/** Asks first, in Exo's dialog: what goes on, about how much comes from whom, and that Windows asks once. */
function ask(mode: Mode, plan: RuntimeItem[], installs: boolean) {
  const size = plan.reduce((sum, item) => sum + (item.size || 60e6), 0)
  if (mode === 'repair') {
    return confirm({
      title: 'Repair Visual C++?',
      body: `Every Visual C++ from 2005 to 2026 installs again from Microsoft (about ${mb(size)}). Windows asks for permission once. A newer one stays as it is.`,
      action: 'Repair',
    })
  }
  const makers = [...new Set(plan.map(item => item.id === 'openal' ? 'Creative' : item.id.startsWith('physx') ? 'NVIDIA' : 'Microsoft'))]
  return confirm({
    title: plan.length === 1 ? `Install ${plan[0].name}?` : `Install ${plan.length} runtimes?`,
    body: (
      <>
        {plan.length > 1 ? <p>{plan.map(item => item.name).join(', ')}.</p> : null}
        <p>About {mb(size)} comes from {makers.join(', ').replace(/, ([^,]*)$/, ' and $1')}. Windows asks for permission once.</p>
      </>
    ),
    action: installs ? 'Install' : 'Update',
  })
}

/** The status line and its two buttons; while they install, the words and the Exo bar for all of it, and Cancel while it downloads. */
function Head({ items, run, note, canceling, onStart, onCancel }: {
  items: RuntimeItem[]
  run: Run | null
  note: { text: string; error: boolean } | null
  canceling: boolean
  onStart: (mode: Mode) => void
  onCancel: () => void
}) {
  const { missing, outdated } = todo(items)
  const phase = run?.progress?.phase
  const downloading = run !== null && (phase === undefined || phase === 'download' || phase === 'verify')
  const overall = run?.progress && phase === 'download' && run.progress.allBytes > 0 ? run.progress.doneBytes / run.progress.allBytes : null
  const action = missing.length > 0 && outdated.length > 0 ? 'Install and update' : missing.length > 0 ? 'Install' : 'Update'
  if (run) {
    return (
      <div className="exo-runtimes-head">
        <div className="exo-runtimes-run">
          <span className="exo-runtimes-status">{working(run.progress, run.mode)}</span>
          <Progress value={overall} label={working(run.progress, run.mode)} />
        </div>
        <div className="exo-runtimes-actions">
          {downloading ? <button type="button" className="exo-set-btn" disabled={canceling} onClick={onCancel}>Cancel</button> : null}
        </div>
      </div>
    )
  }
  return (
    <div className="exo-runtimes-head">
      <Status items={items} note={note} />
      <div className="exo-runtimes-actions">
        {missing.length + outdated.length > 0 ? <button type="button" className="exo-set-btn is-primary" onClick={() => onStart('update')}>{action}</button> : null}
        <button type="button" className="exo-set-btn" onClick={() => onStart('repair')}>Repair all</button>
      </div>
    </div>
  )
}

/**
 * Where the runtimes stand. A run that went well says so for a few seconds, then the line is what's left to do again
 * (a "7 installed" that stayed read as all done beside an Install button); one that failed stays, with Copy logs and
 * what's left beside it.
 */
function Status({ items, note }: { items: RuntimeItem[]; note: { text: string; error: boolean } | null }) {
  const [shown, setShown] = useState(note)
  useEffect(() => {
    setShown(note)
    if (!note || note.error) return
    const timer = window.setTimeout(() => setShown(null), 6000)
    return () => window.clearTimeout(timer)
  }, [note])
  if (!shown?.error) return <span className="exo-runtimes-status" role="status">{shown?.text ?? status(items)}</span>
  const left = todo(items)
  return (
    <span className="exo-runtimes-status is-bad" role="status">
      {shown.text}{left.missing.length + left.outdated.length > 0 ? ` ${status(items)}.` : ''} <CopyLogs error={shown.text} />
    </span>
  )
}

/**
 * A runtime's versions, each fact once: one that's current says its version, an older one its version and the newest,
 * and a missing one nothing its state doesn't say already. Microsoft's end of support always shows. Of the host's info
 * lines only Direct3D 12's is a reading (what this PC's graphics does); the others restate the state beside them.
 */
function versionsOf(item: RuntimeItem) {
  if (item.state === 'info') return (item.id === 'd3d12' ? item.detail : item.installed) ?? ''
  const version = item.state === 'outdated' ? [item.installed, `latest ${item.latest}`].filter(Boolean).join(' · ') : item.state === 'current' ? item.installed : null
  return [version, item.endOfSupport ? 'End of support' : null].filter(Boolean).join(' · ')
}

function RuntimeRow({ item, result, progress }: { item: RuntimeItem; result: RuntimeResult | undefined; progress: RuntimesProgress | null }) {
  const label = progress ? working(progress, 'update') : ''
  return (
    <Row label={item.name} detail={versionsOf(item) || undefined} error={result?.outcome === 'failed' ? result.message : null}>
      {progress?.phase === 'download' ? (
        <Progress size="small" value={progress.total ? progress.bytes / progress.total : null} label={label} />
      ) : progress ? (
        <Progress size="small" value={null} label={label} />
      ) : result ? (
        <State on={result.outcome !== 'failed'}>{OUTCOME[result.outcome]}</State>
      ) : item.state === 'info' ? (
        <State>{item.latest}</State>
      ) : (
        <State on={item.state === 'current'}>{item.state === 'missing' && !item.bulk ? 'When a game asks' : STATE[item.state]}</State>
      )}
    </Row>
  )
}

