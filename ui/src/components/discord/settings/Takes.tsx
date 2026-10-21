import { useEffect, useRef, useState } from 'react'

/** Voice Check's two takes, before and after Exo Voice: one plays at a time. */

// The page's CSP lets media load from blob: but not data:, so a take (a data: WAV from the host) plays as a blob.
function blobUrl(dataUrl: string) {
  const bytes = Uint8Array.from(atob(dataUrl.slice(dataUrl.indexOf(',') + 1)), c => c.charCodeAt(0))
  return URL.createObjectURL(new Blob([bytes], { type: 'audio/wav' }))
}

/** The two takes: one plays at a time, and playing one stops the other. */
export function Takes({ before, after }: { before?: string; after?: string }) {
  const player = useRef<HTMLAudioElement | null>(null)
  const [playing, setPlaying] = useState<'before' | 'after' | null>(null)
  const [error, setError] = useState(false)
  const stop = () => {
    const audio = player.current
    player.current = null
    if (!audio) return
    audio.pause()
    URL.revokeObjectURL(audio.src)
  }
  useEffect(() => stop, [])
  const play = (which: 'before' | 'after', src?: string) => {
    stop()
    setError(false)
    if (playing === which || !src) { setPlaying(null); return }
    const audio = new Audio(blobUrl(src))
    audio.onended = () => setPlaying(current => (current === which ? null : current))
    player.current = audio
    setPlaying(which)
    void audio.play().catch(() => { setPlaying(null); setError(true) })
  }
  return (<>
    <button type="button" className="exo-set-btn" aria-pressed={playing === 'before'} onClick={() => play('before', before)}>{playing === 'before' ? 'Stop' : 'Play Before'}</button>
    <button type="button" className="exo-set-btn" aria-pressed={playing === 'after'} onClick={() => play('after', after)}>{playing === 'after' ? 'Stop' : 'Play After'}</button>
    {error ? <span className="exo-dc-tune-finding is-bad" role="alert">Exo couldn’t play it.</span> : null}
  </>)
}
