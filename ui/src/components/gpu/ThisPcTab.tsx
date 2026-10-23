import { useCallback, useEffect, useState } from 'react'
import { host, type PcHealth, type PcHealthItem } from '../../lib/host'
import { report } from '../../lib/report'
import { trimStop } from '../../lib/utils'
import { Row, State } from '../settings/parts'

type HealthAction = 'apply' | 'undo' | 'chipset' | 'gamemode'
const ROW: Record<HealthAction, PcHealthItem['key']> = { apply: 'power', undo: 'power', chipset: 'chipset', gamemode: 'gamemode' }

/**
 * System > This PC: a row only for what is wrong with the PC's setup (RAM below its rated speed, Resizable BAR off,
 * a power plan that isn't Exo's, an old AMD or Intel chipset driver, Game Mode off), else one quiet line. Exo's power plan, tuned
 * for this PC's processor, is switched to when you press Use Exo's plan, with Undo; the chipset driver Exo updates by itself while
 * you're away, or now with Update (read every few seconds while it installs); the rest are for the BIOS or Windows.
 * Read again when Exo's window comes back, so Game Mode turned on in Settings goes away.
 */
export function ThisPcTab({ active }: { active: boolean }) {
  const [health, setHealth] = useState<PcHealth | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<{ key: PcHealthItem['key']; text: string } | null>(null)

  const load = useCallback(async () => {
    try { setHealth(await host.systemHealth()) } catch (e) { report(e, 'ThisPcTab.state') }
  }, [])

  useEffect(() => {
    void load()
    if (!active) return
    const onFocus = () => { void load() }
    window.addEventListener('focus', onFocus)
    return () => window.removeEventListener('focus', onFocus)
  }, [load, active])

  const installing = health?.items.some(item => item.busy) === true
  useEffect(() => {
    if (!active || !installing) return
    const timer = window.setInterval(() => { void load() }, 5000)
    return () => window.clearInterval(timer)
  }, [load, active, installing])

  const act = async (action: HealthAction) => {
    if (busy) return
    setBusy(true)
    setError(null)
    try {
      const next = await host.systemHealth(action)
      setHealth(next)
      if (!next.ok) setError({ key: ROW[action], text: trimStop(next.message) || 'Not changed' })
    } catch (e) {
      report(e, `ThisPcTab.${action}`)
      setError({ key: ROW[action], text: 'Not changed' })
    } finally {
      setBusy(false)
    }
  }

  if (!health) return null
  if (health.items.length === 0) return <p className="exo-hub-sub">All good</p>
  return (
    <>
      {health.items.map(item => (
        <Row key={item.key} label={item.label} detail={item.detail} error={error?.key === item.key ? error.text : null}>
          <Action item={item} busy={busy} onAct={action => void act(action)} />
        </Row>
      ))}
    </>
  )
}

/** What a row's button does: switch to Exo's power plan or undo it, update the chipset driver (its maker's page after a failed install), open Game Mode's settings. RAM and Resizable BAR are set in the BIOS. */
function Action({ item, busy, onAct }: { item: PcHealthItem; busy: boolean; onAct: (action: HealthAction) => void }) {
  if (item.key === 'power') {
    if (busy) return <State>Working…</State>
    return item.applied
      ? item.canUndo ? <button type="button" className="exo-set-btn" onClick={() => onAct('undo')}>Undo</button> : null
      : <button type="button" className="exo-set-btn is-primary" onClick={() => onAct('apply')}>Use Exo&rsquo;s plan for {item.cpu ?? 'this PC'}</button>
  }
  if (item.key === 'chipset') {
    if (item.current) return <State on>Up to date</State>
    if (item.busy) return <State>Updating…</State>
    if (item.url) {
      const url = item.url
      return <button type="button" className="exo-set-btn" onClick={() => void host.openUrl(url)}>Update</button>
    }
    // Applied: installed, waiting for a restart (Exo never restarts the PC).
    return item.applied ? null : <button type="button" className="exo-set-btn" disabled={busy} onClick={() => onAct('chipset')}>Update</button>
  }
  if (item.key === 'gamemode') return <button type="button" className="exo-set-btn" disabled={busy} onClick={() => onAct('gamemode')}>Open Settings</button>
  return null
}
