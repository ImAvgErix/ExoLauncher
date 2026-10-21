import { useEffect, useRef, useState } from 'react'
import { shouldReduceMotion } from '../../lib/motionPreference'
import { onPageActivityChanged, pageIsVisible } from '../../lib/pageActivity'
import { report } from '../../lib/report'

type Mode = 'av1' | 'hls' | 'failed'

/** Steam's 1080p AV1 profile; Chromium decodes it in software when the GPU cannot. */
const AV1 = 'video/mp4; codecs="av01.0.09M.08"'

function canAv1(): boolean {
  try { return typeof MediaSource !== 'undefined' && MediaSource.isTypeSupported(AV1) } catch { return false }
}

/**
 * One of Steam's trailers at the best quality Steam has: its AV1 stream
 * (DASH, the same bitrate as the H.264 one and visibly sharper), else the
 * H.264 HLS stream held at its top level, else the poster. The players load
 * only here, the first time a trailer plays. Starts muted, with controls;
 * under Reduce motion it waits for Play.
 * Out of sight (another room, Exo minimized) it is the poster: the player
 * and its buffers are released, and it starts again on return.
 */
export function TrailerPlayer({ av1, hls, poster, onScreen }: { av1?: string | null; hls?: string | null; poster: string; onScreen: boolean }) {
  const video = useRef<HTMLVideoElement>(null)
  const [mode, setMode] = useState<Mode>(() => av1 && canAv1() ? 'av1' : hls ? 'hls' : 'failed')
  const [visible, setVisible] = useState(pageIsVisible)
  const [autoPlay] = useState(() => !shouldReduceMotion())
  // The poster holds over the player until it really plays: a stream's first decoded frame (often black) would show meanwhile.
  const [playing, setPlaying] = useState(false)
  useEffect(() => onPageActivityChanged(() => setVisible(pageIsVisible())), [])
  const on = onScreen && visible

  useEffect(() => {
    const el = video.current
    if (!el || !on || mode === 'failed') return
    let destroyed = false
    let stop: (() => void) | null = null
    setPlaying(false)
    const fail = (e: unknown) => {
      if (destroyed) return
      report(e, 'Store.trailer')
      setMode(m => m === 'av1' && hls ? 'hls' : 'failed')
    }
    if (mode === 'av1' && av1) {
      void import('dashjs').then(mod => {
        if (destroyed) return
        const player = mod.MediaPlayer().create()
        player.updateSettings({
          debug: { logLevel: 0 as never }, // LogLevel.LOG_LEVEL_NONE (a const enum, not readable at run time)
          // Start at the top quality and stay there unless the connection cannot keep up.
          streaming: { abr: { initialBitrate: { video: 100_000 }, limitBitrateByPortal: false } },
        })
        player.on(mod.MediaPlayer.events.ERROR, fail)
        player.initialize(el, av1, autoPlay)
        stop = () => player.reset()
      }).catch(fail)
    } else if (mode === 'hls' && hls) {
      void import('hls.js').then(({ default: Hls }) => {
        if (destroyed) return
        if (!Hls.isSupported()) { setMode('failed'); return }
        // No worker: the page's policy allows no blob: workers. A high first estimate starts at 1080p.
        const player = new Hls({ enableWorker: false, capLevelToPlayerSize: false, abrEwmaDefaultEstimate: 20_000_000, maxBufferLength: 30 })
        player.on(Hls.Events.ERROR, (_e, data) => { if (data.fatal) fail(new Error(data.details)) })
        player.loadSource(hls)
        player.attachMedia(el)
        stop = () => player.destroy()
      }).catch(fail)
    }
    return () => { destroyed = true; stop?.() }
  }, [mode, av1, hls, on, autoPlay])

  if (mode === 'failed' || !on) return <img className="exo-store-img" src={poster} alt="" />
  return (
    <>
      <video key={mode} ref={video} className="exo-store-video" poster={poster} controls muted autoPlay={autoPlay} playsInline preload="metadata" onPlaying={() => setPlaying(true)} />
      {autoPlay ? <img className={`exo-store-img exo-trailer-cover${playing ? ' is-gone' : ''}`} src={poster} alt="" /> : null}
    </>
  )
}
