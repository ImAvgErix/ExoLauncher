import { useCallback, useRef, useState } from 'react'
import type { MouseStatus } from '@openmouse/protocol/drivers/mouse-types'
import type { SupportedClient } from '@openmouse/protocol/drivers/registry'
import { Room } from './App'
import type { Candidate, Step, useMouse } from './mouse'

/**
 * Development only: `npx vite` in mouse/, opened with ?mock, shows the room on a
 * Finalmouse Starlight X that is not there, so the whole room can be seen
 * without the mouse or Exo. main.tsx loads this behind import.meta.env.DEV,
 * so a build never contains it. Writes change the pretend mouse after a short
 * wait (the work band shows), the host's calls are answered here, and
 * ?scale=1.25, ?motion=reduce, ?battery=12, ?charging, ?asleep, ?apart, ?lod=1.4
 * and ?fail try the room at another text size, with reduce motion, low,
 * charging, unread for minutes, with the right switch mechanical, on a
 * custom lift-off, or with a write that does not take. ?firmware=1.0.9 is an
 * older Starlight X (no release points reported, no custom lift-off, no Late);
 * ?unread one whose mouse slept through the read (only the receiver answered). ?razer shows a Razer
 * Viper V3 Pro on its HyperSpeed receiver instead, as a friend's reads it
 * (Razer's button controls, asymmetric lift-off, low power mode), for the
 * room with many panels. ?lamzu is a Lamzu with X and Y stored per DPI stage.
 */

const params = new URLSearchParams(location.search)
const RAZER = params.has('razer')
const LAMZU = params.has('lamzu')
const DEVICE = RAZER ? '1532:c1' : LAMZU ? '3554:f5f7' : '361d:200'
const FIRMWARE = params.get('firmware') ?? '1.1.1'
const OLD = FIRMWARE !== '1.1.1'
const UNREAD = params.has('unread')

const START = {
  brand: 'Finalmouse',
  name: 'Finalmouse Starlight X',
  ui: { family: 'finalmouse-slx', defaultDisplayName: 'Finalmouse Starlight X', hideUnsupportedPollingRates: true, forceShowBattery: true, pollingReadOnly: true, hideMotionSync: true },
  batteryPercent: params.has('battery') ? Number(params.get('battery')) : 76,
  batteryVoltageMv: 3940,
  batteryState: params.has('charging') ? 'Charging' : 'Discharging',
  dpi: 800,
  pollingRateHz: 8000,
  supportedPollingRates: [],
  activeProfile: null,
  unitId: null,
  connectionType: 'Wireless',
  connectionDetail: '2.4 GHz receiver · -42 dBm',
  motionSync: null,
  signalStrength: 4,
  liftOffDistance: 'Medium',
  supportedLiftOffDistances: ['Medium', 'High'],
  liftOffScale: null,
  finalmouseIsSlx: true,
  finalmouseDongleLedMode: 2,
  finalmouseIndicatorColor: { r: 61, g: 214, b: 140 },
  finalmouseTournamentScrollMode: 1,
  finalmouseTournamentScrollTimeoutMs: 500,
  finalmouseClickModeL: UNREAD ? null : 1,
  finalmouseClickModeR: UNREAD ? null : params.has('apart') ? 0 : 1,
  finalmouseClickReleaseL: OLD || UNREAD ? null : 0,
  finalmouseClickReleaseR: OLD || UNREAD ? null : 0,
  finalmouseTmrThrL: 25,
  finalmouseTmrThrR: 25,
  finalmouseTmrHystL: 220,
  finalmouseTmrHystR: 220,
  finalmouseTmrMspL: null,
  finalmouseTmrMspR: null,
  finalmousePawLodMm: UNREAD ? null : params.has('lod') ? Number(params.get('lod')) : 1,
  finalmousePawLodCustom: params.has('lod'),
  finalmouseProfileCount: 3,
  finalmouseProfileActive: 0,
  finalmouseProfileEnabledMask: 0b111,
  finalmouseProfileNames: { 0: 'Profile 1', 1: 'Profile 2', 2: 'Profile 3' },
  firmware: UNREAD ? ['Dongle RF 1.1.1'] : [`Mouse ${FIRMWARE}-5289564`, 'Dongle RF 1.1.1'],
} as unknown as MouseStatus

const RAZER_START = {
  brand: 'Razer',
  name: 'Razer Viper V3 Pro',
  ui: {},
  batteryPercent: 56,
  batteryState: 'Discharging',
  dpi: 1600,
  dpiStages: [1600],
  activeDpiStage: 0,
  pollingRateHz: 1000,
  supportedPollingRates: [125, 500, 1000, 2000, 4000, 8000],
  activeProfile: null,
  unitId: null,
  connectionType: 'Wireless',
  connectionDetail: 'HyperSpeed receiver',
  motionSync: null,
  sleepTimeout: 300,
  lowBatteryWarning: 30,
  liftOffDistance: 'Low',
  supportedLiftOffDistances: ['Low', 'Medium', 'High'],
  asymmetricLiftOff: { enabled: null, liftOff: 2, landing: 26, liftOffRange: { min: 2, max: 26 }, landingRange: { min: 1, max: 25 } },
  razerButtonMappings: {
    leftClick: 'Left Click', rightClick: 'Right Click', mouse4: 'Mouse Button 4', mouse5: 'Mouse Button 5',
    scrollUp: 'Scroll Up', scrollDown: 'Scroll Down', sensitivityButton: 'Disabled',
  },
  firmware: ['Mouse 1.14'],
} as unknown as MouseStatus

const LAMZU_START = {
  brand: 'LunaFury',
  name: 'Lamzu Maya X',
  ui: { dpiStageEditor: { minDpi: 50, maxDpi: 30_000, stepDpi: 10, maxStages: 4, countEditable: true } },
  batteryPercent: 64,
  batteryState: 'Discharging',
  dpi: 800,
  dpiStages: [400, 800, 1600],
  dpiStagesY: [400, 720, 1600],
  activeDpiStage: 1,
  pollingRateHz: 1000,
  supportedPollingRates: [125, 250, 500, 1000, 2000, 4000, 8000],
  activeProfile: null,
  unitId: null,
  connectionType: 'Wireless',
  connectionDetail: '2.4 GHz receiver',
  motionSync: null,
  liftOffDistance: 'Low',
  lunafury: { separateDpiAxes: true },
  firmware: ['Mouse 1.0.6'],
} as unknown as MouseStatus

type Fields = Record<string, unknown>
const SETTERS: Record<string, (s: Fields, ...a: number[]) => Fields> = {
  setDpi: (s, dpi) => ({ ...s, dpi }),
  setPawLodMm: (s, mm) => ({ ...s, finalmousePawLodMm: mm, finalmousePawLodCustom: false }),
  setLiftOffScale: (s, raw) => ({ ...s, finalmousePawLodMm: raw / 10, finalmousePawLodCustom: true }),
  setClickMode: (s, l, r, rl, rr) => ({ ...s, finalmouseClickModeL: l, finalmouseClickModeR: r, ...(rl == null ? {} : { finalmouseClickReleaseL: rl, finalmouseClickReleaseR: rr }) }),
  setTmrActuation: (s, l, r, hl, hr) => ({ ...s, finalmouseTmrThrL: Math.round(l * 100), finalmouseTmrThrR: Math.round(r * 100), finalmouseTmrHystL: hl, finalmouseTmrHystR: hr }),
  setActiveProfile: (s, i) => ({ ...s, finalmouseProfileActive: i }),
  setDongleLedMode: (s, m) => ({ ...s, finalmouseDongleLedMode: m }),
  setIndicatorColor: (s, r, g, b) => ({ ...s, finalmouseIndicatorColor: { r, g, b } }),
  setTournamentScrollMode: (s, m) => ({ ...s, finalmouseTournamentScrollMode: m }),
  setTournamentScrollTimeout: (s, ms) => ({ ...s, finalmouseTournamentScrollTimeoutMs: ms }),
  setPollingRate: (s, hz) => ({ ...s, pollingRateHz: hz }),
  setSleepTimeout: (s, sec) => ({ ...s, sleepTimeout: sec }),
  setLowPowerThreshold: (s, pct) => ({ ...s, lowBatteryWarning: pct }),
  setLiftOffDistance: (s, d) => ({ ...s, liftOffDistance: d, asymmetricLiftOff: { ...(s.asymmetricLiftOff as object), enabled: false } }),
  setLiftOff: (s, liftOff, landing) => ({ ...s, asymmetricLiftOff: { ...(s.asymmetricLiftOff as object), enabled: true, liftOff, landing } }),
  setActiveDpiStage: (s, i) => ({ ...s, activeDpiStage: i, dpi: (s.dpiStages as number[])[i] }),
  setDpiStageValue: (s, i, x) => ({ ...s, dpiStages: (s.dpiStages as number[]).map((v, j) => j === i ? x : v) }),
  setLunaFuryDpiStageAxes: (s, i, x, y) => ({ ...s, dpiStages: (s.dpiStages as number[]).map((v, j) => j === i ? x : v), dpiStagesY: (s.dpiStagesY as number[]).map((v, j) => j === i ? y : v) }),
  setLunaFurySeparateDpiAxes: (s, on) => ({ ...s, lunafury: { separateDpiAxes: !!on } }),
}

/** Setters whose first argument is a name, not a number (Razer's button controls). */
const NAMED: Record<string, (s: Fields, ...a: unknown[]) => Fields> = {
  setButtonMapping: (s, control, mapping) => ({ ...s, razerButtonMappings: { ...(s.razerButtonMappings as object), [control as string]: mapping } }),
  setToggleControl: (s, control, value) => ({ ...s, razerButtonMappings: { ...(s.razerButtonMappings as object), [control as string]: value } }),
}

/** A driver client as far as `can` looks: every setter above, doing nothing itself. */
const CLIENT = {
  ...Object.fromEntries([...Object.keys(SETTERS), ...Object.keys(NAMED)].map(name => [name, async () => undefined])),
  getLowPowerOptions: () => [5, 10, 15, 20, 25, 30, 35, 40, 45, 50, 55, 60, 65, 70, 75, 80, 85, 90, 95, 100],
} as unknown as SupportedClient

const CANDIDATE = { id: DEVICE, device: {} as HIDDevice, name: RAZER ? 'Viper V3 Pro' : LAMZU ? 'Maya X' : 'Starlight X', others: [] } satisfies Candidate

const GAMES = [
  { id: 'steam:730', title: 'Counter-Strike 2' },
  { id: 'riot:valorant', title: 'VALORANT' },
  { id: 'steam:1172470', title: 'Apex Legends' },
  { id: 'steam:2357570', title: 'Overwatch 2' },
]

const PROFILES = {
  [DEVICE]: [
    { id: 'desktop', name: 'Desktop', isDefault: true, games: [], savedAt: '2026-10-01T12:00:00Z', steps: [['setDpi', [800]]] },
    { id: 'tac', name: 'Tactical', games: ['steam:730', 'riot:valorant'], savedAt: '2026-10-02T12:00:00Z', steps: [['setDpi', [400]]] },
    { id: 'arena', name: 'Arena', games: ['steam:1172470'], savedAt: '2026-10-03T12:00:00Z', steps: [['setDpi', [1600]]] },
  ],
}

/** Exo's side of the frame's messages, answered in the page itself (standalone, the frame's parent is itself). */
function answerHost() {
  const post = window.postMessage.bind(window)
  window.postMessage = ((message: unknown, ...rest: unknown[]) => {
    const m = message as { exo?: string; id?: number; method?: string } | null
    if (m?.exo !== 'mouse') return (post as (...a: unknown[]) => void)(message, ...rest)
    if (typeof m.id !== 'number') return
    const result = m.method === 'exo.profiles.read' ? { ok: true, data: PROFILES }
      : m.method === 'exo.games' ? { ok: true, games: GAMES }
        : { ok: true }
    window.setTimeout(() => window.dispatchEvent(new MessageEvent('message', { data: { exo: 'mouse', id: m.id, result }, source: window })), 40)
  }) as typeof window.postMessage
}

function look() {
  const scale = Number(params.get('scale'))
  if (scale > 0.5 && scale < 2) document.documentElement.style.setProperty('--exo-scale', String(scale))
  if (params.get('motion') === 'reduce') document.documentElement.dataset.exoMotion = 'reduce'
}

function useMockMouse(): ReturnType<typeof useMouse> {
  const [status, setStatus] = useState(RAZER ? RAZER_START : LAMZU ? LAMZU_START : START)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [readAt, setReadAt] = useState(() => Date.now() - (params.has('asleep') ? 4 * 60_000 : 0))
  const now = useRef(status)
  const applyAll = useCallback(async (steps: Step[]) => {
    setBusy(true)
    setError(null)
    await new Promise(resolve => window.setTimeout(resolve, 900))
    if (params.has('fail')) {
      setError('The mouse did not answer. Make sure it is on and close xpanel.')
      setBusy(false)
      return null
    }
    let next = now.current as unknown as Fields
    for (const [method, args] of steps) next = NAMED[method]?.(next, ...args) ?? SETTERS[method]?.(next, ...(args as number[])) ?? next
    now.current = next as unknown as MouseStatus
    setStatus(now.current)
    setReadAt(Date.now())
    setBusy(false)
    return now.current
  }, [])
  const apply = useCallback((method: string, ...args: unknown[]) => applyAll([[method, args]]), [applyAll])
  return {
    phase: 'ready', candidates: [CANDIDATE], current: CANDIDATE, brand: RAZER ? 'Razer' : LAMZU ? 'LunaFury' : 'Finalmouse', status, error, busy,
    client: CLIENT, readAt, apply, applyAll, choose: () => undefined, retry: () => undefined,
  }
}

export function MockApp() {
  return <Room mouse={useMockMouse()} />
}

answerHost()
look()
