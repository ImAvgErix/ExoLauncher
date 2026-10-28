import { useState } from 'react'
import { host, type PadSettings, type PadStrength } from '../../lib/host'
import { report } from '../../lib/report'
import { Choice } from '../settings/Choice'
import { Row, SwitchRow } from '../settings/parts'

const DEFAULTS: PadSettings = { light: null, playerLight: false, micOff: false, speakerOff: false, vibration: 'full', triggerEffects: 'full' }

/** The light bar: games' own, off, or one of these. */
const LIGHTS = [
  { value: 'games', label: 'Games set it' },
  { value: 'off', label: 'Off' },
  { value: '#FFFFFF', label: 'White' },
  { value: '#1E64FF', label: 'Blue' },
  { value: '#00C8FF', label: 'Cyan' },
  { value: '#8A2BE2', label: 'Purple' },
  { value: '#FF4FA0', label: 'Pink' },
  { value: '#FF2020', label: 'Red' },
  { value: '#FF8C00', label: 'Orange' },
  { value: '#20D060', label: 'Green' },
] as const

const STRENGTHS: { value: PadStrength; label: string }[] = [
  { value: 'full', label: 'Full' }, { value: 'medium', label: 'Medium' }, { value: 'low', label: 'Low' }, { value: 'off', label: 'Off' },
]
// The controller holds trigger motors back at most to a light touch; it has no off for them.
const TRIGGER_STRENGTHS = STRENGTHS.map(s => s.value === 'off' ? { ...s, label: 'Lightest' } : s)

/**
 * A DualSense's or Edge's light, player light, microphone, speaker, vibration and trigger effects (DESIGN.md ›
 * Devices). Each change is sent at once over USB and again each time the controller connects and after each game,
 * since the controller forgets them and a game may set its own light.
 */
export function PadSettingsRows({ id, usb, settings }: { id: string; usb: boolean; settings: PadSettings | null | undefined }) {
  const [current, setCurrent] = useState<PadSettings>(settings ?? DEFAULTS)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  async function change(next: PadSettings) {
    const before = current
    setCurrent(next)
    setBusy(true)
    setError(null)
    try {
      const result = await host.controllerSettings(id, next)
      if (!result.ok) { setCurrent(before); setError(result.message ?? 'The controller didn’t take it.') }
    } catch (cause) {
      report(cause, 'PadSettings.change')
      setCurrent(before)
      setError('The controller didn’t take it. Try again.')
    } finally {
      setBusy(false)
    }
  }
  const off = busy || !usb
  const switched = (key: 'micOff' | 'speakerOff', on: boolean) => change({ ...current, [key]: !on }).then(() => null)
  return (
    <>
      {!usb ? <Row label="Connect it with a cable to change these" /> : null}
      <Row label="Light bar" error={error}>
        <Choice label="Light bar" value={current.light ?? 'games'} choices={LIGHTS} disabled={off}
          onChange={value => void change({ ...current, light: value === 'games' ? null : value })} />
      </Row>
      <SwitchRow id={`pad-player-${id}`} label="Player light" checked={current.playerLight} disabled={off}
        onChange={on => change({ ...current, playerLight: on }).then(() => null)} />
      <SwitchRow id={`pad-mic-${id}`} label="Microphone" checked={!current.micOff} disabled={off} onChange={on => switched('micOff', on)} />
      <SwitchRow id={`pad-speaker-${id}`} label="Speaker" checked={!current.speakerOff} disabled={off} onChange={on => switched('speakerOff', on)} />
      <Row label="Vibration">
        <Choice label="Vibration" value={current.vibration} choices={STRENGTHS} disabled={off}
          onChange={value => void change({ ...current, vibration: value as PadStrength })} />
      </Row>
      <Row label="Trigger effects">
        <Choice label="Trigger effects" value={current.triggerEffects} choices={TRIGGER_STRENGTHS} disabled={off}
          onChange={value => void change({ ...current, triggerEffects: value as PadStrength })} />
      </Row>
    </>
  )
}

/** Controllers' speakers and microphones in Windows: off by default, so Windows never switches your sound to one. */
export function ControllerSoundRow({ on }: { on: boolean | undefined }) {
  const [checked, setChecked] = useState(on ?? false)
  const [busy, setBusy] = useState(false)
  return (
    <SwitchRow id="ctl-sound" label="Sound in Windows" checked={checked} disabled={busy}
      onChange={async next => {
        setBusy(true)
        try {
          const result = await host.controllerSound(next)
          setChecked(result.on)
          return null
        } catch (cause) {
          report(cause, 'ControllerSound.set')
          return 'Windows didn’t take it. Try again.'
        } finally {
          setBusy(false)
        }
      }} />
  )
}
