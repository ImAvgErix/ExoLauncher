import { useCallback, useEffect, useState } from 'react'
import { host, type InterruptDevice, type Interrupts } from '../../lib/host'
import { report } from '../../lib/report'
import { trimStop } from '../../lib/utils'
import { Row, State } from '../settings/parts'
import { Spinner } from '../ui/Spinner'

const MODE: Record<InterruptDevice['mode'], string> = { msi: 'MSI mode', line: 'Shared line', none: 'Not running' }

/**
 * PC › Performance › Interrupts: one row that says whether every device that can use MSI mode does, with Optimize
 * interrupts (one permission prompt, then a restart Exo never does itself), Undo all, and after the restart what took.
 * Devices lists each one, its interrupt now and what Exo does to it, with Revert on the ones Exo switched.
 */
export function InterruptRows({ active }: { active: boolean }) {
  const [state, setState] = useState<Interrupts | null>(null)
  // What is being written, while it is: Optimize, Undo all, or one device's Revert (its id).
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [open, setOpen] = useState(false)

  const load = useCallback(async () => {
    try { setState(await host.systemInterrupts()) } catch (e) { report(e, 'InterruptRows.state') }
  }, [])

  useEffect(() => { if (active) void load() }, [load, active])

  const act = async (action: 'apply' | 'undo' | 'revert', device?: string) => {
    if (busy) return
    setBusy(device ?? action)
    setError(null)
    try {
      const next = await host.systemInterrupts(action, device)
      setState(next)
      if (!next.ok) setError(trimStop(next.message) || 'Not changed')
    } catch (e) {
      report(e, `InterruptRows.${action}`)
      setError('Not changed')
    } finally {
      setBusy(null)
    }
  }

  if (!state || state.devices.length === 0) return null
  const waiting = state.devices.filter(d => d.plan === 'change').length
  // A device on MSI that Optimize still changes takes high priority (the graphics card and USB controllers).
  const sharing = state.devices.filter(d => d.plan === 'change' && d.mode !== 'msi').length
  const detail = state.restart
    ? 'Windows switches them when the PC restarts'
    : state.result
      || (sharing > 0
        ? `${sharing === 1 ? 'A device shares' : `${sharing} devices share`} an interrupt line. MSI mode lets each signal the processor on its own, so none waits on another.`
        : waiting > 0
          ? 'Your graphics card and USB controllers can take high interrupt priority, so a frame or an input never waits behind another device.'
          : 'Every device that can use MSI mode does, and your graphics card and USB controllers go first')
  return (
    <>
      <Row label="Interrupts (MSI mode)" detail={detail} error={error}>
        {state.restart ? <State on>Restart to finish</State> : null}
        {waiting > 0 && !state.restart
          ? <button type="button" className="exo-set-btn is-primary" disabled={!!busy} aria-busy={busy === 'apply'} onClick={() => void act('apply')}>
            {busy === 'apply' ? <Spinner label="Switching devices to MSI mode" /> : null}Optimize interrupts
          </button>
          : null}
        {state.canUndo
          ? <button type="button" className="exo-set-btn" disabled={!!busy} aria-busy={busy === 'undo'} onClick={() => void act('undo')}>
            {busy === 'undo' ? <Spinner label="Putting every device's interrupt back" /> : null}Undo all
          </button>
          : null}
        {waiting === 0 && !state.restart && !state.canUndo ? <State on>Optimized</State> : null}
        <button type="button" className="exo-set-btn" aria-expanded={open} onClick={() => setOpen(!open)}>{open ? 'Hide devices' : 'Devices'}</button>
      </Row>
      {open && state.devices.map(device => (
        <Row key={device.id} label={device.name} detail={`${device.kind[0].toUpperCase()}${device.kind.slice(1)}. ${device.reason}`}>
          {device.changed
            ? <button type="button" className="exo-set-btn" disabled={!!busy} aria-busy={busy === device.id} onClick={() => void act('revert', device.id)}>
              {busy === device.id ? <Spinner label={`Putting ${device.name}'s interrupt back`} /> : null}Revert
            </button>
            : <State on={device.mode === 'msi'}>{MODE[device.mode]}</State>}
        </Row>
      ))}
    </>
  )
}
