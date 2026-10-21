import { useEffect, useRef, useState } from 'react'
import { host, onHostEvent } from '../../lib/host'
import { report } from '../../lib/report'
import { avatarHashOf, type DiscordMemberList } from '../../lib/discord'
import { Avatar } from './presence'
import { showProfile } from './extras'
import { Nameplate, StyledName } from './collectibles'

const shownLists = new Map<string, DiscordMemberList>()

/**
 * The member list down the right, as Discord shows it: role groups with counts, then each member in their
 * role's color. Discord sends a hundred rows at a time; scrolling near the end asks for the next hundred.
 */
export function MemberList({ guildId }: { guildId: string }) {
  // Another channel of the server opens with the list as it was until Discord sends that channel's.
  const [list, setList] = useState<DiscordMemberList | null>(() => shownLists.get(guildId) ?? null)
  // Asked for more and waiting for Discord's answer (discord.members), so a scroll asks once.
  const asked = useRef(false)

  useEffect(() => {
    let live = true
    const load = () => host.discordMembers(guildId).then(next => {
      if (next.items.length) shownLists.set(guildId, next)
      if (live) { asked.current = false; setList(current => next.items.length || !current ? next : current) }
    })
      .catch((error: unknown) => report(error, 'MemberList.load'))
    load()
    const off = onHostEvent('discord.members', event => { if (event.guildId === guildId) load() })
    return () => { live = false; off() }
  }, [guildId])

  function onScroll(event: React.UIEvent<HTMLElement>) {
    const el = event.currentTarget
    if (!list || asked.current || el.scrollHeight - el.scrollTop - el.clientHeight > 600) return
    if (list.items.length >= (list.rows ?? 0)) return
    asked.current = true
    void host.discordMembers(guildId, true).catch(() => { asked.current = false })
  }

  return (
    <aside className="exo-dc-members" aria-label="Members" onScroll={onScroll}>
      {list && list.items.length === 0 ? <p className="exo-dc-note">Members show here once Discord sends them.</p> : null}
      {list?.items.map((item, i) => 'group' in item
        ? <p key={`g${i}`} className="exo-dc-side-label">{item.group} — {item.count}</p>
        : (
          // A member opens their profile card beside the list, as in Discord; right-click is the person menu.
          <button key={item.id} type="button" className={`exo-dc-member${item.status === 'offline' ? ' is-offline' : ''}${item.look?.nameplate ? ' has-nameplate' : ''}`} data-dc-user={item.id} data-dc-name={item.name}
            onClick={event => { const box = event.currentTarget.getBoundingClientRect(); showProfile(item.id, guildId, box.left - 316, box.top, { id: item.id, username: item.name, global_name: item.name, avatar: avatarHashOf(item.avatar) }) }}>
            <Nameplate look={item.look} scope=".exo-dc-member" />
            <Avatar src={item.avatar} name={item.name} status={item.status} size={32} look={item.look} scope=".exo-dc-member" />
            <span className="exo-dc-member-names">
              <span className="exo-dc-member-name exo-dc-name" style={item.color ? { color: item.color, '--exo-dc-role': item.color } as React.CSSProperties : undefined}>
                <StyledName name={item.name} look={item.look} scope=".exo-dc-member" />{item.bot ? <span className="exo-dc-tag">App</span> : null}
              </span>
              {item.activity ? <span className="exo-dc-member-activity">{item.activity}</span> : null}
            </span>
          </button>
        ))}
    </aside>
  )
}

