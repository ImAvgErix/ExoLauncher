import { useEffect, useRef, useState } from 'react'
import { host, onHostEvent } from '../../lib/host'
import { report } from '../../lib/report'
import { avatarHashOf, type DiscordMemberList } from '../../lib/discord'
import { Avatar, RoleIcon } from './presence'
import { showProfile } from './extras'
import { Nameplate, StyledName } from './collectibles'
import { GuildTag } from './guildTag'
import { Icons } from './icons'

const shownLists = new Map<string, DiscordMemberList>()
// A member's row (.exo-dc-member); a row not sent yet takes the same, so the list is its full length from the start.
const ROW = 42

/**
 * The member list down the right, as Discord shows it: role groups with counts, then each member in their
 * role's color. Discord sends a hundred rows at a time: the list is drawn its full length with placeholders, and
 * scrolling to rows not sent yet asks for the hundreds there.
 */
export function MemberList({ guildId }: { guildId: string }) {
  // Another channel of the server opens with the list as it was until Discord sends that channel's.
  const [list, setList] = useState<DiscordMemberList | null>(() => shownLists.get(guildId) ?? null)
  // The hundred asked for and waiting on Discord's answer (discord.members), so a scroll asks once.
  const asked = useRef<number | null>(null)

  useEffect(() => {
    let live = true
    let waiting: number | undefined
    let last = 0
    const load = () => {
      last = Date.now()
      host.discordMembers(guildId).then(next => {
        // The lists of servers visited this session stay cached, but not without end (a big server's is hundreds of KB).
        if (next.items.length) {
          shownLists.delete(guildId)
          shownLists.set(guildId, next)
          while (shownLists.size > 24) shownLists.delete(shownLists.keys().next().value!)
        }
        if (live) { asked.current = null; setList(current => next.items.length || !current ? next : current) }
      })
        .catch((error: unknown) => report(error, 'MemberList.load'))
    }
    load()
    // A presence change is one event and a busy server sends them in bursts; the list is whole either way, so one load a
    // half-second covers them all (an update waiting already covers one that arrives behind it).
    const off = onHostEvent('discord.members', event => {
      if (event.guildId !== guildId || waiting !== undefined) return
      waiting = window.setTimeout(() => { waiting = undefined; if (live) load() }, Math.max(80, 500 - (Date.now() - last)))
    })
    return () => { live = false; window.clearTimeout(waiting); off() }
  }, [guildId])

  function onScroll(event: React.UIEvent<HTMLElement>) {
    const el = event.currentTarget
    if (!list) return
    // The rows in view, each counted as a member's (a group's label is near enough).
    const at = Math.floor(el.scrollTop / ROW)
    const end = Math.min(list.rows ?? list.items.length, at + Math.ceil(el.clientHeight / ROW) + 1)
    let missing = false
    for (let i = at; i < end && !missing; i++) missing = (list.items[i] ?? null) === null
    const hundred = Math.floor(at / 100)
    if (!missing || asked.current === hundred) return
    asked.current = hundred
    void host.discordMembers(guildId, at).catch(() => { asked.current = null })
  }

  const rest = Math.max(0, (list?.rows ?? 0) - (list?.items.length ?? 0))

  return (
    // The pane draws its glass; the list inside it scrolls, so it can fade into the pane's edges (exo-shell.css › pool).
    <aside className="exo-dc-members" aria-label="Members">
      <div className="exo-dc-members-list" onScroll={onScroll}>
      {list && list.items.length === 0 ? <p className="exo-dc-note">Members show here once Discord sends them.</p> : null}
      {list?.items.map((item, i) => item === null
        ? <div key={`p${i}`} className="exo-dc-member is-pending" aria-hidden="true" />
        : 'group' in item
        ? <p key={`g${i}`} className="exo-dc-side-label">{item.group} — {item.count}</p>
        : (
          // A member opens their profile card beside the list, as in Discord; right-click is the person menu.
          <button key={item.id} type="button" className={`exo-dc-member${item.status === 'offline' ? ' is-offline' : ''}${item.look?.nameplate ? ' has-nameplate' : ''}`} data-dc-user={item.id} data-dc-name={item.name}
            onClick={event => { const box = event.currentTarget.getBoundingClientRect(); showProfile(item.id, guildId, box.left - 316, box.top, { id: item.id, username: item.name, global_name: item.name, avatar: avatarHashOf(item.avatar) }) }}>
            <Nameplate look={item.look} scope=".exo-dc-member" />
            <Avatar src={item.avatar} name={item.name} status={item.status} size={32} look={item.look} scope=".exo-dc-member" />
            <span className="exo-dc-member-names">
              <span className="exo-dc-member-name exo-dc-name" style={item.color ? { color: item.color, '--exo-dc-role': item.color } as React.CSSProperties : undefined}>
                <StyledName name={item.name} look={item.look} scope=".exo-dc-member" /><RoleIcon icon={item.roleIcon} /><GuildTag look={item.look} />{item.bot ? <span className="exo-dc-tag">App</span> : null}
                {item.owner ? <span className="exo-dc-member-mark" role="img" aria-label="Server owner"><Icons.Owner size={14} /></span> : null}
                {item.boosting ? <span className="exo-dc-member-mark" role="img" aria-label={`Server boosting since ${new Date(item.boosting).toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric' })}`}>
                  <Icons.Boosting size={14} /></span> : null}
              </span>
              {item.inVoice ? <span className="exo-dc-member-activity is-voice" aria-label={`In voice (${item.inVoice})`}><Icons.Voice size={12} />In voice</span>
                : item.activity ? <span className="exo-dc-member-activity">{item.activity}</span> : null}
            </span>
          </button>
        ))}
      {rest ? <div className="exo-dc-member is-pending" style={{ height: rest * ROW }} aria-hidden="true" /> : null}
      </div>
    </aside>
  )
}

