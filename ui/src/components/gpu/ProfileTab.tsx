import { useCallback, useEffect, useState } from 'react'
import { host, type GpuImportAnswer, type GpuProfile } from '../../lib/host'
import { report } from '../../lib/report'
import { trimStop } from '../../lib/utils'
import { Choice } from '../settings/Choice'
import { DriverRow } from './DriverRow'
import { DriverProfile, ProfileButtons, type LatencyChoice } from './DriverProfile'
import { Row, State } from '../settings/parts'
import { ConfirmDialog } from '../ui/Dialog'
import { Spinner } from '../ui/Spinner'

type Preview = NonNullable<GpuImportAnswer['preview']>

const SYNC = [{ value: 'gsync', label: 'G-SYNC' }, { value: 'fixed', label: 'No G-SYNC' }] as const

/**
 * PC › Graphics › Driver and profile: Exo's global driver profile for this PC's GeForce series, in
 * the variant the user picks (G-SYNC or No G-SYNC, preselected from what the
 * driver says the main screen can do), one tap to use it and Undo. The driver
 * is read again when Exo's window comes back, so a driver update that moved a
 * setting shows with Re-apply. A Profile Inspector export can be imported to
 * the global profile instead, previewed before anything is written. A Radeon and an Intel GPU get their
 * own profile through their driver; one Exo can't reach shows what to set by hand. The driver row (NVIDIA's,
 * AMD's or Intel's newest driver for the card) leads on every vendor's page.
 */
export function ProfileTab({ active }: { active: boolean }) {
  const [profile, setProfile] = useState<GpuProfile | null>(null)
  const [choice, setChoice] = useState<boolean | null>(null)
  const [busy, setBusy] = useState<'apply' | 'undo' | null>(null)
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

  const act = async (action: 'apply' | 'undo', gsync?: boolean, latency?: LatencyChoice) => {
    if (busy) return
    setBusy(action)
    setError(null)
    try {
      const next = await host.gpuProfile(action, gsync, latency)
      setProfile(next)
      setChoice(null)
      if (!next.ok) setError(trimStop(next.message) || 'Not changed')
    } catch (e) {
      report(e, `ProfileTab.${action}`)
      setError('Not changed')
    } finally {
      setBusy(null)
    }
  }

  if (!profile) return null
  // The driver row leads on every vendor's page: NVIDIA's, AMD's or Intel's newest driver for the card.
  const driver = <DriverRow active={active} />
  if (profile.status === 'none' && profile.ok) return <>{driver}<p className="exo-hub-sub">This PC has no graphics card Exo can tune.</p></>

  const nvidia = profile.vendor === 'nvidia'
  const { screen } = profile
  const series = profile.series ? `${profile.series} series` : null
  const gsync = choice ?? profile.gsync
  const head = [series, screenLine(profile)].filter(Boolean).join(' · ')
  if ((profile.vendor === 'amd' || profile.vendor === 'intel') && profile.status !== 'manual') {
    return <>{driver}<DriverProfile active={active} profile={profile} head={head} busy={busy} error={error} onAct={(action, latency) => void act(action, undefined, latency)} /></>
  }
  if (!nvidia) return <>{driver}<ManualProfile profile={profile} head={head} /></>

  return (
    <>
      {driver}
      {/* The profile's sync (G-SYNC or not) sits beside its Use, as the variant it applies; whether it is applied leads
          the line under its name. */}
      <Row label={profile.gpu || 'Graphics card'} detail={<>{profileStatus(profile, gsync)}{head}. {gsync
        ? `G-SYNC, V-Sync in the driver, frames capped just under the refresh rate: turn V-Sync off in games${screen.vrr ? '' : ' (this screen reports no variable refresh)'}`
        : 'Fixed refresh: V-Sync and frame caps stay each game’s'}</>}
        error={error ?? (profile.ok ? null : trimStop(profile.message) || 'The driver did not answer')}>
        <Choice label="Sync" value={gsync ? 'gsync' : 'fixed'} choices={SYNC} disabled={!!busy} onChange={value => setChoice(value === 'gsync')} />
        <ProfileAction profile={profile} gsync={gsync} busy={busy} onAct={action => void act(action, gsync)} />
      </Row>
      <ImportRow />
    </>
  )
}

/** The main screen, its refresh rate, and its variable refresh as the driver reports it. */
function screenLine({ vendor, screen }: GpuProfile) {
  const sync = vendor === 'nvidia' ? (screen.vrr ? 'reports G-SYNC' : 'reports no variable refresh')
    : screen.vrr ? (vendor === 'amd' ? 'FreeSync' : vendor === 'intel' ? 'Adaptive Sync' : null) : null
  return [`${screen.name || 'Main screen'}${screen.hz ? ` at ${screen.hz} Hz` : ''}`, sync].filter(Boolean).join(', ')
}

/** A Radeon or an Intel GPU Exo can't reach: what to set by hand, next to what their driver holds. */
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

/** Where Exo's profile stands, ahead of the row's line: Applied, or what changed since. */
function profileStatus(profile: GpuProfile, gsync: boolean) {
  if (gsync !== profile.gsync) return null
  if (profile.status === 'applied') return <><State on>Applied</State> · </>
  if (profile.status === 'changed') return <><State>{profile.driverUpdated ? 'Driver changed some settings' : 'Some settings changed'}</State> · </>
  return null
}

/** Use (or switch to) the picked variant, Re-apply what changed since, and Undo while Exo's backup is there. */
function ProfileAction({ profile, gsync, busy, onAct }: { profile: GpuProfile; gsync: boolean; busy: 'apply' | 'undo' | null; onAct: (action: 'apply' | 'undo') => void }) {
  const apply = profile.status === 'ready' || gsync !== profile.gsync ? 'Use Exo’s profile' : profile.status === 'changed' ? 'Re-apply' : null
  if (!apply && profile.status !== 'applied') return null
  return <ProfileButtons apply={apply} undo={!!profile.canUndo} busy={busy} onAct={onAct} />
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
        {/* Undo import carries the ring while it puts the profile back; Import… while the file is read and, after Apply, written. */}
        {undo && (
          <button type="button" className="exo-set-btn" disabled={!!busy} aria-busy={busy === 'undo'} onClick={() => void run('undo')}>
            {busy === 'undo' ? <Spinner label="Putting back the settings the import replaced" /> : null}Undo import
          </button>
        )}
        <button type="button" className="exo-set-btn" disabled={!!preview || !!busy} aria-busy={busy === 'pick' || busy === 'apply'} onClick={() => void run('pick')}>
          {busy === 'pick' || busy === 'apply'
            ? <Spinner label={busy === 'pick' ? 'Reading the profile file' : 'Writing the imported profile to the driver'} />
            : null}
          Import…
        </button>
      </Row>
      {preview && (
        <ImportPreview preview={preview} busy={!!busy}
          onCancel={cancel} onApply={() => { setPreview(null); void run('apply') }} />
      )}
    </>
  )
}

/** What the file would change, in plain words, before anything is written. */
function ImportPreview({ preview, busy, onCancel, onApply }: {
  preview: Preview
  busy: boolean
  onCancel: () => void
  onApply: () => void
}) {
  const extra = [
    preview.unknown > 0 ? `${preview.unknown} more Exo has no name for` : null,
    preview.leftOut > 0 ? `${preview.leftOut} left out` : null,
  ].filter(Boolean).join(', ')
  return (
    <ConfirmDialog title={`Apply ${preview.profile ? `“${preview.profile}”` : 'this profile'} to every game?`} action="Apply" busy={busy} form
      onConfirm={onApply} onClose={onCancel}>
      <ul className="exo-graphics-import-list">
        {preview.items.map((item, index) => (
          <li key={index}><span>{item.label}</span><span className="exo-graphics-import-value">{item.value}</span></li>
        ))}
      </ul>
      {extra && <p className="exo-graphics-import-extra">{extra}</p>}
      <p>Undo import puts back what it replaces.</p>
    </ConfirmDialog>
  )
}
