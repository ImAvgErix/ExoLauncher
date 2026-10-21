import { memo, useRef, useState, type CSSProperties } from 'react'
import { host, type DependencyItem } from '../../lib/host'
import { invalidateUpscalerStatus } from '../../lib/upscalerCache'
import { formatTransferPercent } from '../../lib/utils'
import { Row, Section, State } from './parts'

const LINKS = [
  { label: 'Report a problem', url: 'https://github.com/ImAvgErix/ExoLauncher/issues' },
  { label: 'Release notes', url: 'https://github.com/ImAvgErix/ExoLauncher/releases/latest' },
  { label: 'Privacy', url: 'https://github.com/ImAvgErix/ExoLauncher/blob/main/PRIVACY.md' },
  { label: 'Support Exo', url: 'https://www.buymeacoffee.com/UhhErix' },
] as const

type CheckUpdate = () => Promise<{ ok: boolean; message?: string; updateAvailable?: boolean; latest?: string | null }>

function UpdateRow({ version, updateBusy, updateError, updatePercent, updateStatus, updateAvailable, onCheckUpdate, onInstallUpdate }: {
  version: string | null
  updateBusy: boolean
  updateError: string | null
  updatePercent: number | null
  updateStatus: string
  updateAvailable: boolean
  onCheckUpdate: CheckUpdate
  onInstallUpdate: () => Promise<void> | void
}) {
  const [checking, setChecking] = useState(false)
  const [note, setNote] = useState<{ text: string; error: boolean } | null>(null)

  async function check() {
    setChecking(true)
    setNote(null)
    try {
      const result = await onCheckUpdate()
      setNote({
        text: result.message || (result.updateAvailable ? `Version ${result.latest ?? ''} is available.` : "You're up to date."),
        error: !result.ok,
      })
    } catch (error) {
      setNote({ text: error instanceof Error ? error.message : 'Could not check for updates.', error: true })
    } finally {
      setChecking(false)
    }
  }

  // A failed install comes back from the updater (updateError), never as a rejection.
  function install() {
    setNote(null)
    void onInstallUpdate()
  }

  return (
    <Row
      label="Exo Launcher"
      detail={note && !note.error ? note.text : version ? `Version ${version}` : undefined}
      error={note?.error ? note.text : updateBusy ? null : updateError}
    >
      {updateAvailable || updateBusy ? (
        <button type="button" className="exo-set-btn is-primary exo-set-progress" disabled={updateBusy} onClick={install}
          style={{ '--progress': (updatePercent ?? 0) / 100 } as CSSProperties}>
          <span>{updateBusy ? `${updateStatus}${updatePercent == null ? '' : ` ${formatTransferPercent(updatePercent)}%`}` : 'Install update'}</span>
        </button>
      ) : (
        <button type="button" className="exo-set-btn" disabled={checking} onClick={() => void check()}>
          {checking ? 'Checking…' : 'Check for updates'}
        </button>
      )}
    </Row>
  )
}

function RuntimeRows({ deps, depsNote, depsError, depBusy, onRecheck, onGetDependency }: {
  deps: DependencyItem[] | null
  depsNote: string | null
  depsError: string | null
  depBusy: string | null
  onRecheck: () => void
  onGetDependency: (item: DependencyItem) => Promise<void>
}) {
  const missing = (deps ?? []).filter(item => item.status.toLowerCase() !== 'present')
  const allPresent = deps !== null && deps.length > 0 && missing.length === 0
  return (
    <>
      <Row label="Game runtimes" detail={depsNote ?? 'DirectX, Visual C++ and WebView2'} error={depsError}>
        <State on={allPresent}>
          {deps === null ? 'Checking…' : allPresent ? 'All installed' : deps.length === 0 ? 'Not checked' : `${missing.length} missing`}
        </State>
        {deps !== null && !allPresent ? (
          <button type="button" className="exo-set-btn" disabled={depBusy !== null} onClick={onRecheck}>Check again</button>
        ) : null}
      </Row>
      {missing.map(item => {
        const busy = depBusy === item.id
        const installable = item.status.toLowerCase() === 'missing' && item.canOfferInstall
        return (
          <Row key={item.id} label={item.name}>
            <State>{busy ? 'Installing…' : item.status.toLowerCase() === 'missing' ? 'Missing' : "Couldn't check"}</State>
            {installable ? (
              <button type="button" className="exo-set-btn" disabled={depBusy !== null && !busy} onClick={() => void onGetDependency(item)}>
                {busy ? 'Cancel' : 'Install'}
              </button>
            ) : null}
          </Row>
        )
      })}
    </>
  )
}

function RebuildCachesRow() {
  const [busy, setBusy] = useState(false)
  const [note, setNote] = useState<{ text: string; error: boolean } | null>(null)
  const pending = useRef(false)
  async function rebuild() {
    if (pending.current) return
    pending.current = true
    setBusy(true)
    setNote(null)
    try {
      for (const scope of ['sizes', 'upscalers'] as const) {
        const result = await host.repairCache(scope)
        if (!result.ok) throw Error(result.message || 'Caches could not be cleared. Try again.')
        if (scope === 'upscalers') invalidateUpscalerStatus()      }
      setNote({ text: 'Caches cleared.', error: false })
    } catch (error) {
      setNote({ text: error instanceof Error ? error.message : 'Caches could not be cleared. Try again.', error: true })
    } finally {
      pending.current = false
      setBusy(false)
    }
  }
  return (
    <Row
      label="Clear caches"
      detail={note && !note.error ? note.text : 'Fixes wrong game sizes or upscaler versions.'}
      error={note?.error ? note.text : null}
    >
      <button type="button" className="exo-set-btn" disabled={busy} onClick={() => void rebuild()}>
        {busy ? 'Clearing…' : 'Clear'}
      </button>
    </Row>
  )
}

export const AboutSection = memo(function AboutSection(props: {
  version: string | null
  deps: DependencyItem[] | null
  depsNote: string | null
  depsError: string | null
  depBusy: string | null
  updateBusy: boolean
  updateError: string | null
  updatePercent: number | null
  updateStatus: string
  updateAvailable: boolean
  onRecheck: () => void
  onGetDependency: (item: DependencyItem) => Promise<void>
  onCheckUpdate: CheckUpdate
  onInstallUpdate: () => Promise<void> | void
}) {
  return (
    <Section id="about" title="About">
      <UpdateRow
        version={props.version}
        updateBusy={props.updateBusy}
        updateError={props.updateError}
        updatePercent={props.updatePercent}
        updateStatus={props.updateStatus}
        updateAvailable={props.updateAvailable}
        onCheckUpdate={props.onCheckUpdate}
        onInstallUpdate={props.onInstallUpdate}
      />
      <RuntimeRows
        deps={props.deps}
        depsNote={props.depsNote}
        depsError={props.depsError}
        depBusy={props.depBusy}
        onRecheck={props.onRecheck}
        onGetDependency={props.onGetDependency}
      />
      <RebuildCachesRow />
      <div className="exo-set-item">
        <ul className="exo-set-links">
          {LINKS.map(link => (
            <li key={link.url}>
              <button type="button" className="exo-set-link" onClick={() => void host.openUrl(link.url)}>{link.label}</button>
            </li>
          ))}
        </ul>
      </div>
    </Section>
  )
})
