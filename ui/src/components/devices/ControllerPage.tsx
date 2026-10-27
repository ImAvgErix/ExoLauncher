import { useEffect, useRef, useState, type ReactNode } from 'react'
import { host, onHostEvent, type ControllerPolling, type ControllerRead, type ControllerState, type Peripheral, type StickEdge, type StickRest } from '../../lib/host'
import { report } from '../../lib/report'
import { Row, State, SwitchRow } from '../settings/parts'
import { Spinner } from '../ui/Spinner'
import { CalibrationRows } from './Calibration'
import { DeviceColumn, DevicePage, DevicePanel as Panel } from './DevicePage'
import { ProfileRows } from './EdgeProfiles'
import { chipWords, portWords } from './ports'

/** An Edge's profiles by slot, as its Fn buttons name them. */
const PROFILE: Record<number, string> = { 1: 'Default', 2: 'Square', 3: 'Cross', 4: 'Circle' }

/** What the link gives, by model, before measuring: the Edge 1000 a second on USB, a DualSense or DualShock 4 250; Bluetooth about 800. */
function expected(model: string | null, link: Peripheral['connection']) {
  if (model === 'xbox') return 'Windows gives an Xbox controller’s readings as they change: move both sticks while it measures.'
  if (model !== 'dualsense' && model !== 'dualsense-edge' && model !== 'dualshock4') return 'Move a stick while it measures.'
  if (link === 'bluetooth') return 'Bluetooth gives about 700 to 800 a second, with more jitter than a cable.'
  if (model === 'dualsense-edge') return 'An Edge on a cable sends 1000 a second.'
  return `A ${model === 'dualshock4' ? 'DualShock 4' : 'DualSense'} on a cable sends 250 a second. Bluetooth gives about 800.`
}

/**
 * A controller's page (DESIGN.md › Devices): the Controller Lab. The hero holds its picture, its link and charge, a
 * DualSense's firmware and an Edge's active profile. Beside it: its sticks and triggers live and a two-step stick test,
 * and on a DualSense or Edge the controller's own calibration; an Edge's on-board profiles; how it reports (measured);
 * the USB port it's on; and what sits between it and games, with the one switch that lets games read it themselves
 * instead of through Steam Input. What changes a controller does so over USB, never during a game, and can be undone.
 */
export function ControllerPage({ card, name, status, art, visible }: { card: Peripheral; name: string; status: string; art: ReactNode; visible: boolean }) {
  const [read, setRead] = useState<ControllerRead | null>(null)
  const [readAt, setReadAt] = useState(0)
  const live = useLive(card.id, visible)
  useEffect(() => {
    let current = true
    host.controllerRead(card.id).then(next => { if (current) setRead(next) }, (error: unknown) => report(error, 'ControllerPage.read'))
    return () => { current = false }
  }, [card.id, readAt])
  const edge = card.model === 'dualsense-edge'
  const sony = edge || card.model === 'dualsense'
  const usb = card.connection === 'usb'
  const firmware = read?.firmware
  return (
    <DevicePage name={name} status={withCharge(status, card, live)} art={art} stats={[
      { label: 'Firmware', value: firmware?.version ?? null },
      { label: 'Profile', value: edge ? profileName(live?.profile) : null },
    ]}>
      <DeviceColumn>
        <Panel title="Sticks">
          <Sticks id={card.id} live={live} />
          {sony ? <CalibrationRows id={card.id} edge={edge} usb={usb} live={live} state={read?.calibration ?? undefined} onChanged={() => setReadAt(n => n + 1)} /> : null}
        </Panel>
        <Panel title="Triggers"><Triggers live={live} /></Panel>
        {edge ? <Panel title="Profiles"><ProfileRows id={card.id} usb={usb} active={live?.profile} /></Panel> : null}
      </DeviceColumn>
      <DeviceColumn>
        <Panel title="Polling"><Polling id={card.id} hint={expected(card.model, card.connection)} /></Panel>
        {card.port ? <Panel title="USB port"><PortRows port={card.port} /></Panel> : null}
        {firmware?.current === false ? (
          <Panel title="Firmware">
            <Row label={`Firmware ${firmware.latest} is out`} detail="Sony's PlayStation Accessories app updates it. Exo never changes a controller's firmware.">
              <button type="button" className="exo-set-btn" onClick={() => void host.openUrl('https://www.playstation.com/support/hardware/ps5-controller-update/')}>How to update</button>
            </Row>
          </Panel>
        ) : null}
        {read?.layers ? <Panel title="Between it and your games"><LayerRows layers={read.layers} /></Panel> : null}
      </DeviceColumn>
    </DevicePage>
  )
}

/** Windows doesn't know a DualSense's charge; the controller says it in every report. */
function withCharge(status: string, card: Peripheral, live: ControllerState | null) {
  if (card.battery != null || live?.battery == null) return status
  return `${status} · ${live.battery}%${live.charging && live.battery < 100 ? ', charging' : ''}`
}

const profileName = (slot: number | null | undefined) => slot ? PROFILE[slot] ?? `Profile ${slot}` : null

/**
 * The controller's state, at most 60 a second, only while its page is on screen, focused or not (Devices stays
 * mounted behind other rooms). The host stops a live view after ten minutes; it is asked again before that.
 */
function useLive(id: string, visible: boolean) {
  const [live, setLive] = useState<ControllerState | null>(null)
  useEffect(() => {
    if (!visible) return
    const off = onHostEvent('controller.state', state => { if (state.id === id) setLive(state) })
    const watch = () => void host.controllerWatch(id, true).catch((error: unknown) => report(error, 'ControllerPage.watch', { level: 'warn' }))
    watch()
    const again = window.setInterval(watch, 9 * 60_000)
    return () => {
      off()
      window.clearInterval(again)
      void host.controllerWatch(id, false).catch(() => { })
    }
  }, [id, visible])
  return live
}

/** A stick's place as a dot in its circle, 0..255 on each axis. */
function StickView({ x, y, label }: { x: number | undefined; y: number | undefined; label: string }) {
  const cx = x == null ? 50 : 8 + (x / 255) * 84
  const cy = y == null ? 50 : 8 + (y / 255) * 84
  return (
    <svg className="exo-stick" viewBox="0 0 100 100" role="img" aria-label={label}>
      <circle className="exo-stick-gate" cx="50" cy="50" r="42" />
      <line className="exo-stick-axis" x1="8" y1="50" x2="92" y2="50" />
      <line className="exo-stick-axis" x1="50" y1="8" x2="50" y2="92" />
      <circle className="exo-stick-dot" cx={cx} cy={cy} r="5" />
    </svg>
  )
}

type Step = 'idle' | 'rest' | 'edge'
type Result = { rest?: { left: StickRest | null; right: StickRest | null }; edge?: { left: StickEdge | null; right: StickEdge | null } }

/** The stick test: three seconds at rest (center and wander), then six rolled around the edge (reach and roundness). */
function Sticks({ id, live }: { id: string; live: ControllerState | null }) {
  const [step, setStep] = useState<Step>('idle')
  const [result, setResult] = useState<Result>({})
  const [error, setError] = useState<string | null>(null)
  const running = useRef(false)
  async function run() {
    if (running.current) return
    running.current = true
    setError(null)
    setResult({})
    try {
      setStep('rest')
      const rest = await host.controllerTest(id, 'rest', 3)
      if (!rest.ok) throw new Error(rest.message ?? 'The test stopped.')
      setResult({ rest: { left: rest.left as StickRest | null, right: rest.right as StickRest | null } })
      setStep('edge')
      const edge = await host.controllerTest(id, 'edge', 6)
      if (!edge.ok) throw new Error(edge.message ?? 'The test stopped.')
      // A stick the host has no edge for (null) wasn't rolled far enough round to measure.
      setResult(current => ({ ...current, edge: { left: (edge.left ?? null) as StickEdge | null, right: (edge.right ?? null) as StickEdge | null } }))
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'The test stopped.')
    } finally {
      running.current = false
      setStep('idle')
    }
  }
  const asked = step === 'rest' ? 'Leave both sticks alone' : step === 'edge' ? 'Roll both sticks slowly around their edge, a few times' : null
  return (
    <>
      <div className="exo-sticks">
        <StickView x={live?.lx} y={live?.ly} label="Left stick" />
        <StickView x={live?.rx} y={live?.ry} label="Right stick" />
      </div>
      <Row label={asked ?? 'Stick test'} detail={asked ? undefined : 'Center, wander, reach and roundness'} error={error}>
        <button type="button" className="exo-set-btn" disabled={step !== 'idle'} onClick={() => void run()}>
          {step !== 'idle' ? <Spinner label="Testing the sticks" /> : null}{result.edge ? 'Test again' : 'Test'}
        </button>
      </Row>
      {result.rest || result.edge ? (
        <>
          <StickRow label="Left stick" rest={result.rest?.left} edge={result.edge?.left} />
          <StickRow label="Right stick" rest={result.rest?.right} edge={result.edge?.right} />
        </>
      ) : null}
    </>
  )
}

const sentence = (text: string) => text.charAt(0).toUpperCase() + text.slice(1)

/** One stick's numbers and a verdict: centered within 2%, wandering 2 steps or less, reaching 97%, round within 6%. */
function StickRow({ label, rest, edge }: { label: string; rest?: StickRest | null; edge?: StickEdge | null }) {
  const parts: string[] = []
  let good = true
  if (rest) {
    const off = Math.max(Math.abs(rest.offsetX), Math.abs(rest.offsetY))
    parts.push(`center ${off.toFixed(1)}% off`, `wanders ${rest.noise} ${rest.noise === 1 ? 'step' : 'steps'}`)
    good &&= off <= 2 && rest.noise <= 2
  }
  if (edge === null || (edge && edge.covered < 0.75)) parts.push('roll it all the way around to measure its edge')
  else if (edge) {
    parts.push(`reaches ${edge.reach}%`, `${edge.circularity}% out of round`)
    good &&= edge.reach >= 97 && edge.circularity <= 6
  }
  return (
    <Row label={label} detail={sentence(parts.join(' · '))}>
      {edge && edge.covered >= 0.75 ? <State on={good}>{good ? 'Good' : 'Needs calibration'}</State> : null}
    </Row>
  )
}

/** The triggers live. (An Edge's trigger-stop sliders wait until their bits are checked on a pad.) */
function Triggers({ live }: { live: ControllerState | null }) {
  const bar = (value: number | undefined, label: string) => (
    <div className="exo-trigger" role="meter" aria-label={label} aria-valuemin={0} aria-valuemax={255} aria-valuenow={value ?? 0}>
      <span style={{ transform: `translateX(${((value ?? 0) / 255) * 100 - 100}%)` }} />
    </div>
  )
  return (
    <>
      <Row label="L2">{bar(live?.l2, 'L2')}</Row>
      <Row label="R2">{bar(live?.r2, 'R2')}</Row>
    </>
  )
}

/** How it reports, measured over three seconds: rate, fresh readings, the slowest gaps, jitter and repeats. */
function Polling({ id, hint }: { id: string; hint: string }) {
  const [polling, setPolling] = useState<ControllerPolling | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  async function measure() {
    setBusy(true)
    setError(null)
    try {
      const result = await host.controllerMeasure(id, 3)
      if (result.ok && result.polling) setPolling(result.polling)
      else setError(result.message ?? 'Exo couldn’t measure it. Try again.')
    } catch (cause) {
      report(cause, 'ControllerPage.measure')
      setError('Exo couldn’t measure it. Try again.')
    } finally {
      setBusy(false)
    }
  }
  return (
    <>
      <Row label="Polling rate" detail={polling ? polling.fresh != null ? `${polling.fresh} a second carry a new reading` : undefined : hint} error={error}>
        {polling ? <State on>{polling.rate} Hz</State> : null}
        <button type="button" className="exo-set-btn" disabled={busy} onClick={() => void measure()}>{busy ? <Spinner label="Measuring" /> : null}{polling ? 'Again' : 'Measure'}</button>
      </Row>
      {polling ? (
        <>
          <Row label="Slowest 1%" detail={`Jitter ${polling.jitter} ms`}><State>{polling.p99} ms</State></Row>
          {polling.repeats != null ? <Row label="Repeats" detail="Reports that brought nothing new"><State on={polling.repeats < 5}>{polling.repeats}%</State></Row> : null}
        </>
      ) : null}
    </>
  )
}

/** Where it meets the PC. */
function PortRows({ port }: { port: NonNullable<Peripheral['port']> }) {
  return (
    <>
      <Row label="Port" detail={port.chips ? chipWords(port) : undefined}><State on={port.where === 'cpu' && port.hubs === 0}>{portWords(port)}</State></Row>
      {port.interrupts ? <Row label="Interrupts" detail="Set in PC › Performance"><State on={port.interrupts === 'msi'}>{port.interrupts === 'msi' ? 'MSI' : 'Shared line'}</State></Row> : null}
      {port.better === 'free' ? <Row label="A shorter way in" detail="A free port on the processor’s own USB takes one chip out of the way" /> : null}
      {port.better === 'hub' ? <Row label="A shorter way in" detail="Plug it straight into the PC, not into a hub" /> : null}
    </>
  )
}

/**
 * What sits between the controller and games: Steam Input (Exo's one switch for it: games with their own PlayStation
 * support read the controller themselves; the others keep Steam's translation), then remappers and their drivers and a
 * polling driver, found and never changed here.
 */
function LayerRows({ layers }: { layers: NonNullable<ControllerRead['layers']> }) {
  return (
    <>
      <SteamInputRow />
      {layers.tools.map(tool => (
        <Row key={tool.name} label={tool.name} detail={tool.running ? 'Running: it may translate the controller into another one' : 'Installed'}>
          <State on={!tool.running}>{tool.running ? 'Running' : 'Not running'}</State>
        </Row>
      ))}
      {layers.pollingDriver ? (
        <Row label="Polling driver" detail={layers.memoryIntegrity ? 'hidusbf is installed; with Memory Integrity on it can’t change the rate' : 'hidusbf is installed and Memory Integrity is off, which some anti-cheats refuse'}>
          <State>Installed</State>
        </Row>
      ) : null}
      {layers.tools.length === 0 && !layers.pollingDriver && layers.steamPlayStation === null ? <Row label="Nothing in the way" detail="Games read the controller itself" /> : null}
    </>
  )
}

/** Steam Input off for every Steam game that reads the controller itself; Steam closes for a moment to take it. */
function SteamInputRow() {
  const [state, setState] = useState<Awaited<ReturnType<typeof host.steamInputState>> | null>(null)
  const [busy, setBusy] = useState(false)
  useEffect(() => {
    let current = true
    host.steamInputState().then(next => { if (current) setState(next) }, (error: unknown) => report(error, 'ControllerPage.steamInput', { level: 'warn' }))
    return () => { current = false }
  }, [])
  if (!state?.steam) return null
  const detail = state.on
    ? `${state.native} ${state.native === 1 ? 'game reads' : 'games read'} it without Steam Input${state.translated ? `; ${state.translated} that read only Xbox controllers keep it` : ''}`
    : 'Steam Input off in every Steam game that reads a PlayStation controller itself'
  return (
    <SwitchRow id="ctl-native" label="Games read it themselves" detail={detail} checked={state.on} disabled={busy} onChange={async on => {
      setBusy(true)
      try {
        const result = await host.steamInputSet(on)
        setState(await host.steamInputState())
        return result.ok ? result.message ?? null : result.message ?? 'Steam didn’t take it.'
      } catch (cause) {
        report(cause, 'ControllerPage.steamInputSet')
        return 'Steam didn’t take it. Try again.'
      } finally {
        setBusy(false)
      }
    }} />
  )
}
