import { useCallback, useEffect, useRef, useState } from 'react'
import { host } from '../../lib/host'
import { report } from '../../lib/report'
import { avatarUrl, reactionCounts, reactionKey, type DiscordMessage, type DiscordReaction, type DiscordUser } from '../../lib/discord'
import { useAccountSettings } from '../../lib/discordPrefs'
import { loadEmojis } from './EmojiPicker'
import { Icons } from './icons'
import { emojiUrl } from './markdown'

/** One chip under a message: a reaction's normal count, or its super reactions on a chip of their own, as Discord shows them. */
type Chip = { reaction: DiscordReaction; burst: boolean; count: number; mine: boolean }

function chipsOf(reactions: DiscordReaction[]): Chip[] {
  const supers: Chip[] = [], normal: Chip[] = []
  for (const reaction of reactions) {
    const counts = reactionCounts(reaction)
    if (counts.burst > 0) supers.push({ reaction, burst: true, count: counts.burst, mine: reaction.me_burst === true })
    if (counts.normal > 0) normal.push({ reaction, burst: false, count: counts.normal, mine: reaction.me })
  }
  return [...supers, ...normal]
}

/** Whether the account has Nitro (super reactions), read once with the emoji list. */
export function useNitro(guildId: string | null) {
  const [nitro, setNitro] = useState(false)
  useEffect(() => {
    let live = true
    loadEmojis(guildId).then(data => { if (live) setNitro(data.premium) }).catch(() => { })
    return () => { live = false }
  }, [guildId])
  return nitro
}

export function ReactionEmoji({ reaction, size = 16 }: { reaction: Pick<DiscordReaction, 'emoji'>; size?: number }) {
  return reaction.emoji.id
    ? <img src={emojiUrl(reaction.emoji.id, reaction.emoji.animated === true)} alt={`:${reaction.emoji.name}:`} width={size} height={size} draggable={false} />
    : <span>{reaction.emoji.name}</span>
}

const emojiName = (reaction: Pick<DiscordReaction, 'emoji'>) => reaction.emoji.id ? `:${reaction.emoji.name}:` : reaction.emoji.name ?? ''

// The first people of each chip, asked once while its count stays the same (Discord's tooltip).
const firstReactors = new Map<string, Promise<DiscordUser[]>>()

function reactorsKey(message: DiscordMessage, chip: Chip) {
  return `${message.id}:${reactionKey(chip.reaction.emoji)}:${chip.burst ? 1 : 0}:${chip.count}`
}

function nameOf(user: DiscordUser) {
  return user.global_name || user.username
}

// "Ann, Bo and Cy reacted with :fire:", "Ann, Bo, Cy and 4 others ...", as Discord's tooltip reads.
function tipText(users: DiscordUser[], chip: Chip) {
  const names = users.slice(0, 3).map(nameOf)
  const others = chip.count - names.length
  const who = others > 0 ? `${names.join(', ')} and ${others} ${others === 1 ? 'other' : 'others'}` : names.length > 1 ? `${names.slice(0, -1).join(', ')} and ${names[names.length - 1]}` : names[0] ?? ''
  return `${who} ${chip.burst ? 'super reacted' : 'reacted'} with ${emojiName(chip.reaction)}`
}

/**
 * The reactions under a message, Discord's way: super reactions first, in their own colors; a click adds
 * or takes back yours (a super reaction only with Nitro); pointing at one names who reacted; a right-click
 * opens the Reactions list. Show emoji reactions off hides them.
 */
export function Reactions({ message, nitro, onReact, onView }: {
  message: DiscordMessage
  nitro: boolean
  onReact: (emoji: string, add: boolean, burst: boolean) => void
  /** A right-click on a chip: its emoji's key and whether it is the super one, as emoji:true. */
  onView: (chip: string) => void
}) {
  const [tip, setTip] = useState<{ text: string; left: number; top: number } | null>(null)
  const pending = useRef(0)
  if (!useAccountSettings().renderReactions) return null
  const show = (chip: Chip, el: HTMLElement) => {
    const box = el.getBoundingClientRect()
    const at = { left: box.left + box.width / 2, top: box.top - 6 }
    const ask = ++pending.current
    const key = reactorsKey(message, chip)
    let entry = firstReactors.get(key)
    if (!entry) {
      entry = host.discordReactors(message.channel_id, message.id, reactionKey(chip.reaction.emoji), chip.burst, undefined, 3).then(r => r.users ?? [])
      entry.catch(() => firstReactors.delete(key))
      firstReactors.set(key, entry)
    }
    entry.then(users => { if (pending.current === ask && users.length) setTip({ text: tipText(users, chip), ...at }) }).catch(() => { })
  }
  const hide = () => { pending.current++; setTip(null) }
  return (
    <div className="exo-dc-reactions" onPointerLeave={hide}>
      {chipsOf(message.reactions!).map(chip => {
        const key = reactionKey(chip.reaction.emoji)
        const color = chip.burst ? chip.reaction.burst_colors?.[0] : undefined
        return (
          <button key={`${key}:${chip.burst}`} type="button"
            className={`exo-dc-reaction${chip.mine ? ' is-mine' : ''}${chip.burst ? ' is-super' : ''}`}
            style={color ? { '--exo-dc-burst': color } as React.CSSProperties : undefined}
            aria-pressed={chip.mine} aria-label={`${emojiName(chip.reaction)}, ${chip.count}${chip.burst ? ' super reactions' : ''}`}
            aria-disabled={chip.burst && !chip.mine && !nitro}
            onPointerEnter={event => show(chip, event.currentTarget)} onFocus={event => show(chip, event.currentTarget)} onBlur={hide}
            onContextMenu={event => { event.preventDefault(); event.stopPropagation(); hide(); onView(`${key}:${chip.burst}`) }}
            onClick={() => { if (!chip.burst || chip.mine || nitro) onReact(key, !chip.mine, chip.burst) }}>
            <ReactionEmoji reaction={chip.reaction} />
            <span>{chip.count}</span>
          </button>
        )
      })}
      {tip ? <span className="exo-dc-tip is-above" role="tooltip" style={{ left: tip.left, top: tip.top }}>{tip.text}</span> : null}
    </div>
  )
}

/**
 * Discord's Reactions list: each reaction (super ones apart) down the left with its count, the people who
 * gave the chosen one on the right, 100 at a time as you scroll; with Manage Messages, an × takes one off.
 */
export function ReactionsDialog({ message, first, onClose }: { message: DiscordMessage; first: string | null; onClose: () => void }) {
  // first: the chip right-clicked (emoji:burst), or null for the first one.
  const chips = chipsOf(message.reactions ?? [])
  const [chosen, setChosen] = useState<Chip | undefined>(() => chips.find(c => `${reactionKey(c.reaction.emoji)}:${c.burst}` === first) ?? chips[0])
  const [users, setUsers] = useState<DiscordUser[]>([])
  const [more, setMore] = useState(true)
  const [busy, setBusy] = useState(false)
  const [canRemove, setCanRemove] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const asked = useRef(0)
  const chosenKey = chosen ? `${reactionKey(chosen.reaction.emoji)}:${chosen.burst}` : ''
  const load = useCallback((after?: string) => {
    if (!chosen) return
    const ask = ++asked.current
    setBusy(true)
    host.discordReactors(message.channel_id, message.id, reactionKey(chosen.reaction.emoji), chosen.burst, after).then(result => {
      if (asked.current !== ask) return
      const page = result.users ?? []
      setUsers(list => after ? [...list, ...page] : page)
      setMore(page.length >= 100)
      setCanRemove(result.canRemove === true)
      setError(result.ok ? null : result.message ?? 'Discord did not send who reacted.')
    }).catch((err: unknown) => {
      report(err, 'Reactions.list')
      if (asked.current === ask) setError('Discord did not send who reacted.')
    }).finally(() => { if (asked.current === ask) setBusy(false) })
  }, [message.channel_id, message.id, chosen])
  useEffect(() => { setUsers([]); setMore(true); load() }, [chosenKey]) // eslint-disable-line react-hooks/exhaustive-deps -- chosenKey names the list
  useEffect(() => {
    const key = (event: KeyboardEvent) => { if (event.key === 'Escape') { event.stopPropagation(); onClose() } }
    window.addEventListener('keydown', key, true)
    return () => window.removeEventListener('keydown', key, true)
  }, [onClose])
  const remove = (user: DiscordUser) => {
    if (!chosen) return
    void host.discordReact(message.channel_id, message.id, reactionKey(chosen.reaction.emoji), false, { burst: chosen.burst, userId: user.id }).then(result => {
      if (result.ok) setUsers(list => list.filter(u => u.id !== user.id))
      else setError(result.message ?? 'Discord did not take that reaction off.')
    }).catch(() => setError('Discord did not take that reaction off.'))
  }
  return (
    <div className="exo-dc-modal-back" onClick={onClose}>
      <div className="exo-dc-modal exo-dc-reactors" role="dialog" aria-label="Reactions" onClick={event => event.stopPropagation()}>
        <div className="exo-dc-reactors-emoji" role="tablist" aria-label="Reactions">
          {chips.map(chip => {
            const key = `${reactionKey(chip.reaction.emoji)}:${chip.burst}`
            return (
              <button key={key} type="button" role="tab" aria-selected={key === chosenKey} className={chip.burst ? 'is-super' : undefined}
                style={chip.burst && chip.reaction.burst_colors?.[0] ? { '--exo-dc-burst': chip.reaction.burst_colors[0] } as React.CSSProperties : undefined}
                aria-label={`${emojiName(chip.reaction)}, ${chip.count}${chip.burst ? ' super reactions' : ''}`} onClick={() => setChosen(chip)}>
                <ReactionEmoji reaction={chip.reaction} size={20} />
                {chip.burst ? <Icons.Super size={12} /> : null}
                <span>{chip.count}</span>
              </button>
            )
          })}
        </div>
        <div className="exo-dc-reactors-list" onScroll={event => {
          const el = event.currentTarget
          if (more && !busy && el.scrollTop + el.clientHeight > el.scrollHeight - 80 && users.length) load(users[users.length - 1].id)
        }}>
          {error ? <p className="exo-dc-note" role="alert">{error}</p> : null}
          {users.map(user => (
            <div key={user.id} className="exo-dc-reactor">
              <img className="exo-dc-avatar" src={avatarUrl(user, 32)} alt="" draggable={false} loading="lazy" />
              <strong>{nameOf(user)}</strong>
              <span>{user.username}</span>
              {canRemove ? (
                <button type="button" className="exo-dc-icon-btn" aria-label={`Remove ${nameOf(user)}'s reaction`} onClick={() => remove(user)}>
                  <Icons.Close size={16} />
                </button>
              ) : null}
            </div>
          ))}
          {busy ? <p className="exo-dc-note">Loading</p> : null}
        </div>
      </div>
    </div>
  )
}
