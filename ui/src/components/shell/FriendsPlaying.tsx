import { memo, useEffect, useMemo, useState } from 'react'
import type { DiscordFriend } from '../../lib/discord'
import { host, onHostEvent, shelfAction, type Game } from '../../lib/host'
import { titleIdentity } from '../../lib/utils'

/** A game friends are in: one you have (its card), or one you don't (only its name, from their presence). */
type Group = { key: string; title: string; game: Game | null; friends: DiscordFriend[] }

/** How many games the shelf names. */
const MAX_GROUPS = 8

/**
 * Friends are playing (DESIGN.md › Library): who is in a game right now, from Exo's own Discord client. Shows only while
 * it is signed in and someone is playing. A game you have opens or plays in one click; one you don't has Store, which
 * searches for it there.
 */
export const FriendsPlaying = memo(function FriendsPlaying({ games, startingId, onPlay, onOpen, onFind }: {
  games: Game[]
  startingId: string | null
  onPlay: (game: Game) => void
  onOpen: (game: Game) => void
  onFind: (title: string) => void
}) {
  const friends = useFriends()
  const all = useMemo(() => playingGroups(friends, games), [friends, games])
  const sold = useSoldTitles(all.filter(g => !g.game).map(g => g.title))
  // Discord's "Playing" is any program that says so (an editor, a wallpaper app): one the library lacks shows only
  // when the Store sells a game by that name, so Store finds it.
  const groups = useMemo(() => all.filter(g => g.game || sold.has(titleIdentity(g.title))).slice(0, MAX_GROUPS), [all, sold])
  if (groups.length === 0) return null
  return (
    <section className="exo-friends-playing" aria-labelledby="exo-friends-playing-title">
      <h2 className="exo-section-title" id="exo-friends-playing-title">Friends are playing</h2>
      <div className="exo-friends-playing-row">
        {groups.map(({ key, title, game, friends: who }) => {
          const shortcut = game ? shelfAction(game) : null
          return (
            <div key={key} className="exo-friend-pill">
              <span className="exo-friend-faces" aria-hidden="true">
                {who.slice(0, 3).map(f => f.avatar ? <img key={f.id} src={f.avatar} alt="" draggable={false} /> : <span key={f.id}>{f.name.slice(0, 1)}</span>)}
              </span>
              <button type="button" className="exo-friend-pill-text" onClick={() => game ? onOpen(game) : onFind(title)}>
                <strong>{names(who)}</strong>
                <span>{title}</span>
              </button>
              {game && shortcut ? (
                <button type="button" className="exo-friend-pill-go" disabled={game.id === startingId}
                  aria-label={`${game.id === startingId ? 'Starting' : shortcut.label} ${title}`} onClick={() => onPlay(game)}>
                  {game.id === startingId ? 'Starting…' : shortcut.label}
                </button>
              ) : null}
              {!game ? (
                <button type="button" className="exo-friend-pill-go" aria-label={`Find ${title} in the Store`} onClick={() => onFind(title)}>Store</button>
              ) : null}
            </div>
          )
        })}
      </div>
    </section>
  )
})

/**
 * The game in a presence's name. A clip recorder or overlay that reports the game for its user adds itself to the name
 * ("VALORANT with Medal", "Apex Legends via Overwolf"); the game is what comes before it.
 */
function gameName(presence: string): string {
  return presence.trim().replace(/\s+(?:with|via|on|\+|-|\|)\s+(?:Medal(?:\.tv)?|Overwolf|Outplayed|Insights Capture|SteelSeries Moments|Blitz|Mobalytics|Porofessor|U\.GG)\s*$/i, '').trim()
}

function names(who: DiscordFriend[]): string {
  if (who.length <= 2) return who.map(f => f.name).join(' and ')
  return `${who[0].name} and ${who.length - 1} others`
}

// Friends in a game, the most players first: those you have by their card, the rest by the name they show in Discord.
function playingGroups(friends: DiscordFriend[], games: Game[]): Group[] {
  if (friends.length === 0) return []
  const byTitle = new Map(games.map(game => [titleIdentity(game.title), game]))
  const groups = new Map<string, Group>()
  for (const friend of friends) {
    if (friend.kind !== 'friend' || friend.status === 'offline' || friend.rich?.type !== 0) continue
    const name = gameName(friend.rich.name)
    const identity = titleIdentity(name)
    if (!identity) continue
    const game = byTitle.get(identity) ?? null
    const key = game ? game.id : `title:${identity}`
    const group = groups.get(key) ?? { key, title: game ? game.title : name, game, friends: [] }
    group.friends.push(friend)
    groups.set(key, group)
  }
  return [...groups.values()].sort((a, b) => b.friends.length - a.friends.length)
}

/** The titles back out of the effect's key. */
const titlesOf = (key: string) => JSON.parse(key) as string[]

/** Titles asked of the Store this session: true when it sells a game by exactly that name. */
const soldTitles = new Map<string, boolean>()

/**
 * Which of these titles the Store sells, by exact name, asked one at a time (a newer search replaces the one in flight,
 * so they are never asked together) and remembered for the session. A search another one replaced is asked again later.
 */
function useSoldTitles(titles: string[]): ReadonlySet<string> {
  const sold = () => new Set([...soldTitles].filter(([, yes]) => yes).map(([identity]) => identity))
  // A new set only when an answer came in, so the shelf above does not regroup on every draw.
  const [known, setKnown] = useState<ReadonlySet<string>>(sold)
  const key = JSON.stringify(titles)
  useEffect(() => {
    let live = true
    void (async () => {
      for (const title of titlesOf(key)) {
        const identity = titleIdentity(title)
        if (!live || soldTitles.has(identity)) continue
        const res = await host.storeSearch(title).catch(() => null)
        if (!res?.ok) continue
        soldTitles.set(identity, res.items.some(item => titleIdentity(item.name) === identity))
        if (live) setKnown(sold())
      }
    })()
    return () => { live = false }
  }, [key])
  return known
}

// The friends list from Exo's Discord client, kept live by its presence events.
function useFriends(): DiscordFriend[] {
  const [friends, setFriends] = useState<DiscordFriend[]>([])
  useEffect(() => {
    let live = true
    const load = () => { host.discordFriends().then(result => { if (live) setFriends(result.friends) }).catch(() => { }) }
    load()
    // A burst of changed events (a sign-in sync) becomes one read.
    let timer = 0
    const soon = () => { window.clearTimeout(timer); timer = window.setTimeout(load, 400) }
    const offChanged = onHostEvent('discord.changed', event => { if (!event.voice) soon() })
    // Presence also arrives for every server member on screen in Discord: only a friend's changes the list.
    const offPresence = onHostEvent('discord.presence', event =>
      setFriends(list => list.some(f => f.id === event.userId)
        ? list.map(f => f.id === event.userId ? { ...f, status: event.status, activity: event.activity, activityType: event.activityType, rich: event.rich } : f)
        : list))
    return () => { live = false; window.clearTimeout(timer); offChanged(); offPresence() }
  }, [])
  return friends
}
