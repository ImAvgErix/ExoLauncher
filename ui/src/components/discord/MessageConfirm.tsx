import { avatarUrl, displayName, type DiscordMessage } from '../../lib/discord'
import { Confirm } from './admin'
import { MarkdownLine, type MarkdownContext } from './markdown'

export type ConfirmKind = 'delete' | 'pin' | 'unpin'

// Each question in Exo's words; Pin names the channel (a DM is "here").
function words(kind: ConfirmKind, channel: string | null) {
  if (kind === 'pin') {
    return {
      title: 'Pin this message?', action: 'Pin', danger: false, tip: null,
      body: `${channel ? `Everyone in #${channel}` : 'Everyone here'} can find it in Pinned messages.`,
    }
  }
  if (kind === 'unpin') return { title: 'Unpin this message?', action: 'Unpin', danger: false, body: null, tip: 'Hold Shift when you press Unpin to skip this.' }
  return { title: 'Delete this message?', action: 'Delete', danger: true, body: null, tip: 'Hold Shift when you press Delete message to skip this.' }
}

/**
 * Discord's Delete Message, Pin and Unpin questions in Exo's one dialog: the message as it is, Cancel and the action.
 * Holding Shift while choosing the action skips the question, as in Discord.
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
  const { title, body, tip, action, danger } = words(kind, channel)
  return (
    <Confirm title={title} action={action} danger={danger} onConfirm={onConfirm} onClose={onCancel}>
      {body ? <p>{body}</p> : null}
      <div className="exo-dc-delete-quote">
        <img src={avatarUrl(message.author, 40)} alt="" draggable={false} />
        <span>
          <strong>{displayName(message)}</strong>
          <span>{message.content ? <MarkdownLine text={message.content} ctx={ctx} /> : message.attachments.length ? 'Attachment' : ''}</span>
        </span>
      </div>
      {tip ? <p className="exo-dc-delete-tip">{tip}</p> : null}
    </Confirm>
  )
}
