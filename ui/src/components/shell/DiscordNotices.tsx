import { memo, useEffect, useRef } from 'react'
import { onHostEvent } from '../../lib/host'
import type { DiscordNotice } from '../../lib/discord'
import { dismissNotice, findNotice, notify } from '../../lib/notify'
import { Avatar } from '../discord/presence'

/** How long a message's notice stays (Windows' and macOS's own banners), paused while the pointer or focus is on it. */
const STAY_MS = 5000

/**
 * Discord messages that notify while Exo is in front, as notices in Exo's one stack: one per conversation, the
 * person's picture and name, where it was (#channel · Server, or the group), and the message on a few lines. A newer
 * message in the same conversation takes its notice's place and counts up. Pressing one opens that conversation;
 * reading or opening it anywhere (the host's discord.notifyClear) takes it away.
 */
export const DiscordNotices = memo(function DiscordNotices({ onOpen }: {
  onOpen: (notice: DiscordNotice) => void
}) {
  const open = useRef(onOpen)
  useEffect(() => { open.current = onOpen })
  // The message each conversation's notice shows, so clearing an older one leaves a newer one.
  const shown = useRef(new Map<string, string>())

  useEffect(() => onHostEvent('discord.notify', message => {
    const id = `dc:${message.channelId}`
    const where = message.place ? message.server ? `${message.place} · ${message.server}` : message.place : message.server
    shown.current.set(message.channelId, message.messageId)
    notify({
      id, title: message.author, where: where || undefined, text: message.body, stay: STAY_MS, sticky: false,
      icon: <Avatar src={message.avatar} name={message.author} status={null} size={36} />,
      count: (findNotice(id)?.count ?? 0) + 1,
      onOpen: () => open.current(message),
    })
  }), [])

  useEffect(() => onHostEvent('discord.notifyClear', ({ channelId, messageId }) => {
    if (messageId && shown.current.get(channelId) !== messageId) return
    shown.current.delete(channelId)
    dismissNotice(`dc:${channelId}`)
  }), [])

  return null
})
