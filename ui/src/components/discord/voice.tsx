import { useCallback, useEffect, useLayoutEffect, useRef, useState, useSyncExternalStore, type CSSProperties } from 'react'
import { host, onHostEvent } from '../../lib/host'
import { useNotice } from '../../lib/notify'
import { report } from '../../lib/report'
import { useLens } from '../../lib/glass'
import type { DiscordChannel, DiscordVoice, DiscordVoiceMember } from '../../lib/discord'
import { useVideoFeed } from '../../lib/discordVideo'
import { clearGoLiveMessage, startCamera, stopCamera, useCamera, useGoLive } from '../../lib/goLive'
import { ChevronDown, ChevronUp, Close } from '../../brand/icons'
import { MyStreamTile, ShareScreen, useStreamTile } from './GoLivePicker'
import { Icons } from './icons'
import { SoundboardButton } from './Soundboard'
import { setAudioUser, useAccountSettings } from '../../lib/discordPrefs'
import { Slider } from '../ui/Slider'
import { Decoration } from './collectibles'
import { Spinner } from '../ui/Spinner'

const OFF: DiscordVoice = { state: 'off', guildId: null, channelId: null, muted: false, deafened: false, e2ee: false }

/** The call this client is in, who is speaking, an incoming DM call, and the actions on them. */
export function useDiscordVoice() {
  const [voice, setVoice] = useState<DiscordVoice>(OFF)
  const [speaking, setSpeaking] = useState<Set<string>>(new Set())
  const [ringing, setRinging] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  // A call whose camera goes on once it connects: Start Video Call, Join with Video.
  const cameraFor = useRef<string | null>(null)
  useEffect(() => {
    if (!cameraFor.current || voice.state !== 'connected' || voice.channelId !== cameraFor.current) return
    cameraFor.current = null
    void startCamera().then(why => { if (why) setError(why) })
  }, [voice.state, voice.channelId])

  useEffect(() => {
    // A call that started ringing before this mounted (the room opened from its notification) shows too.
    void host.discordVoice('state').then(result => {
      if (!result.voice) return
      setVoice(result.voice)
      const rung = result.voice.ringing
      if (rung) setRinging(current => current ?? rung)
    }).catch(() => { })
    const offVoice = onHostEvent('discord.voice', next => {
      setVoice(next)
      if (next.state === 'off') setSpeaking(new Set())
      // The call ended on its own (a join Discord never answered, a connection that gave up): say why.
      if (next.state === 'off' && next.message) setError(next.message)
    })
    const offSpeaking = onHostEvent('discord.speaking', ({ userId, speaking: on }) => setSpeaking(set => {
      if (set.has(userId) === on) return set
      const next = new Set(set)
      if (on) next.add(userId); else next.delete(userId)
      return next
    }))
    const offCall = onHostEvent('discord.call', call => setRinging(current => call.ringing ? call.channelId : current === call.channelId ? null : current))
    return () => { offVoice(); offSpeaking(); offCall() }
  }, [])

  const run = useCallback(async (action: Parameters<typeof host.discordVoice>[0], params?: Parameters<typeof host.discordVoice>[1]) => {
    setError(null)
    try {
      const result = await host.discordVoice(action, params)
      if (result.voice) setVoice(result.voice)
      if (!result.ok) setError(result.message ?? 'Discord voice did not answer.')
      return result.ok
    } catch (err) {
      report(err, 'DiscordVoice.' + action)
      setError('Discord voice did not answer.')
      return false
    }
  }, [])

  return {
    voice, speaking, ringing, error,
    /** video: the camera goes on as it connects (Join with Video). */
    join: async (guildId: string | null, channelId: string, video = false) => {
      setRinging(null)
      cameraFor.current = video ? channelId : null
      const ok = await run('join', { guildId, channelId })
      if (!ok) cameraFor.current = null
      return ok
    },
    /** A DM call that rings the others, as Discord's Start Voice Call; video: Start Video Call, the camera on as it connects. */
    call: async (channelId: string, video = false) => {
      cameraFor.current = video ? channelId : null
      if (await run('join', { guildId: null, channelId })) void host.discordRing(channelId).catch(() => { })
      else cameraFor.current = null
    },
    leave: () => run('leave'),
    setMuted: (muted: boolean) => run('set', { muted }),
    setDeafened: (deafened: boolean) => run('set', { deafened }),
    /** Decline: Discord stops ringing you here and in your other apps. */
    decline: (channelId: string) => {
      setRinging(null)
      void host.discordDecline(channelId).catch((err: unknown) => report(err, 'DiscordVoice.decline'))
    },
    dismissError: () => setError(null),
  }
}

export type VoiceControls = ReturnType<typeof useDiscordVoice>

// The one mark Discord shows after a person in a voice channel: deafened (by the server, or themselves), muted (by
// the server, themselves, or you), most serious first; the server's own in red.
function VoiceFlag({ member, mutedByYou }: { member: DiscordVoiceMember; mutedByYou: boolean }) {
  const flag = member.serverDeaf ? ['Server deafened', 'is-server', true] as const : member.deaf ? ['Deafened', '', true] as const
    : member.serverMute ? ['Server muted', 'is-server', false] as const : member.mute ? ['Muted', '', false] as const
      : mutedByYou ? ['Muted by you', 'is-yours', false] as const : null
  if (!flag) return null
  const [label, kind, deaf] = flag
  return <span className={`exo-dc-voice-flag ${kind}`} role="img" aria-label={label}>{deaf ? <Icons.Deafened size={14} /> : <Icons.Muted size={14} />}</span>
}

/**
 * Who is in a voice channel, under it in the list, as Discord shows them: a ring while someone speaks (not someone
 * you muted), LIVE while they stream, a camera while it's on, and their mute or deafen mark.
 */
export function VoiceMembers({ members, speaking }: { members: DiscordVoiceMember[]; speaking: Set<string> }) {
  const audio = useAccountSettings().audioUsers
  return (
    <ul className="exo-dc-voice-members">
      {members.map(member => {
        const mutedByYou = audio[member.id]?.muted === true
        return (
          <li key={member.id} className={speaking.has(member.id) && !member.mute && !mutedByYou ? 'is-speaking' : undefined} data-dc-user={member.id} data-dc-name={member.name} data-dc-call="">
            {member.avatar ? <img src={member.avatar} alt="" draggable={false} loading="lazy" /> : <span className="exo-dc-voice-dot" />}
            <span className="exo-dc-item-name">{member.name}</span>
            {member.stream ? <span className="exo-dc-voice-live">LIVE</span> : null}
            {member.video ? <span className="exo-dc-voice-flag" role="img" aria-label="Camera on"><Icons.Camera size={14} /></span> : null}
            <VoiceFlag member={member} mutedByYou={mutedByYou} />
          </li>
        )
      })}
    </ul>
  )
}

// Discord's volume curve: its slider moves in steps you hear as even (amplitude^(1/2.8) below 100, +6 dB at 200),
// what plays is the amplitude; within one step of 100 it is 100.
const toSlider = (volume: number) => volume <= 0 ? 0 : (volume < 100 ? Math.pow(volume / 100, 1 / 2.8) : 20 * Math.log10(volume / 100) / 6 + 1) * 100
const fromSlider = (at: number) => at <= 0 ? 0 : Math.abs(at - 100) < 1 ? 100 : (at < 100 ? Math.pow(at / 100, 2.8) : Math.pow(10, (at / 100 - 1) * 6 / 20)) * 100

/**
 * Discord's call items in a person's menu: User Volume (0-200%) and Mute, only for you, heard at once and kept on
 * the account; while you watch their Go Live, its Stream Volume and Mute Stream too, as in Discord (User Volume is
 * their voice alone, so it never moved the stream's sound); on yourself, your own Mute and Deafen.
 */
export function CallPersonItems({ userId, me }: { userId: string; me: boolean }) {
  const settings = useAccountSettings()
  const audio = settings.audioUsers[userId] ?? { muted: false, volume: 100 }
  const sound = settings.audioStreams[userId] ?? { muted: false, volume: 100 }
  const watched = useWatchedIds().includes(userId)
  const own = useDiscordVoice().voice
  if (me) {
    return (
      <>
        <span className="exo-dc-menu-line" role="separator" />
        <MenuCheck label="Mute" on={own.muted} onToggle={() => void host.discordVoice('set', { muted: !own.muted })} />
        <MenuCheck label="Deafen" on={own.deafened} onToggle={() => void host.discordVoice('set', { deafened: !own.deafened })} />
      </>
    )
  }
  return (
    <>
      <span className="exo-dc-menu-line" role="separator" />
      <VolumeRow label="User volume" volume={audio.volume} onChange={volume => setAudioUser(userId, { volume })} />
      {watched ? <VolumeRow label="Stream volume" volume={sound.volume} disabled={sound.muted} onChange={volume => setAudioUser(userId, { volume }, true)} /> : null}
      <span className="exo-dc-menu-line" role="separator" />
      <MenuCheck label="Mute" on={audio.muted} onToggle={() => setAudioUser(userId, { muted: !audio.muted })} />
      {watched ? <MenuCheck label="Mute stream" on={sound.muted} onToggle={() => setAudioUser(userId, { muted: !sound.muted }, true)} /> : null}
    </>
  )
}

// A volume row of the person menu: 0-200% on Discord's curve.
function VolumeRow({ label, volume, disabled, onChange }: { label: string; volume: number; disabled?: boolean; onChange: (volume: number) => void }) {
  const at = toSlider(volume)
  return (
    <div className="exo-dc-ctx-volume" role="group" aria-label={label}>
      <span>{label}</span>
      <span className="exo-dc-ctx-volume-row">
        <Slider min={0} max={200} value={at} aria-label={label} disabled={disabled} onChange={event => onChange(fromSlider(Number(event.target.value)))} />
        <span>{Math.round(at)}%</span>
      </span>
    </div>
  )
}

// A checkbox row, as the person menu draws them.
function MenuCheck({ label, on, onToggle }: { label: string; on: boolean; onToggle: () => void }) {
  return (
    <button type="button" role="menuitemcheckbox" aria-checked={on} onClick={onToggle}>
      {label}{on ? <Icons.Check size={14} className="exo-dc-ctx-check" /> : null}
    </button>
  )
}

/**
 * A voice channel opened from the list, as Discord shows it: everyone in it as a tile (a ring while
 * they speak, their mute and deafen), and under them the call's controls in one floating bar: mute,
 * deafen and disconnect, or Join Voice when you aren't in it. A DM's call (dm) is the same stage
 * above the conversation, under the DM's own header.
 */
// The streams you watch outlive the call's page, as Discord's do: leave for another server and come back, and they
// are still on (the host never stopped them). As in Discord you watch as many as you like: one is big above the call
// (the last you started or clicked), the others are tiles among the people. Each stops when it ends, when you stop
// watching it, or when you leave the call.
type Watched = { channelId: string; userIds: string[]; focus: string | null }
let watchedStreams: Watched | null = null
const watchedListeners = new Set<() => void>()
const subscribeWatched = (listener: () => void) => { watchedListeners.add(listener); return () => { watchedListeners.delete(listener) } }
function setWatchedStreams(next: Watched | null) {
  watchedStreams = next
  watchedListeners.forEach(listener => listener())
}
// One stream no longer watched: the big one passes to the stream watched last.
function dropWatched(userId: string) {
  if (!watchedStreams) return
  const userIds = watchedStreams.userIds.filter(id => id !== userId)
  setWatchedStreams(userIds.length ? { ...watchedStreams, userIds, focus: watchedStreams.focus === userId ? userIds[userIds.length - 1] : watchedStreams.focus } : null)
}

// Whose Go Live you watch now: a person's menu adds Stream volume for them.
function useWatchedIds() {
  return useSyncExternalStore(subscribeWatched, () => watchedStreams)?.userIds ?? []
}

// Whose streams you watch in this call, which one is big, and Watch Stream and Stop Watching.
function useWatching(channelId: string, members: DiscordVoiceMember[], here: boolean) {
  const current = useSyncExternalStore(subscribeWatched, () => watchedStreams)
  const mine = current?.channelId === channelId ? current : null
  const watching = mine?.userIds ?? []
  // A stream that ended stops being watched, and a call left stops them all (an empty list is one still loading on the way back).
  const ended = members.length > 0 ? watching.filter(id => !members.some(m => m.id === id && m.stream)).join() : ''
  useEffect(() => {
    if (!watchedStreams || watchedStreams.channelId !== channelId) return
    if (!here) { setWatchedStreams(null); void host.discordStream('stop').catch(() => { }); return }
    for (const id of ended ? ended.split(',') : []) { dropWatched(id); void host.discordStream('stop', id).catch(() => { }) }
  }, [channelId, here, ended])
  const watch = (userId: string) => {
    const others = watchedStreams?.channelId === channelId ? watchedStreams.userIds.filter(id => id !== userId) : []
    setWatchedStreams({ channelId, userIds: [...others, userId], focus: userId })
    void host.discordStream('watch', userId).then(result => { if (!result.ok) dropWatched(userId) }).catch(() => dropWatched(userId))
  }
  const stop = (userId: string) => { dropWatched(userId); void host.discordStream('stop', userId).catch(() => { }) }
  const focus = (userId: string) => { if (watchedStreams) setWatchedStreams({ ...watchedStreams, focus: userId }) }
  const big = mine?.focus ? members.find(m => m.id === mine.focus && m.stream) : undefined
  const small = watching.filter(id => id !== big?.id).map(id => members.find(m => m.id === id && m.stream)).filter((m): m is DiscordVoiceMember => !!m)
  return { watching, big, small, watch, stop, focus }
}

export function VoiceRoom({ channel, controls, meId, onJoin, dm = false, chat }: {
  channel: DiscordChannel
  controls: VoiceControls
  meId: string | null
  onJoin: () => void
  dm?: boolean
  /** The channel's text chat beside the call: whether it shows, and Discord's Show Chat / Hide Chat. */
  chat?: { shown: boolean; onToggle: () => void }
}) {
  const { voice, speaking, error } = controls
  const here = voice.channelId === channel.id && voice.state !== 'off'
  const members = channel.members ?? []
  const live = useGoLive()
  const [cameraError, setCameraError] = useState<string | null>(null)
  const notes = [error, cameraError, live.state === 'off' ? live.message : null].filter((note): note is string => !!note)
  // What went wrong with the call, in Exo's notices while its room is open; dismissing it clears them.
  useNotice('dc-voice', notes.length > 0 && { text: notes.join(' '), tone: 'bad', onDismiss: () => { controls.dismissError(); setCameraError(null); clearGoLiveMessage() } })
  const { watching, big, small, watch, stop, focus } = useWatching(channel.id, members, here)
  // Discord's Hide Members while watching: the stream takes the room, the people strip folds away.
  const [membersHidden, setMembersHidden] = useState(false)
  const myStream = useStreamTile() && here
  const [stage, tileWidth] = useTileWidth(members.length + small.length + (myStream ? 1 : 0))
  const audio = useAccountSettings().audioUsers
  return (
    <section className={`exo-dc-chat exo-dc-voice-room${dm ? ' is-dm' : ''}`} aria-label={dm ? `Call with ${channel.name}` : channel.name}>
      {dm ? null : <VoiceHead channel={channel} chat={chat} />}
      {big ? <StreamStage key={big.id} member={big} onStop={() => stop(big.id)} onRetry={() => watch(big.id)} membersHidden={membersHidden} onToggleMembers={() => setMembersHidden(h => !h)} /> : null}
      <div ref={stage} className="exo-dc-voice-stage" hidden={!!big && membersHidden} style={{ '--tile-w': `${tileWidth}px` } as CSSProperties}>
        {/* Joining: your tile on its way, shining in its place (DESIGN.md › Waits). */}
        {members.length === 0 ? here ? <span className="exo-dc-voice-tile exo-shine" role="status" aria-label="Connecting to voice" /> : (
          <div className="exo-dc-voice-empty">
            <span className="exo-dc-voice-empty-icon" aria-hidden="true"><Icons.Voice size={28} /></span>
            <strong>{channel.name}</strong>
            <span>No one is here yet.</span>
          </div>
        ) : members.map(member => (
          <VoiceTile key={member.id} member={member} me={member.id === meId} speaking={speaking.has(member.id) && !audio[member.id]?.muted} muted={member.mute || audio[member.id]?.muted === true} canWatch={here && member.stream === true && !watching.includes(member.id) && member.id !== meId}
            onWatch={() => watch(member.id)} />
        ))}
        {members.length > 0 ? small.map(member => <StreamTile key={`stream-${member.id}`} member={member} onFocus={() => focus(member.id)} onStop={() => stop(member.id)} />) : null}
        {myStream && members.length > 0 ? <MyStreamTile meId={meId} /> : null}
      </div>
      <VoiceBar here={here} dm={dm} channel={channel} controls={controls} onJoin={onJoin} onCameraError={setCameraError} />
    </section>
  )
}

/**
 * The call's bar, glass like the Bar's capsules and Music's player (its edge bends what passes under it): the call's
 * buttons, or out of the call the Join button itself, one glass capsule rather than a button inside one.
 */
function VoiceBar({ here, dm, channel, controls, onJoin, onCameraError }: {
  here: boolean
  dm: boolean
  channel: DiscordChannel
  controls: VoiceControls
  onJoin: () => void
  onCameraError: (why: string | null) => void
}) {
  const bar = useRef<HTMLElement>(null)
  useLens(bar)
  return (
    <footer ref={bar} className={`exo-dc-voice-bar exo-glass${here ? '' : ' is-join'}`}>
      {here ? <CallButtons controls={controls} channelId={channel.id} onCameraError={onCameraError} /> : (
        <button type="button" className="exo-dc-voice-join" disabled={channel.canConnect === false} onClick={onJoin}>{dm ? 'Join call' : 'Join voice'}</button>
      )}
    </footer>
  )
}

// A voice channel's bar: its name (with Discord's lock when private), and Show Chat / Hide Chat.
function VoiceHead({ channel, chat }: { channel: DiscordChannel; chat?: { shown: boolean; onToggle: () => void } }) {
  return (
    <header className="exo-dc-chat-head">
      <span className="exo-dc-hash" aria-hidden="true">{channel.locked ? <Icons.VoiceLock size={20} /> : <Icons.Voice size={20} />}</span>
      <strong>{channel.name}</strong>
      {chat ? (
        <span className="exo-dc-head-tools">
          <button type="button" className="exo-dc-icon-btn" aria-label={chat.shown ? 'Hide chat' : 'Show chat'} aria-pressed={chat.shown} onClick={chat.onToggle}>
            <Icons.Chat size={20} />
          </button>
        </span>
      ) : null}
    </header>
  )
}

/** The call bar's controls while you're in the call: mute, deafen, camera, soundboard, Share Your Screen, disconnect. */
function CallButtons({ controls, channelId, onCameraError }: { controls: VoiceControls; channelId: string; onCameraError: (why: string | null) => void }) {
  const { voice } = controls
  return (
    <>
      <button type="button" className={`exo-dc-voice-btn${voice.muted ? ' is-off' : ''}`} aria-pressed={voice.muted} aria-label={voice.muted ? 'Unmute' : 'Mute'}
        onClick={() => void controls.setMuted(!voice.muted)}>{voice.muted ? <Icons.Muted size={20} /> : <Icons.Mic size={20} />}</button>
      <button type="button" className={`exo-dc-voice-btn${voice.deafened ? ' is-off' : ''}`} aria-pressed={voice.deafened} aria-label={voice.deafened ? 'Undeafen' : 'Deafen'}
        onClick={() => void controls.setDeafened(!voice.deafened)}>{voice.deafened ? <Icons.Deafened size={20} /> : <Icons.Headphones size={20} />}</button>
      <CameraButton onError={onCameraError} />
      <SoundboardButton guildId={voice.guildId} channelId={channelId} />
      <ShareScreen />
      <button type="button" className="exo-dc-voice-btn is-hangup" aria-label="Disconnect" onClick={() => void controls.leave()}><Icons.HangUp size={20} /></button>
    </>
  )
}

/** The widest 16:9 tile that lets everyone fit the stage at once (FaceTime's grid); one person never grows past 960. */
function fitTiles(width: number, height: number, count: number, gap = 12) {
  let best = 0
  for (let columns = 1; columns <= count; columns++) {
    const rows = Math.ceil(count / columns)
    best = Math.max(best, Math.min((width - gap * (columns - 1)) / columns, (height - gap * (rows - 1)) / rows * 16 / 9))
  }
  return Math.max(120, Math.floor(Math.min(best, 960)))
}

function useTileWidth(count: number) {
  const stage = useRef<HTMLDivElement>(null)
  const [width, setWidth] = useState(0)
  useLayoutEffect(() => {
    const element = stage.current
    if (!element) return
    const measure = () => {
      const style = getComputedStyle(element)
      const inner = (size: number, a: string, b: string) => size - parseFloat(a) - parseFloat(b)
      setWidth(fitTiles(inner(element.clientWidth, style.paddingLeft, style.paddingRight), inner(element.clientHeight, style.paddingTop, style.paddingBottom), Math.max(1, count)))
    }
    measure()
    const sizes = new ResizeObserver(measure)
    sizes.observe(element)
    return () => sizes.disconnect()
  }, [count])
  return [stage, width] as const
}

/** One person in the call: their camera when it's on, else their picture over its own blurred light; LIVE and Watch while they stream. */
// muted: by the server, themselves, or you.
function VoiceTile({ member, me, speaking, muted, canWatch, onWatch }: { member: DiscordVoiceMember; me: boolean; speaking: boolean; muted: boolean; canWatch: boolean; onWatch: () => void }) {
  const camera = useVideoFeed('call', member.video && !me ? member.id : null)
  // Discord doesn't send this client its own camera: show the local picture instead.
  const own = useCamera()
  const mine = me ? own.preview : null
  const showing = mine !== null || (member.video === true && camera.live)
  return (
    <article className={`exo-dc-voice-tile${speaking ? ' is-speaking' : ''}${showing ? ' has-video' : ''}`} data-dc-user={member.id} data-dc-name={member.name} data-dc-call="">
      {mine ? <LocalVideo stream={mine} /> : member.video && !me ? <canvas ref={camera.ref} className="exo-dc-voice-video" hidden={!showing} /> : null}
      {showing || !member.avatar ? null : <img className="exo-dc-voice-backdrop" src={member.avatar} alt="" aria-hidden="true" draggable={false} />}
      {showing ? null : member.avatar ? <img className="exo-dc-voice-face" src={member.avatar} alt="" draggable={false} /> : <span className="exo-dc-voice-tile-initial">{member.name.slice(0, 1).toUpperCase()}</span>}
      {showing ? null : <Decoration look={member.look} size={128} />}
      {member.stream ? <span className="exo-dc-live">LIVE</span> : null}
      {canWatch ? <button type="button" className="exo-dc-watch" onClick={onWatch}>Watch stream</button> : null}
      <span className="exo-dc-voice-tile-name">
        {member.deaf ? <Icons.Deafened size={14} /> : muted ? <Icons.Muted size={14} /> : null}
        <span className="exo-dc-voice-tile-label">{member.name}</span>
      </span>
    </article>
  )
}

/** A stream you watch beside the big one, as a tile among the people: click it to make it big; Stop Watching on it. */
function StreamTile({ member, onFocus, onStop }: { member: DiscordVoiceMember; onFocus: () => void; onStop: () => void }) {
  const stream = useVideoFeed('stream', member.id)
  return (
    <article className="exo-dc-voice-tile has-video exo-dc-stream-tile" data-dc-user={member.id} data-dc-name={member.name}>
      <canvas ref={stream.ref} className="exo-dc-voice-video" hidden={!stream.live} />
      <button type="button" className="exo-dc-stream-tile-focus" aria-label={`Focus ${member.name}'s stream`} onClick={onFocus} />
      <span className="exo-dc-live">LIVE</span>
      <button type="button" className="exo-dc-voice-btn is-hangup exo-dc-stream-tile-stop" aria-label="Stop watching" onClick={onStop}><Icons.StopWatching size={16} /></button>
      <span className="exo-dc-voice-tile-name"><span className="exo-dc-voice-tile-label">{member.name}</span></span>
    </article>
  )
}

function LocalVideo({ stream }: { stream: MediaStream }) {
  return <video className="exo-dc-voice-video is-mirrored" autoPlay muted playsInline ref={video => { if (video && video.srcObject !== stream) video.srcObject = stream }} />
}

/** The camera, Discord's Turn On Camera (pressed again while it opens, it stops); why it didn't open goes above the bar. */
function CameraButton({ onError }: { onError: (why: string | null) => void }) {
  const { state } = useCamera()
  const on = state !== 'off'
  return (
    <button type="button" className={`exo-dc-voice-btn${on ? ' is-live' : ''}`} aria-label={on ? 'Turn off camera' : 'Turn on camera'} aria-pressed={on}
      onClick={() => { onError(null); if (on) void stopCamera(); else void startCamera().then(onError) }}>
      <Icons.Camera size={20} />
    </button>
  )
}

// How long a watched stream may take to show its first picture before it counts as not starting.
const STREAM_WAIT_MS = 20_000
// The stream's controls hide this long after the pointer stops over it, as Discord's do.
const STREAM_IDLE_MS = 2_500

/** The Go Live being watched, big above the call, with its sound; Stop Watching ends it, Try Again when it didn't start. */
function StreamStage({ member, onStop, onRetry, membersHidden, onToggleMembers }: {
  member: DiscordVoiceMember
  onStop: () => void
  onRetry: () => void
  membersHidden: boolean
  onToggleMembers: () => void
}) {
  const stream = useVideoFeed('stream', member.id)
  // Discord's Stream Volume and Mute: only for you, kept on the account like a person's User Volume.
  const sound = useAccountSettings().audioStreams[member.id] ?? { muted: false, volume: 100 }
  const level = toSlider(sound.volume)
  // Fullscreen, as Discord's button or a double-click: the stream alone on the whole screen; Esc or the button again leaves.
  const box = useRef<HTMLDivElement>(null)
  const [full, setFull] = useState(false)
  useEffect(() => {
    const changed = () => setFull(document.fullscreenElement === box.current)
    document.addEventListener('fullscreenchange', changed)
    return () => document.removeEventListener('fullscreenchange', changed)
  }, [])
  const toggleFull = () => {
    if (document.fullscreenElement) void document.exitFullscreen().catch(() => { })
    else void box.current?.requestFullscreen().catch(() => { })
  }
  const [round, setRound] = useState(0)
  const [late, setLate] = useState(false)
  useEffect(() => {
    if (stream.live) return
    const timer = window.setTimeout(() => setLate(true), STREAM_WAIT_MS)
    return () => window.clearTimeout(timer)
  }, [stream.live, round])
  const failed = !stream.live && (stream.ended || late)
  const retry = () => { setLate(false); setRound(r => r + 1); onRetry() }
  // The controls and LIVE show while the pointer moves over the stream and hide when it rests or leaves.
  const [idle, setIdle] = useState(false)
  const idleTimer = useRef(0)
  const wake = () => { setIdle(false); window.clearTimeout(idleTimer.current); idleTimer.current = window.setTimeout(() => setIdle(true), STREAM_IDLE_MS) }
  useEffect(() => {
    idleTimer.current = window.setTimeout(() => setIdle(true), STREAM_IDLE_MS)
    return () => window.clearTimeout(idleTimer.current)
  }, [])
  return (
    <div ref={box} className={`exo-dc-stream${membersHidden ? ' is-alone' : ''}${idle && stream.live ? ' is-idle' : ''}${stream.live || failed ? '' : ' exo-shine'}`} onDoubleClick={toggleFull}
      onPointerMove={wake} onPointerLeave={() => { window.clearTimeout(idleTimer.current); setIdle(true) }}>
      <canvas ref={stream.ref} className="exo-dc-stream-video" hidden={!stream.live} />
      {stream.live ? null : failed ? (
        <p className="exo-dc-note" role="alert">
          {member.name}'s stream didn't start.{' '}
          <button type="button" className="exo-dc-tab" onClick={retry}>Try again</button>
        </p>
      ) : <span className="sr-only" role="status">Joining {member.name}'s stream</span>}
      <span className="exo-dc-live">LIVE</span>
      <span className="exo-dc-stream-bar">
        <span className="exo-dc-stream-name">{member.name}</span>
        <span className="exo-dc-stream-tools">
          <span className="exo-dc-stream-sound">
            <button type="button" className={`exo-dc-voice-btn${sound.muted ? ' is-off' : ''}`} aria-label={sound.muted ? 'Unmute stream' : 'Mute stream'} aria-pressed={sound.muted}
              onClick={() => setAudioUser(member.id, { muted: !sound.muted }, true)}>
              {sound.muted ? <Icons.SoundOff size={18} /> : <Icons.Sound size={18} />}
            </button>
            <Slider min={0} max={200} value={level} aria-label="Stream volume" disabled={sound.muted}
              onChange={event => setAudioUser(member.id, { volume: fromSlider(Number(event.target.value)) }, true)} />
          </span>
          {full ? null : (
            <button type="button" className="exo-dc-voice-btn" aria-label={membersHidden ? 'Show members' : 'Hide members'} aria-pressed={membersHidden} onClick={onToggleMembers}>
              {membersHidden ? <ChevronUp size={18} /> : <ChevronDown size={18} />}
            </button>
          )}
          <button type="button" className="exo-dc-voice-btn" aria-label={full ? 'Exit full screen' : 'Full screen'} onClick={toggleFull}>
            {full ? <Icons.ExitFullScreen size={18} /> : <Icons.FullScreen size={18} />}
          </button>
          <button type="button" className="exo-dc-voice-btn is-hangup" aria-label="Stop watching" onClick={onStop}><Icons.StopWatching size={18} /></button>
        </span>
      </span>
    </div>
  )
}

/**
 * Discord's voice bar above your name: connected (a lock when end-to-end encrypted), the call's "channel / server" as
 * a link that opens it, and the hang-up button.
 */
export function VoicePanel({ controls, place, onOpen }: { controls: VoiceControls; place: string | null; onOpen: () => void }) {
  const { voice, error } = controls
  if (voice.state === 'off' && !error) return null
  return (
    <section className="exo-dc-voice" aria-label="Voice">
      <div className="exo-dc-voice-status">
        <strong className={voice.state === 'connected' ? 'is-on' : undefined}>
          {voice.state === 'connecting' ? <Spinner label="Connecting to voice" /> : null}
          {voice.state === 'connected' ? 'Voice connected' : voice.state === 'connecting' ? 'Connecting' : 'Voice'}
          {voice.state === 'connected' && voice.e2ee ? <span className="exo-dc-voice-e2ee" role="img" aria-label="End-to-end encrypted"><Icons.Lock size={12} /></span> : null}
        </strong>
        {error ? <span role="alert">{error}</span> : voice.state !== 'off' && place
          ? <button type="button" className="exo-dc-voice-place" onClick={onOpen}>{place}</button>
          : null}
        {voice.state === 'connected' && voice.micSilent && !voice.muted
          ? <span className="exo-dc-voice-warn" role="status">Your mic seems silent. Check it’s plugged in, unmuted and its gain is up.</span>
          : null}
        {voice.state === 'connected' && voice.myVoiceStopped
          ? <span className="exo-dc-voice-warn" role="status">Only my voice kept holding back your own voice, so it’s off for this call. Teach your voice again in Voice &amp; video.</span>
          : null}
        {/* Someone else's clips (or Exo Capture) record this call: Discord tells everyone in it. */}
        {voice.state === 'connected' && voice.recorded ? <span className="exo-dc-voice-warn" role="status">Someone in this call has clips on, so your voice may be recorded.</span> : null}
      </div>
      {voice.state !== 'off' ? (
        <button type="button" className="exo-dc-icon-btn is-hangup" aria-label="Disconnect" onClick={() => void controls.leave()}>
          <Icons.HangUp size={20} />
        </button>
      ) : (
        <button type="button" className="exo-dc-icon-btn" aria-label="Dismiss" onClick={controls.dismissError}><Close size={14} /></button>
      )}
    </section>
  )
}

/** Someone is calling you in a DM: Join, Join with video (the camera on as it connects), or Decline (Discord stops ringing you everywhere). */
export function IncomingCall({ name, onJoin, onDecline }: { name: string; onJoin: (video: boolean) => void; onDecline: () => void }) {
  return (
    <div className="exo-dc-call exo-glass" role="alert">
      <span><strong>{name}</strong> is calling</span>
      <button type="button" onClick={() => onJoin(false)}>Join</button>
      <button type="button" onClick={() => onJoin(true)}>Join with video</button>
      <button type="button" onClick={onDecline}>Decline</button>
    </div>
  )
}
