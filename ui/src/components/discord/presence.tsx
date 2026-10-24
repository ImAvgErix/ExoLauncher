import { cdnSized, type DiscordLook, type DiscordRoleIcon } from '../../lib/discord'
import type { CustomStatus } from '../../lib/discordPrefs'
import { Decoration } from './collectibles'
import { emojiUrl } from './markdown'

/**
 * An avatar with Discord's status dot (online, idle, do not disturb; nothing when offline or unknown) and their
 * avatar decoration, moving while the pointer is on scope (their row).
 */
export function Avatar({ src, name, status, size, look, scope }: { src: string | null | undefined; name: string; status: string | null; size: number; look?: DiscordLook | null; scope?: string }) {
  return (
    <span className="exo-dc-avatar-wrap" style={{ width: size, height: size }}>
      {src ? <img src={cdnSized(src, size)} alt="" draggable={false} loading="lazy" /> : <span className="exo-dc-initials">{[...name][0] ?? '?'}</span>}
      <Decoration look={look} size={size} scope={scope} />
      {status && status !== 'offline' && status !== 'invisible' ? <span className={`exo-dc-status is-${status}`} aria-label={status} /> : null}
    </span>
  )
}

/** A role's icon beside a name, as Discord shows it: the role's picture or emoji, named for the role. */
export function RoleIcon({ icon }: { icon?: DiscordRoleIcon | null }) {
  if (!icon) return null
  return (
    <span className="exo-dc-role-icon" role="img" aria-label={icon.name}>
      {icon.url ? <img src={icon.url} alt="" draggable={false} /> : icon.emoji}
    </span>
  )
}

/** A custom status as one line: its emoji, its words, and when it clears. */
export function CustomStatusLine({ custom, clears = false }: { custom: CustomStatus; clears?: boolean }) {
  const until = clears && custom.expiresAtMs ? new Date(custom.expiresAtMs).toLocaleString(undefined, { weekday: 'short', hour: 'numeric', minute: '2-digit' }) : null
  return (
    <>
      {custom.emojiId ? <img className="exo-dc-custom-emoji" src={emojiUrl(custom.emojiId, false, 48)} alt={`:${custom.emojiName ?? ''}:`} width={16} height={16} />
        : custom.emojiName ? <span aria-hidden="true">{custom.emojiName} </span> : null}
      {custom.text}{until ? ` · Clears ${until}` : null}
    </>
  )
}
