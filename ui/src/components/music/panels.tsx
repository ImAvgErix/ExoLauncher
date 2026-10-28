import { Fragment, useEffect, useId, useRef, useState, type CSSProperties } from 'react'
import { host } from '../../lib/host'
import { cn } from '../../lib/utils'
import { listenAlong, stopListening, useListenAlong, useListeningFriends } from '../../lib/listenAlong'
import { notify } from '../../lib/notify'
import { confirm } from '../ui/Dialog'
import { report } from '../../lib/report'
import { musicPosition, playbackAnchor, type MusicPlayback } from '../../lib/music'
import { shouldReduceMotion } from '../../lib/motionPreference'
import { onPageActivityChanged, pageIsActive, pageIsVisible } from '../../lib/pageActivity'
import type { LyricsLine, PlayerActOptions, PlayerAction, PlayerFriend, PlayerState, PlayerTrack, PlayerViewData } from '../../lib/player'
import { ChevronDown, ChevronUp, Close, LinkIcon } from '../../brand/icons'
import { Switch } from '../Switch'
import { Qr } from '../ui/Qr'
import { Art } from './Art'
import { Failed, Shelf, TrackList, Waiting } from './pages'
import { runAction, usePlayer, useView } from './playerContext'
import { ErrorNote } from '../ui/ErrorNote'

const arrive = (index: number) => ({ '--i': index }) as CSSProperties

function Failure({ error }: { error: string | null }) {
  return error ? <ErrorNote className="exo-music-error" message={error} /> : null
}

export function QueuePanel({ tick }: { tick: number }) {
  const { adapter, has } = usePlayer()
  // tick changes with every song change, and moved with every queue edit: either reads the queue again.
  const [moved, setMoved] = useState(0)
  // A skip or edit the service refused says so under the title.
  const [failed, setFailed] = useState(false)
  const { data: queue, error } = useView('queue', { offset: tick + moved * 1000 })
  const skip = (track: PlayerTrack) => { setFailed(false); void runAction(adapter, 'skipTo', { uri: track.uri, uid: track.uid }).then(ok => setFailed(!ok)) }
  const none = () => { }
  const edit = (action: PlayerAction, options: PlayerActOptions) => {
    setFailed(false)
    void runAction(adapter, action, options).then(ok => { setFailed(!ok); setMoved(n => n + 1) })
  }
  const queued = queue?.queued ?? []
  return (
    <>
      <h2 className="exo-music-panel-title">Queue</h2>
      <Failure error={error} />
      <Failed when={failed} />
      {!queue && !error ? <Waiting label="Loading the queue" /> : null}
      {queue?.current ?<><h3>Now playing</h3><TrackList tracks={[queue.current]} onPlay={none} onRow={none} /></> : null}
      <Queued queued={queued} skip={skip} edit={edit} />
      {queue ? <QueueNext queue={queue} onAutoplay={on => edit('autoplay', { on })} onRow={skip} /> : null}
      {queue && isEmpty(queue) ? <p className="exo-music-empty">Nothing queued</p> : null}
      {has('history') ? <History tick={tick} /> : null}
    </>
  )
}

const isEmpty = (queue: PlayerViewData) => !queue.current && !queue.queued?.length && !queue.next?.length && !queue.autoplayed?.length

/** What plays next: Spotify and YouTube Music move and remove what you queued; Apple Music removes from Up Next. */
function Queued({ queued, skip, edit }: {
  queued: PlayerTrack[]
  skip: (track: PlayerTrack) => void
  edit: (action: PlayerAction, options: PlayerActOptions) => void
}) {
  const { has } = usePlayer()
  const moves = has('editQueue')
  if (!queued.length) return null
  if (!moves && !has('dequeue')) return <><h3>Next in queue</h3><TrackList tracks={queued} onPlay={() => { }} onRow={skip} /></>
  return (
    <>
      <h3 className="exo-music-queue-head">{moves ? 'Next in queue' : 'Playing next'}
        <button type="button" className="exo-music-queue-clear" onClick={() => edit('clearQueue', {})}>Clear</button>
      </h3>
      <ol className="exo-music-queue">
        {queued.map((track, i) => (
          <li key={track.uid || `${track.uri}:${i}`}>
            <button type="button" className="exo-music-queue-song" onClick={() => skip(track)}>
              <span className="exo-music-row-title">{track.title}</span>
              <span className="exo-music-row-sub">{track.artists?.map(a => a.name).join(', ')}</span>
            </button>
            <span className="exo-music-queue-tools">
              {moves ? (
                <>
                  <button type="button" aria-label={`Move ${track.title} up`} disabled={i === 0}
                    onClick={() => edit('queueMove', { uri: track.uri, uid: track.uid, context: `before|${queued[i - 1]?.uri}|${queued[i - 1]?.uid ?? ''}` })}><ChevronUp size={14} /></button>
                  <button type="button" aria-label={`Move ${track.title} down`} disabled={i === queued.length - 1}
                    onClick={() => edit('queueMove', { uri: track.uri, uid: track.uid, context: `after|${queued[i + 1]?.uri}|${queued[i + 1]?.uid ?? ''}` })}><ChevronDown size={14} /></button>
                </>
              ) : null}
              <button type="button" aria-label={`Remove ${track.title} from the queue`} onClick={() => edit('dequeue', { uri: track.uri, uid: track.uid })}><Close size={14} /></button>
            </span>
          </li>
        ))}
      </ol>
    </>
  )
}

/** Songs you played last, read again with each song change: a click plays one again. */
function History({ tick }: { tick: number }) {
  const { play } = usePlayer()
  const tracks = useView('history', { offset: tick }).data?.tracks
  if (!tracks?.length) return null
  return <><h3>History</h3><TrackList tracks={tracks} onPlay={track => play(undefined, track)} onRow={track => play(undefined, track)} /></>
}

/**
 * What plays after the queue, under the service's Autoplay switch (off: nothing more). Autoplay's songs
 * are YouTube Music's next and Apple Music's autoplayed; Spotify's next plays on with no switch.
 */
function QueueNext({ queue, onAutoplay, onRow }: { queue: PlayerViewData; onAutoplay: (on: boolean) => void; onRow: (track: PlayerTrack) => void }) {
  const { has } = usePlayer()
  const id = useId()
  const switchable = has('autoplay')
  const autoplay = queue.autoplay !== false
  const next = autoplay || !switchable ? queue.next ?? [] : []
  const autoplayed = autoplay ? queue.autoplayed ?? [] : []
  return (
    <>
      {switchable ? (
        <div className="exo-music-autoplay">
          <span className="exo-music-row-title" id={id}>Autoplay</span>
          <Switch checked={autoplay} labelledBy={id} onChange={onAutoplay} />
        </div>
      ) : null}
      {next.length ? <><h3>Next up</h3><TrackList tracks={next} onPlay={() => { }} onRow={onRow} /></> : null}
      {autoplayed.length ? <><h3>Autoplay</h3><TrackList tracks={autoplayed} onPlay={() => { }} onRow={onRow} /></> : null}
    </>
  )
}

export function DevicesPanel() {
  const { adapter } = usePlayer()
  const [refresh, setRefresh] = useState(0)
  const [failed, setFailed] = useState<string | null>(null)
  // refresh re-reads the list after a transfer.
  const { data, error } = useView('devices', { offset: refresh })
  async function transfer(id: string) {
    setFailed(null)
    if (!await runAction(adapter, 'transfer', { context: id })) setFailed("Didn't work. Try again.")
    setRefresh(n => n + 1)
  }
  return (
    <>
      <h2 className="exo-music-panel-title">Play on</h2>
      <Failure error={error ?? failed} />
      {!data && !error ? <Waiting label="Finding devices" /> : data && !data.devices?.length ? <p className="exo-music-empty">No devices found</p> : null}
      <ul className="exo-music-list">
        {(data?.devices ?? []).map((device, index) => (
          <li key={device.id} className="exo-motion-arrive" style={arrive(index)}>
            <button type="button" className={`exo-music-row${device.active ? ' is-on' : ''}`} aria-current={device.active}
              onClick={() => void transfer(device.id)}>
              <span className="exo-music-row-text">
                <span className="exo-music-row-title">{device.local ? 'This computer' : device.name}</span>
                <span className="exo-music-row-sub">{device.active ? 'Playing' : device.type.replace(/_/g, ' ')}</span>
              </span>
            </button>
          </li>
        ))}
      </ul>
    </>
  )
}

function ago(at: number) {
  const minutes = Math.round((Date.now() - at) / 60_000)
  if (minutes < 60) return `${Math.max(1, minutes)} min`
  const hours = Math.round(minutes / 60)
  return hours < 24 ? `${hours} hr` : `${Math.round(hours / 24)} d`
}

/**
 * A count that goes up every ms while Exo is in the foreground, and once when it comes back, so an
 * open panel reads again. Behind a game it rests, and so does the service it would wake.
 */
function usePoll(ms: number) {
  const [tick, setTick] = useState(0)
  useEffect(() => {
    let timer = 0
    let was = pageIsActive()
    const start = () => {
      window.clearInterval(timer)
      timer = 0
      const now = pageIsActive()
      if (now && !was) setTick(n => n + 1)
      was = now
      if (now) timer = window.setInterval(() => setTick(n => n + 1), ms)
    }
    start()
    const off = onPageActivityChanged(start)
    return () => { off(); window.clearInterval(timer) }
  }, [ms])
  return [tick, setTick] as const
}

/** Friend Activity: what the people you follow play, newest first, read again every 30 s while open. */
export function FriendsPanel() {
  const { open, play } = usePlayer()
  const [tick] = usePoll(30_000)
  const { data, error } = useView('friends', { offset: tick })
  const friends = [...(data?.friends ?? [])].sort((a, b) => b.at - a.at)
  return (
    <>
      <h2 className="exo-music-panel-title">Friend activity</h2>
      <Failure error={error} />
      {!data && !error ? <Waiting label="Loading friend activity" /> : null}
      {data && !friends.length ?<p className="exo-music-empty">No friend activity</p> : null}
      <ul className="exo-music-list">
        {friends.map((friend, index) => <FriendRow key={friend.user.uri} friend={friend} index={index} onOpen={open} onPlay={play} />)}
      </ul>
    </>
  )
}

function FriendRow({ friend, index, onOpen, onPlay }: {
  friend: PlayerFriend
  index: number
  onOpen: ReturnType<typeof usePlayer>['open']
  onPlay: ReturnType<typeof usePlayer>['play']
}) {
  const now = Date.now() - friend.at < 15 * 60_000
  const context = friend.track.context
  const contextPage = context && /^spotify:(playlist|album|artist):/.exec(context.uri)?.[1] as 'playlist' | 'album' | 'artist' | undefined
  return (
    <li className="exo-music-friend exo-motion-arrive" style={arrive(index)}>
      <Art src={friend.user.image} round />
      <span className="exo-music-row-text">
        <span className="exo-music-friend-head">
          <span className="exo-music-row-title">{friend.user.name}</span>
          <span className={`exo-music-friend-when${now ? ' is-now' : ''}`}>{now ? 'Now' : ago(friend.at)}</span>
        </span>
        <span className="exo-music-row-sub">
          <button type="button" className="exo-music-link" onClick={() => onPlay(context?.uri, { uri: friend.track.uri })}>{friend.track.title}</button>
          {friend.track.artist ? <> · <button type="button" className="exo-music-link" onClick={() => onOpen({ view: 'artist', uri: friend.track.artist!.uri, title: friend.track.artist!.name })}>{friend.track.artist.name}</button></> : null}
        </span>
        {context && contextPage ? (
          <span className="exo-music-row-sub">
            <button type="button" className="exo-music-link" onClick={() => onOpen({ view: contextPage, uri: context.uri, title: context.name })}>{context.name}</button>
          </span>
        ) : null}
      </span>
    </li>
  )
}

/** Jam: listen along with a Discord friend on any service and, on Spotify, Spotify's own Jam. join: a Jam's token
    from a link opened elsewhere (Discord), joined as soon as the panel shows; onJoin says it was taken. */
export function JamPanel({ join, onJoin }: { join?: string | null; onJoin?: () => void }) {
  const { has } = usePlayer()
  return (
    <>
      <h2 className="exo-music-panel-title">Jam</h2>
      {has('jam') ? <SpotifyJam join={join} onJoin={onJoin} /> : null}
      <ListenAlongList />
    </>
  )
}

/** Your Discord friends listening now, on whatever service: Listen plays their song here, on yours, song after song. */
function ListenAlongList() {
  const { adapter } = usePlayer()
  const along = useListenAlong()
  const { listening, loaded, signedIn } = useListeningFriends()
  const following = along.status === 'off' ? null : along
  const others = listening.filter(({ friend }) => friend.id !== following?.friend.id)

  return (
    <section className="exo-music-along">
      <h3>Listen along</h3>
      {following ? (
        <div className="exo-music-friend">
          <Art src={following.friend.avatar ?? ''} round />
          <span className="exo-music-row-text">
            <span className="exo-music-row-title">{following.friend.name}</span>
            <span className="exo-music-row-sub">{following.status === 'finding' ? `Finding ${following.title}`
              : following.status === 'missing' ? `${following.title} isn't on ${adapter.name}`
                : following.status === 'paused' ? 'Paused' : [following.title, following.artist].filter(Boolean).join(' · ')}</span>
          </span>
          <button type="button" className="exo-set-btn is-compact" onClick={() => stopListening()}>Stop</button>
        </div>
      ) : null}
      {!signedIn ? <p className="exo-music-empty">Sign in to Discord in Exo to listen along with friends</p>
        : loaded && !listening.length ? <p className="exo-music-empty">No friends are listening right now</p> : null}
      {others.length ? (
        <ul className="exo-music-list">
          {others.map(({ friend, rich }, index) => (
            <li key={friend.id} className="exo-music-friend exo-motion-arrive" style={arrive(index)}>
              <Art src={friend.avatar ?? ''} round />
              <span className="exo-music-row-text">
                <span className="exo-music-row-title">{friend.name}</span>
                <span className="exo-music-row-sub">{[rich.details, rich.state, rich.name].filter(Boolean).join(' · ')}</span>
              </span>
              <button type="button" className="exo-set-btn is-compact"
                onClick={() => listenAlong({ id: friend.id, name: friend.name, avatar: friend.avatar }, rich)}>Listen</button>
            </li>
          ))}
        </ul>
      ) : null}
    </section>
  )
}

/** Spotify's Jam: start one, share its link, see who joined, remove someone, leave or end it. */
function SpotifyJam({ join, onJoin }: { join?: string | null; onJoin?: () => void }) {
  const { adapter } = usePlayer()
  const [tick, setTick] = usePoll(5_000)
  const [busy, setBusy] = useState(false)
  const [failed, setFailed] = useState<string | null>(null)
  const { data, error } = useView('jam', { offset: tick })
  const session = data?.session ?? null

  async function run(action: 'jamStart' | 'jamLeave' | 'jamEnd' | 'jamRemove' | 'jamJoin' | 'jamGuestsPlay' | 'jamGuestVolume', options: PlayerActOptions = {}) {
    setBusy(true)
    setFailed(null)
    const ok = await runAction(adapter, action, options)
    if (!ok) setFailed(action === 'jamStart' ? 'Spotify could not start a Jam. Try again.' : action === 'jamJoin' ? 'Spotify could not join that Jam. Check the link.' : 'Spotify did not take that. Try again.')
    setBusy(false)
    setTick(n => n + 1)
  }
  // Ending a Jam ends it for everyone in it: that asks first. Leaving is yours alone.
  async function endOrLeave(owner: boolean) {
    if (owner && await confirm({ title: 'End the Jam?', body: 'It ends for everyone in it, and their music stops.', action: 'End Jam', danger: true }) !== true) return
    await run(owner ? 'jamEnd' : 'jamLeave')
  }
  const joined = useRef<string | null>(null)
  useEffect(() => {
    if (!join) { joined.current = null; return }
    if (joined.current === join) return
    joined.current = join
    onJoin?.()
    void run('jamJoin', { name: join })
    // Once per link: run and onJoin are new each render, the token is what matters.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [join])
  async function copy(link: string) {
    try {
      await navigator.clipboard.writeText(link)
      notify({ text: 'Invite link copied.', tone: 'good' })
    } catch (copyError) {
      report(copyError, 'JamPanel.copy')
      setFailed('Could not copy the link.')
    }
  }

  return (
    <>
      <Failure error={error ?? failed} />
      {!data && !error ? <Waiting label="Loading Jam" /> : null}
      {data && !session ? (
        data.experience === 'NONE'
          ? <p className="exo-music-empty">Not available on this account</p>
          : (
            <>
              <button type="button" className="exo-set-btn is-primary exo-music-jam-start" disabled={busy} onClick={() => void run('jamStart')}>Start a Jam</button>
              <JamJoin busy={busy} onJoin={token => void run('jamJoin', { name: token })} />
            </>
          )
      ) : null}
      {session ? (
        <>
          <div className="exo-music-jam-actions">
            {session.link ? (
              <button type="button" className="exo-set-btn" onClick={() => void copy(session.link)}>
                <LinkIcon size={14} /> Copy invite link
              </button>
            ) : null}
            <button type="button" className="exo-set-btn" disabled={busy}
              onClick={() => void endOrLeave(session.owner)}>{session.owner ? 'End' : 'Leave'}</button>
          </div>
          {session.link ? <Qr text={session.link} label="Jam invite code" className="exo-music-jam-qr" /> : null}
          {session.owner && session.guestsPlay !== undefined ? (
            <label className="exo-music-sound-row">
              <span className="exo-music-sound-copy"><strong>Let others change what's playing</strong></span>
              <Switch checked={session.guestsPlay} label="Let others change what's playing" disabled={busy}
                onChange={on => void run('jamGuestsPlay', { on })} />
            </label>
          ) : null}
          {session.owner && typeof session.guestVolume === 'boolean' ? (
            <label className="exo-music-sound-row">
              <span className="exo-music-sound-copy"><strong>Let others change the volume</strong></span>
              <Switch checked={session.guestVolume} label="Let others change the volume" disabled={busy}
                onChange={on => void run('jamGuestVolume', { on })} />
            </label>
          ) : null}
          <h3>{session.members.length === 1 ? 'Just you' : `${session.members.length} listening`}</h3>
          <ul className="exo-music-list">
            {session.members.map((member, index) => (
              <li key={member.id} className="exo-music-friend exo-motion-arrive" style={arrive(index)}>
                <Art src={member.image} round />
                <span className="exo-music-row-text">
                  <span className="exo-music-row-title">{member.name}</span>
                  <span className="exo-music-row-sub">{member.host ? 'Host' : member.me ? 'You' : 'Guest'}</span>
                </span>
                {session.canRemove && !member.me && !member.host ? (
                  <button type="button" className="exo-set-btn exo-music-toggle" aria-label={`Remove ${member.name}`} disabled={busy}
                    onClick={() => void run('jamRemove', { uid: member.id })}><Close size={14} /></button>
                ) : null}
              </li>
            ))}
          </ul>
        </>
      ) : null}
    </>
  )
}

/** A Jam's token from its invite, as the host reads every form (spotify.link, open.spotify.com/socialsession,
    spotify:socialsession), or the token itself. */
async function jamToken(text: string): Promise<string | null> {
  const value = text.trim()
  if (/^[A-Za-z0-9_-]{4,80}$/.test(value)) return value
  const { uri } = await host.spotifyLink(value)
  return uri?.startsWith('spotify:socialsession:') ? uri.slice('spotify:socialsession:'.length) : null
}

/** Join someone's Jam with the link they shared. */
function JamJoin({ busy, onJoin }: { busy: boolean; onJoin: (token: string) => void }) {
  const [link, setLink] = useState('')
  const [wrong, setWrong] = useState(false)
  const [reading, setReading] = useState(false)
  return (
    <form className="exo-music-jam-join" onSubmit={event => {
      event.preventDefault()
      setReading(true)
      void jamToken(link).catch(() => null).then(token => {
        setReading(false)
        setWrong(!token)
        if (token) onJoin(token)
      })
    }}>
      <h3>Join a Jam</h3>
      <label className="exo-music-search">
        <LinkIcon size={14} />
        <input value={link} placeholder="Paste a Jam link" aria-label="Jam link" onChange={event => { setLink(event.target.value); setWrong(false) }} />
      </label>
      <button type="submit" className="exo-set-btn" disabled={busy || reading || !link.trim()}>Join</button>
      {wrong ? <ErrorNote className="exo-music-error" message="That is not a Jam link. Paste the whole link someone shared." /> : null}
    </form>
  )
}

/** A song's credits: who performed, wrote and produced it, by role, as Spotify's Credits dialog lists them (with each person's roles where Apple Music gives them). */
export function CreditsPanel({ uri }: { uri: string }) {
  const { open } = usePlayer()
  const { data, error } = useView('credits', { uri })
  const groups = data?.groups ?? []
  return (
    <>
      <h2 className="exo-music-panel-title">Credits</h2>
      <Failure error={error} />
      {!data && !error ? <Waiting label="Loading credits" /> : null}
      {data?.title ? <p className="exo-music-row-sub">{data.title}</p> : null}
      {data && !groups.length ? <p className="exo-music-empty">No credits for this song</p> : null}
      {groups.map(group => (
        <section key={group.role} className="exo-music-credits">
          <h3>{group.role}</h3>
          <ul className="exo-music-list">
            {group.people.map(person => (
              <li key={`${person.uri}:${person.name}`}>
                {/^spotify:artist:/.test(person.uri)
                  ? <button type="button" className="exo-music-link" onClick={() => open({ view: 'artist', uri: person.uri, title: person.name })}>{person.name}</button>
                  : person.name}
                {person.roles ? <span className="exo-music-row-sub"> · {person.roles}</span> : null}
              </li>
            ))}
          </ul>
        </section>
      ))}
      {data?.sources ? <p className="exo-music-row-sub">Sources: {data.sources}</p> : null}
    </>
  )
}

/** The playing song large, with its lyrics; synced lines follow the song and a click seeks to one. */
export function NowPlayingPanel({ player, playback, receivedAt }: { player: PlayerState | null; playback: MusicPlayback | null; receivedAt: number }) {
  const { open } = usePlayer()
  const uri = player?.trackUri ?? ''
  return (
    <div className="exo-music-now">
      <Art src={player?.image || playback?.artworkDataUrl || undefined} size={368} />
      <div className="exo-music-now-text">
        <h2>{player?.title || playback?.title || 'Nothing playing'}</h2>
        <p className="exo-music-row-sub">
          {(player?.artists ?? []).map((artist, i) => (
            <span key={`${artist.uri}:${artist.name}`}>{i ? ', ' : ''}
              {artist.uri ? <button type="button" className="exo-music-link" onClick={() => open({ view: 'artist', uri: artist.uri, title: artist.name })}>{artist.name}</button> : artist.name}
            </span>
          ))}
          {player?.artists?.length ? null : playback?.artist}
        </p>
      </div>
      {uri ? <SongTabs uri={uri} playback={playback} receivedAt={receivedAt} /> : null}
    </div>
  )
}

/** Lyrics, and with YouTube Music its Related tab beside them. */
function SongTabs({ uri, playback, receivedAt }: { uri: string; playback: MusicPlayback | null; receivedAt: number }) {
  const { has } = usePlayer()
  const [tab, setTab] = useState<'lyrics' | 'related'>('lyrics')
  if (!has('related')) return <Lyrics uri={uri} playback={playback} receivedAt={receivedAt} />
  const related = tab === 'related'
  return (
    <>
      <div className="exo-shelf-chips" role="tablist" aria-label="About this song">
        <button type="button" role="tab" className="exo-shelf-chip" aria-selected={!related} aria-pressed={!related} onClick={() => setTab('lyrics')}>Lyrics</button>
        <button type="button" role="tab" className="exo-shelf-chip" aria-selected={related} aria-pressed={related} onClick={() => setTab('related')}>Related</button>
      </div>
      {related ? <Related uri={uri} /> : <Lyrics uri={uri} playback={playback} receivedAt={receivedAt} />}
    </>
  )
}

/** The song's Related tab: songs, playlists and artists like it. */
function Related({ uri }: { uri: string }) {
  const { data, error, fresh } = useView('related', { uri })
  const sections = fresh ? (data?.sections ?? []).filter(section => section.items.length) : []
  if (!fresh) return <Waiting label="Loading related music" />
  if (!sections.length) return error ? <Failure error={error} /> : <p className="exo-music-empty">Nothing related to this song</p>
  return <>{sections.map((section, index) => <Shelf key={`${index}:${section.title}`} title={section.title} cards={section.items} more={section.more} />)}</>
}

function Lyrics({ uri, playback, receivedAt }: { uri: string; playback: MusicPlayback | null; receivedAt: number }) {
  const { data, error, fresh } = useView('lyrics', { uri })
  const lines = fresh ? data?.lines ?? [] : []
  const synced = !!data?.synced
  // Word by word (Apple Music's syllable lyrics): the sung words light as the song reaches them.
  const timed = lines.some(line => line.words?.length)
  // Now on this page's clock, ticking while a synced song plays and Exo is on screen. The position
  // runs from when the snapshot arrived, not from when this panel opened.
  const [now, setNow] = useState(() => performance.now())
  useEffect(() => {
    let timer = 0
    const start = () => {
      window.clearInterval(timer)
      timer = 0
      setNow(performance.now())
      if (synced && playback?.status === 'playing' && pageIsVisible()) timer = window.setInterval(() => setNow(performance.now()), timed ? 80 : 250)
    }
    start()
    const off = onPageActivityChanged(start)
    return () => { off(); window.clearInterval(timer) }
  }, [synced, timed, playback])
  const position = playback ? musicPosition(playback, playbackAnchor(playback, receivedAt), now) : null
  let active = -1
  if (synced && position !== null) for (let i = 0; i < lines.length && lines[i].start <= position; i++) active = i
  // A long wait with no one singing (word-timed lyrics): three dots that fill until the next line, which stand in for
  // the line as the one in the middle while it runs.
  const gaps = timed ? gapsOf(lines) : []
  const gap = position !== null ? gaps[active + 1] : undefined
  const waiting = gap && position !== null && position >= gap.from ? gap : null
  const lead = lines.find(line => line.agent)?.agent
  const list = useRef<HTMLOListElement>(null)
  // Keep the sung line (or the wait) in the middle of the panel (only the panel scrolls).
  useEffect(() => {
    const line = list.current?.querySelector<HTMLElement>('.is-active')
    const panel = list.current?.closest<HTMLElement>('.exo-music-panel')
    if (!line || !panel) return
    const top = line.getBoundingClientRect().top - panel.getBoundingClientRect().top + panel.scrollTop
    panel.scrollTo({ top: top - panel.clientHeight / 2 + line.offsetHeight / 2, behavior: shouldReduceMotion() ? 'auto' : 'smooth' })
  }, [active, waiting])
  function seek(start: number) {
    void host.musicControl('seek', { positionMs: start }).catch(seekError => report(seekError, 'NowPlaying.seek'))
  }
  if (!fresh) return <Waiting label="Loading lyrics" />
  if (!lines.length) return error ? <Failure error={error} /> : <p className="exo-music-empty">No lyrics for this song</p>
  const state = (index: number) => index === active && !waiting ? 'is-active' : index < active || index === active && waiting ? 'is-past' : undefined
  return (
    <ol ref={list} className={`exo-music-lyrics${synced ? ' is-synced' : ''}`}>
      {lines.map((line, index) => (
        <Fragment key={index}>
          {gaps[index] ? <Wait gap={gaps[index]!} at={position} on={waiting === gaps[index]} /> : null}
          <li className={cn(state(index), lead && line.agent && line.agent !== lead && 'is-other')}>
            {synced && line.text
              ? <button type="button" onClick={() => seek(line.start)}>{index === active && !waiting && line.words?.length && position !== null ? <Words line={line} at={position} /> : <LineText line={line} />}</button>
              : line.text || ' '}
          </li>
        </Fragment>
      ))}
    </ol>
  )
}

type Gap = { from: number; to: number }

/** No one sings for 4 s or more: a wait worth showing. */
const LONG_WAIT = 4000

/** The wait before each line (before the first: from the song's start), where it's long enough to show. */
function gapsOf(lines: LyricsLine[]): Array<Gap | null> {
  return lines.map((line, index) => {
    const before = index === 0 ? 0 : lines[index - 1].words?.reduce((end, word) => Math.max(end, word.end), 0)
    return before !== undefined && line.start - before >= LONG_WAIT ? { from: before, to: line.start } : null
  })
}

/** Three dots that fill as the wait runs out. */
function Wait({ gap, at, on }: { gap: Gap; at: number | null; on: boolean }) {
  const share = at === null ? 0 : Math.min(1, Math.max(0, (at - gap.from) / (gap.to - gap.from)))
  return (
    <li className={cn('exo-music-lyric-wait', on && 'is-active')} aria-hidden="true">
      {[0, 1, 2].map(dot => <span key={dot} className={on && share > dot / 3 ? 'is-on' : undefined} />)}
    </li>
  )
}

/** A line not being sung: its words, with any background vocals in a smaller row under it. */
function LineText({ line }: { line: LyricsLine }) {
  const bg = line.words?.filter(word => word.bg) ?? []
  if (!bg.length) return <>{line.text}</>
  return <>{line.words!.filter(word => !word.bg).map(word => word.text).join('')}<span className="exo-music-lyric-bg">{bg.map(word => word.text).join('')}</span></>
}

/** The line being sung: each word fills left to right over its own time; background vocals in their row under it. */
function Words({ line, at }: { line: LyricsLine; at: number }) {
  const main = line.words!.filter(word => !word.bg)
  const bg = line.words!.filter(word => word.bg)
  return <>
    {main.map((word, index) => <Word key={index} word={word} at={at} />)}
    {bg.length ? <span className="exo-music-lyric-bg">{bg.map((word, index) => <Word key={index} word={word} at={at} />)}</span> : null}
  </>
}

function Word({ word, at }: { word: NonNullable<LyricsLine['words']>[number]; at: number }) {
  const share = at <= word.start ? 0 : at >= word.end ? 1 : (at - word.start) / Math.max(1, word.end - word.start)
  if (share <= 0 || share >= 1) return <span className={share >= 1 ? 'is-sung' : undefined}>{word.text}</span>
  return <span className="is-singing" style={{ '--exo-sung': share } as CSSProperties}>{word.text}</span>
}
