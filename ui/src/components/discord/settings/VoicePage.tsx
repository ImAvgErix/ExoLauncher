import { useEffect, useRef, useState } from 'react'
import { host, onHostEvent, type MicLevel, type VoiceCheckResult, type VoiceStages } from '../../../lib/host'
import { setDiscordPrefs, useDiscordPrefs, type DiscordClientSettings, type DiscordKeybind } from '../../../lib/discordPrefs'
import { Choice } from '../../settings/Choice'
import { Menu } from '../../ui/Menu'
import { Slider } from '../../ui/Slider'
import { Switch } from '../../Switch'
import { KeybindRecorder } from './KeybindsPage'
import { SoundRow } from './AppPages'
import { Category, LoadError, Row, SliderRow, ToggleRow, useClientSettings } from './parts'
import { cameraError } from '../../../lib/goLive'
import { usePageActive } from '../../../lib/pageActivity'
import { Takes } from './Takes'

type Audio = Awaited<ReturnType<typeof host.discordAudio>>
type Save = (change: Partial<DiscordClientSettings>) => Promise<string | null>

const VOICE_SOUNDS: [string, string][] = [
  ['deafen', 'Deafen'], ['undeafen', 'Undeafen'], ['mute', 'Mute'], ['unmute', 'Unmute'], ['camera_on', 'Camera On'], ['camera_off', 'Camera Off'],
  ['disconnect', 'Voice Disconnected'], ['ptt_start', 'PTT Activate'], ['ptt_stop', 'PTT Deactivate'], ['user_join', 'User Join'],
  ['user_leave', 'User Leave'], ['call_calling', 'Outgoing Ring'],
]

/** Discord settings > Voice & Video: devices and volumes, the input profile and its dials, Push to Talk, attenuation, the camera and the call's sounds. */
export function VoicePage() {
  const { settings, save, error, reload } = useClientSettings()
  return (
    <>
      <h1>Voice & Video</h1>
      <LoadError error={error} onRetry={reload} />
      <Devices />
      {settings ? <InputProfile settings={settings} save={save} /> : null}
      {settings ? <Advanced settings={settings} save={save} /> : null}
      <Camera />
      <Category title="Sounds">
        {VOICE_SOUNDS.map(([sound, label]) => <SoundRow key={sound} label={label} sound={sound} client={settings} save={save} />)}
      </Category>
    </>
  )
}

function Devices() {
  const [audio, setAudio] = useState<Audio | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [round, setRound] = useState(0)
  useEffect(() => {
    host.discordAudio({}).then(read => { setAudio(read); setError(null) }).catch(() => setError("Exo couldn't read your microphones and speakers."))
  }, [round])
  const save = (change: Parameters<typeof host.discordAudio>[0]) => {
    setError(null)
    void host.discordAudio(change).then(setAudio).catch(() => setError("Exo didn't save that. Try again."))
  }
  if (!audio) return <LoadError error={error} onRetry={() => setRound(r => r + 1)} />
  const device = (side: 'input' | 'output') => {
    const list = side === 'input' ? audio.inputs : audio.outputs
    // With none at all, the menu would offer only Default: say so instead.
    if (list.length === 0) return <span className="exo-dc-settings-value">{side === 'input' ? 'No microphone found' : 'No speakers found'}</span>
    const chosen = (side === 'input' ? audio.input : audio.output) ?? ''
    return (
      <Menu label={side === 'input' ? 'Input Device' : 'Output Device'} anchor={side === 'input' ? '--exo-dc-in' : '--exo-dc-out'} offset={4}
        triggerClassName="exo-shelf-sort-trigger exo-dc-settings-device" trigger={list.find(d => d.id === chosen)?.name ?? 'Default'}
        items={[{ id: '', label: 'Default' }, ...list.map(d => ({ id: d.id, label: d.name }))]} checked={chosen}
        onAction={id => save(side === 'input' ? { input: id } : { output: id })} />
    )
  }
  return (
    <Category title="Voice">
      {error ? <p className="exo-dc-settings-note" role="alert">{error}</p> : null}
      <Row label="Microphone">{device('input')}</Row>
      <SliderRow label="Microphone Volume" min={0} max={200} value={audio.inputVolume} render={v => `${v}%`} onCommit={v => save({ inputVolume: v })} />
      <Row label="Speaker">{device('output')}</Row>
      <SliderRow label="Speaker Volume" min={0} max={200} value={audio.outputVolume} render={v => `${v}%`} onCommit={v => save({ outputVolume: v })} />
      <ToggleRow label="Mute Soundboards" detail="Sounds others play in a call stay silent for you." checked={audio.soundboardMuted} onChange={on => save({ soundboardMuted: on })} />
      <SliderRow label="Soundboard Volume" min={0} max={100} value={audio.soundboardVolume} disabled={audio.soundboardMuted} render={v => `${v}%`} onCommit={v => save({ soundboardVolume: v })} />
    </Category>
  )
}

function InputProfile({ settings, save }: { settings: DiscordClientSettings; save: Save }) {
  const live = useMicLevel()
  const ptt = settings.keybinds.find(k => k.action === 'PUSH_TO_TALK')
  const setPtt = (keys: number[]) => {
    const others = settings.keybinds.filter(k => k !== ptt)
    const next: DiscordKeybind[] = [{ action: 'PUSH_TO_TALK', keys }, ...others]
    void save({ keybinds: next })
  }
  return (
    <Category>
      <LiveVoice live={live} />
      <VoiceCheck />
      <ToggleRow label="Noise Suppression" detail="Takes background noise and typing out of your mic." checked={settings.noiseSuppression}
        onChange={value => save({ noiseSuppression: value })} />
      <ToggleRow label="Noise Suppression for Others" detail="Takes background noise out of everyone else's voice, for you. Each person talking uses about as much of your PC as your own mic's."
        checked={settings.othersNoiseSuppression} onChange={value => save({ othersNoiseSuppression: value })} />
      <ToggleRow label="Echo Cancellation" detail="Takes your speakers out of your mic, so nobody hears themselves. Not needed, so not run, on headphones."
        checked={settings.echoCancellation} onChange={value => save({ echoCancellation: value })} />
      <Sensitivity settings={settings} save={save} live={live} />
      <ToggleRow label="Automatic Gain Control" detail="Automatically adjust microphone volume to keep it clear and consistent." checked={settings.autoGain}
        onChange={value => save({ autoGain: value })} />
      <ToggleRow label="Push to Talk" detail="Activates your mic only when you hold down a designated key." checked={settings.pushToTalk} onChange={value => save({ pushToTalk: value })} />
      {settings.pushToTalk ? (
        <>
          <Row label="Push to Talk Keybind" detail="You can add multiple Push to Talk binds under System > Custom Keybinds.">
            <KeybindRecorder label="Push to Talk Keybind" keys={ptt?.keys ?? []} onChange={setPtt} />
          </Row>
          <SliderRow label="Push to Talk Release Delay" min={0} max={2000} step={10} value={settings.pttDelay}
            render={v => (v >= 1000 ? `${(v / 1000).toFixed(2)}s` : `${v} ms`)} onCommit={v => void save({ pttDelay: v })} />
        </>
      ) : null}
    </Category>
  )
}

const CHECK_SECONDS = 8

/**
 * Voice Check: talk (and type, if you like) for a few seconds, then hear yourself before and after, as the
 * call sends you. A mic that clips or is too quiet is named, with one press to set Microphone Volume right.
 */
function VoiceCheck() {
  const [result, setResult] = useState<VoiceCheckResult | null>(null)
  const [left, setLeft] = useState<number | null>(null)
  const record = () => {
    setResult(null)
    setLeft(CHECK_SECONDS)
    const timer = window.setInterval(() => setLeft(n => (n && n > 1 ? n - 1 : n)), 1000)
    host.discordVoiceCheck(CHECK_SECONDS)
      .then(setResult)
      .catch(() => setResult({ ok: false, message: 'The microphone didn\'t answer.' }))
      .finally(() => { window.clearInterval(timer); setLeft(null) })
  }
  return (
    <>
      <Row label="Voice Check" detail={left !== null ? `Talk now… ${left}` : 'Talk for a few seconds, then hear yourself as the call hears you.'} error={result && !result.ok ? result.message : null}>
        <button type="button" className="exo-set-btn" disabled={left !== null} onClick={record}>{result?.ok ? 'Again' : 'Record'}</button>
      </Row>
      {result?.ok ? <VoiceCheckResults result={result} /> : null}
    </>
  )
}

// Peaks near full scale clip; a voice peaking under -24 dB is too quiet for any processing to rescue cleanly.
function micAdvice(r: VoiceCheckResult): { text: string; target?: number } | null {
  if ((r.voicedShare ?? 0) < 0.1) return { text: "No voice heard: talk during the recording, then play it back." }
  const peak = r.peakDb ?? -100
  if ((r.clipped ?? 0) > 0 || peak > -1) return { text: 'Your mic clipped: it was too loud for a moment, which sounds harsh.', target: -6 }
  if (peak < -24) return { text: 'Your mic is quiet: turning it up gives the cleanest sound.', target: -8 }
  return null
}

function VoiceCheckResults({ result }: { result: VoiceCheckResult }) {
  const [volume, setVolume] = useState<number | null>(null)
  const [fixed, setFixed] = useState(false)
  useEffect(() => { host.discordAudio({}).then(audio => setVolume(audio.inputVolume)).catch(() => { }) }, [])
  const advice = micAdvice(result)
  const fix = () => {
    if (advice?.target === undefined || volume === null) return
    const next = Math.round(Math.max(25, Math.min(200, volume * 10 ** ((advice.target - (result.peakDb ?? 0)) / 20))))
    void host.discordAudio({ inputVolume: next }).then(audio => { setVolume(audio.inputVolume); setFixed(true) }).catch(() => { })
  }
  const db = (v?: number) => (v === undefined ? '–' : v < -90 ? 'silent' : `${Math.round(v)} dB`)
  return (
    <div className="exo-dc-settings-row exo-dc-voice-check">
      <span className="exo-dc-settings-copy">
        <strong>Before and after</strong>
        <span>Peak {db(result.peakDb)} · room {db(result.noiseDb)}, {db(result.noiseAfterDb)} after · your voice {db(result.voiceDb)}</span>
        {advice && !fixed ? <span className="exo-dc-voice-check-advice">{advice.text}</span> : null}
        {fixed ? <span>Microphone Volume is now {volume}%. Record again to hear it.</span> : null}
      </span>
      <span className="exo-dc-voice-check-takes">
        <Takes before={result.before} after={result.after} />
        {advice?.target !== undefined && !fixed ? <button type="button" className="exo-set-btn is-primary" onClick={fix}>Fix Microphone Volume</button> : null}
      </span>
    </div>
  )
}

const SUPPRESSION: Record<VoiceStages['suppression'], string> = {
  on: 'DeepFilterNet', light: 'DeepFilterNet, its lighter stage alone while your PC is busy', mic: 'Your microphone’s own, so Exo’s steps aside', loading: 'Loading…', missing: 'Not downloaded yet', off: 'Off',
}
const GAIN: Record<VoiceStages['gain'], string> = { exo: 'Exo’s', windows: 'Windows’', off: 'Off' }
const ECHO: Record<VoiceStages['echo'], string> = { on: 'On in calls, for your speakers', head: 'Not needed on headphones', off: 'Off' }
const percent = (db: number) => `${Math.max(0, Math.min(100, db + 100))}%`

/**
 * The microphone right now, through the call's own chain: how loud it comes in, what is sent (green while the gate
 * lets the voice through), and every stage that ran on it, so each switch can be seen working.
 */
function LiveVoice({ live }: { live: MicLevel | null }) {
  const detail = !live ? 'Opening your microphone…' : live.voiced ? 'Sending your voice.' : 'Nothing sent: the gate holds back what isn’t your voice.'
  return (
    <div className="exo-dc-settings-row is-stacked">
      <span className="exo-dc-settings-copy"><strong>Your mic, live</strong><span>{detail}</span></span>
      <div className="exo-dc-live-meters">
        <LiveMeter label="Microphone" db={live?.level ?? -100} />
        <LiveMeter label="Sent" db={live?.after ?? -100} voiced={live?.voiced} />
      </div>
      {live ? <Stages s={live.stages} /> : null}
    </div>
  )
}

function LiveMeter({ label, db, voiced = false }: { label: string; db: number; voiced?: boolean }) {
  return (
    <>
      <span className="exo-dc-tune-note">{label}</span>
      <div className={`exo-dc-settings-meter${voiced ? ' is-voiced' : ''}`} role="meter" aria-label={label} aria-valuemin={-100} aria-valuemax={0} aria-valuenow={Math.round(db)}>
        <span className="exo-dc-settings-meter-level" style={{ width: percent(db) }} />
      </div>
    </>
  )
}

function Stages({ s }: { s: VoiceStages }) {
  const strength = `, turns noise down by up to ${s.suppressionLimitDb} dB`
  return (
    <dl className="exo-dc-tune-facts">
      <dt>Noise suppression</dt><dd>{SUPPRESSION[s.suppression]}{s.suppression === 'on' || s.suppression === 'light' ? strength : ''}</dd>
      <dt>Echo cancellation</dt><dd>{ECHO[s.echo]}</dd>
      <dt>Gain control</dt><dd>{GAIN[s.gain]}</dd>
    </dl>
  )
}

/** Input Sensitivity: automatic or a threshold, over the level the gate judges (after noise suppression; green while it is open). */
function Sensitivity({ settings, save, live }: { settings: DiscordClientSettings; save: Save; live: MicLevel | null }) {
  const level = live ?? { gateLevel: -100, voiced: false }
  const [threshold, setThreshold] = useState<number | null>(null)
  const shown = threshold ?? settings.sensitivity
  const commit = (value: number) => { setThreshold(null); if (value !== settings.sensitivity) void save({ sensitivity: value }) }
  return (
    <>
      <Row label="Automatically determine input sensitivity">
        <Switch checked={settings.autoSensitivity} label="Automatically determine input sensitivity" onChange={value => void save({ autoSensitivity: value })} />
      </Row>
      <div className="exo-dc-settings-row is-stacked">
        <span className="exo-dc-settings-copy"><strong>Input Sensitivity</strong>{settings.autoSensitivity ? null : <span>{shown} dB</span>}</span>
        <div className={`exo-dc-settings-meter${level.voiced ? ' is-voiced' : ''}`} role="meter" aria-label="Microphone level"
          aria-valuemin={-100} aria-valuemax={0} aria-valuenow={Math.round(level.gateLevel)} aria-valuetext={level.voiced ? 'Speaking' : 'Not speaking'}>
          <span className="exo-dc-settings-meter-level" style={{ width: percent(level.gateLevel) }} />
          {settings.autoSensitivity ? null : (
            <Slider className="is-bare" min={-100} max={0} value={shown} aria-label="Input Sensitivity"
              onChange={event => setThreshold(Number(event.target.value))}
              onPointerUp={event => commit(Number(event.currentTarget.value))} onKeyUp={event => commit(Number(event.currentTarget.value))} />
          )}
        </div>
      </div>
    </>
  )
}

// The live microphone: the host opens it while this shows and Exo is in use, renewed every 5 s, and stops it when it
// goes or Exo is left (a game in front, Exo minimized). Null until the first level arrives.
function useMicLevel() {
  const [level, setLevel] = useState<MicLevel | null>(null)
  const active = usePageActive()
  useEffect(() => {
    if (!active) return
    const renew = () => void host.discordMicTest(true).catch(() => { })
    renew()
    const timer = window.setInterval(renew, 5000)
    const off = onHostEvent('discord.micLevel', setLevel)
    return () => { window.clearInterval(timer); off(); setLevel(null); void host.discordMicTest(false).catch(() => { }) }
  }, [active])
  return active ? level : null
}

function Advanced({ settings, save }: { settings: DiscordClientSettings; save: Save }) {
  return (
    <Category title="Advanced">
      <h3 className="exo-dc-settings-fieldset">Global Attenuation</h3>
      <SliderRow label="Attenuation Strength" detail="Lower the volume of other applications by this percent when someone is speaking. Set to 0% to completely disable global attenuation."
        min={0} max={100} value={settings.attenuation} render={v => `${v}%`} onCommit={v => void save({ attenuation: v })} />
      <ToggleRow label="When I speak" checked={settings.attenuateSelf} disabled={settings.attenuation === 0} onChange={value => save({ attenuateSelf: value })} />
      <ToggleRow label="When others speak" checked={settings.attenuateOthers} disabled={settings.attenuation === 0} onChange={value => save({ attenuateOthers: value })} />
    </Category>
  )
}

/** Camera: which camera calls use, and Test Video to see it. */
function Camera() {
  const prefs = useDiscordPrefs()
  const [cameras, setCameras] = useState<MediaDeviceInfo[]>([])
  const [testing, setTesting] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const video = useRef<HTMLVideoElement>(null)
  useEffect(() => {
    navigator.mediaDevices?.enumerateDevices().then(list => setCameras(list.filter(d => d.kind === 'videoinput'))).catch(() => { })
  }, [testing])
  useEffect(() => {
    if (!testing) return
    let stream: MediaStream | null = null
    let live = true
    navigator.mediaDevices.getUserMedia({ video: prefs.cameraId ? { deviceId: { exact: prefs.cameraId } } : true, audio: false }).then(s => {
      if (!live) { s.getTracks().forEach(t => t.stop()); return }
      stream = s
      if (video.current) video.current.srcObject = s
    }).catch((why: unknown) => { if (live) { setError(cameraError(why)); setTesting(false) } })
    return () => { live = false; stream?.getTracks().forEach(t => t.stop()) }
  }, [testing, prefs.cameraId])
  // Before the camera was first allowed, Windows lists cameras without ids or names: only Default can be chosen then.
  const named = cameras.filter(c => c.deviceId)
  return (
    <Category title="Camera">
      <div className="exo-dc-settings-preview">
        {testing ? <video ref={video} autoPlay muted playsInline /> : null}
        <button type="button" className="exo-set-btn" onClick={() => { setError(null); setTesting(t => !t) }}>{testing ? 'Stop Video' : 'Test Video'}</button>
        {error ? <em role="alert">{error}</em> : null}
      </div>
      <Row label="Camera" detail={cameras.length > 0 && named.length === 0 ? 'Test Video once to choose among your cameras.' : undefined}>
        {cameras.length === 0 ? <span className="exo-dc-settings-value">No camera found</span> : (
          <Choice label="Camera" value={prefs.cameraId ?? ''} onChange={id => setDiscordPrefs({ cameraId: id || null })}
            choices={[{ value: '', label: 'Default' }, ...named.map((c, i) => ({ value: c.deviceId, label: c.label || `Camera ${i + 1}` }))]} />
        )}
      </Row>
    </Category>
  )
}
