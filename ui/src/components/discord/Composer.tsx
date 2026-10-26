import { useEffect, useMemo, useRef, useState, type RefObject } from 'react'
import { host, onHostEvent } from '../../lib/host'
import { useDictation } from '../../lib/dictation'
import { DictateButton } from '../ui/DictateButton'
import { displayName, avatarUrl, type DiscordGuildEmoji, type DiscordMessage, type DiscordSticker, type DiscordUser } from '../../lib/discord'
import { EmojiPicker, emojiText, loadEmojis } from './EmojiPicker'
import { GifPicker } from './GifPicker'
import { StickerPicker } from './extras'
import { PlusMenu } from './PollCreator'
import { Icons } from './icons'
import { ChannelIcon } from './Sidebar'
import { emojiUrl } from './markdown'
import { findUnicodeEmoji, loadUnicodeEmoji, shortcodesToEmoji, type UnicodeEmojiSet } from './unicodeEmoji'
import { accountSettings, convertEmoticons, useAccountSettings } from '../../lib/discordPrefs'
import { appsPlace, onAppStatus, setAppsPlace, type CommandEntry } from '../../lib/discordApps'
import { CommandForm, CommandList, type CommandKeys } from './AppCommands'
import { Confirm } from './admin'
import { Switch } from '../Switch'

/** A file's alt text (Modify Attachment's Description), carried to the send with the file. */
export const fileDescriptions = new WeakMap<File, string>()

const TYPING_EVERY_MS = 8000
const MAX_FILES = 10
const TOKEN = /(^|\s)([:@#])([\w.+-]{2,32})$/

// detail: dimmer words after the name (a username, a channel's category, what @everyone does); color: a role's.
type Suggestion = { key: string; label: string; image?: string; glyph?: React.ReactNode; insert: string; sticker?: string; mention?: string; detail?: string | null; color?: string }
type Draft = { text: string; mentions: Map<string, string> }

// What you were writing in each conversation, kept while Exo runs, as Discord keeps drafts.
const drafts = new Map<string, Draft>()

// Discord settings > Appearance > Show stickers in autocomplete results: :name also offers the stickers you can send.
function useStickers(guildId: string | null) {
  const on = useAccountSettings().includeStickersInAutocomplete
  const [stickers, setStickers] = useState<DiscordSticker[]>([])
  useEffect(() => {
    if (!on) return
    let live = true
    host.discordStickers(guildId).then(result => { if (live) setStickers((result.groups ?? []).filter(g => g.usable).flatMap(g => g.stickers)) }).catch(() => { })
    return () => { live = false }
  }, [on, guildId])
  return on ? stickers : []
}

function stickerSuggestions(stickers: DiscordSticker[], q: string): Suggestion[] {
  return stickers.filter(st => st.format !== 3 && st.name.toLowerCase().includes(q)).slice(0, 3)
    .map(st => ({ key: `sticker:${st.id}`, label: st.name, image: `https://media.discordapp.net/stickers/${st.id}.png?size=64`, insert: '', sticker: st.id }))
}

// A picked server emoji shows in the box as :name: (sent as itself), unless another you can send shares the name.
function asTyped(text: string, byName: Map<string, string>) {
  const name = /^<a?:(\w+):\d+>$/.exec(text)?.[1]
  return name && byName.get(name) === text ? `:${name}:` : text
}

// Server emoji and every Unicode emoji: the names that start with what you typed first.
function emojiSuggestions(emojis: DiscordGuildEmoji[], unicode: UnicodeEmojiSet | null, q: string): Suggestion[] {
  const custom = emojis.filter(e => e.native && e.name.toLowerCase().includes(q)).sort((a, b) => Number(!a.name.toLowerCase().startsWith(q)) - Number(!b.name.toLowerCase().startsWith(q))).slice(0, 4)
    .map(e => ({ key: e.id, label: `:${e.name}:`, image: emojiUrl(e.id, e.animated), insert: emojiText(e) }))
  const plain = unicode ? findUnicodeEmoji(unicode, q, 10 - custom.length).map(e => ({ key: e.emoji, label: `:${e.name}:`, glyph: e.emoji, insert: e.emoji })) : []
  return [...custom, ...plain]
}

type Mentionable = Awaited<ReturnType<typeof host.discordMentionable>>

/** People to @: those in the conversation, then the server's members whose names match (Discord is asked as you type), and its roles. */
function usePeople(guildId: string | null, users: Map<string, DiscordUser>, q: string | null) {
  const [found, setFound] = useState<Mentionable>({ users: [] })
  useEffect(() => {
    if (!guildId || !q) { setFound({ users: [] }); return }
    let live = true
    const look = (ask: boolean) => host.discordMentionable(guildId, q, ask).then(result => { if (live) setFound(result) }).catch(() => { })
    void look(false)
    const asking = window.setTimeout(() => void look(true), 300)
    const off = onHostEvent('discord.colors', event => { if (event.guildId === guildId) void look(false) })
    return () => { live = false; window.clearTimeout(asking); off() }
  }, [guildId, q])
  return useMemo(() => {
    if (q === null) return { people: [], roles: [] }
    const here = [...users.values()].filter(u => (u.global_name ?? u.username).toLowerCase().includes(q) || u.username.toLowerCase().includes(q))
    const more = found.users.filter(u => !here.some(h => h.id === u.id))
    return { people: [...here, ...more].slice(0, 8), roles: found.roles ?? [] }
  }, [users, found, q])
}

/** Channels to #: the server's that you see, whose names match. */
function useChannels(guildId: string | null, q: string | null) {
  const [found, setFound] = useState<NonNullable<Mentionable['channels']>>([])
  useEffect(() => {
    if (!guildId || !q) { setFound([]); return }
    let live = true
    host.discordMentionable(guildId, q, false, true).then(result => { if (live) setFound(result.channels ?? []) }).catch(() => { })
    return () => { live = false }
  }, [guildId, q])
  return q === null ? [] : found
}

// What the open @name or #name offers: people and roles, or channels.
function useMentions(guildId: string | null, users: Map<string, DiscordUser>, open: RegExpExecArray | null) {
  const q = open ? open[3].toLowerCase() : null
  return { people: usePeople(guildId, users, open?.[2] === '@' ? q : null), channels: useChannels(guildId, open?.[2] === '#' ? q : null) }
}

// Discord's @everyone and @here, offered in a server as their names are typed.
const GLOBALS = [
  { name: 'everyone', detail: 'Notify everyone who has permission to view this channel.' },
  { name: 'here', detail: 'Notify everyone online who has permission to view this channel.' },
]

// What @ offers: people (their username after), roles in their color, then @everyone and @here in a server.
function mentionSuggestions(found: ReturnType<typeof usePeople>, q: string, server: boolean): Suggestion[] {
  const people = found.people.map(u => {
    const name = u.global_name ?? u.username
    return { key: u.id, label: name, image: avatarUrl(u, 32), insert: `@${name}`, mention: `<@${u.id}>`, detail: name.toLowerCase() === u.username.toLowerCase() ? null : u.username }
  })
  const roles = found.roles.map(r => ({ key: `role:${r.id}`, label: `@${r.name}`, glyph: '@', insert: `@${r.name}`, mention: `<@&${r.id}>`, color: r.color ?? undefined }))
  const globals = server ? GLOBALS.filter(g => g.name.startsWith(q)).map(g => ({ key: `@${g.name}`, label: `@${g.name}`, glyph: '@', insert: `@${g.name}`, detail: g.detail })) : []
  return [...people, ...roles, ...globals].slice(0, 10)
}

/**
 * The message box: Enter sends, Shift+Enter is a new line, Escape cancels a reply (else marks the channel read, as
 * Discord's Esc does), Up edits your last message. Files come from the + button, paste or a drop; :name and @name suggest emoji and people as you
 * type, and a mention reads as @name in the box. :smile: is sent as the emoji. Each conversation keeps its draft.
 */
export function Composer({ channelId, guildId, users, placeholder, disabled, replyTo, replyPing, onReplyPing, slowmode = 0, error, typers, onSend, onCancel, onEditLast, onMarkRead }: {
  channelId: string
  guildId: string | null
  users: Map<string, DiscordUser>
  placeholder: string
  disabled: boolean
  replyTo: DiscordMessage | null
  /** Whether the reply pings its author (null: replying to yourself, no choice). */
  replyPing: boolean | null
  onReplyPing: () => void
  /** Seconds between your messages here (0 or absent: none, or you're exempt). */
  slowmode?: number
  error: string | null
  typers: string[]
  onSend: (content: string, files: File[]) => Promise<boolean>
  onCancel: () => void
  onEditLast: () => void
  onMarkRead: () => void
}) {
  const [text, setTextNow] = useState(() => drafts.get(channelId)?.text ?? '')
  const mentions = useRef(drafts.get(channelId)?.mentions ?? new Map<string, string>())
  const [files, setFiles] = useState<File[]>([])
  const [spoilers, setSpoilers] = useState<Set<File>>(new Set())
  // Shrinks still running when Enter lands finish before the send reads the files.
  const pendingShrinks = useRef<Promise<void>[]>([])
  const filesNow = useRef<File[]>(files)
  const spoilersNow = useRef<Set<File>>(spoilers)
  filesNow.current = files
  spoilersNow.current = spoilers
  const [note, setNote] = useState<string | null>(null)
  const [picker, setPicker] = useState<Expression | null>(null)
  const [emojis, setEmojis] = useState<DiscordGuildEmoji[]>([])
  // Discord's longest message: 2,000 characters, 4,000 with Nitro.
  const [limit, setLimit] = useState(2000)
  // A message over the limit, asked about: its length.
  const [tooLong, setTooLong] = useState<number | null>(null)
  const [unicode, setUnicode] = useState<UnicodeEmojiSet | null>(null)
  const [pick, setPick] = useState(0)
  const [dismissed, setDismissed] = useState<string | null>(null)
  const [dragging, setDragging] = useState(false)
  const box = useRef<HTMLTextAreaElement>(null)
  const fileInput = useRef<HTMLInputElement>(null)
  const typedAt = useRef(0)
  // Set while submit waits (the emoji list, a picture being shrunk): a second Enter then would send the same message twice.
  const preparing = useRef(false)
  const [cooldown, startCooldown] = useCooldown(channelId)
  // A command chosen from the / list fills the box until it's sent or cancelled.
  const [command, setCommand] = useState<CommandEntry | null>(null)
  const commandKeys: CommandKeys = useRef(null)
  const [appLine, setAppLine] = useState<string | null>(null)
  useEffect(() => onAppStatus(channelId, (line, failed) => { setAppLine(failed ? null : line); if (failed) setNote(line) }), [channelId])
  useEffect(() => {
    setAppsPlace({ channelId, guildId })
    return () => { if (appsPlace()?.channelId === channelId) setAppsPlace(null) }
  }, [channelId, guildId])

  const setText = (next: string | ((current: string) => string)) => setTextNow(current => {
    const value = typeof next === 'function' ? next(current) : next
    drafts.set(channelId, { text: value, mentions: mentions.current })
    return value
  })

  useEffect(() => { box.current?.focus() }, [replyTo])

  useEffect(() => {
    let live = true
    // This server's first: a :name: two servers share means this one's, as in Discord.
    loadEmojis(guildId).then(data => {
      if (!live) return
      setEmojis(data.guilds.toSorted((a, b) => Number(b.id === guildId) - Number(a.id === guildId)).flatMap(g => g.emojis))
      setLimit(data.premium ? 4000 : 2000)
    }).catch(() => { })
    return () => { live = false }
  }, [guildId])

  // Mention from the person menu: it goes in at the end as @name, ready to type after.
  useEffect(() => {
    const mention = (event: Event) => {
      const { id, name } = (event as CustomEvent<{ id: string; name: string }>).detail
      mentions.current.set(`@${name}`, `<@${id}>`)
      setText(current => `${current}${current && !/\s$/.test(current) ? ' ' : ''}@${name} `)
      box.current?.focus()
    }
    window.addEventListener('exo-dc-mention', mention)
    return () => window.removeEventListener('exo-dc-mention', mention)
  })

  // The box grows with its text, up to 240 px, again whenever its width changes (the window resized).
  useEffect(() => {
    const el = box.current
    if (!el) return
    const fit = () => {
      el.style.height = 'auto'
      el.style.height = `${Math.min(el.scrollHeight, 240)}px`
    }
    fit()
    let width = el.clientWidth
    const observer = new ResizeObserver(() => { if (el.clientWidth !== width) { width = el.clientWidth; fit() } })
    observer.observe(el)
    return () => observer.disconnect()
  }, [text])

  const stickers = useStickers(guildId)
  const token = TOKEN.exec(text)
  const open = token && token[0] !== dismissed ? token : null
  const asEmoji = open?.[2] === ':'
  useEffect(() => { if (asEmoji && !unicode) loadUnicodeEmoji().then(setUnicode).catch(() => { }) }, [asEmoji, unicode])
  const { people, channels } = useMentions(guildId, users, open)
  const suggestions = useMemo<Suggestion[]>(() => {
    if (!open) return []
    const q = open[3].toLowerCase()
    if (open[2] === ':') return [...emojiSuggestions(emojis, unicode, q), ...stickerSuggestions(stickers, q)]
    if (open[2] === '#') return channels.map(c => ({ key: c.id, label: c.name, glyph: <ChannelIcon channel={c} />, insert: `#${c.name}`, mention: `<#${c.id}>`, detail: c.category }))
    return mentionSuggestions(people, q, guildId !== null)
  }, [open?.[0], emojis, unicode, stickers, people, channels]) // eslint-disable-line react-hooks/exhaustive-deps -- the token's text is what matters

  function complete(suggestion: Suggestion) {
    // A sticker picked from the list is sent at once, as in Discord.
    if (suggestion.sticker) sendSticker(suggestion.sticker)
    if (suggestion.mention) mentions.current.set(suggestion.insert, suggestion.mention)
    setText(current => current.replace(TOKEN, suggestion.sticker ? '$1' : `$1${suggestion.insert} `))
    setPick(0)
    box.current?.focus()
  }

  function sendSticker(id: string) {
    setNote(null)
    void host.discordSticker(channelId, id)
      .then(result => { if (!result.ok) setNote(result.message ?? 'Discord did not send that sticker.') })
      .catch(() => setNote('Discord did not send that sticker.'))
  }

  function insert(value: string) {
    const el = box.current
    const at = el?.selectionStart ?? text.length
    setText(current => `${current.slice(0, at)}${value}${current.slice(at)}`)
    requestAnimationFrame(() => { el?.focus(); el?.setSelectionRange(at + value.length, at + value.length) })
  }

  // The message as message.txt in its files instead, as Discord's Upload as File does.
  function asFile() {
    addFiles([textFile(text)])
    setText('')
    mentions.current = new Map()
  }

  function addFiles(list: FileList | File[] | null) {
    if (!list) return
    const added = Array.from(list)
    setFiles(current => [...current, ...added].slice(0, MAX_FILES))
    // Big images go up smaller on their own: WebP keeps them looking the same at a fraction of the size.
    for (const file of added.slice(0, MAX_FILES)) {
      const shrinking = shrinkable(file)
      if (!shrinking) continue
      pendingShrinks.current.push(shrinking.then(smaller => {
        if (smaller === file) return
        const alt = fileDescriptions.get(file)
        if (alt) { fileDescriptions.delete(file); fileDescriptions.set(smaller, alt) }
        setSpoilers(current => current.has(file) ? new Set([...current].map(f => f === file ? smaller : f)) : current)
        setFiles(current => current.map(f => f === file ? smaller : f))
      }))
    }
    // Back in the box, as Discord does: Enter sends the files, with or without words.
    requestAnimationFrame(() => box.current?.focus())
  }

  // The custom emoji you can send, by :name: (the first of a shared name).
  const customByName = useMemo(() => new Map(emojis.filter(e => e.native).reverse().map(e => [e.name, emojiText(e)])), [emojis])

  // What goes to Discord: @names back to mentions, :shortcodes: and (when on) emoticons as emoji.
  function outgoing(value: string, byName: Map<string, string> | null) {
    let content = value
    for (const [shown, mention] of mentions.current) content = content.replace(new RegExp(`${escapeRegExp(shown)}(?![\\w])`, 'g'), mention)
    content = shortcodesToEmoji(content, byName, customByName)
    return (accountSettings().convertEmoticons ? convertEmoticons(content) : content).trim()
  }

  async function submit() {
    if (preparing.current) return
    preparing.current = true
    let content: string
    try {
      if (!text.trim() && files.length === 0) return
      if (cooldown > 0) { setNote(`Slowmode is on. Wait ${cooldown} seconds.`); return }
      // :name: needs the emoji list; the first message after opening waits for it.
      const set = unicode ?? (text.includes(':') ? await loadUnicodeEmoji().catch(() => null) : null)
      if (set && !unicode) setUnicode(set)
      content = outgoing(text, set?.byName ?? null)
      if (content.length > limit) { setTooLong(content.length); return }
      if (pendingShrinks.current.length) { await Promise.allSettled(pendingShrinks.current); pendingShrinks.current = [] }
    } finally { preparing.current = false }
    // Nothing awaits between here and the box being cleared, so no second Enter can slip in.
    const sendFrom = filesNow.current
    const sendSpoilers = spoilersNow.current
    const sentText = text
    const sentFiles = sendFrom.map(file => {
      if (!sendSpoilers.has(file)) return file
      const copy = new File([file], `SPOILER_${file.name}`, { type: file.type })
      const alt = fileDescriptions.get(file)
      if (alt) fileDescriptions.set(copy, alt)
      return copy
    })
    const sentMentions = new Map(mentions.current)
    setText(''); setFiles([]); setSpoilers(new Set()); setNote(null)
    mentions.current = new Map()
    drafts.delete(channelId)
    typedAt.current = 0
    const ok = await onSend(content, sentFiles)
    if (ok && slowmode > 0) startCooldown(slowmode)
    if (!ok) {
      // Back in the box when Discord didn't take it, unless you've started another message.
      mentions.current = sentMentions
      setText(current => current || sentText)
      setFiles(current => current.length ? current : sendFrom)
      setSpoilers(sendSpoilers)
    }
  }

  function onChange(value: string) {
    setText(value)
    setPick(0)
    setDismissed(null)
    if (value.trim() && Date.now() - typedAt.current > TYPING_EVERY_MS) {
      typedAt.current = Date.now()
      void host.discordTyping(channelId).catch(() => { })
    }
  }

  // The / list takes its keys first.
  const onKeyDown = (event: React.KeyboardEvent<HTMLTextAreaElement>) => { if (!commandKeys.current?.(event)) textKeys(event) }

  function textKeys(event: React.KeyboardEvent<HTMLTextAreaElement>) {
    if (suggestions.length && (event.key === 'ArrowDown' || event.key === 'ArrowUp')) {
      event.preventDefault()
      setPick(n => (n + (event.key === 'ArrowDown' ? 1 : suggestions.length - 1)) % suggestions.length)
    } else if (suggestions.length && (event.key === 'Tab' || event.key === 'Enter') && !event.shiftKey) {
      event.preventDefault()
      complete(suggestions[Math.min(pick, suggestions.length - 1)])
    } else if (suggestions.length && event.key === 'Escape') {
      // Escape closes the list first; the reply stays.
      event.preventDefault(); event.stopPropagation(); setDismissed(open?.[0] ?? null)
    } else if (event.key === 'Enter' && !event.shiftKey && !event.nativeEvent.isComposing) {
      event.preventDefault(); void submit()
    } else if (event.key === 'Escape' && replyTo) {
      event.preventDefault(); onCancel()
    } else if (event.key === 'Escape') {
      onMarkRead()
    } else if (event.key === 'ArrowUp' && !text) {
      event.preventDefault(); onEditLast()
    }
  }


  return (
    <div
      className={`exo-dc-compose${dragging ? ' is-dragging' : ''}`}
      onDragOver={event => { if (canDrop(disabled, event)) { event.preventDefault(); setDragging(true) } }}
      onDragLeave={() => setDragging(false)}
      onDrop={event => { event.preventDefault(); setDragging(false); if (!disabled) addFiles(event.dataTransfer.files) }}
    >
      {suggestions.length ? <Suggestions suggestions={suggestions} pick={pick} onPick={complete} /> : null}
      {!command && slashTyped(text, dismissed) ? (
        <CommandList channelId={channelId} query={text.slice(1)} keys={commandKeys} onClose={() => setDismissed(text)}
          onPick={entry => { setCommand(entry); setText(''); setNote(null) }} />
      ) : null}
      {replyTo ? <ReplyBar replyTo={replyTo} ping={replyPing} onPing={onReplyPing} onCancel={onCancel} /> : null}
      {files.length ? <FileChips files={files} spoilers={spoilers} setSpoilers={setSpoilers} setFiles={setFiles} /> : null}
      <div className={`exo-dc-input-row${replyTo || files.length ? ' is-attached' : ''}`}>
        <PlusMenu channelId={channelId} disabled={disabled} onFiles={() => fileInput.current?.click()} text={text} onTextFile={asFile} />
        <input ref={fileInput} type="file" multiple hidden onChange={event => { addFiles(event.target.files); event.target.value = '' }} />
        {command ? (
          <CommandForm entry={command} channelId={channelId} guildId={guildId} users={users}
            onDone={() => { setCommand(null); requestAnimationFrame(() => box.current?.focus()) }}
            onCancel={() => { setCommand(null); requestAnimationFrame(() => box.current?.focus()) }} />
        ) : <textarea
          ref={box}
          className="exo-dc-input"
          rows={1}
          value={text}
          placeholder={placeholder}
          disabled={disabled}
          aria-label={placeholder}
          onChange={event => onChange(event.target.value)}
          onKeyDown={onKeyDown}
          onPaste={event => {
            if (event.clipboardData.files.length) { event.preventDefault(); addFiles(event.clipboardData.files); return }
            // Pasted text longer than a message can be goes as message.txt, as Discord pastes it.
            const pasted = event.clipboardData.getData('text/plain')
            if (pasted.length > limit) { event.preventDefault(); addFiles([textFile(pasted)]) }
          }}
        />}
        {command ? null : <MicSlot box={box} />}
        <ComposerTools guildId={guildId} disabled={disabled} picker={picker} setPicker={setPicker}
          onEmoji={text => insert(asTyped(text, customByName))} onGif={url => void onSend(url, [])} onSticker={sendSticker} />
      </div>
      <ComposeFoot problem={error ?? note} status={appLine} typers={typers} slowmode={slowmode} cooldown={cooldown} left={limit - text.length} limit={limit} />
      <TooLong length={tooLong} limit={limit} onClose={() => setTooLong(null)} onFile={() => { setTooLong(null); asFile() }} />
    </div>
  )
}

const textFile = (text: string) => new File([text], 'message.txt', { type: 'text/plain' })

/**
 * A big still image goes to Discord as WebP when that is meaningfully smaller: at quality 1 it looks the
 * same, and Discord draws it inline like the original. Never wins enough or can't decode: the original goes.
 * GIF, SVG, WebP and AVIF are left alone — animated or already compact, re-encoding only loses.
 */
async function shrinkImage(file: File): Promise<File> {
  try {
    const bitmap = await createImageBitmap(file)
    const canvas = new OffscreenCanvas(bitmap.width, bitmap.height)
    canvas.getContext('2d')!.drawImage(bitmap, 0, 0)
    bitmap.close()
    const blob = await canvas.convertToBlob({ type: 'image/webp', quality: 1 })
    if (blob.size >= file.size * 0.85) return file
    const name = file.name.replace(/\.[^.]+$/, '') || 'image'
    return new File([blob], `${name}.webp`, { type: 'image/webp', lastModified: file.lastModified })
  } catch { return file }
}

/** The same image, smaller, when the file is a shrinkable kind — else nothing to wait on. */
function shrinkable(file: File) {
  return file.size > 256 * 1024 && /^image\/(?!gif|svg|webp|avif)/.test(file.type) ? shrinkImage(file) : null
}

// Over the limit, Discord's way out: the message as a file.
function TooLong({ length, limit, onFile, onClose }: { length: number | null; limit: number; onFile: () => void; onClose: () => void }) {
  if (length === null) return null
  return (
    <Confirm title="Upload it as a file?" action="Upload as file" danger={false} onClose={onClose} onConfirm={onFile}>
      <p>Your message is {length.toLocaleString()} characters, and a message can be up to {limit.toLocaleString()}. Shorten it, or send it as a file.</p>
    </Confirm>
  )
}

// Slowmode's wait after you send, per conversation and kept while Exo runs: seconds left, ticking down.
const cooldowns = new Map<string, number>()
function useCooldown(channelId: string) {
  const left = () => Math.max(0, Math.ceil(((cooldowns.get(channelId) ?? 0) - Date.now()) / 1000))
  const [seconds, setSeconds] = useState(left)
  useEffect(() => {
    if (seconds <= 0) return
    const timer = window.setTimeout(() => setSeconds(left()), 1000)
    return () => window.clearTimeout(timer)
  })
  const start = (wait: number) => { cooldowns.set(channelId, Date.now() + wait * 1000); setSeconds(wait) }
  return [seconds, start] as const
}

function slowmodeText(seconds: number) {
  if (seconds < 60) return `${seconds}s`
  if (seconds < 3600) return `${Math.round(seconds / 60)}m`
  return `${Math.round(seconds / 3600)}h`
}

// Over the box while replying: to whom, Discord's @ ON/OFF (whether they're pinged), and Cancel.
function ReplyBar({ replyTo, ping, onPing, onCancel }: { replyTo: DiscordMessage; ping: boolean | null; onPing: () => void; onCancel: () => void }) {
  const name = displayName(replyTo)
  return (
    <p className="exo-dc-compose-bar">
      <span>Replying to <strong>{name}</strong></span>
      {ping !== null ? (
        <button type="button" className={`exo-dc-reply-ping${ping ? ' is-on' : ''}`} aria-pressed={ping} aria-label={`Mention ${name}: ${ping ? 'on' : 'off'}`} onClick={onPing}>
          <Icons.At size={14} />{ping ? 'ON' : 'OFF'}
        </button>
      ) : null}
      <button type="button" onClick={onCancel}>Cancel</button>
    </p>
  )
}

function canDrop(disabled: boolean, event: React.DragEvent) {
  return !disabled && event.dataTransfer.types.includes('Files')
}

// Under the box: what went wrong, else who is typing (Discord's dots), with slowmode (and your wait) on the right.
// left: characters left before the limit, shown in its last tenth (below zero, over it), as Discord counts down.
function ComposeFoot({ problem, status, typers, slowmode, cooldown, left, limit }: { problem: string | null; status?: string | null; typers: string[]; slowmode: number; cooldown: number; left: number; limit: number }) {
  const typing = status ?? typingLine(typers)
  return (
    <p className="exo-dc-compose-foot" role={problem ? 'alert' : 'status'}>
      {!problem && typing ? <span className="exo-dc-typing-dots" aria-hidden="true"><i /><i /><i /></span> : null}
      <span className="exo-dc-compose-line">{problem ?? typing ?? '\u00a0'}</span>
      {slowmode > 0 ? (
        <span className={`exo-dc-slowmode${cooldown > 0 ? ' is-waiting' : ''}`} aria-label={cooldown > 0 ? `Slowmode, ${cooldown} seconds left` : `Slowmode is on: one message every ${slowmodeText(slowmode)}`}>
          <Icons.Slowmode size={14} />{cooldown > 0 ? slowmodeText(cooldown) : null}
        </span>
      ) : null}
      {left <= limit / 10 ? <span className={`exo-dc-count${left < 0 ? ' is-over' : ''}`}>{left}<span className="sr-only">{left < 0 ? ' characters over the limit' : ' characters remaining'}</span></span> : null}
    </p>
  )
}

// Discord's words: "A is typing…", "A and B are typing…", "A, B, and C are typing…", then "Several people are typing…".
function typingLine(typers: string[]) {
  const [a, b, c] = typers
  if (typers.length === 0) return null
  if (typers.length === 1) return `${a} is typing…`
  if (typers.length === 2) return `${a} and ${b} are typing…`
  return typers.length === 3 ? `${a}, ${b}, and ${c} are typing…` : 'Several people are typing…'
}

// The files going with the message, each with Modify Attachment, Mark as spoiler (sent as SPOILER_name, as Discord does)
// and Remove.
function FileChips({ files, spoilers, setSpoilers, setFiles }: {
  files: File[]
  spoilers: Set<File>
  setSpoilers: React.Dispatch<React.SetStateAction<Set<File>>>
  setFiles: React.Dispatch<React.SetStateAction<File[]>>
}) {
  const [editing, setEditing] = useState<File | null>(null)
  // A renamed file is a new File: its alt text and spoiler go with it.
  const save = (file: File, change: { name: string; description: string; spoiler: boolean }) => {
    const name = change.name.trim() || file.name
    const next = name === file.name ? file : new File([file], name, { type: file.type })
    if (change.description.trim()) fileDescriptions.set(next, change.description.trim()); else fileDescriptions.delete(next)
    setFiles(list => list.map(f => f === file ? next : f))
    setSpoilers(set => { const out = new Set(set); out.delete(file); if (change.spoiler) out.add(next); return out })
  }
  // The dialog sits outside the chips' glass row, whose blur would hold it inside the row.
  return (
    <>
      {editing ? <ModifyAttachment file={editing} spoiler={spoilers.has(editing)} onClose={() => setEditing(null)} onSave={change => { save(editing, change); setEditing(null) }} /> : null}
      <div className="exo-dc-files">
        {files.map((file, i) => (
          <span key={`${file.name}-${i}`} className={`exo-dc-file-chip${spoilers.has(file) ? ' is-spoiler' : ''}`}>
            <FileThumb file={file} />
            <span className="exo-dc-file-name">{file.name}</span>
            <button type="button" aria-label={`Modify ${file.name}`} onClick={() => setEditing(file)}><Icons.Edit size={14} /></button>
            <button type="button" aria-label={spoilers.has(file) ? `Show ${file.name}` : `Mark ${file.name} as spoiler`} aria-pressed={spoilers.has(file)}
              onClick={() => setSpoilers(set => { const next = new Set(set); if (!next.delete(file)) next.add(file); return next })}><Icons.Spoiler size={14} /></button>
            <button type="button" aria-label={`Remove ${file.name}`} onClick={() => setFiles(list => list.filter((_, j) => j !== i))}>×</button>
          </span>
        ))}
      </div>
    </>
  )
}

// Discord's Modify Attachment: the file's name, its description (alt text, for a picture or video) and Mark as spoiler.
function ModifyAttachment({ file, spoiler, onSave, onClose }: { file: File; spoiler: boolean; onSave: (change: { name: string; description: string; spoiler: boolean }) => void; onClose: () => void }) {
  const [name, setName] = useState(file.name)
  const [description, setDescription] = useState(fileDescriptions.get(file) ?? '')
  const [hidden, setHidden] = useState(spoiler)
  const media = /^(image|video)\//.test(file.type)
  const done = () => onSave({ name, description, spoiler: hidden })
  return (
    <Confirm title="Modify attachment" action="Save" danger={false} form onClose={onClose} onConfirm={done}>
      <FileThumb file={file} />
      <label className="exo-dc-modal-field"><span>Filename</span>
        <input className="exo-dc-modal-input" value={name} maxLength={200} autoFocus onChange={event => setName(event.target.value)} onKeyDown={event => { if (event.key === 'Enter') done() }} />
      </label>
      {media ? (
        <label className="exo-dc-modal-field"><span>Description (alt text)</span>
          <textarea className="exo-dc-modal-input" rows={2} value={description} maxLength={1024} placeholder="Add a description" onChange={event => setDescription(event.target.value)} />
        </label>
      ) : null}
      <span className="exo-dc-modal-switch"><span>Mark as spoiler</span><Switch checked={hidden} label="Mark as spoiler" onChange={setHidden} /></span>
    </Confirm>
  )
}

// A picture's preview in its chip, freed when the chip goes.
function FileThumb({ file }: { file: File }) {
  const [url, setUrl] = useState<string | null>(null)
  useEffect(() => {
    if (!file.type.startsWith('image/')) return
    const next = URL.createObjectURL(file)
    setUrl(next)
    return () => URL.revokeObjectURL(next)
  }, [file])
  return url ? <img className="exo-dc-file-thumb" src={url} alt="" draggable={false} /> : null
}

function Suggestions({ suggestions, pick, onPick }: { suggestions: Suggestion[]; pick: number; onPick: (suggestion: Suggestion) => void }) {
  return (
    <ul className="exo-dc-suggest" role="listbox" aria-label="Suggestions">
      {suggestions.map((s, i) => (
        <li key={s.key} role="option" aria-selected={i === pick}>
          <button type="button" onMouseDown={event => { event.preventDefault(); onPick(s) }}>
            {s.glyph ? <span className="exo-dc-suggest-glyph" aria-hidden="true" style={s.color ? { color: s.color } : undefined}>{s.glyph}</span> : <img src={s.image} alt="" draggable={false} />}
            <span style={s.color ? { color: s.color } : undefined}>{s.label}</span>
            {s.detail ? <span className="exo-dc-suggest-detail">{s.detail}</span> : null}
          </button>
        </li>
      ))}
    </ul>
  )
}

// Only "/" and a word so far, not closed with Escape: the command list is open.
function slashTyped(text: string, dismissed: string | null) {
  return /^\/\S*$/.test(text) && dismissed !== text
}

function escapeRegExp(text: string) {
  return text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
}

type Expression = 'emoji' | 'gif' | 'sticker'

// Discord's expression picker: GIFs, Stickers and Emoji as tabs along its top, so one button opens all three.
function ExpressionTabs({ picker, setPicker }: { picker: Expression; setPicker: (next: Expression) => void }) {
  const tabs: [Expression, string][] = [['gif', 'GIFs'], ['sticker', 'Stickers'], ['emoji', 'Emoji']]
  return (
    <div className="exo-dc-picker-tabs" role="tablist" aria-label="Expressions">
      {tabs.map(([id, label]) => (
        <button key={id} type="button" role="tab" aria-selected={picker === id} onClick={() => setPicker(id)}>{label}</button>
      ))}
    </div>
  )
}

/**
 * The composer's mic slot, before its tools. While dictation is on (Settings › General) it is dictation's mic. With
 * dictation off the slot belongs to voice messages: a VoiceMessageButton goes here once Exo records them; until then
 * the composer shows no mic.
 */
function MicSlot({ box }: { box: RefObject<HTMLTextAreaElement | null> }) {
  const dictation = useDictation().state?.on !== false
  return dictation ? <DictateButton target={box} /> : null
}

// The one button at the right of the box: emoji, with GIFs and stickers a tab away in the same picker, so the box
// keeps its room and needs no GIF or sticker glyph.
function ComposerTools({ guildId, disabled, picker, setPicker, onEmoji, onGif, onSticker }: {
  guildId: string | null
  disabled: boolean
  picker: Expression | null
  setPicker: (next: Expression | null) => void
  onEmoji: (text: string) => void
  onGif: (url: string) => void
  onSticker: (id: string) => void
}) {
  const tabs = picker ? <ExpressionTabs picker={picker} setPicker={setPicker} /> : null
  return (
    <>
      <button type="button" className="exo-dc-input-btn" aria-label="Emoji, GIFs and stickers" aria-expanded={picker !== null} disabled={disabled} onClick={() => setPicker(picker ? null : 'emoji')}>
        <Icons.Emoji size={22} />
      </button>
      {picker === 'emoji' ? (
        <EmojiPicker guildId={guildId} tabs={tabs} onClose={() => setPicker(null)}
          onPick={emoji => { onEmoji(emoji.text); setPicker(null) }} />
      ) : null}
      {picker === 'gif' ? <GifPicker tabs={tabs} onClose={() => setPicker(null)} onPick={url => { setPicker(null); onGif(url) }} /> : null}
      {picker === 'sticker' ? <StickerPicker guildId={guildId} tabs={tabs} onClose={() => setPicker(null)} onPick={id => { setPicker(null); onSticker(id) }} /> : null}
    </>
  )
}
