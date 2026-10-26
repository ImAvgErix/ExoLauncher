import { useEffect, useState } from 'react'
import { host } from '../../lib/host'
import { report } from '../../lib/report'
import { usePageClock } from '../../lib/pageActivity'
import { Switch } from '../Switch'
import { Choice } from '../settings/Choice'
import { Slider } from '../ui/Slider'
import { Waiting } from './pages'
import { runAction, usePlayer, useView } from './playerContext'

/** Spotify's own audio settings, as its audio view reads them. */
export type SpotifyAudio = {
  quality: number
  lossless: boolean
  crossfade: boolean
  crossfadeMs: number
  gapless: boolean
  autoMix: boolean
  normalize: boolean
  mono: boolean
  exclusive: boolean | null
  /** Smart Shuffle for what is playing; null where Spotify doesn't offer it (only your playlists and Liked Songs). */
  smartShuffle: boolean | null
  equalizer: { supported: boolean; enabled: boolean | null; filters: { key: string; frequency: number; gain: number }[] | null }
}

const QUALITIES = [
  { value: '0', label: 'Automatic' },
  { value: '1', label: 'Low' },
  { value: '2', label: 'Normal' },
  { value: '3', label: 'High' },
  { value: '4', label: 'Very high' },
  { value: '5', label: 'Lossless' },
]

/**
 * Sound: the service's own audio settings where it has them (Spotify: quality up to Lossless,
 * crossfade, gapless, AutoMix, volume normalization, mono and exclusive mode), and Exo's sleep
 * timer for every service.
 */
export function SoundPanel() {
  const { has } = usePlayer()
  return (
    <>
      <h2 className="exo-music-panel-title">Sound</h2>
      {has('audio') ? <SpotifySound /> : has('quality') ? <ServiceQuality /> : null}
      {has('speed') ? <Speed /> : null}
      <SleepTimer />
    </>
  )
}

/** Audio quality as the service's own settings name its choices (YouTube Music: Low to Always high; Apple Music: High or Standard). */
function ServiceQuality() {
  const { adapter } = usePlayer()
  const [tick, setTick] = useState(0)
  const { data, error } = useView('audio', { offset: tick })
  const [picked, setPicked] = useState<string | null>(null)
  const [failed, setFailed] = useState(false)
  if (!data?.qualities?.length) return error ? <p className="exo-music-error" role="alert">{error}</p> : <Waiting label="Loading audio quality" />
  const value = picked ?? data.quality ?? ''
  return (
    <div className="exo-music-sound">
      {failed ? <p className="exo-music-error" role="alert">{adapter.name} did not take that. Try again.</p> : null}
      <Row label="Audio quality" detail={data.qualities.find(option => option.id === value)?.detail}>
        <Choice label="Audio quality" value={value} choices={data.qualities.map(option => ({ value: option.id, label: option.name }))}
          onChange={next => {
            setPicked(next)
            setFailed(false)
            void runAction(adapter, 'audio', { index: Number(next) }).then(ok => { setFailed(!ok); setPicked(null); setTick(n => n + 1) })
          }} />
      </Row>
    </div>
  )
}

function SpotifySound() {
  const { adapter } = usePlayer()
  const [tick, setTick] = useState(0)
  const { data, error } = useView('audio', { offset: tick })
  const [audio, setAudio] = useState<SpotifyAudio | null>(null)
  const [failed, setFailed] = useState<string | null>(null)
  useEffect(() => { if (data) setAudio(data as unknown as SpotifyAudio) }, [data])
  if (!audio) return error ? <p className="exo-music-error" role="alert">{error}</p> : <Waiting label="Loading sound settings" />
  const set = (setting: string, options: { index?: number; on?: boolean; uid?: string }, next: Partial<SpotifyAudio>) => {
    setAudio(current => current && { ...current, ...next })
    setFailed(null)
    void runAction(adapter, 'audio', { context: setting, ...options }).then(ok => {
      if (!ok) setFailed('Spotify did not take that. Try again.')
      setTick(n => n + 1)
    })
  }
  const qualities = audio.lossless ? QUALITIES : QUALITIES.filter(q => q.value !== '5')
  return (
    <div className="exo-music-sound">
      {failed ? <p className="exo-music-error" role="alert">{failed}</p> : null}
      <Row label="Audio quality" detail={audio.quality === 5 ? 'Up to 24-bit/44.1 kHz FLAC' : audio.lossless ? 'Lossless is on your plan' : undefined}>
        <Choice label="Audio quality" value={String(audio.quality)} choices={qualities}
          onChange={value => set('quality', { index: Number(value) }, { quality: Number(value) })} />
      </Row>
      {audio.exclusive === null ? null : (
        <Row label="Exclusive mode" detail="Bit-perfect sound for Lossless: Spotify alone uses the output device while it plays.">
          <Switch checked={audio.exclusive} label="Exclusive mode" onChange={on => set('exclusive', { on }, { exclusive: on })} />
        </Row>
      )}
      <Row label="Crossfade" detail={audio.crossfade ? `${Math.round(audio.crossfadeMs / 1000)} s between songs` : undefined}>
        <Switch checked={audio.crossfade} label="Crossfade" onChange={on => set('crossfade', { on }, { crossfade: on })} />
      </Row>
      {audio.crossfade ? (
        <Slider className="exo-music-sound-range" min={1} max={12} step={1} value={Math.max(1, Math.round(audio.crossfadeMs / 1000))}
          aria-label="Crossfade length in seconds"
          onChange={event => setAudio(current => current && { ...current, crossfadeMs: Number(event.target.value) * 1000 })}
          onPointerUp={event => set('crossfadeMs', { index: Number((event.target as HTMLInputElement).value) * 1000 }, {})}
          onKeyUp={event => set('crossfadeMs', { index: Number((event.target as HTMLInputElement).value) * 1000 }, {})} />
      ) : null}
      <Row label="Gapless playback">
        <Switch checked={audio.gapless} label="Gapless playback" onChange={on => set('gapless', { on }, { gapless: on })} />
      </Row>
      <Row label="AutoMix" detail="Spotify mixes the playlists it can and crossfades every other song">
        <Switch checked={audio.autoMix} label="AutoMix" onChange={on => set('autoMix', { on }, on ? { autoMix: on, crossfade: true } : { autoMix: on })} />
      </Row>
      <Row label="Normalize volume" detail="Every song at the same loudness">
        <Switch checked={audio.normalize} label="Normalize volume" onChange={on => set('normalize', { on }, { normalize: on })} />
      </Row>
      <Row label="Smart Shuffle" detail={audio.smartShuffle === null ? 'Works when you play your playlists or Liked Songs' : "Shuffle adds songs picked for what you're playing"}>
        {audio.smartShuffle === null ? null : (
          <Switch checked={audio.smartShuffle} label="Smart Shuffle" onChange={on => set('smartShuffle', { on }, { smartShuffle: on })} />
        )}
      </Row>
      <Row label="Mono audio">
        <Switch checked={audio.mono} label="Mono audio" onChange={on => set('mono', { on }, { mono: on })} />
      </Row>
      {audio.equalizer.supported && audio.equalizer.filters && audio.equalizer.enabled !== null ? (
        <>
          <Row label="Equalizer" detail="Spotify's own six bands">
            <Switch checked={audio.equalizer.enabled} label="Equalizer"
              onChange={on => set('equalizer', { on }, { equalizer: { ...audio.equalizer, enabled: on } })} />
          </Row>
          {audio.equalizer.enabled ? (
            <div className="exo-music-eq">
              {audio.equalizer.filters.map(band => (
                <label key={band.key} className="exo-music-eq-band">
                  <span>{band.gain > 0 ? '+' : ''}{Math.round(band.gain)}</span>
                  <Slider vertical min={-12} max={12} step={1} value={Math.round(band.gain)} aria-label={`${hz(band.frequency)} gain`}
                    onChange={event => {
                      const gain = Number(event.target.value)
                      setAudio(current => current && { ...current, equalizer: { ...current.equalizer, filters: current.equalizer.filters?.map(f => f.key === band.key ? { ...f, gain } : f) ?? null } })
                    }}
                    onPointerUp={event => set('equalizerGain', { index: Number((event.target as HTMLInputElement).value), uid: band.key }, {})}
                    onKeyUp={event => set('equalizerGain', { index: Number((event.target as HTMLInputElement).value), uid: band.key }, {})} />
                  <span>{hz(band.frequency)}</span>
                </label>
              ))}
            </div>
          ) : null}
        </>
      ) : null}
    </div>
  )
}

const hz = (frequency: number) => frequency >= 1000 ? `${frequency / 1000} kHz` : `${frequency} Hz`

const SPEEDS = [5, 8, 10, 12, 15, 20, 25, 30, 35]

/** Podcasts' and audiobooks' playback speed, the service's own (Spotify: 0.5 to 3.5 times). */
function Speed() {
  const { adapter, revision } = usePlayer()
  const { data } = useView('player', { offset: revision })
  const [speed, setSpeed] = useState<number | null>(null)
  const [failed, setFailed] = useState(false)
  const now = Math.round((speed ?? data?.speed ?? 1) * 10)
  return (
    <>
      <Row label="Playback speed" detail="For podcasts and audiobooks">
        <Choice label="Playback speed" value={String(SPEEDS.includes(now) ? now : 10)}
          choices={SPEEDS.map(value => ({ value: String(value), label: `${value / 10}×` }))}
          onChange={value => {
            setSpeed(Number(value) / 10)
            setFailed(false)
            void runAction(adapter, 'speed', { index: Number(value) }).then(ok => { if (!ok) { setFailed(true); setSpeed(null) } })
          }} />
      </Row>
      {failed ? <p className="exo-music-error" role="alert">{adapter.name} did not take that. Try again.</p> : null}
    </>
  )
}

function Row({ label, detail, children }: { label: string; detail?: string; children: React.ReactNode }) {
  return (
    <div className="exo-music-sound-row">
      <span className="exo-music-sound-copy"><strong>{label}</strong>{detail ? <span>{detail}</span> : null}</span>
      {children}
    </div>
  )
}

// ---- sleep timer: Exo's own, so it works the same for every service ----

let sleepAt: number | null = null
let sleepTimer = 0
const sleepListeners = new Set<() => void>()

function setSleep(minutes: number | null) {
  window.clearTimeout(sleepTimer)
  sleepAt = minutes ? Date.now() + minutes * 60_000 : null
  if (minutes) {
    sleepTimer = window.setTimeout(() => {
      sleepAt = null
      sleepListeners.forEach(listener => listener())
      void host.musicControl('pause').catch(error => report(error, 'SleepTimer.pause'))
    }, minutes * 60_000)
  }
  sleepListeners.forEach(listener => listener())
}

const SLEEP = [15, 30, 45, 60, 90]

function SleepTimer() {
  const [, setNow] = useState(0)
  useEffect(() => {
    const update = () => setNow(Date.now())
    sleepListeners.add(update)
    return () => { sleepListeners.delete(update) }
  }, [])
  // The minutes left count down while Exo is on screen, and catch up when it comes back.
  usePageClock(30_000, Date.now)
  const left = sleepAt ? Math.max(1, Math.round((sleepAt - Date.now()) / 60_000)) : null
  return (
    <Row label="Sleep timer" detail={left ? `Pauses in ${left} min` : 'Pause the music after a while'}>
      <Choice label="Sleep timer" value={left ? 'on' : 'off'}
        choices={[{ value: 'off', label: left ? 'Turn off' : 'Off' }, ...(left ? [{ value: 'on', label: `${left} min left` }] : []), ...SLEEP.map(m => ({ value: String(m), label: `${m} min` }))]}
        onChange={value => { if (value === 'off') setSleep(null); else if (value !== 'on') setSleep(Number(value)) }} />
    </Row>
  )
}
