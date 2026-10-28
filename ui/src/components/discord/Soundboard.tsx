import { useEffect, useRef, useState } from 'react'
import { host, onHostEvent } from '../../lib/host'
import { report } from '../../lib/report'
import type { DiscordVoice } from '../../lib/discord'
import { emojiUrl } from './emojiUrl'
import { Icons } from './icons'
import { useDismiss } from '../../lib/dismiss'
import { Skeleton } from '../ui/Skeleton'
import { ErrorNote } from '../ui/ErrorNote'

type Sound = { sound_id: string; name: string; volume?: number; emoji_id?: string | null; emoji_name?: string | null; guild_id?: string | null; available?: boolean }

const soundUrl = (id: string) => `https://cdn.discordapp.com/soundboard-sounds/${id}`

/** Plays a sound here, at its own volume. */
function playHere(id: string, volume: number) {
  const audio = new Audio(soundUrl(id))
  audio.volume = Math.max(0, Math.min(1, volume))
  void audio.play().catch(() => { })
}

function SoundTile({ sound, onPlay }: { sound: Sound; onPlay: () => void }) {
  return (
    <button type="button" className="exo-dc-sound" onClick={onPlay} disabled={sound.available === false}>
      {sound.emoji_id ? <img src={emojiUrl(sound.emoji_id, false, 48)} alt="" draggable={false} /> : sound.emoji_name ? <span aria-hidden="true">{sound.emoji_name}</span> : null}
      <span className="exo-dc-sound-name">{sound.name}</span>
    </button>
  )
}

/**
 * Discord's Soundboard in a call: this server's sounds and Discord's own; pressing one plays it for
 * everyone in the channel (and here at once, as Discord does).
 */
export function SoundboardButton({ guildId, channelId }: { guildId: string | null; channelId: string }) {
  const [open, setOpen] = useState(false)
  const [sounds, setSounds] = useState<{ defaults: Sound[]; guild: Sound[] } | null>(null)
  const [error, setError] = useState<string | null>(null)
  const box = useRef<HTMLDivElement>(null)
  useDismiss(box, () => setOpen(false), { on: open })
  useEffect(() => {
    if (!open) return
    let live = true
    setError(null)
    host.discordSoundboard('list', { guildId }).then(result => {
      if (!live) return
      if (!result.ok) { setError(result.message ?? 'Discord did not answer.'); return }
      const items = (result.guild as { items?: Sound[] } | null)?.items ?? []
      setSounds({ defaults: Array.isArray(result.defaults) ? result.defaults as Sound[] : [], guild: items })
    }).catch((err: unknown) => { report(err, 'Soundboard.list'); if (live) setError('Discord did not answer.') })
    const escape = (event: KeyboardEvent) => { if (event.key === 'Escape') setOpen(false) }
    document.addEventListener('keydown', escape)
    return () => { live = false; document.removeEventListener('keydown', escape) }
  }, [open, guildId])

  function play(sound: Sound) {
    setError(null)
    playHere(sound.sound_id, sound.volume ?? 1)
    host.discordSoundboard('play', { channelId, soundId: sound.sound_id, guildId: sound.guild_id ?? null, emojiId: sound.emoji_id ?? null, emojiName: sound.emoji_name ?? null })
      .then(result => { if (!result.ok) setError(result.message ?? 'Discord did not play it.') })
      .catch(() => setError('Discord did not play it.'))
  }

  return (
    <span className="exo-dc-soundboard">
      <button type="button" className={`exo-dc-voice-btn${open ? ' is-live' : ''}`} aria-label="Soundboard" aria-expanded={open} onClick={() => setOpen(value => !value)}>
        <Icons.Soundboard size={20} />
      </button>
      {open ? (
        <div ref={box} className="exo-dc-soundboard-pop" role="dialog" aria-label="Soundboard">
          {error ? <ErrorNote className="exo-dc-note" message={error} /> : null}
          {sounds === null && !error ? <Skeleton label="Loading sounds" tiles={8} /> : null}
          {sounds?.guild.length ? (
            <section>
              <p className="exo-dc-picker-head">This server</p>
              <div className="exo-dc-sound-grid">{sounds.guild.map(sound => <SoundTile key={sound.sound_id} sound={sound} onPlay={() => play(sound)} />)}</div>
            </section>
          ) : null}
          {sounds?.defaults.length ? (
            <section>
              <p className="exo-dc-picker-head">Discord</p>
              <div className="exo-dc-sound-grid">{sounds.defaults.map(sound => <SoundTile key={sound.sound_id} sound={sound} onPlay={() => play(sound)} />)}</div>
            </section>
          ) : null}
        </div>
      ) : null}
    </span>
  )
}

/**
 * Someone else played a sound in the call you're in: it plays here, as in Discord (not while deafened). The shell's,
 * so it plays wherever you are in Exo; the host leaves out your own, which played when you pressed it.
 */
export function useSoundboardSounds(voice: DiscordVoice) {
  const current = useRef(voice)
  useEffect(() => { current.current = voice })
  useEffect(() => onHostEvent('discord.soundboard', event => {
    const now = current.current
    if (now.state === 'off' || now.channelId !== event.channelId || now.deafened) return
    playHere(event.soundId, event.volume)
  }), [])
}
