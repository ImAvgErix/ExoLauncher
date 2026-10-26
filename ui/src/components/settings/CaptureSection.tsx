import { memo, useState, type KeyboardEvent } from 'react'
import { host } from '../../lib/host'
import { Slider } from '../ui/Slider'
import { keysLabel, saveCaptureSettings, useCapture, type CaptureSettings, type CaptureState } from '../../lib/capture'
import { Choice } from '../settings/Choice'
import { Row, Section, State, SwitchRow } from './parts'

const MODES = [
  { value: 'games', label: 'While playing' },
  { value: 'always', label: 'Always' },
  { value: 'off', label: 'Off' },
] as const
const LENGTHS = [
  { value: '30', label: '30 s' },
  { value: '60', label: '1 min' },
  { value: '120', label: '2 min' },
  { value: '300', label: '5 min' },
] as const
const RATES = [
  { value: '60', label: '60 fps' },
  { value: '120', label: '120 fps' },
] as const
const KEEP_GB = [
  { value: '0', label: 'No limit' },
  { value: '25', label: '25 GB' },
  { value: '50', label: '50 GB' },
  { value: '100', label: '100 GB' },
] as const
const KEEP_DAYS = [
  { value: '0', label: 'Never' },
  { value: '7', label: '7 days' },
  { value: '30', label: '30 days' },
  { value: '90', label: '90 days' },
] as const

type Keys = 'saveReplayKeys' | 'recordKeys' | 'snipKeys'

/** The keys pressed, as "Ctrl+Alt+F10"; null while only modifiers are down. */
function comboOf(event: KeyboardEvent): string | null {
  const key = event.key
  if (['Control', 'Alt', 'Shift', 'Meta'].includes(key)) return null
  const name = key === ' ' ? 'Space' : key === 'PrintScreen' ? 'PrintScreen' : /^F\d{1,2}$/.test(key) ? key
    : key.length === 1 && /[a-z0-9]/i.test(key) ? key.toUpperCase() : event.code.startsWith('Digit') ? event.code.slice(5) : null
  if (!name) return null
  return [event.ctrlKey ? 'Ctrl' : null, event.altKey ? 'Alt' : null, event.shiftKey ? 'Shift' : null, name].filter(Boolean).join('+')
}

/** A hotkey: press Change, then the keys; Escape keeps the old ones. */
function HotkeyRow({ label, field, settings, ok, disabled }: {
  label: string
  field: Keys
  settings: CaptureSettings | null
  ok: boolean
  disabled: boolean
}) {
  const [listening, setListening] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const id = `set-capture-${field}`
  async function onKeyDown(event: KeyboardEvent<HTMLButtonElement>) {
    if (!listening) return
    event.preventDefault()
    event.stopPropagation()
    if (event.key === 'Escape') { setListening(false); return }
    const combo = comboOf(event)
    if (!combo) return
    setListening(false)
    setError(await saveCaptureSettings({ [field]: combo }))
  }
  return (
    <Row label={label} labelId={id} error={error ?? (!ok ? 'Another app already uses these keys. Pick others.' : null)}>
      <button
        type="button"
        className={`exo-set-btn exo-capture-hotkey${listening ? ' is-listening' : ''}`}
        aria-labelledby={id}
        disabled={disabled}
        onClick={() => { setError(null); setListening(value => !value) }}
        onBlur={() => setListening(false)}
        onKeyDown={event => void onKeyDown(event)}
      >
        {listening ? 'Press keys' : keysLabel(settings?.[field])}
      </button>
    </Row>
  )
}

/**
 * Settings > Capture: Exo Capture's instant replay, quality, keys, folder and
 * storage limit. Every row saves at once. Sound has no rows: every capture has the
 * mix, then the game, Discord voice and the microphone each on its own track.
 */
export const CaptureSection = memo(function CaptureSection() {
  const { state, settings } = useCapture()
  if (state && !state.present) {
    return (
      <Section id="capture" title="Capture">
        <Row label="Exo Capture" detail="Instant replay, recording and snips">
          <State>Not installed</State>
        </Row>
      </Section>
    )
  }
  return (
    <Section id="capture" title="Capture">
      <ReplayRows state={state} settings={settings} />
      <CaptureKeyRows state={state} settings={settings} />
      <StorageRows settings={settings} />
      <ShrinkRow settings={settings} />
    </Section>
  )
})

/** The capture keys and Print Screen, here and in first-run setup. */
export function CaptureKeyRows({ state, settings }: { state: CaptureState | null; settings: CaptureSettings | null }) {
  return (
    <>
      <HotkeyRow label="Save replay" field="saveReplayKeys" settings={settings} ok={state?.hotkeys.saveReplay ?? true} disabled={!settings} />
      <HotkeyRow label="Record" field="recordKeys" settings={settings} ok={state?.hotkeys.record ?? true} disabled={!settings} />
      <HotkeyRow label="Snip" field="snipKeys" settings={settings} ok={state?.hotkeys.snip ?? true} disabled={!settings} />
      <PrintScreenRow state={state} settings={settings} />
    </>
  )
}

const save = (patch: Partial<CaptureSettings>) => saveCaptureSettings(patch)

/** One error per row: a failed save reads under the row whose control made it. */
function useRowErrors() {
  const [error, setError] = useState<{ row: string; text: string } | null>(null)
  return {
    of: (row: string) => (error?.row === row ? error.text : null),
    set: (row: string) => (text: string | null) => setError(text ? { row, text } : null),
  }
}

function encoderLine(state: CaptureState | null): string | undefined {
  if (!state?.codec) return undefined
  const gpu = state.encoder?.replace(/ (HEVC|H\.?264|AV1) Encoder MFT$/i, '')
  return `${state.codec}${gpu ? ` on ${gpu}` : ''}, at the game's own size`
}

function ReplayRows({ state, settings }: { state: CaptureState | null; settings: CaptureSettings | null }) {
  const errors = useRowErrors()
  const [quality, setQuality] = useState<number | null>(null)
  const disabled = !settings
  const commit = () => { if (quality !== null) void save({ quality }).then(e => { errors.set('quality')(e); setQuality(null) }) }
  const shown = quality ?? settings?.quality ?? 50
  return (
    <>
      <Row label="Instant replay" labelId="set-capture-mode" detail="Keeps the last moments in memory so you can save them" error={errors.of('mode')}>
        <Choice
          label="Instant replay"
          value={settings?.replayMode ?? 'games'}
          choices={MODES}
          disabled={disabled}
          onChange={value => void save({ replayMode: value as CaptureSettings['replayMode'] }).then(errors.set('mode'))}
        />
      </Row>
      <Row label="Replay length" labelId="set-capture-length" error={errors.of('length')}>
        <Choice
          label="Replay length"
          value={String(settings?.replaySeconds ?? 60)}
          choices={LENGTHS}
          disabled={disabled}
          onChange={value => void save({ replaySeconds: Number(value) }).then(errors.set('length'))}
        />
      </Row>
      <Row label="Frame rate" labelId="set-capture-fps" detail="60 is lighter on the game; 120 only where your screen runs that fast" error={errors.of('fps')}>
        <Choice
          label="Frame rate"
          value={String(settings?.maxFps ?? 60)}
          choices={RATES}
          disabled={disabled}
          onChange={value => void save({ maxFps: Number(value) }).then(errors.set('fps'))}
        />
      </Row>
      <Row label="Quality" labelId="set-capture-quality" detail={encoderLine(state)} error={errors.of('quality')}>
        <div className="exo-capture-quality">
          <span>Smaller files</span>
          <Slider
            min={0}
            max={100}
            step={5}
            value={shown}
            disabled={disabled}
            aria-labelledby="set-capture-quality"
            onChange={event => setQuality(Number(event.target.value))}
            onPointerUp={commit}
            onKeyUp={commit}
          />
          <span>Best quality</span>
        </div>
      </Row>
    </>
  )
}

function PrintScreenRow({ state, settings }: { state: CaptureState | null; settings: CaptureSettings | null }) {
  const printBlocked = state?.hotkeys.printScreen === false && settings?.printScreen === true
  return (
    <SwitchRow
      id="set-capture-prtsc"
      label="Print Screen snips"
      detail={printBlocked ? 'Windows keeps Print Screen for Snipping Tool. Turn that off in Windows Settings > Accessibility > Keyboard.' : 'The Print Screen key opens Exo’s snip'}
      checked={settings?.printScreen === true}
      disabled={!settings}
      onChange={value => save({ printScreen: value })}
    />
  )
}

function ShrinkRow({ settings }: { settings: CaptureSettings | null }) {
  return (
    <SwitchRow
      id="set-capture-shrink"
      label="Make recordings smaller"
      detail="While you aren’t playing and the PC is plugged in, Exo re-saves your recordings, older ones too, at about a third of the size, with audio untouched. It checks every file looks the same first, and keeps the original if not."
      checked={settings?.autoShrink !== false}
      disabled={!settings}
      onChange={value => save({ autoShrink: value })}
    />
  )
}

function StorageRows({ settings }: { settings: CaptureSettings | null }) {
  const errors = useRowErrors()
  const [picking, setPicking] = useState(false)
  const disabled = !settings
  async function browse() {
    if (picking) return
    setPicking(true)
    const fail = errors.set('folder')
    fail(null)
    try {
      const result = await host.pickFolder('Choose where captures are saved')
      if (result.ok && result.path) fail(await save({ folder: result.path }))
      else if (!result.ok && !result.cancelled) fail(result.message ?? 'Could not open the folder picker.')
    } catch (error) {
      fail(error instanceof Error ? error.message : 'Could not open the folder picker.')
    } finally { setPicking(false) }
  }
  return (
    <>
      <Row label="Save folder" detail={settings?.folder} error={errors.of('folder')}>
        <button type="button" className="exo-set-btn" disabled={disabled || picking} onClick={() => void browse()}>Change</button>
      </Row>
      <Row label="Keep up to" labelId="set-capture-gb" detail="Oldest first; favorites are never deleted" error={errors.of('gb')}>
        <Choice label="Keep up to" value={String(settings?.keepGb ?? 0)} choices={KEEP_GB} disabled={disabled} onChange={value => void save({ keepGb: Number(value) }).then(errors.set('gb'))} />
      </Row>
      <Row label="Delete after" labelId="set-capture-days" error={errors.of('days')}>
        <Choice label="Delete after" value={String(settings?.keepDays ?? 0)} choices={KEEP_DAYS} disabled={disabled} onChange={value => void save({ keepDays: Number(value) }).then(errors.set('days'))} />
      </Row>
    </>
  )
}
