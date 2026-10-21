import { useEffect, useRef } from 'react'
import { openToggle } from './openToggle'
import { avatarUrl, displayName, jumpToMessage, type DiscordMessage, type DiscordPlace } from '../../lib/discord'
import { Icons } from './icons'
import { Markdown, type MarkdownContext } from './markdown'

/** A popover's box: a press outside it, or Escape, closes it. */
export function useOutside(onClose: () => void) {
  const box = useRef<HTMLDivElement>(null)
  useEffect(() => {
    // A press in a question the popover asked (Unpin) isn't outside it.
    const away = (event: PointerEvent) => {
      if (box.current && !box.current.contains(event.target as Node) && !openToggle(event.target) && !(event.target as Element).closest?.('.exo-dc-modal-back')) onClose()
    }
    const escape = (event: KeyboardEvent) => { if (event.key === 'Escape') onClose() }
    document.addEventListener('pointerdown', away, true)
    document.addEventListener('keydown', escape)
    return () => { document.removeEventListener('pointerdown', away, true); document.removeEventListener('keydown', escape) }
  }, [onClose])
  return box
}

/**
 * A found message (search, pins, the Inbox): where it is (when not here), who and when, the text, and
 * Jump, which opens it in its conversation; onRemove adds the Inbox's × (the pins' Unpin), told whether Shift was held.
 */
export function Result({ message, place, here, ctx, onJumped, onRemove, removeLabel = 'Remove from Inbox' }: {
  message: DiscordMessage; place: DiscordPlace | null; here: string; ctx: MarkdownContext; onJumped: () => void; onRemove?: (shift: boolean) => void; removeLabel?: string
}) {
  const reachable = place !== null || message.channel_id === here
  const jump = () => {
    onJumped()
    jumpToMessage({
      guildId: place ? place.guildId : ctx.guildId, channelId: message.channel_id, messageId: message.id,
      parentId: place?.parentId ?? null, name: place?.name ?? null,
    })
  }
  return (
    <article className="exo-dc-result">
      {place && message.channel_id !== here ? <p className="exo-dc-result-place">{place.guildId ? `#${place.name}` : place.name}{place.guild ? <span> · {place.guild}</span> : null}</p> : null}
      <div className="exo-dc-result-row">
        <img className="exo-dc-avatar" src={avatarUrl(message.author)} alt="" draggable={false} loading="lazy" />
        <div>
          <p className="exo-dc-msg-head"><strong>{displayName(message)}</strong><time className="exo-dc-msg-time">{new Date(message.timestamp).toLocaleString()}</time></p>
          {message.content ? <Markdown text={message.content} ctx={ctx} /> : <p className="exo-dc-small">Attachment</p>}
        </div>
        <span className="exo-dc-result-tools">
          {reachable ? <button type="button" className="exo-dc-jump" onClick={jump}>Jump</button> : null}
          {onRemove ? <button type="button" className="exo-dc-jump" aria-label={removeLabel} onClick={event => onRemove(event.shiftKey)}><Icons.Close size={14} /></button> : null}
        </span>
      </div>
    </article>
  )
}
