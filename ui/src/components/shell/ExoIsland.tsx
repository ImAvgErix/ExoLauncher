import { memo, useEffect, useRef, useState } from 'react'
import { createPortal } from 'react-dom'
import { useGoLive } from '../../lib/goLive'
import { host, onHostEvent } from '../../lib/host'
import { usePageClock } from '../../lib/pageActivity'
import type { DiscordVoice, DiscordVoiceMember } from '../../lib/discord'
import { Icons } from '../discord/icons'
import { CallPersonItems, useDiscordVoice, type VoiceControls } from '../discord/voice'
import { useSoundboardSounds } from '../discord/Soundboard'
import { startDiscordShell, useAccountSettings } from '../../lib/discordPrefs'
import { BarCapsule } from './BarCapsule'
import { useDismiss } from '../../lib/dismiss'

/**
 * The Exo Island (DESIGN_NEXT › Exo Island): the capsule left of the Rooms, only while something
 * is live that its room isn't showing. A Discord call outside the Discord room (where, who is in it
 * and who is speaking; mute, deafen and leave from anywhere). Downloads show in the Library. The song has its own
 * capsule right of the Rooms. Nothing live: nothing drawn.
 */
export const ExoIsland = memo(function ExoIsland({ inDiscord, onOpenDiscord }: {
  inDiscord: boolean
  onOpenDiscord: () => void
}) {
  const voice = useDiscordVoice()
  const inCall = voice.voice.state !== 'off'
  const since = useCallStart(voice.voice.state === 'connected')
  // Always mounted: the call's soundboard, Camera On and Off and Toggle Streamer Mode go on outside the Discord room.
  useSoundboardSounds(voice.voice)
  useEffect(startDiscordShell, [])
  return (
    <BarCapsule>
      {inCall && !inDiscord ? <CallCapsule voice={voice} since={since} onOpen={onOpenDiscord} /> : null}
    </BarCapsule>
  )
})

type CallPlace = { channel: string; server: string | null; icon: string | null; members: DiscordVoiceMember[] }

/** Where the call is (channel, server and its icon, or the DM) and who is in it, kept current as people come and go. */
function useCallPlace({ state, guildId, channelId }: DiscordVoice): CallPlace | null {
  const [place, setPlace] = useState<CallPlace | null>(null)
  useEffect(() => {
    if (state === 'off' || !channelId) { setPlace(null); return }
    // reads: only the newest answer lands. A busy server changes many times a second (anyone muting,
    // joining, leaving): its changes are read once each quarter second, not once each.
    let live = true, reads = 0, timer = 0
    const read = () => {
      const at = ++reads
      if (guildId) {
        void host.discordChannels(guildId).then(list => {
          const channel = list.channels.find(c => c.id === channelId)
          if (live && at === reads) setPlace({ channel: channel?.name ?? 'Voice', server: list.name, icon: list.icon ?? null, members: channel?.members ?? [] })
        }).catch(() => { })
      } else {
        void host.discordHome().then(home => {
          const dm = home.dms.find(d => d.id === channelId)
          if (live && at === reads) setPlace({ channel: dm?.name ?? 'Call', server: null, icon: dm?.avatar ?? null, members: [] })
        }).catch(() => { })
      }
    }
    read()
    const off = onHostEvent('discord.changed', event => {
      if (!guildId || event.guildId !== guildId || timer) return
      timer = window.setTimeout(() => { timer = 0; read() }, 250)
    })
    return () => { live = false; window.clearTimeout(timer); off() }
  }, [state, guildId, channelId])
  return place
}

/** When the call connected, kept here (always mounted) so the timer runs on while the Discord room is open. */
function useCallStart(connected: boolean) {
  const [since, setSince] = useState<number | null>(null)
  useEffect(() => { setSince(connected ? Date.now() : null) }, [connected])
  return since
}

/** The call's length, as FaceTime shows it: 4:07, then 1:02:33. It stops ticking while Exo is minimized. */
function CallTimer({ since }: { since: number }) {
  const now = usePageClock(1000, Date.now)
  const total = Math.max(0, Math.floor((now - since) / 1000))
  const h = Math.floor(total / 3600), m = Math.floor(total / 60) % 60, s = total % 60
  const two = (n: number) => String(n).padStart(2, '0')
  return <span className="exo-call-time">{h ? `${h}:${two(m)}:${two(s)}` : `${m}:${two(s)}`}</span>
}

const FACES = 4

function CallCapsule({ voice, since, onOpen }: { voice: VoiceControls; since: number | null; onOpen: () => void }) {
  const { state } = voice.voice
  const place = useCallPlace(voice.voice)
  const members = place?.members ?? []
  const streaming = useGoLive().state !== 'off'
  return (
    <div className={`exo-island exo-call${voice.speaking.size > 0 ? ' is-live' : ''}`} role="group" aria-label="Discord call">
      <button type="button" className="exo-island-main exo-call-place" onClick={onOpen} aria-label={`Open the call${place ? `: ${place.channel}` : ''}`}>
        {place?.icon ? <img className="exo-call-icon" src={place.icon} alt="" draggable={false} /> : <span className="exo-island-dot" aria-hidden="true" />}
        <span className="exo-call-text">
          <strong>{state === 'connecting' ? 'Connecting…' : place?.channel ?? 'In a call'}</strong>
          {place?.server ? <small>{place.server}</small> : null}
        </span>
      </button>
      {members.length ? <CallFaces members={members} speaking={voice.speaking} /> : null}
      {streaming ? <span className="exo-call-live">Live</span> : null}
      {since !== null ? <CallTimer since={since} /> : null}
      <CallButtons voice={voice} />
    </div>
  )
}

/** Mute, deafen and leave, from anywhere. */
function CallButtons({ voice }: { voice: VoiceControls }) {
  const { muted, deafened } = voice.voice
  return (
    <>
        <button type="button" className={`exo-island-btn${muted ? ' is-off' : ''}`} aria-label={muted ? 'Unmute' : 'Mute'} aria-pressed={muted} onClick={() => void voice.setMuted(!muted)}>
          {muted ? <Icons.Muted size={15} /> : <Icons.Mic size={15} />}
        </button>
        <button type="button" className={`exo-island-btn${deafened ? ' is-off' : ''}`} aria-label={deafened ? 'Undeafen' : 'Deafen'} aria-pressed={deafened} onClick={() => void voice.setDeafened(!deafened)}>
          {deafened ? <Icons.Deafened size={15} /> : <Icons.Headphones size={15} />}
        </button>
        <button type="button" className="exo-island-btn is-leave" aria-label="Leave call" onClick={() => void voice.leave()}>
          <Icons.HangUp size={15} />
        </button>
    </>
  )
}

type PersonAt = { id: string; name: string; me: boolean; x: number; y: number }

/** Who is in the call: up to four faces, a green ring on whoever speaks (not someone you muted), then how many more.
    Right-click a face for Discord's call items on them: User Volume and Mute. */
function CallFaces({ members, speaking }: { members: DiscordVoiceMember[]; speaking: Set<string> }) {
  const audio = useAccountSettings().audioUsers
  const [menu, setMenu] = useState<PersonAt | null>(null)
  const open = (event: React.MouseEvent) => {
    const id = (event.target as Element).closest('[data-dc-user]')?.getAttribute('data-dc-user')
    const member = members.find(m => m.id === id)
    if (!member) return
    event.preventDefault()
    const { clientX: x, clientY: y } = event
    void host.discordHome().then(home => setMenu({ id: member.id, name: member.name, me: home.me?.id === member.id, x, y })).catch(() => { })
  }
  return (
    <span className="exo-call-faces" aria-label={`${members.length} in the call`} onContextMenu={open}>
      {members.slice(0, FACES).map(member => {
        const ring = speaking.has(member.id) && !audio[member.id]?.muted ? ' is-speaking' : ''
        return member.avatar
          ? <img key={member.id} className={`exo-call-face${ring}`} src={member.avatar} alt={member.name} draggable={false} data-dc-user={member.id} />
          : <span key={member.id} className={`exo-call-face${ring}`} role="img" aria-label={member.name} data-dc-user={member.id}>{member.name.slice(0, 1).toUpperCase()}</span>
      })}
      {members.length > FACES ? <span className="exo-call-face is-more">+{members.length - FACES}</span> : null}
      {menu ? <PersonMenu at={menu} onClose={() => setMenu(null)} /> : null}
    </span>
  )
}

// The person menu's call items, in Discord's menu look (the room's own menu needs the room).
function PersonMenu({ at, onClose }: { at: PersonAt; onClose: () => void }) {
  const box = useRef<HTMLDivElement>(null)
  useDismiss(box, onClose)
  useEffect(() => {
    // Handled here, so the app's own Escape (which closes the room or sheet under the Bar) leaves it alone.
    const key = (event: KeyboardEvent) => { if (event.key === 'Escape') { event.preventDefault(); onClose() } }
    document.addEventListener('keydown', key)
    return () => document.removeEventListener('keydown', key)
  }, [onClose])
  return createPortal(
    <div className="exo-dc exo-call-menu">
      <div ref={box} className="exo-dc-menu exo-dc-ctx" role="menu" aria-label={at.name} style={{ left: at.x, top: at.y }}>
        <CallPersonItems userId={at.id} me={at.me} />
      </div>
    </div>,
    document.body)
}
