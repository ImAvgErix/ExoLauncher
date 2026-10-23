import { memo, useEffect, useRef, useState, type CSSProperties } from 'react'
import { host } from '../../lib/host'
import { usePageActive } from '../../lib/pageActivity'
import { shouldReduceMotion } from '../../lib/motionPreference'
import { Poll, PollResultLine } from './Poll'
import { Attachments, fit, MovingImage, size, sized } from './Attachments'
import { LottieSticker } from './LottieSticker'
import { performanceMode, avatarUrl, displayName, jumpToMessage, openDiscord, type DiscordEmbed, type DiscordInvite, type DiscordMessage } from '../../lib/discord'
import { Reactions, ReactionsDialog, useNitro } from './Reactions'
import { EmojiPicker } from './EmojiPicker'
import { Icons } from './icons'
import { showImage, showProfile } from './extras'
import { inviteCode, Link, Markdown, MarkdownLine, type MarkdownContext } from './markdown'
import { accountSettings, useAccountSettings } from '../../lib/discordPrefs'
import { EPHEMERAL, LOADING } from '../../lib/discordApps'
import { MessageComponents } from './Components'
import { MessageApps } from './AppCommands'
import { CompanyLogo } from '../../brand/CompanyLogo'
import { openLink } from '../../lib/music'
import { Decoration, lookOf, StyledName } from './collectibles'
import { useDismiss } from '../../lib/dismiss'

/**
 * A GIF link: plays by itself while Exo is active when Play GIFs when Discord is focused is on (not in
 * Performance mode or with reduced motion), else holds still until you point at it, as in Discord.
 */
function Gifv({ src, link, box }: { src: string; link: string; box: { width?: number; height?: number } }) {
  const { gifAutoPlay } = useAccountSettings()
  const active = usePageActive()
  const auto = gifAutoPlay && !performanceMode() && !shouldReduceMotion()
  const video = useRef<HTMLVideoElement>(null)
  useEffect(() => {
    const el = video.current
    if (!el) return
    if (auto && active) void el.play().catch(() => { })
    else el.pause()
  }, [auto, active])
  return (
    <video ref={video} className="exo-dc-media" src={src} data-dc-link={link} autoPlay={auto && active} loop muted playsInline {...box}
      onPointerEnter={event => { if (!auto || !active) void event.currentTarget.play().catch(() => { }) }}
      onPointerLeave={event => { if (!auto || !active) event.currentTarget.pause() }} />
  )
}

function Embed({ embed, ctx }: { embed: DiscordEmbed; ctx: MarkdownContext }) {
  // Discord settings > Appearance: links' pictures (When posted as links to chat) and the cards (Show embeds and link previews).
  const { inlineEmbedMedia, renderEmbeds } = useAccountSettings()
  const media = embed.type === 'image' || embed.type === 'gifv'
  if (media ? !inlineEmbedMedia : !renderEmbeds) return null
  // A bare image or GIF link shows as the picture alone, the way Discord does.
  if (embed.type === 'image' && embed.thumbnail) {
    const box = fit(embed.thumbnail.width, embed.thumbnail.height)
    const src = embed.thumbnail.proxy_url ?? embed.thumbnail.url ?? ''
    return (
      <button type="button" className="exo-dc-media" aria-label="Open image" onClick={() => showImage(src, embed.url ?? src)}>
        <MovingImage src={src} width={embed.thumbnail.width} height={embed.thumbnail.height} moving={/\.gif($|\?)/i.test(embed.thumbnail.url ?? src)} alt="" link={embed.url ?? embed.thumbnail.url} box={box} />
      </button>
    )
  }
  if (embed.type === 'gifv' && embed.video) {
    const box = fit(embed.video.width, embed.video.height)
    const src = embed.video.proxy_url ?? embed.video.url ?? ''
    return (
      <button type="button" className="exo-dc-gifv" aria-label="Open GIF" onClick={() => showImage(src, embed.url ?? src, true)}>
        <Gifv src={src} link={embed.url ?? src} box={box} />
        <span className="exo-dc-gif-badge">GIF</span>
      </button>
    )
  }
  return <EmbedCard embed={embed} ctx={ctx} />
}

function EmbedFields({ fields, ctx }: { fields: NonNullable<DiscordEmbed['fields']>; ctx: MarkdownContext }) {
  return (
    <div className="exo-dc-embed-fields">
      {fields.map((field, i) => (
        <div key={i} className={field.inline ? 'is-inline' : undefined}>
          <p className="exo-dc-embed-field">{field.name}</p>
          <Markdown text={field.value} ctx={ctx} />
        </div>
      ))}
    </div>
  )
}

function EmbedHead({ embed }: { embed: DiscordEmbed }) {
  return (
    <>
      {embed.provider?.name ? <p className="exo-dc-embed-small">{embed.provider.name}</p> : null}
      {embed.author?.name ? <p className="exo-dc-embed-author">{embed.author.name}</p> : null}
      {embed.title ? (
        <p className="exo-dc-embed-title">{embed.url ? <Link url={embed.url}>{embed.title}</Link> : embed.title}</p>
      ) : null}
    </>
  )
}

// An embed's picture at most 400 by 300; a video's (YouTube's) in the video's own shape with its thumbnail's bars cropped, as Discord shows it.
function EmbedImage({ image, video }: { image: NonNullable<DiscordEmbed['image']>; video?: DiscordEmbed['video'] }) {
  const shape = video?.width && video.height ? video : image
  const src = image.proxy_url ?? image.url!
  return (
    <img className={`exo-dc-embed-image${shape === video ? ' is-video' : ''}`} src={sized(src, image.width, image.height, { width: 400, height: 300 })} alt="" loading="lazy"
      draggable={false} data-dc-link={image.url} {...fit(shape.width, shape.height, 400, 300)} onClick={() => showImage(src, image.url!)} />
  )
}

function EmbedCard({ embed, ctx }: { embed: DiscordEmbed; ctx: MarkdownContext }) {
  const image = embed.image ?? (embed.type === 'video' ? embed.thumbnail : undefined)
  const thumb = embed.type === 'video' ? undefined : embed.thumbnail
  const color = embed.color === undefined ? undefined : `#${embed.color.toString(16).padStart(6, '0')}`
  return (
    <div className="exo-dc-embed" style={color ? { borderLeftColor: color } : undefined}>
      <div className="exo-dc-embed-body">
        <EmbedHead embed={embed} />
        {embed.description ? <Markdown text={embed.description} ctx={ctx} /> : null}
        {embed.fields?.length ? <EmbedFields fields={embed.fields} ctx={ctx} /> : null}
        {image?.url ? <EmbedImage image={image} video={embed.type === 'video' ? embed.video : undefined} /> : null}
        {embed.footer?.text ? <p className="exo-dc-embed-small">{embed.footer.text}</p> : null}
      </div>
      {thumb?.url ? <img className="exo-dc-embed-thumb" src={sized(thumb.proxy_url ?? thumb.url, thumb.width, thumb.height, { width: 80, height: 80 })} alt="" loading="lazy" draggable={false} data-dc-link={thumb.url} /> : null}
    </div>
  )
}

const SYSTEM: Record<number, string> = {
  1: 'added someone to the group.',
  2: 'removed someone from the group.',
  4: 'changed the channel name.',
  5: 'changed the channel icon.',
  6: 'pinned a message to this channel.',
  7: 'joined the server.',
  8: 'boosted the server!',
  9: 'boosted the server! The server has reached Level 1!',
  10: 'boosted the server! The server has reached Level 2!',
  11: 'boosted the server! The server has reached Level 3!',
  12: 'added a channel to follow here. Its most important updates will show up here.',
  14: 'This server has been removed from Server Discovery.',
  15: 'This server is eligible for Server Discovery again.',
  18: 'started a thread.',
  22: 'Invite your friends to this server.',
  24: 'AutoMod blocked a message.',
  27: 'started a stage.',
  28: 'ended the stage.',
  29: 'is now a speaker.',
  31: 'changed the stage topic.',
  36: 'turned on security actions.',
  37: 'turned off security actions.',
  38: 'reported a raid.',
  39: 'reported a false alarm.',
}

// Discord's message times: "Today at 3:45 PM", "Yesterday at …", else the date and time; the gutter shows the clock only.
function time(iso: string, withDate: boolean) {
  const date = new Date(iso)
  // Discord settings > Language & Time > Time format: Auto, 12-hour or 24-hour.
  const cycle = accountSettings().timestampHourCycle
  const clock = date.toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit', ...(cycle === 1 ? { hour12: true } : cycle === 2 ? { hourCycle: 'h23' as const } : {}) })
  if (!withDate) return clock
  const day = new Date(date).setHours(0, 0, 0, 0)
  const today = new Date().setHours(0, 0, 0, 0)
  if (day === today) return `Today at ${clock}`
  if (today - day === 86_400_000) return `Yesterday at ${clock}`
  return `${date.toLocaleDateString()} ${clock}`
}

export type MessageActions = {
  onReply: (message: DiscordMessage) => void
  onEdit: (message: DiscordMessage) => void
  /** The inline edit closed (saved or cancelled). */
  onEditDone: () => void
  onDelete: (message: DiscordMessage) => void
  /** burst: a super reaction. */
  onReact: (message: DiscordMessage, emoji: string, add: boolean, burst?: boolean) => void
  onPin: (message: DiscordMessage) => void
  onForward: (message: DiscordMessage) => void
  /** Read up to just before this message (Mark Unread, or Alt+click). */
  onMarkUnread: (message: DiscordMessage) => void
  /** A server text channel: Create Thread from a message, and a thread under one opens. */
  onThread?: (message: DiscordMessage) => void
  onOpenThread?: (thread: { id: string; name: string }) => void
  /** Manage Messages here: others' messages can be deleted. */
  canManageMessages?: boolean
  /** Pin Messages here (always in a DM). */
  canPin?: boolean
  /** A message Discord didn't take: send it again, or drop it. */
  onRetry?: (message: DiscordMessage) => void
  onDiscard?: (message: DiscordMessage) => void
}

type MessageProps = {
  message: DiscordMessage
  grouped: boolean
  ctx: MarkdownContext
  mine: boolean
  canSend: boolean
  /** Being edited in place, Discord's way. */
  editing?: boolean
  actions: MessageActions
}

/** One message; grouped under the one before it when the same person wrote it within minutes. */
export const Message = memo(function Message(props: MessageProps) {
  const { message, ctx, actions } = props
  const name = displayName(message)
  if (message.type === 3) return <CallLine message={message} name={name} meId={ctx.meId} />
  if (message.type === 46) return <PollResultLine message={message} name={name} />
  if (message.type === 67) return <FriendAcceptedLine message={message} name={name} other={ctx.dmName ?? null} mine={message.author.id === ctx.meId} />
  if (SYSTEM[message.type]) return <SystemLine message={message} name={name} onOpenThread={actions.onOpenThread} />
  // A thread's first message is the one it started from, shown as itself.
  if (message.type === 21 && message.referenced_message) return <Message {...props} grouped={false} message={message.referenced_message} />
  return <UserMessage {...props} name={name} />
})

// A system line: who did what, and when; a thread started from here names it and opens it, as Discord's line does.
function SystemLine({ message, name, onOpenThread }: { message: DiscordMessage; name: string; onOpenThread?: (thread: { id: string; name: string }) => void }) {
  const thread = message.type === 18 && message.message_reference?.channel_id ? { id: message.message_reference.channel_id, name: message.content } : null
  return (
    <div className="exo-dc-msg is-system" data-msg-id={message.id}>
      <span className="exo-dc-msg-gutter" aria-hidden="true">→</span>
      <p>
        <strong>{name}</strong> {thread ? <>started a thread: <button type="button" className="exo-dc-link" onClick={() => onOpenThread?.(thread)}>{thread.name}</button></> : SYSTEM[message.type]}
        {' '}<time className="exo-dc-msg-time">{time(message.timestamp, true)}</time>
      </p>
    </div>
  )
}

// What heads a message: the reply it answers (not a forward's source), or the command an app answered.
function headOf(message: DiscordMessage) {
  const reply = message.message_reference?.type === 1 ? null : message.referenced_message ?? null
  return { reply, command: reply ? null : message.interaction_metadata ?? message.interaction ?? null }
}

function UserMessage({ message, grouped, ctx, mine, canSend, editing, actions, name }: MessageProps & { name: string }) {
  // Compact (Discord settings > Accessibility > Chat Message Display) puts the name on every line.
  const compact = useAccountSettings().messageDisplayCompact
  const { reply, command } = headOf(message)
  const continued = grouped && !compact && !reply && !command
  const classes = ['exo-dc-msg', continued && 'is-grouped', mentions(message, ctx.meId) && 'is-mention', message.pending && !message.failed && 'is-pending', message.failed && 'is-failed'].filter(Boolean).join(' ')
  // Discord's Reactions list, open on one reaction (a right-click on it) or the first (View Reactions).
  const [reactors, setReactors] = useState<string | null | false>(false)
  return (
    <div data-msg-id={message.id} onContextMenu={event => openMore(event, message)} className={classes}
      onClick={event => { if (event.altKey && !message.pending) { event.preventDefault(); actions.onMarkUnread(message) } }}>
      {reply ? <ReplyQuote reply={reply} ctx={ctx} /> : null}
      {command ? <CommandLine command={command} /> : null}
      <div className="exo-dc-msg-row">
        {continued
          ? <time className="exo-dc-msg-gutter">{time(message.timestamp, false)}</time>
          : (
            // Their picture with their avatar decoration, moving while the message is pointed at.
            <span className="exo-dc-avatar exo-dc-msg-face is-clickable" data-dc-user={message.author.id} data-dc-name={name} data-dc-username={message.author.username}
              onClick={event => { const box = event.currentTarget.getBoundingClientRect(); showProfile(message.author.id, ctx.guildId, box.right + 8, box.top, message.author) }}>
              <img src={avatarUrl(message.author)} alt="" draggable={false} loading="lazy" />
              <Decoration look={lookOf(message.author, message.member)} size={40} scope=".exo-dc-msg" />
            </span>
          )}
        <MessageBody message={message} name={name} head={!continued} ctx={ctx} editing={editing === true} actions={actions} onViewReactions={setReactors} />
      </div>
      {message.pending || editing ? null : <ActionsBar message={message} ctx={ctx} mine={mine} canSend={canSend} actions={actions} onViewReactions={() => setReactors(null)} />}
      {reactors !== false && message.reactions?.length ? <ReactionsDialog message={message} first={reactors} onClose={() => setReactors(false)} /> : null}
    </div>
  )
}

// The name line, the text (or its edit box), then what hangs under it: a forward, files on their way, a thread, reactions.
function MessageBody({ message, name, head, ctx, editing, actions, onViewReactions }: { message: DiscordMessage; name: string; head: boolean; ctx: MarkdownContext; editing: boolean; actions: MessageActions; onViewReactions: (emoji: string) => void }) {
  const nitro = useNitro(ctx.guildId)
  return (
    <div className="exo-dc-msg-body">
      {head ? (
        <p className="exo-dc-msg-head">
          <AuthorName message={message} name={name} ctx={ctx} />
          {message.author.bot ? <span className="exo-dc-tag">App</span> : null}
          <time className="exo-dc-msg-time">{time(message.timestamp, true)}</time>
        </p>
      ) : null}
      {editing ? <InlineEdit message={message} onDone={actions.onEditDone} />
        : (message.flags ?? 0) & LOADING ? <Thinking name={name} /> : <MessageContent message={message} ctx={ctx} />}
      {message.components?.length && !editing ? <MessageComponents message={message} ctx={ctx} /> : null}
      {(message.flags ?? 0) & EPHEMERAL ? <OnlyYou message={message} /> : null}
      {message.message_snapshots?.length ? <Forwarded message={message} ctx={ctx} /> : null}
      {message.uploading ? <Uploading upload={message.uploading} /> : null}
      {message.failed ? <Failed message={message} actions={actions} /> : null}
      {message.thread ? <ThreadBox thread={message.thread} onOpen={actions.onOpenThread} /> : null}
      {message.reactions?.length ? <Reactions message={message} nitro={nitro} onView={onViewReactions} onReact={(emoji, add, burst) => actions.onReact(message, emoji, add, burst)} /> : null}
    </div>
  )
}

// The author's name in their role color (Role Colors in Accessibility can show it beside the name or not at all).
function AuthorName({ message, name, ctx }: { message: DiscordMessage; name: string; ctx: MarkdownContext }) {
  const color = ctx.colors.get(message.author.id)
  return (
    <strong className="is-clickable exo-dc-name" style={color ? { color, '--exo-dc-role': color } as React.CSSProperties : undefined}
      data-dc-user={message.author.id} data-dc-name={name} data-dc-username={message.author.username}
      onClick={event => showProfile(message.author.id, ctx.guildId, event.clientX + 12, event.clientY, message.author)}>
      <StyledName name={name} look={lookOf(message.author, message.member)} scope=".exo-dc-msg" />
    </strong>
  )
}

// The edited mark sits at the end of the last line, as in Discord.
function MessageContent({ message, ctx }: { message: Pick<DiscordMessage, 'content' | 'edited_timestamp' | 'attachments' | 'poll' | 'sticker_items' | 'embeds' | 'flags'> & Partial<DiscordMessage>; ctx: MarkdownContext }) {
  const edited = message.edited_timestamp ? <span className="exo-dc-edited">(edited)</span> : null
  const jam = useJam(message.content)
  // A jam link alone shows as its card, as a link with a preview shows as the preview.
  const text = message.content && !linksOnly(message) && message.content.trim() !== jam
  // SUPPRESS_EMBEDS (4): the sender removed the link previews.
  const embeds = (message.flags ?? 0) & 4 ? [] : message.embeds
  return (
    <>
      {text ? <Markdown text={message.content} ctx={ctx} tail={edited} /> : edited}
      {message.attachments.length ? <Attachments files={message.attachments} guildId={ctx.guildId} moderator={ctx.moderator} /> : null}
      {message.poll && message.id ? <Poll message={message as DiscordMessage} meId={ctx.meId} /> : null}
      {/* A sticker, still or animated (Lottie, as Discord's own packs are), its name showing under the pointer. */}
      {message.sticker_items?.map(sticker => (
        <span key={sticker.id} className="exo-dc-sticker-msg">
          {sticker.format_type === 3
            ? <LottieSticker id={sticker.id} size={160} label={sticker.name} />
            : <StickerImage id={sticker.id} name={sticker.name} format={sticker.format_type} />}
          <span className="exo-dc-sticker-name" aria-hidden="true">{sticker.name}</span>
        </span>
      ))}
      {embeds.map((embed, i) => <Embed key={i} embed={embed} ctx={ctx} />)}
      {jam ? <SpotifyJam url={jam} /> : null}
      {invitesIn(message.content).map(code => <InviteCard key={code} code={code} mine={!!ctx.meId && message.author?.id === ctx.meId} />)}
    </>
  )
}

// A Spotify Jam: its own link, or a share short link (spotify.link) that may be one; each short link is asked about once.
const JAM_LINK = /https?:\/\/(?:open\.spotify\.com\/socialsession\/[\w-]+|(?:spotify\.link|spotify\.app\.link)\/\w+)[^\s<>]*/i
const jams = new Map<string, Promise<boolean>>()
function isJam(url: string) {
  if (/\/socialsession\//i.test(url)) return Promise.resolve(true)
  let known = jams.get(url)
  if (!known) {
    known = host.spotifyLink(url).then(result => !!result.ok && !!result.uri?.startsWith('spotify:socialsession:')).catch(() => false)
    jams.set(url, known)
  }
  return known
}

/** The message's Spotify Jam link once it is known to be one; null otherwise. */
function useJam(text: string) {
  const url = JAM_LINK.exec(text)?.[0] ?? null
  const [jam, setJam] = useState(false)
  useEffect(() => {
    let live = true
    setJam(false)
    if (url) void isJam(url).then(yes => { if (live) setJam(yes) })
    return () => { live = false }
  }, [url])
  return url && jam ? url : null
}

/** A Spotify Jam invite, as Discord shows an activity invite: what it is, and Join, which joins it in Exo's Music room. */
function SpotifyJam({ url }: { url: string }) {
  return (
    <div className="exo-dc-jam">
      <p className="exo-dc-jam-label">Invite to a Spotify Jam</p>
      <div className="exo-dc-jam-row">
        <CompanyLogo company="spotify" size={40} />
        <span className="exo-dc-jam-what"><strong>Spotify Jam</strong><span>Listen together</span></span>
        <button type="button" className="button button--primary" onClick={() => openLink(url)}>Join</button>
      </div>
    </div>
  )
}

// A message's server invites (discord.gg/code, discord.com/invite/code), each once, in order.
function invitesIn(text: string) {
  return [...new Set((text.match(/https?:\/\/\S+/g) ?? []).map(inviteCode).filter((code): code is string => !!code))]
}

// Each invite is asked about once: what it opens, or 'invalid' (unknown or expired); null when Discord didn't answer.
type InviteRead = DiscordInvite | 'invalid' | null
const invites = new Map<string, InviteRead>()
const reading = new Map<string, Promise<InviteRead>>()
function readInvite(code: string) {
  let read = reading.get(code)
  if (!read) {
    read = host.discordServer('invite', { invite: code })
      .then(result => result.ok && result.invite ? result.invite : result.invalid ? 'invalid' as const : null, () => null)
      .then(answer => { invites.set(code, answer); if (!answer) reading.delete(code); return answer })
    reading.set(code, read)
  }
  return read
}

/**
 * Discord's invite card under a message with a server invite: the server's icon, name and badge, who's online and how
 * many are in it, and Join (Joined, opening it, when you're in it); an expired or unknown one says so.
 */
function InviteCard({ code, mine }: { code: string; mine: boolean }) {
  const [invite, setInvite] = useState<InviteRead | undefined>(() => invites.get(code))
  useEffect(() => {
    let live = true
    if (!invites.has(code)) void readInvite(code).then(answer => { if (live) setInvite(answer) })
    return () => { live = false }
  }, [code])
  if (invite === null) return null
  if (invite === 'invalid') {
    return (
      <div className="exo-dc-jam is-invalid">
        <p className="exo-dc-jam-label">{mine ? 'You sent an invite, but' : "You've been invited, but"}</p>
        <div className="exo-dc-jam-row">
          <span className="exo-dc-invite-icon" aria-hidden="true" />
          <span className="exo-dc-jam-what"><strong>Invalid Invite</strong><span>{mine ? 'Try sending a new invite' : 'Ask for a new invite'}</span></span>
        </div>
      </div>
    )
  }
  const join = () => {
    if (!invite) return
    if (invite.joined && invite.guildId) return openDiscord({ guildId: invite.guildId, channelId: invite.channelId, outside: false })
    // Joining changes what the card says: it asks again when it is next drawn.
    invites.delete(code)
    reading.delete(code)
    window.dispatchEvent(new CustomEvent('exo-dc-invite', { detail: code }))
  }
  const count = (n: number | null, what: string) => n === null ? null : <span className={`exo-dc-invite-count is-${what.toLowerCase()}`}>{n.toLocaleString()} {what}</span>
  return (
    <div className="exo-dc-jam">
      <p className="exo-dc-jam-label">{mine ? 'You sent an invite to join a server' : "You've been invited to join a server"}</p>
      <div className="exo-dc-jam-row">
        {invite?.icon ? <img className="exo-dc-invite-icon" src={invite.icon} alt="" draggable={false} />
          : <span className="exo-dc-invite-icon" aria-hidden="true">{invite?.name ? initials(invite.name) : null}</span>}
        <span className="exo-dc-jam-what">
          <strong className="exo-dc-invite-name">
            <span>{invite ? invite.name : 'Resolving Invite'}</span>
            {invite?.verified || invite?.partnered ? <span className="exo-dc-invite-badge" aria-label={invite.verified ? 'Verified' : 'Partnered'}><Icons.Verified size={16} filled /></span> : null}
          </strong>
          {invite ? <span className="exo-dc-invite-counts">{count(invite.online, 'Online')}{count(invite.members, 'Members')}</span> : null}
        </span>
        {invite ? <button type="button" className={`button ${invite.joined ? 'button--secondary' : 'button--primary'}`} onClick={join}>{invite.joined ? 'Joined' : 'Join'}</button> : null}
      </div>
    </div>
  )
}

function initials(name: string) {
  return name.split(/\s+/).map(word => [...word][0] ?? '').join('').slice(0, 3)
}

// A forwarded message, Discord's way: "Forwarded", the message itself under a bar, and where it came from.
function Forwarded({ message, ctx }: { message: DiscordMessage; ctx: MarkdownContext }) {
  const shot = message.message_snapshots![0].message
  const from = message.message_reference
  const origin = from?.channel_id ? ctx.channelName(from.channel_id) : null
  const go = () => {
    if (from?.channel_id && from.message_id) jumpToMessage({ guildId: from.guild_id ?? null, channelId: from.channel_id, messageId: from.message_id })
  }
  return (
    <div className="exo-dc-forward">
      <p className="exo-dc-forward-label"><Icons.Forward size={14} /> Forwarded</p>
      <MessageContent message={{ ...shot, attachments: shot.attachments ?? [], embeds: shot.embeds ?? [] }} ctx={ctx} />
      <p className="exo-dc-forward-from">
        {origin && from?.guild_id === ctx.guildId ? <button type="button" className="exo-dc-link" onClick={go}>#{origin}</button> : null}
        <time>{time(shot.timestamp, true)}</time>
      </p>
    </div>
  )
}

// An app's answer still on its way (Discord's deferred response): "is thinking…" with the typing dots.
function Thinking({ name }: { name: string }) {
  return <p className="exo-dc-thinking" role="status"><span className="exo-dc-typing-dots" aria-hidden="true"><i /><i /><i /></span>{name} is thinking…</p>
}

// Under an ephemeral answer: only you see it, and Dismiss takes it away (it was never saved to the channel).
function OnlyYou({ message }: { message: DiscordMessage }) {
  return (
    <p className="exo-dc-only-you">
      <Icons.Spoiler size={14} />Only you can see this •{' '}
      <button type="button" className="exo-dc-link" onClick={() => void host.discordApps(message.channel_id, 'dismiss', { messageId: message.id }).catch(() => { })}>Dismiss message</button>
    </p>
  )
}

// The thread started from a message, under it: its name and replies; a click opens it.
function ThreadBox({ thread, onOpen }: { thread: NonNullable<DiscordMessage['thread']>; onOpen?: (thread: { id: string; name: string }) => void }) {
  return (
    <button type="button" className="exo-dc-thread-box" disabled={!onOpen} onClick={() => onOpen?.(thread)}>
      <Icons.Threads size={16} />
      <strong>{thread.name}</strong>
      <span>{thread.message_count ?? 0} {thread.message_count === 1 ? 'Message' : 'Messages'}</span>
    </button>
  )
}

// Over an app's answer: who used which command, as Discord shows it ("used /play").
function CommandLine({ command }: { command: NonNullable<DiscordMessage['interaction_metadata']> }) {
  const who = command.user
  return (
    <p className="exo-dc-msg-reply is-command">
      {who ? <img src={avatarUrl(who, 32)} alt="" draggable={false} /> : null}
      {who ? <strong>{who.global_name || who.username}</strong> : null}
      <span>{command.name ? <>used <span className="exo-dc-command">/{command.name}</span></> : 'used a command'}</span>
    </p>
  )
}

/**
 * A still or APNG sticker. Discord settings > Accessibility > Play animated stickers: Always (unless
 * Performance mode or reduced motion), on interaction (under the pointer), or never; and never while Exo is in the background.
 */
function StickerImage({ id, name, format }: { id: string; name: string; format: number }) {
  const [hover, setHover] = useState(false)
  const mode = accountSettings().animateStickers
  const active = usePageActive()
  const plays = format === 2 && active && (hover ? mode !== 2 : mode === 0 && !performanceMode() && !shouldReduceMotion())
  return (
    <img className="exo-dc-sticker" alt={name} draggable={false} onPointerEnter={() => setHover(true)} onPointerLeave={() => setHover(false)}
      src={`https://media.discordapp.net/stickers/${id}.${format === 4 ? 'gif' : 'png'}?size=320${format === 2 && !plays ? '&passthrough=false' : ''}`} />
  )
}

// Your files on their way: each name and size, and one bar for how much has gone up.
// Discord keeps a message it didn't take where it was, in red, with Retry and Delete.
function Failed({ message, actions }: { message: DiscordMessage; actions: MessageActions }) {
  return (
    <p className="exo-dc-failed" role="alert">
      {message.failed}
      {actions.onRetry ? <button type="button" className="exo-dc-link" onClick={() => actions.onRetry?.(message)}>Retry</button> : null}
      {actions.onDiscard ? <button type="button" className="exo-dc-link is-bad" onClick={() => actions.onDiscard?.(message)}>Delete</button> : null}
    </p>
  )
}

function Uploading({ upload }: { upload: NonNullable<DiscordMessage['uploading']> }) {
  return (
    <div className="exo-dc-uploading" role="status" aria-label={`Uploading, ${Math.round(upload.progress * 100)} percent`}>
      {upload.files.map((file, i) => (
        <span key={`${file.name}-${i}`} className="exo-dc-file-chip">{file.name}<span className="exo-dc-file-size">{size(file.size)}</span></span>
      ))}
      <span className="exo-dc-upload-bar" aria-hidden="true"><i style={{ '--fill': upload.progress } as CSSProperties} /></span>
    </div>
  )
}

/** Discord's edit in place: Enter saves, Shift+Enter is a new line, Escape cancels; the composer's draft stays as it was. */
function InlineEdit({ message, onDone }: { message: DiscordMessage; onDone: () => void }) {
  const [text, setText] = useState(message.content)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const box = useRef<HTMLTextAreaElement>(null)
  useEffect(() => {
    const el = box.current
    if (!el) return
    el.focus()
    el.setSelectionRange(el.value.length, el.value.length)
  }, [])
  useEffect(() => {
    const el = box.current
    if (!el) return
    el.style.height = 'auto'
    el.style.height = `${Math.min(el.scrollHeight, 320)}px`
  }, [text])
  async function save() {
    const content = text.trim()
    if (busy) return
    if (!content || content === message.content.trim()) { onDone(); return }
    setBusy(true)
    setError(null)
    const result = await host.discordEdit(message.channel_id, message.id, content).catch(() => ({ ok: false, message: null }))
    setBusy(false)
    if (result.ok) onDone()
    else setError(result.message ?? 'Discord did not take the edit.')
  }
  return (
    <div className="exo-dc-inline-edit">
      <textarea ref={box} className="exo-dc-input" rows={1} value={text} maxLength={4000} aria-label="Edit message" disabled={busy}
        onChange={event => setText(event.target.value)}
        onKeyDown={event => {
          if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); onDone() }
          else if (event.key === 'Enter' && !event.shiftKey && !event.nativeEvent.isComposing) { event.preventDefault(); void save() }
        }} />
      <p className="exo-dc-inline-edit-foot" role={error ? 'alert' : undefined}>
        {error ?? <>escape to <button type="button" onClick={onDone}>cancel</button> • enter to <button type="button" onClick={() => void save()}>save</button></>}
      </p>
    </div>
  )
}

// Add Reaction, and with Nitro Add Super Reaction beside it, as in Discord's hover bar.
function ReactButton({ message, ctx, actions, burst = false }: { message: DiscordMessage; ctx: MarkdownContext; actions: MessageActions; burst?: boolean }) {
  const [open, setOpen] = useState(false)
  return (
    <>
      <button type="button" aria-label={burst ? 'Add Super Reaction' : 'Add Reaction'} onClick={() => setOpen(value => !value)}>{burst ? <Icons.Super size={18} /> : <Icons.React size={18} />}</button>
      {open ? (
        <EmojiPicker guildId={ctx.guildId} reacting onClose={() => setOpen(false)}
          onPick={emoji => { setOpen(false); actions.onReact(message, emoji.reaction, true, burst) }} />
      ) : null}
    </>
  )
}

/** Discord's hover bar: react, reply, edit, and a More menu with copy, pin and delete. */
function ActionsBar({ message, ctx, mine, canSend, actions, onViewReactions }: {
  message: DiscordMessage
  ctx: MarkdownContext
  mine: boolean
  canSend: boolean
  actions: MessageActions
  onViewReactions: () => void
}) {
  const [more, setMore] = useState(false)
  // Add Reaction from the menu opens the reaction picker on the bar, as Discord's menu does.
  const [reacting, setReacting] = useState(false)
  // Where a right-click opened the menu (null: under the More button).
  const [at, setAt] = useState<{ left: number; top: number } | null>(null)
  const nitro = useNitro(ctx.guildId)
  const bar = useRef<HTMLDivElement>(null)
  // One message's menu at a time: a press anywhere else, or Escape, closes it (the press on another More opens that one).
  useDismiss(bar, () => setMore(false), { on: more })
  useEffect(() => {
    if (!more) return
    const escape = (event: KeyboardEvent) => { if (event.key === 'Escape') { event.stopPropagation(); setMore(false) } }
    document.addEventListener('keydown', escape, true)
    return () => document.removeEventListener('keydown', escape, true)
  }, [more])
  const editable = mine && (message.type === 0 || message.type === 19)
  return (
    <div className="exo-dc-msg-actions" ref={bar}>
      <ReactButton message={message} ctx={ctx} actions={actions} />
      {nitro ? <ReactButton message={message} ctx={ctx} actions={actions} burst /> : null}
      {canSend ? <button type="button" aria-label="Reply" onClick={() => actions.onReply(message)}><Icons.Reply size={18} /></button> : null}
      {editable ? <button type="button" aria-label="Edit" onClick={() => actions.onEdit(message)}><Icons.Edit size={18} /></button> : null}
      <button type="button" aria-label="Forward" onClick={() => actions.onForward(message)}><Icons.Forward size={18} /></button>
      <button type="button" aria-label="More" aria-expanded={more} onClick={event => { const point = takePoint(event.currentTarget); setAt(point); setMore(value => point ? true : !value) }}><Icons.More size={18} /></button>
      {reacting ? (
        <EmojiPicker guildId={ctx.guildId} reacting onClose={() => setReacting(false)}
          onPick={emoji => { setReacting(false); actions.onReact(message, emoji.reaction, true, false) }} />
      ) : null}
      {more ? <MessageMenu message={message} ctx={ctx} mine={mine} canSend={canSend} actions={actions} at={at} editable={editable}
        onClose={() => setMore(false)} onReact={() => { setMore(false); setReacting(true) }} onViewReactions={onViewReactions} /> : null}
    </div>
  )
}

// The message's More menu (and right-click), in Discord's order: Add Reaction, Edit, Reply, Forward, Apps, Create Thread,
// View Reactions; Copy Text, Pin, Mark Unread, Copy Message Link, Delete; Copy Message ID in Developer Mode.
function MessageMenu({ message, ctx, mine, canSend, actions, at, editable, onClose, onReact, onViewReactions }: {
  message: DiscordMessage
  ctx: MarkdownContext
  mine: boolean
  canSend: boolean
  actions: MessageActions
  at: { left: number; top: number } | null
  editable: boolean
  onClose: () => void
  onReact: () => void
  onViewReactions: () => void
}) {
  // Developer Mode (Discord settings > Developer) adds Copy Message ID.
  const developer = useAccountSettings().developerMode
  const link = `https://discord.com/channels/${ctx.guildId ?? '@me'}/${message.channel_id}/${message.id}`
  const copy = (text: string) => { void navigator.clipboard.writeText(text).catch(() => { }); onClose() }
  const run = (action: (message: DiscordMessage) => void) => () => { onClose(); action(message) }
  return (
    <div role="menu" {...menuPlace(at)}>
      <button type="button" role="menuitem" onClick={onReact}><Icons.React size={16} />Add Reaction</button>
      {editable ? <button type="button" role="menuitem" onClick={run(actions.onEdit)}><Icons.Edit size={16} />Edit Message</button> : null}
      {canSend ? <button type="button" role="menuitem" onClick={run(actions.onReply)}><Icons.Reply size={16} />Reply</button> : null}
      <button type="button" role="menuitem" onClick={run(actions.onForward)}><Icons.Forward size={16} />Forward</button>
      <MessageApps channelId={message.channel_id} messageId={message.id} onRun={onClose} />
      {actions.onThread && !message.thread ? <button type="button" role="menuitem" onClick={run(actions.onThread)}><Icons.Threads size={16} />Create Thread</button> : null}
      {message.reactions?.length ? <button type="button" role="menuitem" onClick={() => { onClose(); onViewReactions() }}><Icons.ViewReactions size={16} />View Reactions</button> : null}
      <span className="exo-dc-menu-line" role="separator" />
      {message.content ? <button type="button" role="menuitem" onClick={() => copy(message.content)}><Icons.Copy size={16} />Copy Text</button> : null}
      {actions.canPin !== false ? <button type="button" role="menuitem" onClick={run(actions.onPin)}><Icons.Pin size={16} />{message.pinned ? 'Unpin Message' : 'Pin Message'}</button> : null}
      <button type="button" role="menuitem" onClick={run(actions.onMarkUnread)}><Icons.Unread size={16} />Mark Unread</button>
      <button type="button" role="menuitem" onClick={() => copy(link)}><Icons.Link size={16} />Copy Message Link</button>
      {mine || actions.canManageMessages ? <button type="button" role="menuitem" className="is-danger" onClick={run(actions.onDelete)}><Icons.Delete size={16} />Delete Message</button> : null}
      {developer ? <button type="button" role="menuitem" onClick={() => copy(message.id)}><Icons.CopyId size={16} />Copy Message ID</button> : null}
    </div>
  )
}

// The point a right-click left on the More button for its menu (read once), or null for a plain click.
function takePoint(button: HTMLButtonElement) {
  const point = button.dataset.at?.split(',').map(Number)
  delete button.dataset.at
  return point ? { left: point[0], top: point[1] } : null
}

// The message menu under its More button, or at the right-click's point.
function menuPlace(at: { left: number; top: number } | null) {
  return at ? { className: 'exo-dc-menu is-message is-at', style: at } : { className: 'exo-dc-menu is-message' }
}

function mentions(message: DiscordMessage, meId: string | null) {
  return meId !== null && (message.mention_everyone === true || message.mentions.some(user => user.id === meId))
}

// A right-click on a message opens its More menu, as in Discord (not on selected text, a name, or a message on its way).
function openMore(event: React.MouseEvent<HTMLDivElement>, message: DiscordMessage) {
  if (message.pending || window.getSelection()?.toString() || (event.target as Element).closest('[data-dc-user]')) return
  event.preventDefault()
  // The menu opens at the pointer, as Discord's does; the More button opens it under itself.
  const more = event.currentTarget.querySelector<HTMLButtonElement>('[aria-label="More"]')
  if (!more) return
  more.dataset.at = `${event.clientX},${event.clientY}`
  more.click()
}

// A message that is only links, each with a preview that shows (or an invite, which shows its card), shows those alone.
function linksOnly(message: Pick<DiscordMessage, 'content' | 'embeds' | 'flags'>) {
  const links = message.content.trim().split(/\s+/)
  if (!links.every(link => /^https?:\/\/\S+$/.test(link))) return false
  const previewed = links.filter(link => !inviteCode(link))
  if (!previewed.length) return true
  if ((message.flags ?? 0) & 4) return false
  const { inlineEmbedMedia, renderEmbeds } = accountSettings()
  const shown = message.embeds.filter(e => e.type === 'image' || e.type === 'gifv' ? inlineEmbedMedia : renderEmbeds)
  return shown.length >= previewed.length
}

// The quote over a reply, Discord's way; clicking it jumps to the original.
function ReplyQuote({ reply, ctx }: { reply: DiscordMessage; ctx: MarkdownContext }) {
  // A message of links alone shows as its previews, so its quote reads like an attachment's.
  const words = reply.content && !linksOnly(reply) ? reply.content : ''
  const text = words || (reply.attachments.length || reply.embeds.length ? 'Click to see attachment' : 'Original message was deleted')
  return (
    <p className="exo-dc-msg-reply" role="button" tabIndex={0} aria-label="Jump to the message replied to"
      onClick={() => window.dispatchEvent(new CustomEvent('exo-dc-jump', { detail: reply.id }))}>
      <img src={avatarUrl(reply.author, 32)} alt="" draggable={false} />
      <strong>{displayName(reply)}</strong>
      <span className={words ? undefined : 'is-italic'}>{words ? <MarkdownLine text={words} ctx={ctx} /> : text}</span>
    </p>
  )
}

// "X started a call that lasted a few seconds." / "You missed a call from X that lasted 5 minutes.", Discord's way.
function CallLine({ message, name, meId }: { message: DiscordMessage; name: string; meId: string | null }) {
  const ended = message.call?.ended_timestamp
  const missed = !!ended && meId !== null && message.author.id !== meId && !message.call?.participants.includes(meId)
  const lasted = ended ? ` that lasted ${callLength(Date.parse(ended) - Date.parse(message.timestamp))}` : ''
  return (
    <div className="exo-dc-msg is-system" data-msg-id={message.id}>
      <span className={`exo-dc-msg-gutter exo-dc-call-icon${missed ? ' is-missed' : ''}`} aria-hidden="true"><Icons.Call size={16} /></span>
      <p>{missed ? <>You missed a call from <strong>{name}</strong>{lasted}.</> : <><strong>{name}</strong> started a call{lasted}.</>} <time className="exo-dc-msg-time">{time(message.timestamp, true)}</time></p>
    </div>
  )
}

// Discord's line where a DM begins with an accepted friend request (type 67), with the note sent along if any.
function FriendAcceptedLine({ message, name, other, mine }: { message: DiscordMessage; name: string; other: string | null; mine: boolean }) {
  const them = <strong>{mine ? other ?? 'their' : name}</strong>
  const note = message.content ? <>: <strong>{message.content}</strong></> : null
  return (
    <div className="exo-dc-msg is-system" data-msg-id={message.id}>
      <span className="exo-dc-msg-gutter exo-dc-friend-accepted" aria-hidden="true"><Icons.FriendAccepted size={16} /></span>
      <p>{mine ? <>You accepted {them}{other ? "'s" : ''} friend request</> : <>{them} accepted your friend request</>}{note}. <time className="exo-dc-msg-time">{time(message.timestamp, true)}</time></p>
    </div>
  )
}

// moment's humanize, which Discord uses: "a few seconds", "a minute", "5 minutes", "an hour", "2 hours".
function callLength(ms: number) {
  const s = Math.max(0, ms / 1000)
  if (s < 45) return 'a few seconds'
  if (s < 90) return 'a minute'
  if (s < 45 * 60) return `${Math.round(s / 60)} minutes`
  if (s < 90 * 60) return 'an hour'
  return `${Math.round(s / 3600)} hours`
}
