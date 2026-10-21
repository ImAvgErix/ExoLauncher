import { host, onHostEvent } from '../../lib/host'
import { applyPollVote, applyReaction, type DiscordMessage, type DiscordMessageEvent } from '../../lib/discord'

/**
 * Each conversation's newest messages as last seen, the way Discord's own client keeps them: going back to one shows
 * it at once while Discord is asked what changed, and a channel hovered for a moment is read before it is clicked.
 * Live changes keep conversations not on screen current. Memory only, the most recent 30 conversations.
 */
type Kept = {
  messages: DiscordMessage[]
  /** Discord has messages older than these. */
  older: boolean
  /** Where the user had read to when Discord sent these (undefined: not asked with them). */
  lastRead?: string | null
  at: number
}

export const PAGE = 50
const CONVERSATIONS = 30
const KEEP = 100
/** A page read this recently is still the present: opening it doesn't ask again. */
export const FRESH_MS = 5000

const kept = new Map<string, Kept>()
type Newest = { ok: boolean; message?: string | null; messages?: DiscordMessage[] | null; lastRead?: string | null }
const asking = new Map<string, Promise<Newest>>()

export function keptMessages(channelId: string): Kept | null {
  return kept.get(channelId) ?? null
}

export function keepMessages(channelId: string, messages: DiscordMessage[], older: boolean, lastRead?: string | null) {
  const real = messages.filter(m => !m.pending)
  kept.delete(channelId)
  if (real.length === 0) return
  kept.set(channelId, { messages: real.slice(-KEEP), older: older || real.length > KEEP, lastRead, at: Date.now() })
  while (kept.size > CONVERSATIONS) kept.delete(kept.keys().next().value!)
}

export function forgetMessages(channelId: string) {
  kept.delete(channelId)
}

/** The newest page, one request at a time per conversation (an open after a hover waits for the hover's). */
export function newestMessages(channelId: string, peek: boolean): Promise<Newest> {
  const running = asking.get(channelId)
  if (running) return running
  const request = host.discordMessages(channelId, peek ? { peek: true } : {}).then(result => {
    if (result.ok && result.messages) {
      const page = byId(result.messages)
      keepMessages(channelId, page, page.length >= PAGE, result.lastRead ?? null)
    }
    return result
  }).finally(() => asking.delete(channelId))
  asking.set(channelId, request)
  return request
}

export const isAsking = (channelId: string) => asking.has(channelId)

let hover: number | undefined
let hovered: string | null = null
/** Reads a conversation the pointer rests on a moment (not one it passes over), unless it was read just now. */
export function prefetchMessages(channelId: string | null | undefined) {
  if ((channelId ?? null) === hovered) return
  hovered = channelId ?? null
  window.clearTimeout(hover)
  if (!channelId) return
  hover = window.setTimeout(() => {
    const known = kept.get(channelId)
    if (asking.has(channelId) || (known && Date.now() - known.at < 30_000)) return
    void newestMessages(channelId, true).catch(() => { })
  }, 80)
}

/** A new, edited or deleted message applied to a list (a new one waits while older messages show). */
export function applyMessage(list: DiscordMessage[], event: DiscordMessageEvent, meId: string | null, viewingOlder: boolean): DiscordMessage[] {
  if (event.kind === 'delete') return list.filter(m => m.id !== event.id)
  const incoming = event.message
  if (!incoming) return list
  const at = list.findIndex(m => m.id === incoming.id)
  if (event.kind === 'update') return at < 0 ? list : list.map(m => m.id === incoming.id ? { ...m, ...incoming } : m)
  if (at >= 0 || viewingOlder) return list
  return [...list.filter(m => !(m.pending && !m.failed && !m.uploading && m.content === incoming.content && incoming.author.id === meId)), incoming]
}

/**
 * The newest page laid over what shows: what is older than the page stays (pages scrolled up to), the page replaces
 * the rest, and messages unchanged keep their objects so they don't redraw. A page that doesn't reach back to what
 * shows replaces it all. Messages being sent stay at the end.
 */
export function mergeNewest(list: DiscordMessage[], page: DiscordMessage[]): { messages: DiscordMessage[]; gap: boolean } {
  if (page.length === 0) return { messages: list, gap: false }
  const real = list.filter(m => !m.pending)
  const first = page[0].id
  const gap = page.length >= PAGE && !real.some(m => !isAfter(m.id, first))
  const before = new Map(real.map(m => [m.id, m]))
  const fresh = page.map(m => {
    const was = before.get(m.id)
    return was && JSON.stringify(was) === JSON.stringify(m) ? was : m
  })
  const older = gap ? [] : real.filter(m => isAfter(first, m.id))
  return { messages: [...older, ...fresh, ...list.filter(m => m.pending)], gap }
}

export function isAfter(id: string, other: string) {
  try { return BigInt(id) > BigInt(other) } catch { return false }
}

// Oldest first, whatever order Discord sent the page in.
export function byId(messages: DiscordMessage[]) {
  return [...messages].sort((a, b) => Date.parse(a.timestamp) - Date.parse(b.timestamp) || (isAfter(a.id, b.id) ? 1 : isAfter(b.id, a.id) ? -1 : 0))
}

// Conversations not on screen stay current (the one on screen is written back when it closes).
function patch(channelId: string, change: (messages: DiscordMessage[]) => DiscordMessage[]) {
  const entry = kept.get(channelId)
  if (entry) entry.messages = change(entry.messages)
}
onHostEvent('discord.message', event => patch(event.channelId, list => applyMessage(list, event, null, false)))
onHostEvent('discord.reaction', event => patch(event.channelId, list => list.map(m => m.id === event.messageId ? { ...m, reactions: applyReaction(m.reactions, event) } : m)))
onHostEvent('discord.pollVote', event => patch(event.channelId, list => list.map(m => m.id === event.messageId && m.poll ? { ...m, poll: applyPollVote(m.poll, event.answerId, event.add, event.me) } : m)))
