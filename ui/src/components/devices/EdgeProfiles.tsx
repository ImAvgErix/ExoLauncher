import { useEffect, useState } from 'react'
import { host, type EdgeProfile, type EdgeProfiles as Profiles, type EdgeStick } from '../../lib/host'
import { report } from '../../lib/report'
import { Choice } from '../settings/Choice'
import { Row, State } from '../settings/parts'
import { Switch } from '../Switch'
import { Dialog } from '../ui/Dialog'
import { Slider } from '../ui/Slider'
import { Spinner } from '../ui/Spinner'

/** The Fn button that picks each slot. */
const SLOT: Record<number, string> = { 1: 'Fn + Triangle', 2: 'Fn + Square', 3: 'Fn + Cross', 4: 'Fn + Circle' }
const PRESETS = [
  { value: '0', label: 'Default' }, { value: '1', label: 'Quick' }, { value: '2', label: 'Precise' },
  { value: '3', label: 'Steady' }, { value: '4', label: 'Digital' }, { value: '5', label: 'Dynamic' },
]
const STRENGTHS = [{ value: 'strong', label: 'Strong' }, { value: 'medium', label: 'Medium' }, { value: 'weak', label: 'Weak' }, { value: 'off', label: 'Off' }]
/** What a back button can send. */
const TARGETS = [
  { value: 'off', label: 'Off' },
  ...([[0x05, 'Cross'], [0x04, 'Circle'], [0x06, 'Square'], [0x07, 'Triangle'], [0x08, 'R1'], [0x09, 'R2'], [0x0A, 'R3'], [0x0B, 'L1'], [0x0C, 'L2'],
    [0x0D, 'L3'], [0x00, 'Up'], [0x01, 'Left'], [0x02, 'Down'], [0x03, 'Right'], [0x10, 'Options'], [0x11, 'Touchpad']] as const)
    .map(([code, label]) => ({ value: String(code), label })),
]
/** The back buttons: their place in the remap table and their bit in the disable bitmap. */
const BACK = [{ label: 'Left back button', index: 14, bit: 22 }, { label: 'Right back button', index: 15, bit: 23 }] as const
const SWAP = 31
const bitOn = (bits: number, bit: number) => ((bits >>> bit) & 1) === 1
const withBit = (bits: number, bit: number, on: boolean) => (on ? bits | (1 << bit) : bits & ~(1 << bit)) >>> 0

/**
 * An Edge's on-board profiles (DESIGN.md › Devices › controller): the four slots its Fn buttons pick, the one in use
 * marked. The default (Fn + Triangle) is the controller's own; Square, Cross and Circle open the editor. What a profile
 * holds is applied inside the controller, so it works in every game, on every PC, with nothing added in between.
 */
export function ProfileRows({ id, usb, active }: { id: string; usb: boolean; active: number | null | undefined }) {
  const [list, setList] = useState<Profiles | null>(null)
  const [editing, setEditing] = useState<EdgeProfile | null>(null)
  useEffect(() => {
    if (!usb) return
    let current = true
    host.controllerProfiles(id).then(next => { if (current) setList(next) }, (error: unknown) => report(error, 'EdgeProfiles.read'))
    return () => { current = false }
  }, [id, usb])
  if (!usb) return <Row label="Profiles" detail="Connect it with a cable to change its profiles" />
  if (!list) return <Row label="Profiles" detail={<Spinner label="Reading its profiles" />} />
  if (!list.ok || !list.profiles) return <Row label="Profiles" error={list.message ?? 'The controller didn’t give its profiles.'} />
  return (
    <>
      {list.profiles.map(profile => (
        <Row key={profile.slot} label={SLOT[profile.slot]} detail={profile.assigned ? profile.name : 'Empty'}>
          {active === profile.slot ? <State on>In use</State> : null}
          {profile.slot > 1 ? (
            <button type="button" className="exo-set-btn" onClick={() => setEditing(profile.assigned ? profile : blank(profile.slot))}>{profile.assigned ? 'Edit' : 'Create'}</button>
          ) : null}
        </Row>
      ))}
      {editing ? <ProfileDialog id={id} profile={editing} onClose={() => setEditing(null)} onSaved={next => { setList(next); setEditing(null) }} /> : null}
    </>
  )
}

function blank(slot: number): EdgeProfile {
  const stick: EdgeStick = { preset: 0, points: [0, 0, 128, 128, 196, 196, 225, 225], deadzone: 0, adjustment: 0 }
  return {
    slot, assigned: false, intact: true, canUndo: false, name: `Profile ${slot}`, id: '', left: stick, right: stick, l2: [0, 100], r2: [0, 100],
    unifiedTriggers: false, vibration: 'strong', triggerEffects: 'strong',
    buttons: [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15], disabled: (1 << 22 | 1 << 23) >>> 0, updated: 0,
  }
}

/** The profile editor: its name, each stick's response, the triggers' ranges, the back buttons, rumble and trigger effects. */
function ProfileDialog({ id, profile, onClose, onSaved }: { id: string; profile: EdgeProfile; onClose: () => void; onSaved: (list: Profiles) => void }) {
  const [draft, setDraft] = useState(profile)
  const [busy, setBusy] = useState<'save' | 'clear' | 'undo' | null>(null)
  const [error, setError] = useState<string | null>(null)
  const set = (patch: Partial<EdgeProfile>) => setDraft(current => ({ ...current, ...patch }))

  async function run(what: 'save' | 'clear' | 'undo') {
    setBusy(what)
    setError(null)
    try {
      const result = what === 'save' ? await host.controllerProfileSave(id, draft)
        : what === 'clear' ? await host.controllerProfileClear(id, draft.slot)
        : await host.controllerProfileUndo(id, draft.slot)
      if (result.ok) onSaved(result)
      else setError(result.message ?? 'The controller didn’t take it. Try again.')
    } catch (cause) {
      report(cause, 'EdgeProfiles.write')
      setError('The controller didn’t take it. Try again.')
    } finally {
      setBusy(null)
    }
  }

  return (
    <Dialog title={`${SLOT[draft.slot]} profile`} form onClose={onClose} className="exo-profile-dialog" actions={
      <>
        <button type="button" className="exo-set-btn" data-autofocus onClick={onClose}>Cancel</button>
        {profile.canUndo ? (
          <button type="button" className="exo-set-btn" disabled={busy !== null} onClick={() => void run('undo')}>
            {busy === 'undo' ? <Spinner label="Putting it back" /> : null}Undo
          </button>
        ) : null}
        {profile.assigned ? (
          <button type="button" className="exo-set-btn is-danger" disabled={busy !== null} onClick={() => void run('clear')}>
            {busy === 'clear' ? <Spinner label="Clearing" /> : null}Clear
          </button>
        ) : null}
        <button type="button" className="exo-set-btn is-primary" disabled={busy !== null || !draft.name.trim()} onClick={() => void run('save')}>
          {busy === 'save' ? <Spinner label="Saving to the controller" /> : null}Save
        </button>
      </>
    }>
      <label className="exo-field exo-profile-name">
        <input value={draft.name} maxLength={40} aria-label="Profile name" onChange={event => set({ name: event.target.value })} />
      </label>
      <div className="exo-profile-grid">
        <section aria-label="Sticks">
          <StickEditor label="Left stick" stick={draft.left} onChange={left => set({ left })} />
          <StickEditor label="Right stick" stick={draft.right} onChange={right => set({ right })} />
          <Row label="Swap the sticks" labelId="pf-swap">
            <Switch checked={bitOn(draft.disabled, SWAP)} labelledBy="pf-swap" onChange={on => set({ disabled: withBit(draft.disabled, SWAP, on) })} />
          </Row>
        </section>
        <section aria-label="Triggers and buttons">
          <TriggerEditor label="L2" range={draft.l2} onChange={l2 => set(draft.unifiedTriggers ? { l2, r2: l2 } : { l2 })} />
          {draft.unifiedTriggers ? null : <TriggerEditor label="R2" range={draft.r2} onChange={r2 => set({ r2 })} />}
          <Row label="Same for both triggers" labelId="pf-unified">
            <Switch checked={draft.unifiedTriggers} labelledBy="pf-unified" onChange={on => set({ unifiedTriggers: on, r2: on ? draft.l2 : draft.r2 })} />
          </Row>
          {BACK.map(back => (
            <Row key={back.index} label={back.label}>
              <Choice label={back.label} value={bitOn(draft.disabled, back.bit) ? 'off' : String(draft.buttons[back.index])} choices={TARGETS}
                onChange={value => {
                  const buttons = [...draft.buttons]
                  buttons[back.index] = value === 'off' ? back.index : Number(value)
                  set({ buttons, disabled: withBit(draft.disabled, back.bit, value === 'off') })
                }} />
            </Row>
          ))}
          <Row label="Rumble">
            <Choice label="Rumble" value={draft.vibration} choices={STRENGTHS} onChange={value => set({ vibration: value as EdgeProfile['vibration'] })} />
          </Row>
          <Row label="Trigger effects">
            <Choice label="Trigger effects" value={draft.triggerEffects} choices={STRENGTHS} onChange={value => set({ triggerEffects: value as EdgeProfile['triggerEffects'] })} />
          </Row>
        </section>
      </div>
      {!profile.intact ? <p className="exo-dialog-error">The controller’s copy of this profile doesn’t check out. Saving writes it whole again.</p> : null}
      {error ? <p className="exo-dialog-error" role="alert">{error}</p> : null}
    </Dialog>
  )
}

/** One stick: Sony's preset, its adjustment (the presets that have one) and its deadzone, with the curve they make. */
function StickEditor({ label, stick, onChange }: { label: string; stick: EdgeStick; onChange: (stick: EdgeStick) => void }) {
  const custom = stick.preset === 255
  const [points, setPoints] = useState(stick.points)
  useEffect(() => {
    if (custom) return
    let current = true
    host.controllerCurve(stick.preset, stick.deadzone, stick.adjustment).then(result => { if (current && result.ok && result.points) setPoints(result.points) }, () => { })
    return () => { current = false }
  }, [custom, stick.preset, stick.deadzone, stick.adjustment])
  return (
    <div className="exo-profile-stick">
      <Curve points={custom ? stick.points : points} label={`${label} curve`} />
      <div>
        <Row label={label}>
          <Choice label={`${label} response`} value={custom ? 'custom' : String(stick.preset)}
            choices={custom ? [...PRESETS, { value: 'custom', label: 'Custom' }] : PRESETS}
            onChange={value => { if (value !== 'custom') onChange({ ...stick, preset: Number(value), adjustment: 0 }) }} />
        </Row>
        {!custom && stick.preset > 0 ? (
          <Row label="Adjust" detail={stick.adjustment === 0 ? 'As Sony made it' : stick.adjustment > 0 ? `+${stick.adjustment}` : String(stick.adjustment)}>
            <Slider min={-5} max={5} step={1} value={stick.adjustment} aria-label={`${label} adjustment`}
              onChange={event => onChange({ ...stick, adjustment: Number(event.target.value) })} />
          </Row>
        ) : null}
        <Row label="Deadzone" detail={`${Math.round(stick.deadzone * 100)}%`}>
          <Slider min={0} max={30} step={1} value={Math.round(stick.deadzone * 100)} disabled={custom} aria-label={`${label} deadzone`}
            onChange={event => onChange({ ...stick, deadzone: Number(event.target.value) / 100 })} />
        </Row>
      </div>
    </div>
  )
}

/** A trigger's range: how far in it starts, and where it's fully pressed. */
function TriggerEditor({ label, range, onChange }: { label: string; range: number[]; onChange: (range: number[]) => void }) {
  const [start, full] = range
  return (
    <Row label={label} detail={`Starts at ${start}%, full at ${full}%`}>
      <div className="exo-profile-range">
        <Slider min={0} max={100} step={1} value={start} aria-label={`${label} starts at`} onChange={event => onChange([Math.min(Number(event.target.value), full), full])} />
        <Slider min={0} max={100} step={1} value={full} aria-label={`${label} full at`} onChange={event => onChange([start, Math.max(Number(event.target.value), start)])} />
      </div>
    </Row>
  )
}

/** A stick's response: how far the stick goes (across) against what a game reads (up), through its four points. */
function Curve({ points, label }: { points: number[]; label: string }) {
  const at: string[] = ['0,100']
  for (let i = 0; i < 8; i += 2) at.push(`${(points[i] / 255) * 100},${100 - (points[i + 1] / 255) * 100}`)
  if (points[6] < 255) at.push('100,0')
  return (
    <svg className="exo-profile-curve" viewBox="0 0 100 100" role="img" aria-label={label}>
      <line className="exo-stick-axis" x1="0" y1="100" x2="100" y2="0" />
      <polyline points={at.join(' ')} />
    </svg>
  )
}
