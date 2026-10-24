import { memo, useEffect, useRef, useState, type CSSProperties } from 'react'
import { host } from '../../lib/host'
import { usePageActive } from '../../lib/pageActivity'
import { shouldReduceMotion } from '../../lib/motionPreference'
import { Poll, PollResultLine } from './Poll'
import { Attachments, fit, MovingImage, size, sized } from './Attachments'
import { LottieSticker } from './LottieSticker'
import { performanceMode, avatarUrl, displayName, jumpToMessage, openDiscord, type DiscordEmbed, type DiscordInvite, type DiscordMessage } from '../../lib/discord'
import { Reactions, ReactionsDialog, useNitro } from './Reactions'
import { EmojiPicker, noteReaction, QuickEmoji, useQuickReactions, type QuickReaction } from './EmojiPicker'
import { Confirm } from './admin'
import { Icons } from './icons'
import { RoleIcon } from './presence'
import { SubContext, Submenu } from './UserMenu'
import { reminderTimes, saveMessage, unsaveMessage, useSaved } from '../../lib/discordSaved'
import { usePageTheme } from '../../lib/appearance'
import { Play } from '../../brand/icons'
import { showImage, showProfile } from './extras'
import { inviteCode, Link, Markdown, MarkdownLine, type MarkdownContext } from './markdown'
import { accountSettings, useAccountSettings } from '../../lib/discordPrefs'
import { EPHEMERAL, LOADING, SILENT } from '../../lib/discordApps'
import { MessageComponents } from './Components'
import { MessageApps } from './AppCommands'
import { CompanyLogo } from '../../brand/CompanyLogo'
import { openLink } from '../../lib/music'
import { Decoration, lookOf, StyledName } from './collectibles'
import { useDismiss } from '../../lib/dismiss'
import { GuildTag } from './guildTag'

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
  const author = embed.author
  const icon = author?.proxy_icon_url ?? author?.icon_url
  return (
    <>
      {embed.provider?.name ? <p className="exo-dc-embed-small">{embed.provider.name}</p> : null}
      {author?.name ? (
        <p className="exo-dc-embed-author">
          {icon ? <img className="exo-dc-embed-icon" src={sized(icon, 24, 24, { width: 24, height: 24 })} alt="" draggable={false} /> : null}
          {author.url ? <Link url={author.url}>{author.name}</Link> : author.name}
        </p>
      ) : null}
      {embed.title ? (
        <p className="exo-dc-embed-title">{embed.url ? <Link url={embed.url}>{embed.title}</Link> : embed.title}</p>
      ) : null}
    </>
  )
}

// The players Discord plays inside link previews (YouTube, Twitch, Vimeo, TikTok, SoundCloud), started with Discord's
// own parameters: YouTube and Twitch play at once, and Twitch's player is told the page it's in (it plays nowhere else).
const PLAYERS = new Set(['www.youtube.com', 'www.youtube-nocookie.com', 'player.twitch.tv', 'clips.twitch.tv', 'player.vimeo.com', 'www.tiktok.com', 'w.soundcloud.com'])
function playerUrl(raw: string | undefined) {
  let url: URL
  try { url = new URL(raw ?? '') } catch { return null }
  if (url.protocol !== 'https:' || !PLAYERS.has(url.hostname)) return null
  if (url.hostname.includes('youtube')) url.searchParams.set('autoplay', '1')
  if (url.hostname.endsWith('twitch.tv')) { url.searchParams.delete('parent'); url.searchParams.set('parent', location.hostname); url.searchParams.set('autoplay', 'true') }
  return url.toString()
}

// Sandboxed as Discord's: no way out of the frame but a link the person opens. The page sends no referrer, but players
// need to know the site they're on (YouTube refuses to play without it): just Exo's origin, nothing more.
const PLAYER_SANDBOX = 'allow-forms allow-modals allow-popups allow-popups-to-escape-sandbox allow-same-origin allow-scripts'
const PLAYER_REFERRER = 'strict-origin-when-cross-origin'

// A video link's preview: its picture with Play, then the site's own player in its place, as Discord plays it.
function EmbedPlayer({ image, video, player }: { image: NonNullable<DiscordEmbed['image']>; video: NonNullable<DiscordEmbed['video']>; player: string }) {
  const [playing, setPlaying] = useState(false)
  const box = fit(video.width ?? image.width, video.height ?? image.height, 400, 300)
  if (playing) return <iframe className="exo-dc-embed-player" src={player} {...box} sandbox={PLAYER_SANDBOX} referrerPolicy={PLAYER_REFERRER} allow="autoplay; fullscreen; encrypted-media; picture-in-picture" allowFullScreen title="Video" />
  return (
    <button type="button" className="exo-dc-embed-play" aria-label="Play" onClick={() => setPlaying(true)}>
      <img className="exo-dc-embed-image is-video" src={sized(image.proxy_url ?? image.url!, image.width, image.height, { width: 400, height: 300 })} alt="" loading="lazy" draggable={false} data-dc-link={image.url} {...box} />
      <span className="exo-dc-embed-play-mark" aria-hidden="true"><Play size={22} /></span>
    </button>
  )
}

// Spotify's and Apple Music's own players for their links, as Discord shows them: a track, an episode or show, an
// album, artist or playlist, each at its player's height (Apple Music's in the page's light or dark).
function musicPlayer(link: string | undefined, theme: string): { src: string; height: number } | null {
  let url: URL
  try { url = new URL(link ?? '') } catch { return null }
  if (url.hostname === 'open.spotify.com') {
    const found = /^\/(?:intl-[\w-]+\/)?(track|album|playlist|episode|show|artist)\/(\w+)/.exec(url.pathname)
    if (!found) return null
    const heights: Record<string, number> = { track: 80, episode: 232, show: 232 }
    return { src: `https://open.spotify.com/embed/${found[1]}/${found[2]}`, height: heights[found[1]] ?? 352 }
  }
  if (url.hostname === 'music.apple.com') {
    const parts = url.pathname.slice(1).split('/')
    const kind = (/^[a-z]{2}(-[a-z]{2})?$/.test(parts[0] ?? '') ? parts[1] : parts[0])?.toLowerCase()
    const song = url.searchParams.get('i')
    const height = song || kind === 'song' ? 175 : kind === 'music-video' ? 371 : 450
    return { src: `https://embed.music.apple.com/${url.pathname.slice(1)}?theme=${theme === 'light' ? 'light' : 'dark'}&i=${encodeURIComponent(song ?? '')}`, height }
  }
  return null
}

function MusicPlayer({ player }: { player: { src: string; height: number } }) {
  return <iframe className="exo-dc-embed-music" src={player.src} style={{ height: player.height }} sandbox={PLAYER_SANDBOX} referrerPolicy={PLAYER_REFERRER} allow="encrypted-media; fullscreen; clipboard-write" title="Player" />
}

// An embed's picture at most 400 by 300; a video's (YouTube's) in the video's own shape with its thumbnail's bars cropped, as Discord shows it.
function EmbedImage({ image, video }: { image: NonNullable<DiscordEmbed['image']>; video?: DiscordEmbed['video'] }) {
  // A site's player (no file of Discord's own to play): Play swaps the picture for it.
  const player = video && !video.proxy_url ? playerUrl(video.url) : null
  if (video && player) return <EmbedPlayer image={image} video={video} player={player} />
  const shape = video?.width && video.height ? video : image
  const src = image.proxy_url ?? image.url!
  return (
    <img className={`exo-dc-embed-image${shape === video ? ' is-video' : ''}`} src={sized(src, image.width, image.height, { width: 400, height: 300 })} alt="" loading="lazy"
      draggable={false} data-dc-link={image.url} {...fit(shape.width, shape.height, 400, 300)} onClick={() => showImage(src, image.url!)} />
  )
}

// Embeds that share a link (a post's several pictures) as one preview with each picture, as Discord merges them.
function mergeEmbeds(embeds: DiscordEmbed[]) {
  const out: DiscordEmbed[] = []
  const at = new Map<string, number>()
  for (const embed of embeds) {
    const first = embed.url ? at.get(embed.url) : undefined
    if (first === undefined) {
      if (embed.url) at.set(embed.url, out.length)
      out.push(embed)
    } else if (embed.image) {
      const into = out[first]
      out[first] = { ...into, images: [...(into.images ?? (into.image ? [into.image] : [])), embed.image] }
    }
  }
  return out
}

// Up to four of a preview's pictures in Discord's grid: two side by side, the first beside two, or two by two.
function EmbedGallery({ images }: { images: NonNullable<DiscordEmbed['images']> }) {
  const shown = images.filter(i => i.url).slice(0, 4)
  const items = shown.map(i => ({ src: i.proxy_url ?? i.url!, original: i.url! }))
  return (
    <div className={`exo-dc-embed-gallery is-${shown.length}`}>
      {shown.map((image, index) => (
        <img key={index} src={sized(items[index].src, image.width, image.height, { width: 400, height: 300 })} alt="" loading="lazy" draggable={false}
          data-dc-link={image.url} onClick={() => showImage(items[index].src, items[index].original, false, { items, index })} />
      ))}
    </div>
  )
}

function EmbedCard({ embed, ctx }: { embed: DiscordEmbed; ctx: MarkdownContext }) {
  const theme = usePageTheme()
  const music = musicPlayer(embed.url, theme)
  if (music) return <MusicPlayer player={music} />
  const image = embed.image ?? (embed.type === 'video' ? embed.thumbnail : undefined)
  const thumb = embed.type === 'video' ? undefined : embed.thumbnail
  const color = embed.color === undefined ? undefined : `#${embed.color.toString(16).padStart(6, '0')}`
  return (
    <div className="exo-dc-embed" style={color ? { borderLeftColor: color } : undefined}>
      <div className="exo-dc-embed-body">
        <EmbedHead embed={embed} />
        {embed.description ? <Markdown text={embed.description} ctx={ctx} /> : null}
        {embed.fields?.length ? <EmbedFields fields={embed.fields} ctx={ctx} /> : null}
        {(embed.images?.length ?? 0) > 1 ? <EmbedGallery images={embed.images!} />
          : image?.url ? <EmbedImage image={image} video={embed.type === 'video' ? embed.video : undefined} /> : null}
        <EmbedFoot embed={embed} />
      </div>
      {thumb?.url ? <img className="exo-dc-embed-thumb" src={sized(thumb.proxy_url ?? thumb.url, thumb.width, thumb.height, { width: 80, height: 80 })} alt="" loading="lazy" draggable={false} data-dc-link={thumb.url} /> : null}
    </div>
  )
}

// The footer: its icon and words, then when the embed is from, as Discord joins them with a dot.
function EmbedFoot({ embed }: { embed: DiscordEmbed }) {
  const icon = embed.footer?.proxy_icon_url ?? embed.footer?.icon_url
  const when = embed.timestamp ? time(embed.timestamp, true) : null
  if (!embed.footer?.text && !when) return null
  return (
    <p className="exo-dc-embed-small exo-dc-embed-foot">
      {icon ? <img className="exo-dc-embed-icon is-foot" src={sized(icon, 20, 20, { width: 20, height: 20 })} alt="" draggable={false} /> : null}
      <span>{[embed.footer?.text, when].filter(Boolean).join(' • ')}</span>
    </p>
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
  /** An announcement channel: its messages can be published to the servers following it. */
  announcement?: boolean
  /** A message action Discord didn't take: its reason, under the message box. */
  onError?: (message: string) => void
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
              <img src={ctx.avatars?.get(message.author.id) ?? avatarUrl(message.author)} alt="" draggable={false} loading="lazy" />
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
          {(message.flags ?? 0) & SILENT ? <span className="exo-dc-msg-silent" role="img" aria-label="This is a @silent message."><Icons.BellOff size={14} /></span> : null}
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
  const look = lookOf(message.author, message.member)
  return (
    <>
    <strong className="is-clickable exo-dc-name" style={color ? { color, '--exo-dc-role': color } as React.CSSProperties : undefined}
      data-dc-user={message.author.id} data-dc-name={name} data-dc-username={message.author.username}
      onClick={event => showProfile(message.author.id, ctx.guildId, event.clientX + 12, event.clientY, message.author)}>
      <StyledName name={name} look={look} scope=".exo-dc-msg" />
    </strong>
    <RoleIcon icon={ctx.roleIcons?.get(message.author.id)} />
    <GuildTag look={look} />
    </>
  )
}

// The edited mark sits at the end of the last line, as in Discord.
function MessageContent({ message, ctx }: { message: Pick<DiscordMessage, 'content' | 'edited_timestamp' | 'attachments' | 'poll' | 'sticker_items' | 'embeds' | 'flags'> & Partial<DiscordMessage>; ctx: MarkdownContext }) {
  const edited = message.edited_timestamp ? <span className="exo-dc-edited">(edited)</span> : null
  const jam = useJam(message.content)
  // A jam link alone shows as its card, as a link with a preview shows as the preview.
  const text = message.content && !linksOnly(message) && message.content.trim() !== jam
  // SUPPRESS_EMBEDS (4): the sender removed the link previews.
  const embeds = (message.flags ?? 0) & 4 ? [] : mergeEmbeds(message.embeds)
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
        <button type="button" className="exo-set-btn is-primary" onClick={() => openLink(url)}>Join</button>
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
          <span className="exo-dc-jam-what"><strong>Invalid invite</strong><span>{mine ? 'Try sending a new invite' : 'Ask for a new invite'}</span></span>
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
            <span>{invite ? invite.name : 'Resolving invite'}</span>
            {invite?.verified || invite?.partnered ? <span className="exo-dc-invite-badge" aria-label={invite.verified ? 'Verified' : 'Partnered'}><Icons.Verified size={16} filled /></span> : null}
          </strong>
          {invite ? <span className="exo-dc-invite-counts">{count(invite.online, 'Online')}{count(invite.members, 'Members')}</span> : null}
        </span>
        {invite ? <button type="button" className={`exo-set-btn${invite.joined ? '' : ' is-primary'}`} onClick={join}>{invite.joined ? 'Joined' : 'Join'}</button> : null}
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
      <button type="button" aria-label={burst ? 'Add super reaction' : 'Add reaction'} onClick={() => setOpen(value => !value)}>{burst ? <Icons.Super size={18} /> : <Icons.React size={18} />}</button>
      {open ? (
        <EmojiPicker guildId={ctx.guildId} reacting onClose={() => setOpen(false)}
          onPick={emoji => { setOpen(false); actions.onReact(message, emoji.reaction, true, burst) }} />
      ) : null}
    </>
  )
}

// Whether you reacted to this message with a quick reaction's emoji already (a press then takes it back).
const reacted = (message: DiscordMessage, quick: QuickReaction) => !!message.reactions?.some(r => r.me
  && (quick.custom ? r.emoji.id === quick.custom.id : !r.emoji.id && r.emoji.name === quick.emoji))

// One quick reaction: a press adds it, or takes yours back, as Discord's hover bar and menu do.
function QuickButton({ quick, message, actions, menu, onDone }: { quick: QuickReaction; message: DiscordMessage; actions: MessageActions; menu?: boolean; onDone?: () => void }) {
  const mine = reacted(message, quick)
  return (
    <button type="button" role={menu ? 'menuitem' : undefined} className="exo-dc-quick-react" aria-label={`${mine ? 'Click to remove' : 'Click to react'} :${quick.name}:`}
      aria-pressed={mine} onClick={() => { if (!mine) noteReaction(quick.key); actions.onReact(message, quick.reaction, !mine, false); onDone?.() }}>
      <QuickEmoji quick={quick} />
    </button>
  )
}

// The hover bar's three reactions used most, before Add Reaction.
function QuickReactions({ message, ctx, actions }: { message: DiscordMessage; ctx: MarkdownContext; actions: MessageActions }) {
  const quick = useQuickReactions(ctx.guildId, false)
  if (!quick.length) return null
  return <>{quick.map(q => <QuickButton key={q.reaction} quick={q} message={message} actions={actions} />)}<span className="exo-dc-msg-actions-line" aria-hidden="true" /></>
}

// The menu's row of four, above Add Reaction.
function QuickRow({ message, ctx, actions, onClose }: { message: DiscordMessage; ctx: MarkdownContext; actions: MessageActions; onClose: () => void }) {
  const quick = useQuickReactions(ctx.guildId, true).slice(0, 4)
  if (!quick.length) return null
  return (
    <div className="exo-dc-quick-row" role="group" aria-label="Quick reactions">
      {quick.map(q => <QuickButton key={q.reaction} quick={q} message={message} actions={actions} menu onDone={onClose} />)}
    </div>
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
  // Remove Embeds or Remove All Reactions, asked first as Discord asks (Shift skips it).
  const [asking, setAsking] = useState<Ask | null>(null)
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
      <QuickReactions message={message} ctx={ctx} actions={actions} />
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
        onClose={() => setMore(false)} onReact={() => { setMore(false); setReacting(true) }} onViewReactions={onViewReactions}
        onAsk={(ask, now) => { setMore(false); if (now) void messageAction(message, mine, ask, actions); else setAsking(ask) }} /> : null}
      {asking ? <AskConfirm ask={asking} onClose={() => setAsking(null)} onConfirm={() => { setAsking(null); void messageAction(message, mine, asking, actions) }} /> : null}
    </div>
  )
}

// The message's More menu (and right-click), in Discord's order: Add Reaction, Edit, Reply, Forward, Apps, Create Thread,
// View Reactions; Copy Text, Pin, Mark Unread, Copy Message Link, Delete; Copy Message ID in Developer Mode.
type Ask = 'removeEmbeds' | 'clearReactions'

// Discord's Speak Message: "Name said …" (or "Name replied to Other …"), mentions, channels and emoji read as words,
// links as their site, in a voice for the app's language. Pressed again while it speaks, it stops.
let speaking: string | null = null
function speakMessage(message: DiscordMessage, ctx: MarkdownContext) {
  const synth = window.speechSynthesis
  if (!synth) return
  synth.cancel()
  if (speaking === message.id) { speaking = null; return }
  const nameOf = (id: string) => { const u = ctx.users.get(id); return u ? u.global_name ?? u.username : 'someone' }
  const body = message.content
    .replace(/https?:\/\/[^\s>]+/g, url => { try { return new URL(url).hostname.replace(/^www\./, '') } catch { return url } })
    .replace(/<@!?(\d+)>/g, (_, id: string) => `@${nameOf(id)}`)
    .replace(/<@&(\d+)>/g, (_, id: string) => `@${ctx.roles?.[id]?.name ?? 'unknown-role'}`)
    .replace(/<#(\d+)>/g, () => 'a channel')
    .replace(/<a?:(\w+):\d+>/g, (_, name: string) => `Emoji ${name}`)
    .replace(/<\/([^\s:>]+):\d+>/g, (_, name: string) => `/${name}`)
  const who = displayName(message)
  const replied = message.referenced_message ? displayName(message.referenced_message) : null
  const line = new SpeechSynthesisUtterance(replied ? `${who} replied to ${replied} ${body}` : `${who} said ${body}`)
  line.lang = document.documentElement.lang || navigator.language
  line.onend = () => { if (speaking === message.id) speaking = null }
  speaking = message.id
  synth.speak(line)
}

// Publish, Remove Embeds or Remove All Reactions; Discord's own line when it doesn't take (the menu is gone by then).
async function messageAction(message: DiscordMessage, mine: boolean, action: Ask | 'publish', actions: MessageActions) {
  const result = await host.discordMessageAction(message.channel_id, message.id, action, { mine, flags: message.flags ?? 0 }).catch(() => null)
  if (!result?.ok) actions.onError?.(result?.message ?? 'Discord did not take that.')
}

// Discord's confirmations for Remove Embeds and Remove All Reactions.
function AskConfirm({ ask, onClose, onConfirm }: { ask: Ask; onClose: () => void; onConfirm: () => void }) {
  return ask === 'removeEmbeds' ? (
    <Confirm title="Are you sure?" action="Remove all embeds" onClose={onClose} onConfirm={onConfirm}>
      <p>This will remove all embeds on this message for everyone.</p>
      <p className="exo-dc-note">Hold Shift when removing embeds to skip this.</p>
    </Confirm>
  ) : (
    <Confirm title="Remove all reactions" action="Remove all reactions" onClose={onClose} onConfirm={onConfirm}>
      <p>Are you sure you want to remove all reactions on this message?</p>
    </Confirm>
  )
}

function MessageMenu({ message, ctx, mine, canSend, actions, at, editable, onClose, onReact, onViewReactions, onAsk }: {
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
  /** Remove Embeds or Remove All Reactions; now: Shift held, no question. */
  onAsk: (ask: Ask, now: boolean) => void
}) {
  // Developer Mode (Discord settings > Developer) adds Copy Message ID.
  // Publish: an announcement channel's message by you (or a moderator's), not published yet (Discord's CROSSPOSTED, 1).
  const publishable = actions.announcement === true && (mine || actions.canManageMessages === true) && ((message.flags ?? 0) & 1) === 0
  const developer = useAccountSettings().developerMode
  const link = `https://discord.com/channels/${ctx.guildId ?? '@me'}/${message.channel_id}/${message.id}`
  const copy = (text: string) => { void navigator.clipboard.writeText(text).catch(() => { }); onClose() }
  const run = (action: (message: DiscordMessage) => void) => () => { onClose(); action(message) }
  return (
    <div role="menu" {...menuPlace(at)}>
      <QuickRow message={message} ctx={ctx} actions={actions} onClose={onClose} />
      <button type="button" role="menuitem" onClick={onReact}><Icons.React size={16} />Add reaction</button>
      {editable ? <button type="button" role="menuitem" onClick={run(actions.onEdit)}><Icons.Edit size={16} />Edit message</button> : null}
      {canSend ? <button type="button" role="menuitem" onClick={run(actions.onReply)}><Icons.Reply size={16} />Reply</button> : null}
      <button type="button" role="menuitem" onClick={run(actions.onForward)}><Icons.Forward size={16} />Forward</button>
      {publishable ? <button type="button" role="menuitem" onClick={() => { onClose(); void messageAction(message, mine, 'publish', actions) }}><Icons.Announcement size={16} />Publish</button> : null}
      <MessageApps channelId={message.channel_id} messageId={message.id} onRun={onClose} />
      {actions.onThread && !message.thread ? <button type="button" role="menuitem" onClick={run(actions.onThread)}><Icons.Threads size={16} />Create thread</button> : null}
      <ReactionItems message={message} actions={actions} onClose={onClose} onViewReactions={onViewReactions} onAsk={onAsk} />
      <SpeakItem message={message} ctx={ctx} onClose={onClose} />
      <SavedItems message={message} onClose={onClose} onError={actions.onError} />
      <span className="exo-dc-menu-line" role="separator" />
      {message.content ? <button type="button" role="menuitem" onClick={() => copy(message.content)}><Icons.Copy size={16} />Copy text</button> : null}
      {actions.canPin !== false ? <button type="button" role="menuitem" onClick={run(actions.onPin)}><Icons.Pin size={16} />{message.pinned ? 'Unpin message' : 'Pin message'}</button> : null}
      <button type="button" role="menuitem" onClick={run(actions.onMarkUnread)}><Icons.Unread size={16} />Mark unread</button>
      <button type="button" role="menuitem" onClick={() => copy(link)}><Icons.Link size={16} />Copy message link</button>
      <RemoveItems message={message} mine={mine} actions={actions} onDelete={run(actions.onDelete)} onAsk={onAsk} />
      {developer ? <button type="button" role="menuitem" onClick={() => copy(message.id)}><Icons.CopyId size={16} />Copy message ID</button> : null}
    </div>
  )
}

// Bookmark Message (or Remove Bookmark), and Remind Me with Discord's times; a reminder set shows when it's due, with Mark
// Complete and Edit Reminder.
function SavedItems({ message, onClose, onError }: { message: DiscordMessage; onClose: () => void; onError?: (why: string) => void }) {
  const saved = useSaved()
  const [sub, setSub] = useState<string | null>(null)
  if (message.pending || message.failed) return null
  const due = saved.reminders.get(message.id)
  const done = (work: Promise<string | null>) => { onClose(); void work.then(why => { if (why) onError?.(why) }) }
  const bookmarked = saved.bookmarks.has(message.id)
  return (
    <SubContext.Provider value={{ open: sub, show: setSub }}>
      <button type="button" role="menuitem" onClick={() => done(bookmarked ? unsaveMessage(message.channel_id, message.id) : saveMessage(message.channel_id, message.id))}>
        <Icons.Bookmark size={16} />{bookmarked ? 'Remove bookmark' : 'Bookmark message'}
      </button>
      {due ? <button type="button" role="menuitem" onClick={() => done(unsaveMessage(message.channel_id, message.id))}><Icons.Check size={16} />Mark complete</button> : null}
      <Submenu label={due ? `Edit reminder (${new Date(due).toLocaleString(undefined, { weekday: 'short', hour: 'numeric', minute: '2-digit' })})` : 'Remind me'}>
        {reminderTimes().map(time => <button key={time.label} type="button" role="menuitem" onClick={() => done(saveMessage(message.channel_id, message.id, time.at))}>{time.label}</button>)}
      </Submenu>
    </SubContext.Provider>
  )
}

// Speak Message (Stop speaking message while it speaks), for a message with words.
function SpeakItem({ message, ctx, onClose }: { message: DiscordMessage; ctx: MarkdownContext; onClose: () => void }) {
  if (!message.content || !('speechSynthesis' in window)) return null
  return (
    <button type="button" role="menuitem" onClick={() => { onClose(); speakMessage(message, ctx) }}>
      <Icons.Speak size={16} />{speaking === message.id ? 'Stop speaking message' : 'Speak message'}
    </button>
  )
}

// View Reactions, and for a moderator Remove All Reactions.
function ReactionItems({ message, actions, onClose, onViewReactions, onAsk }: { message: DiscordMessage; actions: MessageActions; onClose: () => void; onViewReactions: () => void; onAsk: (ask: Ask, now: boolean) => void }) {
  if (!message.reactions?.length) return null
  return (
    <>
      <button type="button" role="menuitem" onClick={() => { onClose(); onViewReactions() }}><Icons.ViewReactions size={16} />View reactions</button>
      {actions.canManageMessages ? <button type="button" role="menuitem" className="is-danger" onClick={event => onAsk('clearReactions', event.shiftKey)}>Remove all reactions</button> : null}
    </>
  )
}

// Your message's (or a moderator's) Remove Embeds while its embeds show (not SUPPRESS_EMBEDS, 4), and Delete.
function RemoveItems({ message, mine, actions, onDelete, onAsk }: { message: DiscordMessage; mine: boolean; actions: MessageActions; onDelete: () => void; onAsk: (ask: Ask, now: boolean) => void }) {
  if (!mine && !actions.canManageMessages) return null
  return (
    <>
      {message.embeds.length && ((message.flags ?? 0) & 4) === 0 ? <button type="button" role="menuitem" className="is-danger" onClick={event => onAsk('removeEmbeds', event.shiftKey)}>Remove embeds</button> : null}
      <button type="button" role="menuitem" className="is-danger" onClick={onDelete}><Icons.Delete size={16} />Delete message</button>
    </>
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
