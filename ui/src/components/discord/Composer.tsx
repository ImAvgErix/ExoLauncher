import { useEffect, useMemo, useRef, useState } from 'react'
import { host, onHostEvent } from '../../lib/host'
import { displayName, avatarUrl, type DiscordGuildEmoji, type DiscordMessage, type DiscordSticker, type DiscordUser } from '../../lib/discord'
import { EmojiPicker, emojiText, loadEmojis } from './EmojiPicker'
import { GifPicker } from './GifPicker'
import { StickerPicker } from './extras'
import { PlusMenu } from './PollCreator'
import { Icons } from './icons'
import { emojiUrl } from './markdown'
import { findUnicodeEmoji, loadUnicodeEmoji, shortcodesToEmoji, type UnicodeEmojiSet } from './unicodeEmoji'
import { accountSettings, convertEmoticons, useAccountSettings } from '../../lib/discordPrefs'
import { appsPlace, onAppStatus, setAppsPlace, type CommandEntry } from '../../lib/discordApps'
import { CommandForm, CommandList, type CommandKeys } from './AppCommands'

const TYPING_EVERY_MS = 8000
const MAX_FILES = 10
const TOKEN = /(^|\s)([:@])([\w.+-]{2,32})$/

type Suggestion = { key: string; label: string; image?: string; glyph?: string; insert: string; sticker?: string; mention?: string }
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

/** People to @: those in the conversation, then the server's members whose names match (Discord is asked as you type). */
function usePeople(guildId: string | null, users: Map<string, DiscordUser>, q: string | null) {
  const [found, setFound] = useState<DiscordUser[]>([])
  useEffect(() => {
    if (!guildId || !q) { setFound([]); return }
    let live = true
    const look = (ask: boolean) => host.discordMentionable(guildId, q, ask).then(result => { if (live) setFound(result.users) }).catch(() => { })
    void look(false)
    const asking = window.setTimeout(() => void look(true), 300)
    const off = onHostEvent('discord.colors', event => { if (event.guildId === guildId) void look(false) })
    return () => { live = false; window.clearTimeout(asking); off() }
  }, [guildId, q])
  return useMemo(() => {
    if (q === null) return []
    const here = [...users.values()].filter(u => (u.global_name ?? u.username).toLowerCase().includes(q) || u.username.toLowerCase().includes(q))
    const more = found.filter(u => !here.some(h => h.id === u.id))
    return [...here, ...more].slice(0, 8)
  }, [users, found, q])
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
  const [note, setNote] = useState<string | null>(null)
  const [picker, setPicker] = useState<Expression | null>(null)
  const [emojis, setEmojis] = useState<DiscordGuildEmoji[]>([])
  const [unicode, setUnicode] = useState<UnicodeEmojiSet | null>(null)
  const [pick, setPick] = useState(0)
  const [dismissed, setDismissed] = useState<string | null>(null)
  const [dragging, setDragging] = useState(false)
  const box = useRef<HTMLTextAreaElement>(null)
  const fileInput = useRef<HTMLInputElement>(null)
  const typedAt = useRef(0)
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
    loadEmojis(guildId).then(data => { if (live) setEmojis(data.guilds.toSorted((a, b) => Number(b.id === guildId) - Number(a.id === guildId)).flatMap(g => g.emojis)) }).catch(() => { })
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
  const people = usePeople(guildId, users, open?.[2] === '@' ? open[3].toLowerCase() : null)
  const suggestions = useMemo<Suggestion[]>(() => {
    if (!open) return []
    const q = open[3].toLowerCase()
    if (open[2] === ':') return [...emojiSuggestions(emojis, unicode, q), ...stickerSuggestions(stickers, q)]
    return people.map(u => ({ key: u.id, label: u.global_name ?? u.username, image: avatarUrl(u, 32), insert: `@${u.global_name ?? u.username}`, mention: `<@${u.id}>` }))
  }, [open?.[0], emojis, unicode, stickers, people]) // eslint-disable-line react-hooks/exhaustive-deps -- the token's text is what matters

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

  function addFiles(list: FileList | File[] | null) {
    if (!list) return
    setFiles(current => [...current, ...Array.from(list)].slice(0, MAX_FILES))
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
    if (!text.trim() && files.length === 0) return
    if (cooldown > 0) { setNote(`Slowmode is on. Wait ${cooldown} seconds.`); return }
    // :name: needs the emoji list; the first message after opening waits for it.
    const set = unicode ?? (text.includes(':') ? await loadUnicodeEmoji().catch(() => null) : null)
    if (set && !unicode) setUnicode(set)
    const content = outgoing(text, set?.byName ?? null)
    const sentText = text
    const sentFiles = files.map(file => spoilers.has(file) ? new File([file], `SPOILER_${file.name}`, { type: file.type }) : file)
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
      setFiles(current => current.length ? current : files)
      setSpoilers(spoilers)
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
        <PlusMenu channelId={channelId} disabled={disabled} onFiles={() => fileInput.current?.click()} />
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
          maxLength={4000}
          aria-label={placeholder}
          onChange={event => onChange(event.target.value)}
          onKeyDown={onKeyDown}
          onPaste={event => { if (event.clipboardData.files.length) { event.preventDefault(); addFiles(event.clipboardData.files) } }}
        />}
        <ComposerTools guildId={guildId} disabled={disabled} picker={picker} setPicker={setPicker}
          onEmoji={text => insert(asTyped(text, customByName))} onGif={url => void onSend(url, [])} onSticker={sendSticker} />
      </div>
      <ComposeFoot problem={error ?? note} status={appLine} typers={typers} slowmode={slowmode} cooldown={cooldown} />
    </div>
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
function ComposeFoot({ problem, status, typers, slowmode, cooldown }: { problem: string | null; status?: string | null; typers: string[]; slowmode: number; cooldown: number }) {
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

// The files going with the message, each with Mark as spoiler (sent as SPOILER_name, as Discord does) and Remove.
function FileChips({ files, spoilers, setSpoilers, setFiles }: {
  files: File[]
  spoilers: Set<File>
  setSpoilers: React.Dispatch<React.SetStateAction<Set<File>>>
  setFiles: React.Dispatch<React.SetStateAction<File[]>>
}) {
  return (
    <div className="exo-dc-files">
      {files.map((file, i) => (
        <span key={`${file.name}-${i}`} className={`exo-dc-file-chip${spoilers.has(file) ? ' is-spoiler' : ''}`}>
          <FileThumb file={file} />
          <span className="exo-dc-file-name">{file.name}</span>
          <button type="button" aria-label={spoilers.has(file) ? `Show ${file.name}` : `Mark ${file.name} as spoiler`} aria-pressed={spoilers.has(file)}
            onClick={() => setSpoilers(set => { const next = new Set(set); if (!next.delete(file)) next.add(file); return next })}><Icons.Spoiler size={14} /></button>
          <button type="button" aria-label={`Remove ${file.name}`} onClick={() => setFiles(list => list.filter((_, j) => j !== i))}>×</button>
        </span>
      ))}
    </div>
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
            {s.glyph ? <span className="exo-dc-suggest-glyph" aria-hidden="true">{s.glyph}</span> : <img src={s.image} alt="" draggable={false} />}
            <span>{s.label}</span>
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
