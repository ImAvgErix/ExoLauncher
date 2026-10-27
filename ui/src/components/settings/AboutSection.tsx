import { memo, useRef, useState } from 'react'
import { host } from '../../lib/host'
import { invalidateUpscalerStatus } from '../../lib/upscalerCache'
import { WHATS_NEW } from '../../lib/whatsNew'
import { Progress } from '../ui/Progress'
import { Spinner } from '../ui/Spinner'
import { Row, Section, State } from './parts'

/** What's new opens the sheet of this version's notes (LauncherApp); the rest open in the browser. */
const LINKS = [
  { label: 'Report a problem', url: 'https://github.com/UhhErix/ExoLauncher/issues' },
  { label: "What's new", url: null },
  { label: 'Privacy', url: 'https://github.com/UhhErix/ExoLauncher/blob/main/PRIVACY.md' },
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
      {/* The update going in is a transfer: its words and its bar in the row, where the button was (never a bar in a button). */}
      {updateBusy ? (
        <>
          <State>{updateStatus}</State>
          <Progress size="small" value={updatePercent == null ? null : updatePercent / 100} label={updateStatus} />
        </>
      ) : updateAvailable ? (
        <button type="button" className="exo-set-btn is-primary" onClick={install}>Install update</button>
      ) : (
        <button type="button" className="exo-set-btn" disabled={checking} onClick={() => void check()}>
          {checking ? <Spinner label="Checking for updates" /> : null}
          Check for updates
        </button>
      )}
    </Row>
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
        {busy ? <Spinner label="Clearing caches" /> : null}
        Clear
      </button>
    </Row>
  )
}

export const AboutSection = memo(function AboutSection(props: {
  version: string | null
  updateBusy: boolean
  updateError: string | null
  updatePercent: number | null
  updateStatus: string
  updateAvailable: boolean
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
      <RebuildCachesRow />
      <div className="exo-set-item">
        <ul className="exo-set-links">
          {LINKS.map(link => (
            <li key={link.label}>
              <button type="button" className="exo-set-link" onClick={() => link.url ? void host.openUrl(link.url) : window.dispatchEvent(new Event(WHATS_NEW))}>{link.label}</button>
            </li>
          ))}
        </ul>
      </div>
    </Section>
  )
})
