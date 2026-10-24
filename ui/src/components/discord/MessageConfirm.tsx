import { useEffect } from 'react'
import { avatarUrl, displayName, type DiscordMessage } from '../../lib/discord'
import { MarkdownLine, type MarkdownContext } from './markdown'
import { ModalBack } from './SettingsLayer'

export type ConfirmKind = 'delete' | 'pin' | 'unpin'

// Discord's words for each question; Pin names the channel (a DM is "the current channel").
function words(kind: ConfirmKind, channel: string | null) {
  if (kind === 'pin') {
    return {
      title: 'Pin it. Pin it good.', action: 'Oh yeah. Pin it', danger: false, tip: null,
      body: `Hey, just double checking that you want to pin this message to ${channel ? `#${channel}` : 'the current channel'} for posterity and greatness?`,
    }
  }
  if (kind === 'unpin') {
    return {
      title: 'Unpin message', action: 'Remove it please!', danger: true, body: 'You sure you want to remove this pinned message?',
      tip: 'You can hold down shift when clicking unpin message to bypass this confirmation entirely.',
    }
  }
  return {
    title: 'Delete message', action: 'Delete', danger: true, body: 'Are you sure you want to delete this message?',
    tip: 'Tip: hold Shift when you press Delete Message to skip this.',
  }
}

/**
 * Discord's Delete Message, Pin and Unpin questions: the message as it is, Cancel and the action. Holding Shift while
 * choosing the action skips the question, as in Discord.
 */
export function MessageConfirm({ kind, message, channel, ctx, onConfirm, onCancel }: {
  kind: ConfirmKind
  message: DiscordMessage
  /** The server channel's name, for Pin's question (null in a DM). */
  channel: string | null
  ctx: MarkdownContext
  onConfirm: () => void
  onCancel: () => void
}) {
  useEffect(() => {
    const key = (event: KeyboardEvent) => {
      if (event.key === 'Escape') { event.stopPropagation(); onCancel() }
      else if (event.key === 'Enter') { event.preventDefault(); onConfirm() }
    }
    window.addEventListener('keydown', key, true)
    return () => window.removeEventListener('keydown', key, true)
  }, [onConfirm, onCancel])
  const { title, body, tip, action, danger } = words(kind, channel)
  return (
    <ModalBack onClose={onCancel}>
      <div className="exo-dc-modal exo-dc-delete" role="alertdialog" aria-label={title} onClick={event => event.stopPropagation()}>
        <h2>{title}</h2>
        <p className="exo-dc-note">{body}</p>
        <div className="exo-dc-delete-quote">
          <img src={avatarUrl(message.author, 40)} alt="" draggable={false} />
          <span>
            <strong>{displayName(message)}</strong>
            <span>{message.content ? <MarkdownLine text={message.content} ctx={ctx} /> : message.attachments.length ? 'Attachment' : ''}</span>
          </span>
        </div>
        {tip ? <p className="exo-dc-delete-tip">{tip}</p> : null}
        <footer className="exo-dc-delete-foot">
          <button type="button" className="exo-dc-tab" onClick={onCancel}>Cancel</button>
          <button type="button" className={`exo-dc-tab${danger ? ' is-danger' : ' is-raised'}`} onClick={onConfirm} autoFocus>{action}</button>
        </footer>
      </div>
    </ModalBack>
  )
}
