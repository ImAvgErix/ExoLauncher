import { useState, type ReactNode } from 'react'
import { openLink } from '../../lib/music'
import { jumpToMessage, openDiscord, type DiscordUser } from '../../lib/discord'
import { accountSettings } from '../../lib/discordPrefs'
import { showProfile } from './extras'
import { emojiUrl } from './emojiUrl'

export { emojiUrl }

/**
 * Discord's message markdown, drawn as React nodes (never HTML): code blocks and
 * spans, bold, italic, underline, strike, spoilers, quotes, headings and -# small
 * text, links (opened in the browser, Spotify's in the Music room), user/role/channel mentions, custom emoji and
 * <t:…> timestamps. Anything it does not know stays plain text.
 */
export type MarkdownContext = {
  users: Map<string, DiscordUser>
  channelName: (id: string) => string | null
  meId: string | null
  guildId: string | null
  /** Name colors by user id (their top colored role), for authors and mentions. */
  colors: Map<string, string | null>
  /** The server's roles, for role mentions. */
  roles?: Record<string, { name: string; color: string | null }>
  /** The other person in a 1:1 DM, for lines like "You accepted X's friend request." */
  dmName?: string | null
  /** You may Manage Messages here (Show spoiler content: On servers I moderate). */
  moderator?: boolean
}

const INLINE = new RegExp([
  /(`+)([\s\S]*?[^`])\1(?!`)/.source,                // 1, 2 code
  /\|\|([\s\S]+?)\|\|/.source,                       // 3 spoiler
  /\*\*([\s\S]+?)\*\*(?!\*)/.source,                 // 4 bold
  /__([\s\S]+?)__(?!_)/.source,                      // 5 underline
  /~~([\s\S]+?)~~/.source,                           // 6 strike
  /\*(?!\s)([\s\S]*?[^\s*])\*(?!\*)/.source,         // 7 italic
  /\b_(?!\s)([\s\S]*?[^\s_])_\b/.source,             // 8 italic
  /<(a?):(\w{2,32}):(\d{5,20})>/.source,             // 9, 10, 11 custom emoji
  /<@!?(\d{5,20})>/.source,                          // 12 user
  /<@&(\d{5,20})>/.source,                           // 13 role
  /<#(\d{5,20})>/.source,                            // 14 channel
  /<t:(-?\d{1,13})(?::([tTdDfFR]))?>/.source,        // 15, 16 timestamp
  /\[([^\]\n]{1,256})\]\(<?(https?:\/\/[^)\s>]+)>?\)/.source, // 17, 18 masked link
  /<?(https?:\/\/[^\s<>]*[^\s<>.,:;"')\]!?])>?/.source, // 19 link
  /(@everyone|@here)/.source,                        // 20
  /\\([^A-Za-z0-9\s])/.source,                      // 21 an escaped character (\* \_ \. …) shows as itself
].join('|'), 'g')

/** A link in Discord: a Spotify page or Jam opens in the Music room (when Spotify is your service), anything else in the browser. */
export const open = openLink

/** Discord settings > Appearance > Show spoiler content: Always, or On servers I moderate where you may Manage Messages. */
export function spoilersShown(setting: string, moderator: boolean | undefined) {
  return setting === 'ALWAYS' || (setting === 'IF_MODERATOR' && moderator === true)
}

function Spoiler({ moderator, children }: { moderator?: boolean; children: ReactNode }) {
  const [clicked, setShown] = useState(false)
  const shown = clicked || spoilersShown(accountSettings().renderSpoilers, moderator)
  return (
    <span
      className={`exo-dc-spoiler${shown ? ' is-shown' : ''}`}
      role="button"
      tabIndex={0}
      onClick={() => setShown(true)}
      onKeyDown={event => { if (event.key === 'Enter' || event.key === ' ') setShown(true) }}
    >{children}</span>
  )
}

function Timestamp({ seconds, style }: { seconds: number; style: string }) {
  const date = new Date(seconds * 1000)
  return <time className="exo-dc-time-chip" dateTime={date.toISOString()}>{formatTimestamp(date, style)}</time>
}

export function formatTimestamp(date: Date, style: string): string {
  switch (style) {
    case 't': return date.toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' })
    case 'T': return date.toLocaleTimeString()
    case 'd': return date.toLocaleDateString()
    case 'D': return date.toLocaleDateString(undefined, { dateStyle: 'long' })
    case 'F': return date.toLocaleString(undefined, { dateStyle: 'full', timeStyle: 'short' })
    case 'R': return relative(date)
    default: return date.toLocaleString(undefined, { dateStyle: 'long', timeStyle: 'short' })
  }
}

function relative(date: Date): string {
  const seconds = (date.getTime() - Date.now()) / 1000
  const units: [Intl.RelativeTimeFormatUnit, number][] = [['year', 31536000], ['month', 2592000], ['day', 86400], ['hour', 3600], ['minute', 60]]
  const format = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' })
  for (const [unit, size] of units) if (Math.abs(seconds) >= size) return format.format(Math.round(seconds / size), unit)
  return format.format(Math.round(seconds), 'second')
}


function inline(text: string, ctx: MarkdownContext, jumbo: boolean, key = 'i'): ReactNode[] {
  const out: ReactNode[] = []
  let last = 0
  let n = 0
  INLINE.lastIndex = 0
  for (const m of text.matchAll(INLINE)) {
    const at = m.index
    if (at > last) out.push(text.slice(last, at))
    last = at + m[0].length
    const k = `${key}.${n++}`
    out.push(token(m, ctx, jumbo, k))
  }
  if (last < text.length) out.push(text.slice(last))
  return out
}

// eslint-disable-next-line complexity -- one branch per markdown token, in the regex's order
function token(m: RegExpMatchArray, ctx: MarkdownContext, jumbo: boolean, k: string): ReactNode {
  if (m[21] !== undefined) return m[21]
  if (m[2] !== undefined) return <code key={k} className="exo-dc-code">{m[2]}</code>
  if (m[3] !== undefined) return <Spoiler key={k} moderator={ctx.moderator}>{inline(m[3], ctx, false, k)}</Spoiler>
  if (m[4] !== undefined) return <strong key={k}>{inline(m[4], ctx, false, k)}</strong>
  if (m[5] !== undefined) return <u key={k}>{inline(m[5], ctx, false, k)}</u>
  if (m[6] !== undefined) return <s key={k}>{inline(m[6], ctx, false, k)}</s>
  if (m[7] !== undefined) return <em key={k}>{inline(m[7], ctx, false, k)}</em>
  if (m[8] !== undefined) return <em key={k}>{inline(m[8], ctx, false, k)}</em>
  if (m[11] !== undefined) {
    return <img key={k} className={`exo-dc-emoji${jumbo ? ' is-jumbo' : ''}`} src={emojiUrl(m[11], m[9] === 'a' && accountSettings().animateEmoji, jumbo ? 96 : 48)} alt={`:${m[10]}:`} draggable={false} />
  }
  if (m[12] !== undefined) return <UserMention key={k} id={m[12]} ctx={ctx} />
  if (m[13] !== undefined) return <RoleMention key={k} id={m[13]} ctx={ctx} />
  if (m[14] !== undefined) return <ChannelMention key={k} id={m[14]} ctx={ctx} />
  if (m[15] !== undefined) return <Timestamp key={k} seconds={Number(m[15])} style={m[16] ?? 'f'} />
  if (m[18] !== undefined) return <Link key={k} url={m[18]}>{inline(m[17], ctx, false, k)}</Link>
  if (m[19] !== undefined) return <Link key={k} url={m[19]}>{m[19]}</Link>
  if (m[20] !== undefined) return <span key={k} className="exo-dc-mention">{m[20]}</span>
  return m[0]
}

// @name, as Discord draws a mention; a click opens their profile card, as in Discord.
function UserMention({ id, ctx }: { id: string; ctx: MarkdownContext }) {
  const user = ctx.users.get(id)
  if (!user) return <span className="exo-dc-mention">@unknown-user</span>
  return (
    <span className={`exo-dc-mention is-clickable${id === ctx.meId ? ' is-me' : ''}`} role="button" tabIndex={0}
      data-dc-user={id} data-dc-name={user.global_name || user.username} data-dc-username={user.username}
      onClick={event => showProfile(id, ctx.guildId, event.clientX + 12, event.clientY, user)}
      onKeyDown={event => { if (event.key === 'Enter') { const box = event.currentTarget.getBoundingClientRect(); showProfile(id, ctx.guildId, box.right + 8, box.top, user) } }}>
      @{user.global_name || user.username}
    </span>
  )
}

// @role in the role's color, as Discord shows it; a role Exo doesn't know is Discord's @deleted-role.
function RoleMention({ id, ctx }: { id: string; ctx: MarkdownContext }) {
  const role = ctx.roles?.[id]
  return (
    <span className="exo-dc-mention is-role" style={role?.color ? { '--exo-dc-role': role.color } as React.CSSProperties : undefined}>
      @{role?.name ?? 'deleted-role'}
    </span>
  )
}

// #channel; a click opens it.
function ChannelMention({ id, ctx }: { id: string; ctx: MarkdownContext }) {
  const name = ctx.channelName(id)
  if (!name) return <span className="exo-dc-mention">#unknown</span>
  const go = () => openDiscord({ guildId: ctx.guildId, channelId: id, outside: false })
  return (
    <span className="exo-dc-mention is-clickable" role="button" tabIndex={0} onClick={go} onKeyDown={event => { if (event.key === 'Enter') go() }}>#{name}</span>
  )
}

// A Discord link (discord.com/channels/server or @me/channel[/message]) opens here, at the message, as Discord's app opens
// its own links; any other goes to the browser.
const DISCORD_LINK = /^https:\/\/(?:(?:ptb|canary)\.)?discord(?:app)?\.com\/channels\/(@me|\d{5,20})\/(\d{5,20})(?:\/(\d{5,20}))?\/?$/

function follow(url: string) {
  const [, place, channelId, messageId] = DISCORD_LINK.exec(url) ?? []
  const guildId = place === '@me' ? null : place
  if (!channelId) open(url)
  else if (messageId) jumpToMessage({ guildId, channelId, messageId })
  else openDiscord({ guildId, channelId, outside: false })
}

export function Link({ url, children }: { url: string; children: ReactNode }) {
  return (
    <a className="exo-dc-link" href={url} onClick={event => { event.preventDefault(); follow(url) }}>{children}</a>
  )
}

const ONLY_EMOJI = /^(?:\s*<a?:\w{2,32}:\d{5,20}>\s*|\s*\p{Extended_Pictographic}(?:️|‍\p{Extended_Pictographic})*\s*){1,27}$/u

/** A message's text as blocks: fenced code, quotes, headings, small text and lines of inline markdown. */
/** One line of a message (a reply's quote): mentions, emoji and styling as in the message, its lines run together. */
export function MarkdownLine({ text, ctx }: { text: string; ctx: MarkdownContext }) {
  return <>{inline(text.replace(/\s*\n+\s*/g, ' '), ctx, false, 'l')}</>
}

/** tail: drawn at the end of the last line (the edited mark), as Discord places it. */
export function Markdown({ text, ctx, tail }: { text: string; ctx: MarkdownContext; tail?: ReactNode }) {
  const jumbo = ONLY_EMOJI.test(text)
  const blocks: ReactNode[] = []
  const parts = text.split(/```(?:([\w+-]{1,20})\n)?([\s\S]*?)```/g)
  for (let i = 0; i < parts.length; i += 3) {
    const last = i + 3 >= parts.length
    if (parts[i]) blocks.push(...lines(parts[i], ctx, jumbo, `b${i}`, last ? tail : null))
    else if (last && tail) blocks.push(<p key="t">{tail}</p>)
    if (i + 2 < parts.length) blocks.push(<pre key={`c${i}`} className="exo-dc-pre"><code>{parts[i + 2].replace(/^\n|\n$/g, '')}</code></pre>)
  }
  return <div className={`exo-dc-text${jumbo ? ' is-jumbo' : ''}`}>{blocks}</div>
}

const HEADINGS = ['exo-dc-h1', 'exo-dc-h2', 'exo-dc-h3']
// An empty line keeps its height.
const BLANK = '\u00a0'

// One line: a heading, -# small text, a list item (- or 1., nested by its leading spaces), or plain.
function row(text: string, ctx: MarkdownContext, jumbo: boolean, k: string, tail: ReactNode): ReactNode {
  const heading = /^(#{1,3}) (.+)$/.exec(text)
  if (heading) return <p key={k} className={HEADINGS[heading[1].length - 1]}>{inline(heading[2], ctx, false, k)}{tail}</p>
  if (text.startsWith('-# ')) return <p key={k} className="exo-dc-small">{inline(text.slice(3), ctx, false, k)}{tail}</p>
  const item = /^(\s*)(?:[-*]|(\d{1,9})\.) (.+)$/.exec(text)
  if (item) {
    const depth = Math.min(3, Math.floor(item[1].length / 2))
    return (
      <p key={k} className="exo-dc-li" data-marker={item[2] ? `${item[2]}.` : '•'} style={depth ? { '--exo-dc-depth': depth } as React.CSSProperties : undefined}>
        {inline(item[3], ctx, false, k)}{tail}
      </p>
    )
  }
  return <p key={k}>{text ? inline(text, ctx, jumbo, k) : BLANK}{tail}</p>
}

// Lines, with > quotes (a run of them is one quote) and >>> (everything after it is quoted).
function lines(text: string, ctx: MarkdownContext, jumbo: boolean, key: string, tail: ReactNode = null): ReactNode[] {
  const out: ReactNode[] = []
  let quote: string[] = []
  const rows = text.replace(/^\n+|\n+$/g, '').split('\n')
  const flush = (i: number, end: ReactNode) => {
    if (quote.length === 0) return
    const k = `${key}q${i}`
    out.push(<blockquote key={k} className="exo-dc-quote">{quote.map((q, j) => row(q, ctx, false, `${k}.${j}`, j === quote.length - 1 ? end : null))}</blockquote>)
    quote = []
  }
  const rest = rows.findIndex(r => r.startsWith('>>> '))
  rows.forEach((text, i) => {
    const end = i === rows.length - 1 ? tail : null
    if (rest >= 0 && i >= rest) { quote.push(i === rest ? text.slice(4) : text); if (end) flush(i, end); return }
    if (text.startsWith('> ') || text === '>') { quote.push(text.slice(2)); if (end) flush(i, end); return }
    flush(i, null)
    out.push(row(text, ctx, jumbo, `${key}l${i}`, end))
  })
  flush(rows.length, null)
  return out
}
