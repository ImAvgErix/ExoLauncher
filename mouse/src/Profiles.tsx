import { useEffect, useRef, useState } from 'react'
import type { MouseStatus } from '@openmouse/protocol/drivers/mouse-types'
import { can, type Step, type useMouse } from './mouse'
import { call, onHostEvent, tell } from './exo-hid'
import { GamePicker, LinkedCovers, type Game } from './GamePicker'
import { Panel } from './ui'
import { indicatorColor } from './Finalmouse'

type Client = ReturnType<typeof useMouse>['client']
/** games: the Exo game ids this profile is for; it applies while one of them runs. */
/**
 * isDefault marks Desktop: built in, always there, always first, the profile the
 * mouse goes back to whenever no linked game is running. It is never renamed,
 * deleted or linked to games; Update saves the mouse's current settings into it.
 */
type Profile = { id: string; name: string; steps: Step[]; savedAt: string; games?: string[]; isDefault?: boolean }

const KEY = 'exo-mouse.profiles.v1'

type All = Record<string, Profile[]>

/** Profiles an earlier Exo Mouse kept in this frame's own storage. */
function readOld(): All | null {
  try {
    const raw = localStorage.getItem(KEY)
    return raw ? JSON.parse(raw) as All : null
  } catch { return null }
}

/**
 * Exo keeps the profiles in its own data folder, so they survive anything that
 * clears this frame. Older ones move there once, and leave the frame only after
 * Exo has saved them.
 */
async function readAll(): Promise<All> {
  const reply = await call('exo.profiles.read') as unknown as { data?: All | null }
  if (reply.data) return reply.data
  const old = readOld()
  if (!old) return {}
  await writeAll(old)
  try { localStorage.removeItem(KEY) } catch { /* moved already; the copy here is harmless */ }
  return old
}

async function writeAll(all: All): Promise<void> {
  await call('exo.profiles.write', { data: all })
}

/**
 * The settings this mouse has now, as the setter calls that would put them
 * back: only setters its driver has, only values the mouse reported, never a
 * value the mouse treats as fixed (PerfectPolling, a hidden Motion Sync).
 */
export function snapshot(status: MouseStatus, client: Client): Step[] {
  const ui = status.ui ?? {}
  const steps: Step[] = []
  const add = (method: string, ...args: unknown[]) => { if (can(client, method) && args.every(a => a !== null && a !== undefined)) steps.push([method, args]) }
  const slx = !!status.finalmouseIsSlx
  add('setDpi', status.dpi)
  if (!ui.pollingReadOnly && (status.supportedPollingRates?.length ?? 1) > 0) add('setPollingRate', status.pollingRateHz)
  if (slx) {
    const mm = status.finalmousePawLodMm
    if (mm != null) status.finalmousePawLodCustom ? add('setLiftOffScale', Math.round(mm * 10)) : add('setPawLodMm', mm)
  } else if (status.liftOffDistance) add('setLiftOffDistance', status.liftOffDistance)
  if (!ui.hideMotionSync && !ui.hideProcessingCard) add('setMotionSync', status.motionSync)
  if (!ui.hideAngleSnapping && !ui.hideProcessingCard) add('setAngleSnapping', status.angleSnapping)
  if (!ui.hideRippleControl && !ui.hideProcessingCard) add('setRippleControl', status.rippleControl)
  add('setDebounceTime', status.debounceMs)
  if (!ui.hideSleepCard) add('setSleepTimeout', status.sleepTimeout)
  if (status.powerModes?.length) add('setPowerMode', status.powerMode)
  if (slx) {
    // Modes first: actuation only takes on a side that is analog. Release points only when the
    // mouse reported them; actuation only when both sides are calibrated (a side reading 0 is not).
    const relL = status.finalmouseClickReleaseL
    const relR = status.finalmouseClickReleaseR
    if (relL != null && relR != null) add('setClickMode', status.finalmouseClickModeL, status.finalmouseClickModeR, relL, relR)
    else add('setClickMode', status.finalmouseClickModeL, status.finalmouseClickModeR)
    // Rapid trigger rides along only where the firmware reported it.
    const hyst = status.finalmouseTmrHystL != null && status.finalmouseTmrHystR != null ? [status.finalmouseTmrHystL, status.finalmouseTmrHystR] : []
    if (status.finalmouseTmrThrL && status.finalmouseTmrThrR)
      add('setTmrActuation', status.finalmouseTmrThrL / 100, status.finalmouseTmrThrR / 100, ...hyst)
    const color = indicatorColor(status)
    if (color) add('setIndicatorColor', color.r, color.g, color.b)
    add('setDongleLedMode', status.finalmouseDongleLedMode)
    add('setTournamentScrollMode', status.finalmouseTournamentScrollMode)
    if (status.finalmouseTournamentScrollMode) add('setTournamentScrollTimeout', status.finalmouseTournamentScrollTimeoutMs)
  }
  return steps
}

/**
 * The mouse has a profile when every setting the profile holds is what the
 * mouse has now. A profile saved before Exo Mouse learned a setting still
 * matches, instead of being rewritten on every connect.
 */
const same = (target: Step[], now: Step[]) => changes(target, now).length === 0

const DESKTOP = 'Desktop'
const isDesktopName = (name: string) => name.trim().toLowerCase() === DESKTOP.toLowerCase()

/**
 * This mouse's profiles with Desktop in place: the one already marked, else one
 * already called Desktop, else a new one from the settings the mouse has now.
 * It comes first, with no games; any other profile marked default is not.
 */
function withDesktop(list: Profile[], now: Step[]): Profile[] {
  const desktop = list.find(p => p.isDefault) ?? list.find(p => isDesktopName(p.name))
    ?? { id: crypto.randomUUID(), name: DESKTOP, steps: now, savedAt: new Date().toISOString() }
  const rest = list.filter(p => p.id !== desktop.id).map(p => p.isDefault ? { ...p, isDefault: false } : p)
  return [{ ...desktop, name: DESKTOP, isDefault: true, games: [] }, ...rest]
}

/** How long a switch keeps trying while the mouse sleeps: every 3 s for a minute. */
const LAND_TRIES = 20
const LAND_CHECK_MS = 3_000
const sleep = (ms: number) => new Promise<void>(resolve => window.setTimeout(resolve, ms))

/** Only the settings that differ from the mouse now: fewer writes, a faster switch. */
export const changes = (target: Step[], now: Step[]) => target.filter(step => !now.some(n => JSON.stringify(n) === JSON.stringify(step)))

/**
 * Game state that outlives Profiles: it unmounts whenever the mouse reconnects
 * (a wireless mouse waking, a replug), while this frame and Exo's game events
 * carry on. running: every game Exo says is running, newest last. switchFor: the
 * mounted Profiles' reaction to a start or stop, none while it is unmounted.
 * landing: the newest switch; a newer one, or Profiles unmounting, stops an
 * older one waiting.
 */
const games = {
  running: [] as string[],
  switchFor: null as ((id: string, isRunning: boolean) => void) | null,
  landing: 0,
}

onHostEvent('game.runState', data => {
  const d = data as { id?: string; isRunning?: boolean }
  if (!d.id) return
  const was = games.running.includes(d.id)
  if (!!d.isRunning === was) return
  games.running = d.isRunning ? [...games.running, d.id] : games.running.filter(g => g !== d.id)
  games.switchFor?.(d.id, !!d.isRunning)
})

/** The profile of the newest running game linked to one, if any. */
const runningProfile = (profiles: Profile[]) =>
  [...games.running].reverse().map(g => profiles.find(p => p.games?.includes(g))).find(Boolean)

/**
 * Exo's own profiles for this mouse model: named sets of settings, as many as
 * you like, saved on this PC. Save current stores what the mouse has now;
 * Apply puts it back; the one that matches the mouse right now says Active.
 * A profile can be linked to games: when one starts (Exo says so, room open
 * or not), the profile is written; when it closes, Desktop goes back.
 */
export function Profiles({ device, status, client, applyAll, busy }: {
  device: string
  status: MouseStatus
  client: Client
  applyAll: (steps: Step[]) => Promise<MouseStatus | null>
  busy: boolean
}) {
  const [all, setAll] = useState<All>({})
  // A failed read or save is one line in Exo's notice, never a line that moves the rows.
  useEffect(() => {
    readAll().then(all => { setAll(all); setLoaded(true) }).catch(() => tell({ notice: 'Exo could not read your saved mouse profiles.' }))
  }, [])
  const [name, setName] = useState('')
  const [renaming, setRenaming] = useState<string | null>(null)
  const [draft, setDraft] = useState('')
  const [deleting, setDeleting] = useState<string | null>(null)
  /** The profile being written (its Apply carries the work band), then the one just written: Active with a check. */
  const [applying, setApplying] = useState<string | null>(null)
  const [applied, setApplied] = useState<string | null>(null)
  const apply = async (p: Profile) => {
    setApplying(p.id)
    const took = await applyAll(changes(p.steps, now))
    setApplying(a => a === p.id ? null : a)
    if (!took) return
    setApplied(p.id)
    window.setTimeout(() => setApplied(a => a === p.id ? null : a), 3000)
  }
  const active = (p: Profile) => same(p.steps, now) || applied === p.id
  const list = all[device] ?? []
  const now = snapshot(status, client)
  // The saved profile the mouse has now, for Exo's title bar (none when its settings match none).
  const current = list.find(p => same(p.steps, now))?.name ?? null
  useEffect(() => { tell({ profile: current }) }, [current])
  useEffect(() => () => tell({ profile: null }), [])

  const [installed, setInstalled] = useState<Game[]>([])
  const [linking, setLinking] = useState<string | null>(null)
  // Read again each time the picker opens: games installed since this frame started show up.
  const pickerOpen = linking !== null
  useEffect(() => {
    let live = true
    call('exo.games')
      .then(r => { if (live) setInstalled((r as unknown as { games?: Game[] }).games ?? []) })
      .catch(() => { if (live && !pickerOpen) setInstalled([]) })
    return () => { live = false }
  }, [pickerOpen])
  const byId = new Map(installed.map(g => [g.id, g]))
  const titles = new Map(installed.map(g => [g.id, g.title]))
  const titlesNow = useRef(titles)
  titlesNow.current = titles

  // The newest list, settings and writer, for the game events below.
  const live = useRef({ list, now, applyAll })
  live.current = { list, now, applyAll }
  /**
   * Writes a profile and keeps at it until the mouse has it. A sleeping wireless
   * mouse drops writes while its receiver still answers reads, so a switch made
   * while it sleeps lands when it wakes: checked every few seconds for a minute.
   * The notice says so only once the mouse has the profile. A newer switch
   * replaces an older one still waiting.
   */
  const land = (steps: Step[], notice: string | null) => {
    const ticket = ++games.landing
    void (async () => {
      for (let attempt = 0; attempt < LAND_TRIES; attempt++) {
        if (games.landing !== ticket) return
        const current = live.current.now
        if (attempt > 0 && same(steps, current)) break
        const took = await live.current.applyAll(attempt === 0 ? steps : changes(steps, current))
        await sleep(LAND_CHECK_MS)
        if (games.landing !== ticket) return
        if (took && same(steps, live.current.now)) break
        if (attempt === LAND_TRIES - 1) {
          tell({ notice: 'Your mouse did not take its profile. Apply it again in Devices.' })
          return
        }
      }
      if (notice) tell({ notice })
    })()
  }

  // Starts and stops while this is unmounted are only recorded in `games`; the
  // connect below lands whatever they leave.
  useEffect(() => {
    games.switchFor = (id, isRunning) => {
      const profiles = live.current.list
      if (isRunning) {
        const profile = profiles.find(p => p.games?.includes(id))
        if (!profile) return
        // The room may be closed and the last read a minute old: write the whole profile.
        land(profile.steps, `${profile.name} mouse profile on for ${titlesNow.current.get(id) ?? 'your game'}.`)
        return
      }
      if (!profiles.some(p => p.games?.includes(id))) return
      // Another linked game still running keeps its profile; otherwise Desktop (always there once loaded).
      const next = runningProfile(profiles) ?? profiles.find(p => p.isDefault)
      if (next) land(next.steps, `${next.name} mouse profile on.`)
    }
    return () => {
      games.switchFor = null
      // The next connect lands the right profile with fresh reads; a switch waiting here would use stale ones.
      games.landing++
    }
  }, [])

  // When the mouse connects (Exo starting, the mouse waking, a replug) it takes the
  // profile of the newest linked game running, else the default, once per connection, quietly.
  const [loaded, setLoaded] = useState(false)
  const settled = useRef(false)
  useEffect(() => {
    if (!loaded || settled.current) return
    settled.current = true
    const fixed = withDesktop(list, now)
    if (JSON.stringify(fixed) !== JSON.stringify(list)) save(fixed)
    const target = runningProfile(fixed) ?? fixed[0]
    if (!same(target.steps, now)) land(target.steps, null)
  }, [loaded, list, now, applyAll])

  /** A game is linked to one profile at most (never Desktop): linking it here unlinks it elsewhere. */
  const toggleGame = (profile: Profile, game: string) => {
    if (profile.isDefault) return
    const on = !profile.games?.includes(game)
    save(list.map(p => p.id === profile.id
      ? { ...p, games: on ? [...(p.games ?? []), game] : (p.games ?? []).filter(g => g !== game) }
      : on ? { ...p, games: (p.games ?? []).filter(g => g !== game) } : p))
  }

  const save = (next: Profile[]) => {
    // Profiles Exo could not read would be overwritten with this mouse's list alone.
    if (!loaded) return
    const updated = { ...all, [device]: next }
    setAll(updated)
    writeAll(updated).catch(() => tell({ notice: 'Exo could not save your mouse profiles. Try again.' }))
  }
  /** Save current: a new profile, or the one with that name (Desktop included) updated to what the mouse has now. */
  const add = () => {
    const clean = name.trim().slice(0, 40)
    if (!clean) return
    const existing = list.find(p => p.name.toLowerCase() === clean.toLowerCase())
    save(existing
      ? list.map(p => p.id === existing.id ? { ...p, steps: now, savedAt: new Date().toISOString() } : p)
      : [...list, { id: crypto.randomUUID(), name: clean, steps: now, savedAt: new Date().toISOString() }])
    setName('')
  }
  const update = (id: string) => save(list.map(p => p.id === id ? { ...p, steps: now, savedAt: new Date().toISOString() } : p))
  const rename = (id: string) => {
    const clean = draft.trim().slice(0, 40)
    // Desktop's name is taken: another profile cannot become it.
    if (clean && !isDesktopName(clean)) save(list.map(p => p.id === id && !p.isDefault ? { ...p, name: clean } : p))
    setRenaming(null)
  }
  const remove = (id: string) => {
    if (list.find(p => p.id === id)?.isDefault) return
    if (deleting !== id) { setDeleting(id); window.setTimeout(() => setDeleting(d => d === id ? null : d), 3000); return }
    save(list.filter(p => p.id !== id))
    setDeleting(null)
  }

  return (
    <Panel title="Profiles">
      {list.map(p => (
        <div key={p.id} className="m-row">
          {renaming === p.id && !p.isDefault ? (
            <input className="m-text" aria-label="Profile name" autoFocus maxLength={40} value={draft}
              onChange={e => setDraft(e.target.value)} onBlur={() => rename(p.id)}
              onKeyDown={e => { if (e.key === 'Enter') rename(p.id); if (e.key === 'Escape') setRenaming(null) }} />
          ) : (
            <span className="m-row-label">
              <span className="m-profile-name">
                {p.name}
                {active(p) ? (
                  <span className="m-row-value is-good m-switch-in">
                    {applied === p.id ? <svg className="m-check" viewBox="0 0 24 24" aria-hidden="true"><path d="M5 12l5 5l10 -10" pathLength="1" /></svg> : null}
                    Active
                  </span>
                ) : null}
              </span>
              <LinkedCovers ids={p.games ?? []} games={byId} />
            </span>
          )}
          <span className="m-row-control m-actions">
            {/* The profile on the mouse says Active beside its name; it has nothing to apply. */}
            {active(p) ? null : (
              <button type="button" className={`m-button is-compact${applying === p.id ? ' m-work' : ''}`} disabled={busy} onClick={() => void apply(p)}>Apply</button>
            )}
            {p.isDefault ? (
              active(p) ? null : <button type="button" className="m-button is-compact" onClick={() => update(p.id)}>Update</button>
            ) : (
              <>
                <button type="button" className="m-button is-compact" aria-haspopup="dialog" onClick={() => setLinking(p.id)}>Games</button>
                <button type="button" className="m-button is-compact" onClick={() => { setRenaming(p.id); setDraft(p.name) }}>Rename</button>
                <button type="button" className="m-button is-compact is-danger" onClick={() => remove(p.id)}>{deleting === p.id ? 'Sure?' : 'Delete'}</button>
              </>
            )}
          </span>
        </div>
      ))}
      {(() => {
        const picking = list.find(p => p.id === linking)
        return picking ? (
          <GamePicker profile={picking} profiles={list} games={installed}
            onToggle={game => toggleGame(picking, game)} onClose={() => setLinking(null)} />
        ) : null
      })()}
      <div className="m-row m-profile-new">
        <input className="m-text" aria-label="New profile name" placeholder="Name" maxLength={40} value={name}
          onChange={e => setName(e.target.value)} onKeyDown={e => { if (e.key === 'Enter') add() }} />
        <span className="m-row-control">
          <button type="button" className="m-button" disabled={!loaded || !name.trim()} onClick={add}>
            {list.some(p => p.name.toLowerCase() === name.trim().toLowerCase()) ? 'Update' : 'Save current'}
          </button>
        </span>
      </div>
    </Panel>
  )
}
