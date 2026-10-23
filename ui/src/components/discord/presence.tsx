import { cdnSized, type DiscordLook } from '../../lib/discord'
import { Decoration } from './collectibles'

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
