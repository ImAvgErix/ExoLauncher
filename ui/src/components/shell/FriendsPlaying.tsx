import { memo, useEffect, useMemo, useState } from 'react'
import type { DiscordFriend } from '../../lib/discord'
import { host, onHostEvent, shelfAction, type Game } from '../../lib/host'
import { titleIdentity } from '../../lib/utils'

type Group = { game: Game; friends: DiscordFriend[] }

/**
 * Friends are playing (DESIGN.md › Library): who is in a game you own right
 * now, from Exo's own Discord client. Shows only while it is signed in and
 * someone is playing; Play or Install is one click.
 */
export const FriendsPlaying = memo(function FriendsPlaying({ games, startingId, onPlay, onOpen }: {
  games: Game[]
  startingId: string | null
  onPlay: (game: Game) => void
  onOpen: (game: Game) => void
}) {
  const friends = useFriends()
  const groups = useMemo(() => playingGroups(friends, games), [friends, games])
  if (groups.length === 0) return null
  return (
    <section className="exo-friends-playing" aria-labelledby="exo-friends-playing-title">
      <h2 className="exo-section-title" id="exo-friends-playing-title">Friends are playing</h2>
      <div className="exo-friends-playing-row">
        {groups.map(({ game, friends: who }) => {
          const shortcut = shelfAction(game)
          return (
            <div key={game.id} className="exo-friend-pill">
              <span className="exo-friend-faces" aria-hidden="true">
                {who.slice(0, 3).map(f => f.avatar ? <img key={f.id} src={f.avatar} alt="" draggable={false} /> : <span key={f.id}>{f.name.slice(0, 1)}</span>)}
              </span>
              <button type="button" className="exo-friend-pill-text" onClick={() => onOpen(game)}>
                <strong>{names(who)}</strong>
                <span>{game.title}</span>
              </button>
              {shortcut ? (
                <button type="button" className="exo-friend-pill-go" disabled={game.id === startingId}
                  aria-label={`${game.id === startingId ? 'Starting' : shortcut.label} ${game.title}`} onClick={() => onPlay(game)}>
                  {game.id === startingId ? 'Starting…' : shortcut.label}
                </button>
              ) : null}
            </div>
          )
        })}
      </div>
    </section>
  )
})

function names(who: DiscordFriend[]): string {
  if (who.length <= 2) return who.map(f => f.name).join(' and ')
  return `${who[0].name} and ${who.length - 1} others`
}

// Friends playing a game you own (installed or not), the most players first.
function playingGroups(friends: DiscordFriend[], games: Game[]): Group[] {
  if (friends.length === 0) return []
  const byTitle = new Map(games.map(game => [titleIdentity(game.title), game]))
  const groups = new Map<string, Group>()
  for (const friend of friends) {
    if (friend.kind !== 'friend' || friend.status === 'offline' || friend.rich?.type !== 0) continue
    const game = byTitle.get(titleIdentity(friend.rich.name))
    if (!game) continue
    const group = groups.get(game.id) ?? { game, friends: [] }
    group.friends.push(friend)
    groups.set(game.id, group)
  }
  return [...groups.values()].sort((a, b) => b.friends.length - a.friends.length).slice(0, 4)
}

// The friends list from Exo's Discord client, kept live by its presence events.
function useFriends(): DiscordFriend[] {
  const [friends, setFriends] = useState<DiscordFriend[]>([])
  useEffect(() => {
    let live = true
    const load = () => { host.discordFriends().then(result => { if (live) setFriends(result.friends) }).catch(() => { }) }
    load()
    const offChanged = onHostEvent('discord.changed', event => { if (!event.voice) load() })
    // Presence also arrives for every server member on screen in Discord: only a friend's changes the list.
    const offPresence = onHostEvent('discord.presence', event =>
      setFriends(list => list.some(f => f.id === event.userId)
        ? list.map(f => f.id === event.userId ? { ...f, status: event.status, activity: event.activity, activityType: event.activityType, rich: event.rich } : f)
        : list))
    return () => { live = false; offChanged(); offPresence() }
  }, [])
  return friends
}
