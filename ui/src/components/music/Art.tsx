import { useEffect, useState } from 'react'
import { Folder, Heart, MusicNotes } from '../../brand/icons'

/**
 * Cover art that never flashes empty: a new picture is fetched and decoded
 * first, and the previous one stays until it is ready. Liked Songs is a heart
 * and a folder its icon; art the service has no picture for shows a quiet note.
 */

/** Liked Songs (Spotify) and Liked Music (YouTube Music, playlist LM) draw the heart when they have no picture. */
export function isLikedCard(card: { kind: string; uri: string; image?: string }): boolean {
  return !card.image && (card.kind === 'collection' || /:VLLM$/.test(card.uri))
}

export function Art({ src, round = false, liked = false, folder = false, size, className }: {
  src?: string
  round?: boolean
  liked?: boolean
  folder?: boolean
  size?: number
  className?: string
}) {
  const [shown, setShown] = useState(src ?? '')
  const [failed, setFailed] = useState(false)
  useEffect(() => {
    const next = src ?? ''
    if (next === shown) return
    if (!next) { setShown(''); return }
    let live = true
    const image = new Image()
    image.decoding = 'async'
    image.src = next
    image.decode().then(
      () => { if (live) { setShown(next); setFailed(false) } },
      () => { if (live) { setShown(next); setFailed(true) } })
    return () => { live = false }
  }, [src, shown])
  const style = size ? { width: size, height: size } : undefined
  const classes = ['exo-music-art', round && 'is-round', (liked || folder) && 'is-icon', className].filter(Boolean).join(' ')
  if (liked || folder) {
    const glyph = size ? Math.round(size / 3) : 16
    return <span className={classes} style={style}>{liked ? <Heart size={glyph} filled /> : <Folder size={glyph} />}</span>
  }
  return (
    <span className={classes} style={style}>
      {shown && !failed
        ? <img key={shown} src={shown} alt="" loading="lazy" decoding="async" onError={() => setFailed(true)} />
        : src && !failed ? null : <MusicNotes size={size ? Math.round(size / 4) : 14} />}
    </span>
  )
}
