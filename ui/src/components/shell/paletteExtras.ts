import { useEffect, useMemo, useState } from 'react'
import { Hash, Members, MessageCircle, MusicNotes, Next, Pause, Previous } from '../../brand/icons'
import { host } from '../../lib/host'
import { openDiscord, type DiscordHome } from '../../lib/discord'
import type { Command } from '../blocks/command-menu-1'

const MUSIC: Command[] = [
  { id: 'music-toggle', group: 'Music', title: 'Play or pause', icon: Pause },
  { id: 'music-next', group: 'Music', title: 'Next song', icon: Next },
  { id: 'music-previous', group: 'Music', title: 'Previous song', icon: Previous },
  { id: 'music-open', group: 'Music', title: 'Open Music', icon: MusicNotes },
]

type PaletteChannel = { id: string; guildId: string; name: string; server: string }

/**
 * Ctrl+K beyond the Library (DESIGN_NEXT › Ctrl+K): your Discord DMs, servers and (once you type) their channels
 * from Exo's own client, read when the palette opens, and the music controls.
 */
export function usePaletteExtras(open: boolean): Command[] {
  const [home, setHome] = useState<DiscordHome | null>(null)
  const [channels, setChannels] = useState<PaletteChannel[]>([])
  useEffect(() => {
    if (!open) return
    let live = true
    host.discordHome().then(async next => {
      if (!live) return
      setHome(next)
      // Each server's list is the client's own cache, so reading them all costs no request to Discord.
      const lists = await Promise.all(next.guilds.map(g => host.discordChannels(g.id).then(list => ({ g, list })).catch(() => null)))
      if (live) setChannels(lists.flatMap(entry => entry ? entry.list.channels.filter(c => c.kind !== 'category')
        .map(c => ({ id: c.id, guildId: entry.g.id, name: c.name, server: entry.g.name })) : []))
    }).catch(() => { })
    return () => { live = false }
  }, [open])
  return useMemo(() => {
    const dms: Command[] = (home?.dms ?? []).map(dm => ({
      id: `dm-${dm.id}`, group: 'Discord', title: dm.name, icon: MessageCircle,
    }))
    const servers: Command[] = (home?.guilds ?? []).map(guild => ({
      id: `server-${guild.id}`, group: 'Discord', title: guild.name, icon: Members,
    }))
    const rooms: Command[] = channels.map(c => ({
      id: `channel-${c.guildId}-${c.id}`, group: 'Discord', title: c.name, detail: c.server, icon: Hash, searchOnly: true,
    }))
    return [...dms, ...servers, ...rooms, ...MUSIC]
  }, [home, channels])
}

/** Runs a Discord or music command; the room to show after it, or null when it is not one of these.
    onFail: why a music control the player refused did nothing. */
export function runPaletteExtra(command: Command, onFail: (message: string) => void): 'friends' | 'music' | 'stay' | null {
  const { id } = command
  if (id.startsWith('dm-')) { openDiscord({ guildId: null, channelId: id.slice(3) }); return 'friends' }
  if (id.startsWith('server-')) { openDiscord({ guildId: id.slice(7), channelId: null }); return 'friends' }
  if (id.startsWith('channel-')) { const [guildId, channelId] = id.slice(8).split('-'); openDiscord({ guildId, channelId }); return 'friends' }
  if (id === 'music-open') return 'music'
  const action = id === 'music-toggle' ? 'toggle' : id === 'music-next' ? 'next' : id === 'music-previous' ? 'previous' : null
  if (!action) return null
  void host.musicControl(action).then(
    result => { if (!result.ok) onFail(result.message ?? 'The player could not respond.') },
    () => onFail('The player could not respond.'))
  return 'stay'
}
