import { useState } from 'react'
import type { MouseStatus } from '@openmouse/protocol/drivers/mouse-types'
import { can } from './mouse'
import type { Section } from './section'
import { ColorField, Panel, Row, Segmented, Slider } from './ui'

/**
 * Finalmouse facts from xpanel (xpanel.finalmouse.com, read with REA):
 * actuation 0.01-0.40 mm (default 0.25), rapid trigger 150-250 µm (default
 * 220), release point Normal 0 / Early 1 / Late 2 per side, Late only on
 * firmware 1.1.1 or newer, custom lift-off 1.0-1.7 mm on firmware 1.1.0 or
 * newer, receiver light Off 0 / Battery 1 / Custom color 2 / TMR-DS 4. A
 * side whose actuation reads 0 is not calibrated and cannot go analog.
 */
const ACTUATION_DEFAULT = 25
const RT_DEFAULT = 220
const LATE_FIRMWARE = '1.1.1'
const CUSTOM_LOD_FIRMWARE = '1.1.0'

// xpanel's own names: TMR Analog, Smart Rapid Trigger, Actuation point, Release point, the TMR-DS panel.
const CLICK = [{ value: 0, label: 'Mechanical' }, { value: 1, label: 'TMR Analog' }] as const
const RELEASE = [{ value: 0, label: 'Normal' }, { value: 1, label: 'Early' }, { value: 2, label: 'Late' }] as const

/** Every version the mouse reports ("Mouse 1.1.1-5289564", "Dongle RF 1.1.1"), by what it is for. */
function firmware(status: MouseStatus): Map<string, string> {
  const map = new Map<string, string>()
  for (const line of status.firmware ?? []) {
    const m = /^(.*?)\s*(\d+(?:\.\d+)+)/.exec(line)
    if (m) map.set(m[1].trim().toLowerCase(), m[2])
  }
  return map
}

function older(version: string, than: string): boolean {
  const a = version.split('.').map(Number)
  const b = than.split('.').map(Number)
  for (let i = 0; i < Math.max(a.length, b.length); i++) {
    if ((a[i] ?? 0) !== (b[i] ?? 0)) return (a[i] ?? 0) < (b[i] ?? 0)
  }
  return false
}

/**
 * Whether the mouse's own firmware (which holds these settings) is at least `version`; null when the
 * mouse has not said, so a setting is offered rather than hidden. The receiver's versions are numbered
 * on their own and gate nothing.
 */
function firmwareAtLeast(status: MouseStatus, version: string): boolean | null {
  const mouse = firmware(status).get('mouse')
  return mouse ? !older(mouse, version) : null
}

/** A Starlight X on its cable (361d:310, 311) keeps the profiles saved for it on its receiver (361d:300, 301). */
export function finalmouseProfileKey(id: string): string {
  return id === '361d:310' ? '361d:300' : id === '361d:311' ? '361d:301' : id
}

/**
 * Why a Starlight X setting shows no value: the mouse did not answer the read (asleep or out of
 * range: it reports its firmware when it does), or its firmware does not report that setting.
 */
export function finalmouseUnread(status: MouseStatus): string {
  return firmware(status).has('mouse') ? 'Not reported by this firmware' : 'Move the mouse to read it'
}

/**
 * The Starlight X's custom lift-off, 1.0-1.7 mm: offered unless the mouse's firmware is known to be
 * older than it (then `needs` says which it needs), and always when it is already set.
 */
export function finalmouseCustomLod(status: MouseStatus): { offered: boolean; needs: string | null } {
  const offered = !!status.finalmousePawLodCustom || firmwareAtLeast(status, CUSTOM_LOD_FIRMWARE) !== false
  return { offered, needs: offered ? null : `Custom needs firmware ${CUSTOM_LOD_FIRMWARE}` }
}

/**
 * The Starlight X's own settings: its TMR analog main switches (mode,
 * actuation, rapid trigger and release point per side), onboard profiles,
 * and the receiver's light and Bhop mode.
 */
export function FinalmousePanels(section: Section) {
  if (!section.status.finalmouseIsSlx) return null
  return (
    <>
      <Switches {...section} />
      <Receiver {...section} />
    </>
  )
}

const TOGETHER = [{ value: 'both', label: 'Together' }, { value: 'apart', label: 'Separate' }] as const

type Side = { mode: number; calibrated: boolean; thr: number; rt: number; rel: number | null | undefined }
type SideSetters = { click: (v: number) => void; thr: (v: number) => void; rt: (v: number) => void; rel: (v: number) => void }

/**
 * The main switches. Both sides set alike show as one set of rows (Together,
 * as Exo's magnetic switches do), written to both sides at once; Separate
 * gives each side its own. A side that is not calibrated stays mechanical, so
 * the sides can only go together when both are.
 */
function Switches({ status, client, apply, busy }: Section) {
  const [pick, setPick] = useState<'both' | 'apart' | null>(null)
  if (!can(client, 'setClickMode')) return null
  const modeL = status.finalmouseClickModeL
  const modeR = status.finalmouseClickModeR
  const mask = status.finalmouseProfileEnabledMask
  const profiles = Array.from({ length: status.finalmouseProfileCount ?? 0 }, (_, i) => i)
    .filter(i => mask == null || (mask & (1 << i)) !== 0 || i === status.finalmouseProfileActive)
  const profileRow = profiles.length > 1 && can(client, 'setActiveProfile') ? (
    <Row label="Onboard profile">
      <Segmented
        label="Onboard profile"
        options={profiles.map(i => ({ value: i, label: status.finalmouseProfileNames?.[i] || String(i + 1) }))}
        value={status.finalmouseProfileActive}
        onChange={v => void apply('setActiveProfile', v)}
        disabled={busy}
      />
    </Row>
  ) : null
  const unread = finalmouseUnread(status)
  // Modes not read (the mouse asleep at the read, or a firmware that does not report them): both sides
  // can still be set together, and the read after the write shows the rest.
  if (modeL == null || modeR == null) {
    return (
      <Panel title="TMR-DS">
        <Row label="Click" value={unread}>
          <Segmented label="Click" options={CLICK} value={null} onChange={v => void apply('setClickMode', v, v)} disabled={busy} />
        </Row>
        {profileRow}
      </Panel>
    )
  }
  const relL = status.finalmouseClickReleaseL
  const relR = status.finalmouseClickReleaseR
  const left: Side = { mode: modeL, calibrated: status.finalmouseTmrThrL !== 0, thr: status.finalmouseTmrThrL || ACTUATION_DEFAULT, rt: status.finalmouseTmrHystL ?? RT_DEFAULT, rel: relL }
  const right: Side = { mode: modeR, calibrated: status.finalmouseTmrThrR !== 0, thr: status.finalmouseTmrThrR || ACTUATION_DEFAULT, rt: status.finalmouseTmrHystR ?? RT_DEFAULT, rel: relR }
  const releases = relL != null && relR != null
  // Late: on firmware 1.1.1 or newer, or when the mouse's firmware is not known; dimmed, with why, on older.
  const lateNeeds = relL !== 2 && relR !== 2 && firmwareAtLeast(status, LATE_FIRMWARE) === false ? `Late needs firmware ${LATE_FIRMWARE}` : null
  const release = lateNeeds ? RELEASE.map(o => ({ ...o, disabled: o.value === 2 })) : RELEASE
  const actuation = can(client, 'setTmrActuation')
  const tmr = (l: number, r: number, hl: number, hr: number) => void apply('setTmrActuation', l / 100, r / 100, hl, hr)
  // The mouse takes both sides in one command. Release points ride along once it reported them or one
  // is picked (the other side Normal); a firmware without them reads back none, and the row says so.
  const setModes = (l: number, r: number, rl = relL, rr = relR) =>
    void (rl != null || rr != null ? apply('setClickMode', l, r, rl ?? 0, rr ?? 0) : apply('setClickMode', l, r))
  const pairable = left.calibrated && right.calibrated
  const alike = left.mode === right.mode && left.thr === right.thr && left.rt === right.rt && left.rel === right.rel
  const together = pairable && (pick ?? (alike ? 'both' : 'apart')) === 'both'

  const rows = (name: string, side: Side, set: SideSetters) => {
    const label = (what: string) => (name ? `${name} ${what.toLowerCase()}` : what)
    return (
      <>
        <Row label={label('Click')}>
          {side.calibrated
            ? <Segmented label={label('Click')} options={CLICK} value={side.mode} onChange={set.click} disabled={busy} />
            : <span className="m-state">Mechanical</span>}
        </Row>
        {side.mode === 1 && actuation ? (
          <>
            <Row label={label('Actuation point')}>
              <Slider label={label('Actuation point')} min={1} max={40} step={1} value={side.thr} format={v => `${(v / 100).toFixed(2)} mm`}
                onCommit={set.thr} disabled={busy} />
            </Row>
            <Row label={label('Smart Rapid Trigger')} value={status.finalmouseTmrHystL == null ? unread : undefined}>
              <Slider label={label('Smart Rapid Trigger')} min={150} max={250} step={10} value={side.rt} format={v => `${v} µm`}
                onCommit={set.rt} disabled={busy} />
            </Row>
            <Row label={label('Release point')} value={releases ? lateNeeds ?? undefined : unread}>
              <Segmented label={label('Release point')} options={release} value={side.rel} onChange={set.rel} disabled={busy} />
            </Row>
          </>
        ) : null}
      </>
    )
  }

  return (
    <Panel title="TMR-DS">
      {pairable ? (
        <Row label="Sides">
          <Segmented label="Sides" options={TOGETHER} value={together ? 'both' : 'apart'} onChange={setPick} />
        </Row>
      ) : null}
      {together ? rows('', left, {
        // Together writes the shown (left) values to both sides.
        click: v => setModes(v, v, relL, relL),
        thr: v => tmr(v, v, left.rt, left.rt),
        rt: v => tmr(left.thr, left.thr, v, v),
        rel: v => setModes(left.mode, left.mode, v, v),
      }) : (
        <>
          {rows('Left', left, {
            click: v => setModes(v, modeR),
            thr: v => tmr(v, right.thr, left.rt, right.rt),
            rt: v => tmr(left.thr, right.thr, v, right.rt),
            rel: v => setModes(modeL, modeR, v, relR),
          })}
          {rows('Right', right, {
            click: v => setModes(modeL, v),
            thr: v => tmr(left.thr, v, left.rt, right.rt),
            rt: v => tmr(left.thr, right.thr, left.rt, v),
            rel: v => setModes(modeL, modeR, relL, v),
          })}
        </>
      )}
      {profileRow}
    </Panel>
  )
}

const LIGHT = [{ value: 0, label: 'Off' }, { value: 1, label: 'Battery level' }, { value: 2, label: 'Custom color' }, { value: 4, label: 'TMR-DS' }] as const
const BHOP = [{ value: 0, label: 'Off' }, { value: 1, label: 'Scroll up' }, { value: 2, label: 'Scroll down' }, { value: 3, label: 'Both' }] as const
const PASS = [{ value: 100, label: '100 ms' }, { value: 500, label: '500 ms' }, { value: 1000, label: '1 s' }, { value: 1500, label: '1.5 s' }] as const

type Rgb = { r: number; g: number; b: number }
/** The receiver's custom color (command 54), which the patched driver reads; see scripts/patch-protocol.mjs. */
export const indicatorColor = (status: MouseStatus) => (status as unknown as { finalmouseIndicatorColor?: Rgb | null }).finalmouseIndicatorColor ?? null
const hex = ({ r, g, b }: Rgb) => `#${[r, g, b].map(v => v.toString(16).padStart(2, '0')).join('')}`
const rgb = (value: string): [number, number, number] => [1, 3, 5].map(i => parseInt(value.slice(i, i + 2), 16)) as [number, number, number]

/**
 * The receiver's light, Bhop mode and its pass-through window; on the cable there is no receiver,
 * so Bhop mode alone. A value the mouse did not report shows with nothing picked and why, and can
 * still be set.
 */
function Receiver({ status, client, apply, busy }: Section) {
  const color = indicatorColor(status)
  const unread = finalmouseUnread(status)
  const cabled = status.connectionType === 'Wired'
  const rows = [
    !cabled && can(client, 'setDongleLedMode') && (
      // The custom color sits in the light's own row, beside the pill, while Custom is on.
      <Row key="led" label="Receiver light" value={status.finalmouseDongleLedMode == null ? unread : undefined}>
        <span className="m-inline">
          {status.finalmouseDongleLedMode === 2 && color && can(client, 'setIndicatorColor') ? (
            <ColorField label="Light color" value={hex(color)} disabled={busy} onCommit={v => void apply('setIndicatorColor', ...rgb(v))} />
          ) : null}
          <Segmented label="Receiver light" options={LIGHT} value={status.finalmouseDongleLedMode} onChange={v => void apply('setDongleLedMode', v)} disabled={busy} />
        </span>
      </Row>
    ),
    can(client, 'setTournamentScrollMode') && (
      <Row key="scroll" label="Bhop mode" value={status.finalmouseTournamentScrollMode == null ? unread : undefined}>
        <Segmented label="Bhop mode" options={BHOP} value={status.finalmouseTournamentScrollMode} onChange={v => void apply('setTournamentScrollMode', v)} disabled={busy} />
      </Row>
    ),
    !!status.finalmouseTournamentScrollMode && can(client, 'setTournamentScrollTimeout') && (
      <Row key="pass" label="Pass-through window" value={status.finalmouseTournamentScrollTimeoutMs == null ? unread : undefined}>
        <Segmented label="Pass-through window" options={PASS} value={status.finalmouseTournamentScrollTimeoutMs} onChange={v => void apply('setTournamentScrollTimeout', v)} disabled={busy} />
      </Row>
    ),
  ].filter(Boolean)
  return rows.length ? <Panel title={cabled ? 'Scroll wheel' : 'Receiver'}>{rows}</Panel> : null
}
