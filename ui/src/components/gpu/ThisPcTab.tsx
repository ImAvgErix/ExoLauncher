import { useCallback, useEffect, useState } from 'react'
import { host, type PcHealth, type PcHealthItem } from '../../lib/host'
import { report } from '../../lib/report'
import { trimStop } from '../../lib/utils'
import { Row, State } from '../settings/parts'
import { Spinner } from '../ui/Spinner'

type HealthAction = 'apply' | 'undo' | 'chipset' | 'gamemode' | 'gpusched' | 'gpupref' | 'gpupref-undo'
const ROW: Record<HealthAction, PcHealthItem['key']> = { apply: 'power', undo: 'power', chipset: 'chipset', gamemode: 'gamemode', gpusched: 'gpusched', gpupref: 'gpupref', 'gpupref-undo': 'gpupref' }

/**
 * PC › Performance › Setup: a row only for what is wrong with the PC's setup (RAM below its rated speed, Resizable BAR off,
 * a power plan that isn't Exo's, an old AMD or Intel chipset driver, Game Mode off, GPU scheduling off on a frame generation card), else one quiet line. Exo's power plan, tuned
 * for this PC's processor, is switched to when you press Use Exo's plan, with Undo; the chipset driver Exo updates by itself while
 * you're away, or now with Update (read every few seconds while it installs); the rest are for the BIOS or Windows.
 * Read again when Exo's window comes back, so Game Mode turned on in Settings goes away.
 */
export function ThisPcTab({ active }: { active: boolean }) {
  const [health, setHealth] = useState<PcHealth | null>(null)
  const [busy, setBusy] = useState<HealthAction | null>(null)
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
    setBusy(action)
    setError(null)
    try {
      const next = await host.systemHealth(action)
      setHealth(next)
      if (!next.ok) setError({ key: ROW[action], text: trimStop(next.message) || 'Not changed' })
    } catch (e) {
      report(e, `ThisPcTab.${action}`)
      setError({ key: ROW[action], text: 'Not changed' })
    } finally {
      setBusy(null)
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

/** What each action is doing while it runs, for its bar. */
const WORKING: Record<HealthAction, string> = {
  apply: 'Switching to Exo’s power plan', undo: 'Putting your power plan back', chipset: 'Updating the chipset driver',
  gamemode: 'Opening Settings', gpusched: 'Opening Settings', gpupref: 'Setting games to the fast card', 'gpupref-undo': 'Putting the graphics card choice back',
}

/**
 * What a row's button does: switch to Exo's power plan or undo it, update the chipset driver (its maker's page after a
 * failed install), open Game Mode's settings. RAM and Resizable BAR are set in the BIOS. The one pressed keeps its words
 * and carries the bar while it works; the chipset's Update carries it for as long as the install runs.
 */
function Action({ item, busy, onAct }: { item: PcHealthItem; busy: HealthAction | null; onAct: (action: HealthAction) => void }) {
  const button = (action: HealthAction, words: string, primary = false, working = busy === action) => (
    <button type="button" className={`exo-set-btn${primary ? ' is-primary' : ''}`} disabled={!!busy || working} aria-busy={working}
      onClick={() => onAct(action)}>
      {working ? <Spinner label={WORKING[action]} /> : null}{words}
    </button>
  )
  if (item.key === 'power') {
    return item.applied
      ? item.canUndo ? button('undo', 'Undo') : null
      : button('apply', `Use Exo’s plan for ${item.cpu ?? 'this PC'}`, true)
  }
  if (item.key === 'chipset') {
    if (item.current) return <State on>Up to date</State>
    if (item.busy) return button('chipset', 'Update', false, true)
    if (item.url) {
      const url = item.url
      return <button type="button" className="exo-set-btn" onClick={() => void host.openUrl(url)}>Update</button>
    }
    // Applied: installed, waiting for a restart (Exo never restarts the PC).
    return item.applied ? null : button('chipset', 'Update')
  }
  if (item.key === 'gpupref') {
    return item.applied
      ? item.canUndo ? button('gpupref-undo', 'Undo') : null
      : button('gpupref', 'Use the fast card', true)
  }
  if (item.key === 'gamemode' || item.key === 'gpusched') return button(item.key, 'Open Settings')
  return null
}
