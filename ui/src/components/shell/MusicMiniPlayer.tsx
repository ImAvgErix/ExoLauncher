import { useEffect, useRef, useState, type ReactNode } from 'react'
import { host, onHostEvent, type MusicVolumeSource } from '../../lib/host'
import { musicTime, musicPosition, playbackAnchor, warmMusic, type MusicAction, type MusicPlayback, type MusicService } from '../../lib/music'
import { MusicNotes, Next, Pause, Play, Previous } from '../../brand/icons'
import { MusicVolume } from './MusicVolume'
import { onPageActivityChanged, pageIsActive, pageIsVisible } from '../../lib/pageActivity'
import { BarCapsule } from './BarCapsule'
import { Slider } from '../ui/Slider'
import { ErrorNote } from '../ui/ErrorNote'

/** anchor: performance.now() at which playback.positionMs was true. floor: the
    position already on screen when a resync arrived slightly behind it; the
    clock holds there until the true position catches up instead of stepping back. */
type Clock = { anchor: number; floor: number | null }
type Snapshot = { playback: MusicPlayback; clock: Clock }
/** The app's own level in percent; null until the app has answered once. */
type Volume = { level: number | null; source: MusicVolumeSource | null }
type Optimistic = { playing?: boolean }
const trackKey = (playback?: MusicPlayback) => `${playback?.title ?? ''}
${playback?.artist ?? ''}`

/** Nothing loaded, or an idle provider session (no track length before
    anything plays): not a song to show. */
function isIdleSession(playback: MusicPlayback) {
  return playback.status === 'none' || !playback.title || (playback.status !== 'playing' && !(playback.durationMs && playback.durationMs > 0))
}

/** Already shown or older than what is shown (an RPC answer overtaken by an event). */
function isStale(playback: MusicPlayback, shown: MusicPlayback | undefined) {
  return shown?.seq !== undefined && playback.seq !== undefined && playback.seq <= shown.seq
}

function shownPosition(playback: MusicPlayback, clock: Clock, now: number) {
  const position = musicPosition(playback, clock.anchor, now)
  return position !== null && clock.floor !== null ? Math.max(position, clock.floor) : position
}

/** Anchor a fresh snapshot on this page's clock. The same song arriving up to
    1.2 s behind what is on screen (0.3 s once paused) is a resync, not a seek:
    the time holds rather than jumping back. */
function nextClock(previous: Snapshot | null, playback: MusicPlayback, now: number): Clock {
  const anchor = playbackAnchor(playback, now)
  if (!previous || previous.playback.provider !== playback.provider || trackKey(previous.playback) !== trackKey(playback)) return { anchor, floor: null }
  const before = shownPosition(previous.playback, previous.clock, now)
  const after = musicPosition(playback, anchor, now)
  const slack = playback.status === 'playing' ? 1200 : 300
  return { anchor, floor: before !== null && after !== null && before > after && before - after < slack ? before : null }
}

function withOptimisticPlayback(playback: MusicPlayback | undefined, optimistic: Optimistic | null) {
  if (!playback) return undefined
  if (!optimistic) return playback
  const playing = optimistic.playing ?? (playback.status === 'playing')
  return {
    ...playback,
    status: (playing ? 'playing' : 'paused') as MusicPlayback['status'],
    canPlay: playing === false ? true : playback.canPlay,
    canPause: playing === true ? true : playback.canPause,
  }
}

/** Follows the provider while the shell is up, also while the Music room hides
    the mini player, so it returns with the live song instead of a stale one.
    The volume is its own state: it arrives on its own clock (the provider's
    page often answers before its song does), so it is never dropped for want
    of a song, and while it is unknown it is asked for again until it answers. */
function usePlayback(service: MusicService, hidden: boolean) {
  const [snapshot, setSnapshot] = useState<Snapshot | null>(null)
  const [volume, setVolume] = useState<Volume>({ level: null, source: null })
  const refresh = useRef<() => void>(() => { })
  const readVolume = useRef<() => void>(() => { })
  useEffect(() => {
    let live = true, reading = false, heard = 0
    setVolume({ level: null, source: null })
    // Another provider's snapshot (a switch in flight) or an answer older than
    // an event already shown is ignored; the host clears a switched provider itself.
    const accept = (playback: MusicPlayback | null) => {
      if (!live) return
      const now = performance.now()
      setSnapshot(previous => {
        const current = playback && playback.provider === service && !isStale(playback, previous?.playback) ? playback : previous?.playback
        if (!current || (previous && previous.playback === current)) return previous
        return { playback: current, clock: nextClock(previous, current, now) }
      })
    }
    const acceptVolume = (level: number, source: MusicVolumeSource | null) => {
      if (!live) return
      setVolume(previous => previous.level === level && previous.source === (source ?? previous.source) ? previous : { level, source: source ?? previous.source })
    }
    // Volume is optional: a session without a volume endpoint still shows its
    // song, so the two are asked for separately. A read that started before the
    // app's own volume event is older than it and is dropped.
    const read = async () => {
      if (reading) return
      reading = true
      const since = heard
      try {
        const result = await host.musicVolume()
        if (live && since === heard && result.ok && typeof result.level === 'number') acceptVolume(result.level * 100, result.source ?? null)
      } catch { /* asked again while unknown */ } finally { reading = false }
    }
    refresh.current = () => {
      void host.musicPlayback().then(state => accept(state.playback)).catch(() => { })
      void read()
    }
    readVolume.current = () => void read()
    refresh.current()
    const offPlayback = onHostEvent('music.playback', data => accept(data as MusicPlayback))
    // The app's own slider moved (Spotify, YouTube Music): follow it at once.
    const offVolume = onHostEvent('music.volume', data => { if (typeof data?.level === 'number') { heard++; acceptVolume(data.level * 100, 'app') } })
    return () => { live = false; refresh.current = () => { }; readVolume.current = () => { }; offPlayback(); offVolume() }
  }, [service])

  // A session volume (Apple Music) has no app event; re-read it while
  // the player is on screen so the slider follows the Windows mixer. A level
  // not known yet (the app is starting, has no audio session yet) is asked for
  // the same way until it answers. Behind another window (a game, the mixer)
  // it is read every five seconds instead, and at once when Exo comes back.
  const follow = !hidden && !!snapshot && (volume.source === 'session' || volume.level === null)
  useEffect(() => {
    if (!follow) return
    let timer = 0
    let every = 0
    const stop = () => { if (timer) { window.clearInterval(timer); timer = 0 } }
    const start = () => {
      if (!pageIsVisible()) { stop(); return }
      const next = pageIsActive() ? 1000 : 5000
      if (timer && next === every) return
      if (!timer || next < every) readVolume.current()
      stop()
      every = next
      timer = window.setInterval(() => readVolume.current(), every)
    }
    start()
    const off = onPageActivityChanged(start)
    return () => { stop(); off() }
  }, [follow])
  return { snapshot, volume, refresh }
}

/** A provider can report no song for a moment between two (Spotify swapping tracks, a page
    reloading). The last song stays up through that gap, up to four seconds, so the capsule
    never collapses and regrows between songs. */
function useHeldSnapshot(snapshot: Snapshot | null): Snapshot | null {
  const idle = !snapshot || isIdleSession(snapshot.playback)
  const last = useRef<Snapshot | null>(null)
  const [expired, setExpired] = useState(false)
  if (!idle) last.current = snapshot
  useEffect(() => {
    setExpired(false)
    if (!idle) return
    const timer = window.setTimeout(() => setExpired(true), 4000)
    return () => clearTimeout(timer)
  }, [idle])
  if (!idle) return snapshot
  return expired ? null : last.current
}

export function MusicMiniPlayer({ service, onOpen, hidden = false }: { service: MusicService; onOpen: () => void; hidden?: boolean }) {
  const { snapshot: live, volume, refresh } = usePlayback(service, hidden)
  const snapshot = useHeldSnapshot(live)
  const [pending, setPending] = useState<MusicAction | null>(null)
  const pendingRef = useRef<MusicAction | null>(null)
  const controls = useRef<MusicAction[]>([])
  const alive = useRef(true)
  const [error, setError] = useState<string | null>(null)
  const [seekPreview, setSeekPreview] = useState<{ key: string; position: number } | null>(null)
  const [optimistic, setOptimistic] = useState<Optimistic | null>(null)
  const optimisticTimer = useRef(0)
  useEffect(() => { alive.current = true; return () => { alive.current = false; controls.current = []; clearTimeout(optimisticTimer.current) } }, [])
  const playback = withOptimisticPlayback(snapshot?.playback, optimistic)

  useEffect(() => {
    if (!error) return
    const timer = window.setTimeout(() => setError(null), 4500)
    return () => clearTimeout(timer)
  }, [error])

  function predictControl(action: MusicAction) {
    clearTimeout(optimisticTimer.current)
    if (action === 'play' || action === 'pause' || action === 'toggle')
      setOptimistic({ playing: action === 'toggle' ? playback?.status !== 'playing' : action === 'play' })
    if (action === 'next' || action === 'previous') setSeekPreview(null)
  }

  async function control(action: MusicAction) {
    controls.current.push(action)
    setError(null)
    predictControl(action)
    if (pendingRef.current) return
    while (controls.current.length && alive.current) {
      const next = controls.current.shift()!
      pendingRef.current = next; setPending(next)
      try {
        const result = await host.musicControl(next)
        if (alive.current && !result.ok && !controls.current.length) {
          setOptimistic(null); setError(result.message ?? 'Playback control unavailable.')
        }
      } catch {
        if (alive.current && !controls.current.length) { setOptimistic(null); setError('The player could not respond.') }
      } finally {
        pendingRef.current = null
      }
    }
    if (alive.current) {
      setPending(null)
      // The host republishes on accepted controls; release optimism shortly
      // after so a silently ignored control snaps back.
      optimisticTimer.current = window.setTimeout(() => setOptimistic(null), 1500)
    }
  }

  // Its own capsule right of the Rooms: it slides in and out there, so nothing else in the Bar moves.
  if (hidden || !playback || !snapshot || isIdleSession(playback)) return <BarCapsule>{null}</BarCapsule>
  const playing = playback.status === 'playing'
  const seekKey = `${service}:${trackKey(playback)}`
  const preview = seekPreview?.key === seekKey ? seekPreview.position : null
  return (
    <BarCapsule>
    <div className="exo-music-mini" role="group" aria-label="Mini player" onPointerEnter={warmMusic}>
      {/* A press shows its result at once (optimistic) and the player catches up quietly behind it. */}
      <div className="exo-mini-controls" role="group" aria-label="Music playback" aria-busy={pending !== null}>
        <button className="exo-mini-skip" aria-label="Previous song" disabled={!playback.canPrev} onClick={() => void control('previous')}><Previous size={22} /></button>
        <button className="exo-mini-play" aria-label={playing ? 'Pause music' : 'Play music'} disabled={!(playing ? playback.canPause : playback.canPlay)} onClick={() => void control('toggle')}>{playing ? <Pause size={22} /> : <Play size={22} />}</button>
        <button className="exo-mini-skip" aria-label="Next song" disabled={!playback.canNext} onClick={() => void control('next')}><Next size={22} /></button>
      </div>
      {/* The song's place is the line alone: no numbers, so the capsule stays short. */}
      <MusicClock playback={snapshot.playback} clock={snapshot.clock} preview={preview}>
        {position => (
          <>
            <div className="exo-mini-lcd">
              <button className="exo-mini-track" onClick={onOpen} aria-label={`Open Music: ${playback.title} by ${playback.artist}`}>
                <MiniArtwork src={playback.artworkDataUrl ?? null} track={trackKey(playback)} />
                <span key={trackKey(playback)} className="exo-mini-info exo-motion-switch"><span className="exo-mini-title">{playback.title}</span><span className="exo-mini-artist">{playback.artist}</span></span>
              </button>
              <MusicSeek key={seekKey} position={position} duration={playback.durationMs ?? null} enabled={playback.canSeek === true} onPreview={position => setSeekPreview(position === null ? null : { key: seekKey, position })} />
            </div>
          </>
        )}
      </MusicClock>
      <MusicVolume key={service} volume={volume.level} onRefresh={() => refresh.current()} />
      {error && <ErrorNote as="span" className="exo-mini-error" message={error} />}
    </div>
    </BarCapsule>
  )
}

/** The ticker lives here, so only the position readouts repaint while a song
    plays. It wakes on each whole second of the song (not of the wall clock), so
    the time never skips or repeats a second; it runs only while the position
    advances and Exo is on screen (focused or not), and each snapshot re-anchors it. */
function MusicClock({ playback, clock, preview, children }: { playback: MusicPlayback; clock: Clock; preview: number | null; children: (position: number | null) => ReactNode }) {
  const [now, setNow] = useState(() => performance.now())
  const rate = playback.status === 'playing' && typeof playback.rate === 'number' && playback.rate > 0 ? playback.rate : 0
  useEffect(() => {
    let timer = 0
    const stop = () => { if (timer) { window.clearTimeout(timer); timer = 0 } }
    const tick = () => {
      const at = performance.now()
      setNow(at)
      timer = 0
      if (!rate || !pageIsVisible()) return
      const position = shownPosition(playback, clock, at) ?? 0
      timer = window.setTimeout(tick, Math.min(1000, Math.max(40, (1000 - position % 1000) / rate + 10)))
    }
    tick()
    const off = onPageActivityChanged(() => { stop(); tick() })
    return () => { stop(); off() }
  }, [playback, clock, rate])
  return children(preview ?? shownPosition(playback, clock, now))
}

/**
 * The cover never blanks or flickers between songs: the next song's art is decoded off screen and
 * fades in over the last one only once it is ready (one retry after a failed load), so the last
 * cover stays until then. The same song's cover never unloads: an update without art (or with the
 * same art at another size) keeps it. A song that really has no art shows the note after a while.
 */
function MiniArtwork({ src, track }: { src: string | null; track: string }) {
  // under: the cover it replaced, kept beneath while the new one fades in over it.
  const [shown, setShown] = useState<{ src: string; track: string; under: string | null } | null>(null)
  const held = shown?.track === track
  useEffect(() => {
    let live = true
    let timer = 0
    if (!src) {
      if (held) return
      timer = window.setTimeout(() => setShown(null), 6000)
      return () => clearTimeout(timer)
    }
    let tries = 0
    const load = () => {
      const image = new Image()
      image.referrerPolicy = 'no-referrer'
      image.src = src
      image.decode()
        .then(() => { if (live) setShown(previous => previous?.src === src ? previous : { src, track, under: previous?.src ?? null }) })
        .catch(() => { if (live && tries++ < 1) timer = window.setTimeout(load, 350) })
    }
    load()
    return () => { live = false; clearTimeout(timer) }
  }, [src, track, held])
  if (!shown) return <span className="exo-mini-art"><MusicNotes size={16} /></span>
  return (
    <span className="exo-mini-cover">
      {shown.under ? <img src={shown.under} alt="" referrerPolicy="no-referrer" /> : null}
      <img key={shown.src} className={shown.under ? 'is-over' : undefined} src={shown.src} alt="" referrerPolicy="no-referrer" onError={() => setShown(null)} />
    </span>
  )
}

function MusicSeek({ position, duration, enabled, onPreview }: { position: number | null; duration: number | null; enabled: boolean; onPreview: (position: number | null) => void }) {
  const [error, setError] = useState<string | null>(null)
  const timer = useRef(0), alive = useRef(true), dragging = useRef(false), revision = useRef(0)
  useEffect(() => { alive.current = true; return () => { alive.current = false; clearTimeout(timer.current) } }, [])
  const setDraft = onPreview
  const value = position ?? 0
  async function commit(target: number) {
    clearTimeout(timer.current)
    const request = ++revision.current
    try {
      const result = await host.musicControl('seek', { positionMs: target })
      if (alive.current && request === revision.current) { setError(result.ok ? null : result.message ?? 'Seeking is unavailable.'); if (!result.ok) setDraft(null) }
    } catch { if (alive.current && request === revision.current) { setDraft(null); setError('The player could not seek.') } }
    finally { if (alive.current && request === revision.current) { timer.current = window.setTimeout(() => setDraft(null), 1200) } }
  }
  return <>
    <Slider compact className="exo-mini-seek" min={0} max={duration ?? 1} step={500} value={value}
      aria-label="Seek song" aria-valuetext={`${musicTime(value)} of ${musicTime(duration)}`} disabled={!enabled || !duration}
      onPointerDown={() => { dragging.current = true }}
      onChange={event => { const next = Number(event.target.value); setDraft(next); if (!dragging.current) void commit(next) }}
      onPointerUp={event => { dragging.current = false; void commit(Number(event.currentTarget.value)) }}
      onPointerCancel={() => { dragging.current = false; setDraft(null) }} />
    {error && <ErrorNote as="span" className="exo-mini-error" message={error} />}
  </>
}
