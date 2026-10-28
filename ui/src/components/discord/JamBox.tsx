import { useEffect, useState } from 'react'
import { Close } from '../../brand/icons'
import { host, onHostEvent } from '../../lib/host'
import type { MusicPlayback } from '../../lib/music'
import { listenAlong, sameSong, stopListening, useListenAlong, useListeningFriends } from '../../lib/listenAlong'
import { Avatar } from './presence'

/** What Exo's music has on: a song playing or paused, or null. */
function usePlayback() {
  const [now, setNow] = useState<MusicPlayback | null>(null)
  useEffect(() => {
    let live = true
    const take = (playback: MusicPlayback | null | undefined) =>
      setNow(playback && (playback.status === 'playing' || playback.status === 'paused') && playback.title ? playback : null)
    host.musicPlayback().then(result => { if (live) take(result.playback) }).catch(() => { if (live) setNow(null) })
    const off = onHostEvent('music.playback', take)
    return () => { live = false; off() }
  }, [])
  return now
}

/**
 * The Jam box, above Voice connected: one row, while there is a Jam or a song to show. Listening along with someone
 * (their song, and Stop); else a friend in your call playing a song (Listen plays it here, on your service); else the
 * friends on the song you're playing; else the song Exo plays, with pause and play. Friends' songs are their Discord
 * activity (lib/listenAlong.ts); yours is Exo's player, shown here only: Exo never shares it with Discord.
 * callIds: who is in your call.
 */
export function JamBox({ callIds }: { callIds: readonly string[] }) {
  const along = useListenAlong()
  const now = usePlayback()
  const playing = now?.status === 'playing' ? now.title : null
  // Friends are only read while there could be a Jam: in a call, with music playing, or listening along.
  const { listening } = useListeningFriends(along.status !== 'off' || callIds.length > 0 || playing !== null)

  if (along.status !== 'off') {
    const line = along.status === 'finding' ? `Finding ${along.title}` : along.status === 'missing' ? `${along.title} isn't on your service`
      : along.status === 'paused' ? 'Paused' : [along.title, along.artist].filter(Boolean).join(' · ')
    return (
      <section className="exo-dc-jam" aria-label="Jam">
        <Avatar src={along.friend.avatar} name={along.friend.name} status={null} size={32} />
        <span className="exo-dc-me-names"><strong>Listening along with {along.friend.name}</strong><span>{line}</span></span>
        <button type="button" className="exo-dc-icon-btn" aria-label="Stop listening along" onClick={() => stopListening()}><Close size={14} /></button>
      </section>
    )
  }
  const inCall = listening.find(({ friend, rich }) => callIds.includes(friend.id) && !sameSong(rich.details, playing))
  if (inCall) {
    const { friend, rich } = inCall
    return (
      <section className="exo-dc-jam" aria-label="Jam">
        <Avatar src={friend.avatar} name={friend.name} status={null} size={32} />
        <span className="exo-dc-me-names"><strong>{friend.name} is listening</strong><span>{[rich.details, rich.state].filter(Boolean).join(' · ')}</span></span>
        <button type="button" className="exo-set-btn is-compact"
          onClick={() => listenAlong({ id: friend.id, name: friend.name, avatar: friend.avatar }, rich)}>Listen</button>
      </section>
    )
  }
  const withYou = playing ? listening.filter(({ rich }) => sameSong(rich.details, playing)) : []
  if (!withYou.length) return now ? <NowPlaying now={now} /> : null
  const [first] = withYou
  return (
    <section className="exo-dc-jam" aria-label="Jam">
      <Avatar src={first.friend.avatar} name={first.friend.name} status={null} size={32} />
      <span className="exo-dc-me-names">
        <strong>{withYou.length === 1 ? first.friend.name : `${first.friend.name} and ${withYou.length - 1} more`}</strong>
        <span>Listening with you</span>
      </span>
    </section>
  )
}

/** The song Exo plays: its cover, title and artist. Its controls are the Bar's player's. */
function NowPlaying({ now }: { now: MusicPlayback }) {
  return (
    <section className="exo-dc-jam" aria-label="Now playing">
      {now.artworkDataUrl ? <img className="exo-dc-jam-art" src={now.artworkDataUrl} alt="" draggable={false} /> : null}
      <span className="exo-dc-me-names"><strong>{now.title}</strong><span>{now.artist}</span></span>
    </section>
  )
}
