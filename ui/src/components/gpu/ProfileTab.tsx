import { useCallback, useEffect, useId, useRef, useState } from 'react'
import { host, type GpuImportAnswer, type GpuProfile } from '../../lib/host'
import { report } from '../../lib/report'
import { trimStop } from '../../lib/utils'
import { Choice } from '../settings/Choice'
import { DriverRow } from './DriverRow'
import { Row, State } from '../settings/parts'

type Preview = NonNullable<GpuImportAnswer['preview']>

const SYNC = [{ value: 'gsync', label: 'G-SYNC' }, { value: 'fixed', label: 'No G-SYNC' }] as const

/**
 * System > Graphics > Driver and profile: Exo's global driver profile for this PC's GeForce series, in
 * the variant the user picks (G-SYNC or No G-SYNC, preselected from what the
 * driver says the main screen can do), one tap to use it and Undo. The driver
 * is read again when Exo's window comes back, so a driver update that moved a
 * setting shows with Re-apply. A Profile Inspector export can be imported to
 * the global profile instead, previewed before anything is written. AMD and
 * Intel show what to set: Exo reads their drivers but does not write them.
 */
export function ProfileTab({ active }: { active: boolean }) {
  const [profile, setProfile] = useState<GpuProfile | null>(null)
  const [choice, setChoice] = useState<boolean | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(async () => {
    try { setProfile(await host.gpuProfile()) } catch (e) { report(e, 'ProfileTab.state') }
  }, [])

  useEffect(() => {
    void load()
    if (!active) return
    const onFocus = () => { void load() }
    window.addEventListener('focus', onFocus)
    return () => window.removeEventListener('focus', onFocus)
  }, [load, active])

  const act = async (action: 'apply' | 'undo', gsync?: boolean) => {
    if (busy) return
    setBusy(true)
    setError(null)
    try {
      const next = await host.gpuProfile(action, gsync)
      setProfile(next)
      setChoice(null)
      if (!next.ok) setError(trimStop(next.message) || 'Not changed')
    } catch (e) {
      report(e, `ProfileTab.${action}`)
      setError('Not changed')
    } finally {
      setBusy(false)
    }
  }

  if (!profile) return null
  if (profile.status === 'none' && profile.ok) return <p className="exo-hub-sub">This PC has no graphics card Exo can tune.</p>

  const nvidia = profile.vendor === 'nvidia'
  const { screen } = profile
  const screenLine = [
    `${screen.name || 'Main screen'}${screen.hz ? ` at ${screen.hz} Hz` : ''}`,
    nvidia ? (screen.vrr ? 'reports G-SYNC' : 'reports no variable refresh') : null,
  ].filter(Boolean).join(', ')
  const series = profile.series ? `${profile.series} series` : null
  const gsync = choice ?? profile.gsync
  const head = [series, screenLine].filter(Boolean).join(' · ')
  if (!nvidia) return <ManualProfile profile={profile} head={head} />

  return (
    <>
      <DriverRow active={active} />
      <Row label={profile.gpu || 'Graphics card'} detail={head} error={error ?? (profile.ok ? null : trimStop(profile.message) || 'The driver did not answer')}>
        {busy ? <State>Working…</State> : <ProfileAction profile={profile} gsync={gsync} onAct={action => void act(action, gsync)} />}
      </Row>
      <Row
        label="Sync"
        detail={gsync
          ? `G-SYNC full screen and windowed, V-Sync on in the driver, frames capped just under the refresh rate. Turn V-Sync off in games.${screen.vrr ? '' : ' Your screen does not report variable refresh.'}`
          : 'Fixed refresh: variable refresh off, and V-Sync and frame caps stay each game’s.'}
      >
        <Choice label="Sync" value={gsync ? 'gsync' : 'fixed'} choices={SYNC} disabled={busy} onChange={value => setChoice(value === 'gsync')} />
      </Row>
      <ImportRow />
    </>
  )
}

/** AMD and Intel: what to set by hand, next to what their driver holds. */
function ManualProfile({ profile, head }: { profile: GpuProfile; head: string }) {
  const same = (a: string, b: string) => a.toLowerCase() === b.toLowerCase()
  return (
    <>
      <Row label={profile.gpu || 'Graphics card'} detail={head} />
      {profile.message && <p className="exo-hub-sub">{profile.message}</p>}
      {profile.items.map(item => (
        <Row key={item.key} label={item.label} detail={item.why}>
          <State on={!!item.current && same(item.current, item.value)}>
            {item.value}{item.current && !same(item.current, item.value) ? ` (now ${item.current})` : ''}
          </State>
        </Row>
      ))}
    </>
  )
}

/** Use (or switch to) the picked variant, Applied or the change since, and Undo while Exo's backup is there. */
function ProfileAction({ profile, gsync, onAct }: { profile: GpuProfile; gsync: boolean; onAct: (action: 'apply' | 'undo') => void }) {
  const undo = profile.canUndo && <button type="button" className="exo-set-btn" onClick={() => onAct('undo')}>Undo</button>
  const use = <button type="button" className="exo-set-btn is-primary" onClick={() => onAct('apply')}>Use Exo&rsquo;s profile</button>
  if (profile.status === 'ready' || gsync !== profile.gsync) return <>{use}{undo}</>
  if (profile.status === 'applied') return <><State on>Applied</State>{undo}</>
  if (profile.status === 'changed') {
    return (
      <>
        <State>{profile.driverUpdated ? 'Driver changed some settings' : 'Some settings changed'}</State>
        <button type="button" className="exo-set-btn is-primary" onClick={() => onAct('apply')}>Re-apply</button>
        {undo}
      </>
    )
  }
  return null
}

/** Import profile: a Profile Inspector export of the global profile, read into a list first; nothing is written until Apply, and Undo puts back what it replaced. */
function ImportRow() {
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [preview, setPreview] = useState<Preview | null>(null)
  const [undo, setUndo] = useState<GpuImportAnswer['undo']>(null)

  useEffect(() => {
    let live = true
    host.gpuImport('state').then(answer => { if (live) setUndo(answer.undo ?? null) }, () => undefined)
    return () => { live = false }
  }, [])

  const run = async (what: 'pick' | 'apply' | 'undo') => {
    if (busy) return
    setBusy(what)
    setError(null)
    try {
      const answer = await host.gpuImport(what)
      setUndo(answer.undo ?? null)
      if (answer.ok && answer.preview) setPreview(answer.preview)
      if (!answer.ok && !answer.cancelled) setError(trimStop(answer.message) || 'Not changed')
    } catch (e) {
      report(e, `ProfileTab.import.${what}`)
      setError('Not changed')
    } finally {
      setBusy(null)
    }
  }
  const cancel = () => { setPreview(null); void host.gpuImport('cancel').catch(() => undefined) }

  return (
    <>
      <Row
        label="Profile Inspector file"
        detail={undo ? `Imported ${undo.profile ? `“${undo.profile}”` : 'the global profile'}, ${undo.count} settings` : 'Import a global profile export (.nip). You see every setting before anything changes.'}
        error={error}
      >
        {busy === 'pick' ? <State>Opening…</State> : busy ? <State>Saving…</State> : (
          <>
            {undo && <button type="button" className="exo-set-btn" onClick={() => void run('undo')}>Undo import</button>}
            <button type="button" className="exo-set-btn" disabled={!!preview} onClick={() => void run('pick')}>Import…</button>
          </>
        )}
      </Row>
      {preview && (
        <ImportPreview preview={preview} busy={!!busy}
          onCancel={cancel} onApply={() => { setPreview(null); void run('apply') }} />
      )}
    </>
  )
}

/** What the file would change, in plain words, before anything is written. Cancel takes focus. */
function ImportPreview({ preview, busy, onCancel, onApply }: {
  preview: Preview
  busy: boolean
  onCancel: () => void
  onApply: () => void
}) {
  const headingId = useId()
  const cancel = useRef<HTMLButtonElement>(null)
  useEffect(() => { cancel.current?.focus() }, [])
  const extra = [
    preview.unknown > 0 ? `${preview.unknown} more Exo has no name for` : null,
    preview.leftOut > 0 ? `${preview.leftOut} left out` : null,
  ].filter(Boolean).join(', ')
  return (
    <div className="exo-graphics-import" role="group" aria-labelledby={headingId}>
      <p id={headingId} className="exo-graphics-import-title">
        Apply {preview.profile ? `“${preview.profile}”` : 'this profile'} to every game?
      </p>
      <ul className="exo-graphics-import-list">
        {preview.items.map((item, index) => (
          <li key={index}><span>{item.label}</span><span className="exo-graphics-import-value">{item.value}</span></li>
        ))}
      </ul>
      {extra && <p className="exo-graphics-import-extra">{extra}</p>}
      <span className="exo-graphics-import-actions">
        <button ref={cancel} type="button" className="exo-set-btn" onClick={onCancel}>Cancel</button>
        <button type="button" className="exo-set-btn is-primary" disabled={busy} onClick={onApply}>Apply</button>
      </span>
    </div>
  )
}
