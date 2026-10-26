import { useEffect, useMemo, useRef, useState } from 'react'
import { host, onHostEvent } from '../../lib/host'
import { avatarUrl, type DiscordUser } from '../../lib/discord'
import { appIcon, commandEntries, loadCommands, runCommand, type AppChoice, type AppCommand, type AppInfo, type AppOption, type CommandEntry } from '../../lib/discordApps'
import { uploadFile } from './Chat'
import { Icons } from './icons'
import { Skeleton } from '../ui/Skeleton'

// Command option types.
const STRING = 3, INTEGER = 4, BOOLEAN = 5, USER = 6, CHANNEL = 7, ROLE = 8, MENTIONABLE = 9, NUMBER = 10, ATTACHMENT = 11

/** Keys the open list or form takes before the message box does (true: taken). */
export type CommandKeys = React.RefObject<((event: React.KeyboardEvent) => boolean) | null>

/**
 * Discord's command list: typing / lists the commands here (the server's apps and your own), each subcommand on its own
 * line with its app; Up and Down move, Enter or Tab chooses, Escape closes.
 */
export function CommandList({ channelId, query, keys, onPick, onClose }: { channelId: string; query: string; keys: CommandKeys; onPick: (entry: CommandEntry) => void; onClose: () => void }) {
  const [entries, setEntries] = useState<CommandEntry[] | null>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const [pick, setPick] = useState(0)
  useEffect(() => {
    let live = true
    void loadCommands(channelId).then(r => { if (live) { setEntries(commandEntries(r.commands, r.apps)); setProblem(r.message) } })
    return () => { live = false }
  }, [channelId])
  const q = query.toLowerCase()
  const shown = useMemo(() => (entries ?? [])
    .filter(e => [e.command.name, ...e.path].join(' ').toLowerCase().includes(q))
    .sort((a, b) => Number(!a.command.name.toLowerCase().startsWith(q)) - Number(!b.command.name.toLowerCase().startsWith(q)))
    .slice(0, 25), [entries, q])
  useEffect(() => { setPick(0) }, [q])
  useEffect(() => {
    keys.current = event => {
      if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); onClose(); return true }
      if (!shown.length) return false
      if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
        event.preventDefault()
        setPick(n => (n + (event.key === 'ArrowDown' ? 1 : shown.length - 1)) % shown.length)
        return true
      }
      if ((event.key === 'Enter' || event.key === 'Tab') && !event.shiftKey) { event.preventDefault(); onPick(shown[Math.min(pick, shown.length - 1)]); return true }
      return false
    }
  })
  useEffect(() => () => { keys.current = null }, [keys])
  return (
    <ul className="exo-dc-suggest exo-dc-commands" role="listbox" aria-label="Commands">
      {entries === null ? <li><Skeleton label="Loading commands" avatar /></li> : null}
      {entries !== null && !shown.length ? <li className="exo-dc-note">{problem ?? (entries.length ? 'No commands match' : 'No apps here have commands')}</li> : null}
      {shown.map((entry, i) => {
        const icon = appIcon(entry.app, 24)
        return (
          <li key={`${entry.command.id}:${entry.path.join(' ')}`} role="option" aria-selected={i === pick}>
            <button type="button" onMouseDown={event => { event.preventDefault(); onPick(entry) }}>
              {icon ? <img src={icon} alt="" draggable={false} /> : <span className="exo-dc-suggest-glyph" aria-hidden="true">/</span>}
              <span className="exo-dc-command-name">/{[entry.command.name, ...entry.path].join(' ')}</span>
              <span className="exo-dc-command-note">{entry.description}</span>
              {entry.app ? <span className="exo-dc-command-app">{entry.app.name}</span> : null}
            </button>
          </li>
        )
      })}
    </ul>
  )
}

type Value = { text: string; value?: string | number | boolean; label?: string; file?: File }

/**
 * A chosen command in the message box, Discord's way: the command as a chip, then a field for each option (the required
 * ones first; optional ones join from the chips after them). Choices and autocomplete list above the field; people, roles
 * and channels are picked by name. Enter sends it, Escape goes back to the message.
 */
export function CommandForm({ entry, channelId, guildId, users, onDone, onCancel }: {
  entry: CommandEntry
  channelId: string
  guildId: string | null
  users: Map<string, DiscordUser>
  onDone: () => void
  onCancel: () => void
}) {
  const required = entry.options.filter(o => o.required)
  const [used, setUsed] = useState<string[]>(() => required.map(o => o.name))
  const [values, setValues] = useState<Record<string, Value>>({})
  const [focus, setFocus] = useState<string | null>(required[0]?.name ?? null)
  const [problem, setProblem] = useState<string | null>(null)
  const [sending, setSending] = useState(false)
  const set = (name: string, value: Value) => { setValues(v => ({ ...v, [name]: value })); setProblem(null) }
  const fields = entry.options.filter(o => used.includes(o.name))
  const optional = entry.options.filter(o => !used.includes(o.name))

  async function send() {
    if (sending) return
    const missing = required.find(o => values[o.name]?.value === undefined && !values[o.name]?.file)
    if (missing) { setFocus(missing.name); setProblem(`Fill in ${missing.name} first.`); return }
    setSending(true)
    const uploads: string[] = []
    const options: { name: string; value: unknown }[] = []
    for (const option of fields) {
      const v = values[option.name]
      if (!v) continue
      if (v.file) {
        const up = await uploadFile(v.file)
        if (typeof up !== 'string') { setSending(false); setProblem(up.message); return }
        options.push({ name: option.name, value: String(uploads.length) })
        uploads.push(up)
      } else if (v.value !== undefined) options.push({ name: option.name, value: v.value })
    }
    const ok = await runCommand(channelId, { commandId: entry.command.id, path: entry.path, options, ...(uploads.length ? { uploads } : {}) })
    setSending(false)
    if (ok) onDone()
  }

  const form = useRef<HTMLDivElement>(null)
  useEffect(() => { if (!required.length) form.current?.focus() }, [required.length])

  return (
    <div ref={form} className="exo-dc-command-form" role="group" tabIndex={-1} aria-label={`/${[entry.command.name, ...entry.path].join(' ')}`}
      onKeyDown={event => {
        if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); onCancel() }
        else if (event.key === 'Enter' && !event.shiftKey && !(event.target instanceof HTMLButtonElement)) { event.preventDefault(); void send() }
      }}>
      <span className="exo-dc-command-chip">/{[entry.command.name, ...entry.path].join(' ')}</span>
      {fields.map(option => (
        <OptionField key={option.name} option={option} entry={entry} channelId={channelId} guildId={guildId} users={users} values={values}
          value={values[option.name]} focused={focus === option.name} onFocus={() => setFocus(option.name)} onChange={value => set(option.name, value)}
          onEnter={() => void send()} onCancel={onCancel} />
      ))}
      {optional.map(option => (
        <button key={option.name} type="button" className="exo-dc-command-more" onClick={() => { setUsed(u => [...u, option.name]); setFocus(option.name) }}>
          <Icons.Plus size={12} />{option.name}
        </button>
      ))}
      {problem ? <span className="exo-dc-command-problem" role="alert">{problem}</span> : null}
      {!fields.length && !problem ? <span className="exo-dc-command-hint">{entry.description}</span> : null}
    </div>
  )
}

type Suggestion = { key: string; label: string; note?: string; image?: string | null; value: string | number; text: string }

type FieldProps = {
  option: AppOption
  entry: CommandEntry
  channelId: string
  guildId: string | null
  users: Map<string, DiscordUser>
  values: Record<string, Value>
  value?: Value
  focused: boolean
  onFocus: () => void
  onChange: (value: Value) => void
  onEnter: () => void
  onCancel: () => void
}

// One option's field: what kind of value it takes decides the control.
function OptionField(props: FieldProps) {
  const { option, value, onFocus, onChange } = props
  if (option.type === BOOLEAN) {
    return (
      <span className="exo-dc-command-field">
        <span className="exo-dc-command-label">{option.name}</span>
        <span className="exo-dc-command-bool" role="radiogroup" aria-label={option.name}>
          {[true, false].map(b => (
            <button key={String(b)} type="button" role="radio" aria-checked={value?.value === b} className={value?.value === b ? 'is-current' : undefined}
              onClick={() => onChange({ text: String(b), value: b })} onFocus={onFocus}>{b ? 'True' : 'False'}</button>
          ))}
        </span>
      </span>
    )
  }
  if (option.type === ATTACHMENT) {
    return (
      <label className="exo-dc-command-field">
        <span className="exo-dc-command-label">{option.name}</span>
        <span className="exo-dc-command-file">{value?.file?.name ?? 'Choose a file'}</span>
        <input type="file" hidden onChange={event => { const file = event.target.files?.[0]; if (file) onChange({ text: file.name, file }) }} />
      </label>
    )
  }
  return <TextField {...props} />
}

// Typed text, with what it suggests above it: Up and Down move, Tab takes one, Enter takes one or sends, Escape cancels.
function TextField({ option, entry, channelId, guildId, users, values, value, focused, onFocus, onChange, onEnter, onCancel }: FieldProps) {
  const input = useRef<HTMLInputElement>(null)
  const [pick, setPick] = useState(0)
  useEffect(() => { if (focused) input.current?.focus() }, [focused])
  const suggestions = useSuggestions(option, entry, channelId, guildId, users, values, value?.text ?? '', focused)
  // Text and numbers are taken as typed (numbers checked by the host); choices, people, roles and channels only from the list.
  const free = (option.type === STRING || option.type === INTEGER || option.type === NUMBER) && !option.choices?.length
  const take = (s: Suggestion) => { onChange({ text: s.text, value: s.value, label: s.label }); setPick(0) }
  const keyDown = (event: React.KeyboardEvent<HTMLInputElement>) => {
    const chosen = suggestions[Math.min(pick, suggestions.length - 1)]
    const act = fieldKey(event, suggestions.length > 0, chosen !== undefined && value?.label !== chosen.label)
    if (!act) return
    event.preventDefault(); event.stopPropagation()
    if (act === 'move') setPick(n => (n + (event.key === 'ArrowDown' ? 1 : suggestions.length - 1)) % suggestions.length)
    else if (act === 'take') take(chosen)
    else if (act === 'send') onEnter()
    else onCancel()
  }
  return (
    <span className="exo-dc-command-field">
      <span className="exo-dc-command-label">{option.name}</span>
      <input ref={input} className="exo-dc-command-input" value={value?.text ?? ''} aria-label={option.description ? `${option.name}: ${option.description}` : option.name}
        size={Math.max(4, Math.min(40, (value?.text ?? '').length + 1))} inputMode={option.type === INTEGER || option.type === NUMBER ? 'decimal' : undefined}
        onFocus={onFocus} onKeyDown={keyDown}
        onChange={event => onChange(free ? { text: event.target.value, value: event.target.value.trim() === '' ? undefined : event.target.value } : { text: event.target.value })} />
      {focused && suggestions.length ? (
        <ul className="exo-dc-suggest exo-dc-command-choices" role="listbox" aria-label={option.name}>
          {suggestions.map((s, i) => (
            <li key={s.key} role="option" aria-selected={i === pick}>
              <button type="button" onMouseDown={event => { event.preventDefault(); take(s) }}>
                {s.image ? <img src={s.image} alt="" draggable={false} /> : null}
                <span>{s.label}</span>
                {s.note ? <span className="exo-dc-command-note">{s.note}</span> : null}
              </button>
            </li>
          ))}
        </ul>
      ) : null}
      {focused && option.description && !suggestions.length ? <span className="exo-dc-command-hint">{option.description}</span> : null}
    </span>
  )
}

function fieldKey(event: React.KeyboardEvent, listed: boolean, takes: boolean): 'move' | 'take' | 'send' | 'cancel' | null {
  if (listed && (event.key === 'ArrowDown' || event.key === 'ArrowUp')) return 'move'
  if (listed && event.key === 'Tab' && !event.shiftKey) return 'take'
  if (event.key === 'Enter' && !event.shiftKey) return takes ? 'take' : 'send'
  return event.key === 'Escape' ? 'cancel' : null
}

// What a field suggests: its choices, the app's autocomplete answers (asked as you type), or people, roles and channels.
function useSuggestions(option: AppOption, entry: CommandEntry, channelId: string, guildId: string | null, users: Map<string, DiscordUser>,
  values: Record<string, Value>, text: string, focused: boolean): Suggestion[] {
  const [found, setFound] = useState<Suggestion[]>([])
  const q = text.trim().toLowerCase()
  const others = JSON.stringify(Object.entries(values).filter(([n, v]) => n !== option.name && v.value !== undefined).map(([name, v]) => ({ name, value: v.value })))
  useEffect(() => {
    if (!focused) return
    let live = true
    let stop = () => { }
    const run = async () => {
      if (option.autocomplete) {
        const options = [...JSON.parse(others) as { name: string; value: unknown }[], { name: option.name, value: text }]
        const result = await host.discordApps(channelId, 'autocomplete', { commandId: entry.command.id, path: entry.path, options, focused: option.name }).catch(() => null)
        if (!live || !result?.ok || !result.nonce) return
        const nonce = result.nonce
        stop = onHostEvent('discord.interaction', event => {
          if (event.kind === 'choices' && event.nonce === nonce && live) setFound((event.choices ?? []).map(choiceSuggestion))
        })
        return
      }
      const list: Suggestion[] = []
      if ((option.type === USER || option.type === MENTIONABLE)) {
        const people = guildId ? await host.discordMentionable(guildId, q, q.length > 0).then(r => r.users).catch(() => [] as DiscordUser[]) : [...users.values()]
        list.push(...people.filter(u => (u.global_name ?? u.username).toLowerCase().includes(q) || u.username.toLowerCase().includes(q))
          .map(u => ({ key: u.id, label: u.global_name || u.username, note: u.username, image: avatarUrl(u, 24), value: u.id, text: u.global_name || u.username })))
      }
      if ((option.type === ROLE || option.type === MENTIONABLE || option.type === CHANNEL) && guildId) {
        const place = await host.discordChannels(guildId).catch(() => null)
        if (option.type !== CHANNEL) {
          list.push(...Object.entries(place?.roles ?? {}).filter(([, r]) => r.name.toLowerCase().includes(q))
            .map(([id, r]) => ({ key: id, label: `@${r.name}`, value: id, text: `@${r.name}` })))
        } else {
          list.push(...(place?.channels ?? []).filter(c => c.kind !== 'category' && (!option.channel_types?.length || option.channel_types.includes(c.type)) && c.name.toLowerCase().includes(q))
            .map(c => ({ key: c.id, label: `#${c.name}`, value: c.id, text: `#${c.name}` })))
        }
      }
      if (live) setFound(list.slice(0, 10))
    }
    const wait = window.setTimeout(() => void run(), option.autocomplete ? 300 : 120)
    return () => { live = false; window.clearTimeout(wait); stop() }
  }, [focused, option, entry, channelId, guildId, users, q, text, others])
  if (option.choices?.length) return option.choices.filter(c => !q || c.name.toLowerCase().includes(q)).slice(0, 25).map(choiceSuggestion)
  return focused ? found : []
}

function choiceSuggestion(choice: AppChoice): Suggestion {
  return { key: String(choice.value), label: choice.name, value: choice.value, text: choice.name }
}

/** The user (2) or message (3) commands here, for a person's or a message's Apps menu (asked when the menu opens). */
export function useContextCommands(channelId: string | null, type: 2 | 3) {
  const [list, setList] = useState<{ command: AppCommand; app: AppInfo | null }[]>([])
  useEffect(() => {
    if (!channelId) return
    let live = true
    void loadCommands(channelId).then(r => {
      if (!live) return
      const apps = new Map(r.apps.map(a => [a.id, a]))
      setList(r.commands.filter(c => c.type === type).map(command => ({ command, app: apps.get(command.application_id) ?? null })))
    })
    return () => { live = false }
  }, [channelId, type])
  return list
}

/** A message's More menu: Apps opens its message commands in place; choosing one runs it on this message. */
export function MessageApps({ channelId, messageId, onRun }: { channelId: string; messageId: string; onRun: () => void }) {
  const [open, setOpen] = useState(false)
  const list = useContextCommands(channelId, 3)
  if (!list.length) return null
  return (
    <>
      <button type="button" role="menuitem" aria-expanded={open} onClick={() => setOpen(o => !o)}>
        <Icons.Apps size={16} />Apps<Icons.Crumb size={14} className={`exo-dc-ctx-arrow${open ? ' is-open' : ''}`} />
      </button>
      {open ? list.map(({ command, app }) => {
        const icon = appIcon(app, 16)
        return (
          <button key={command.id} type="button" role="menuitem" className="exo-dc-menu-indent"
            onClick={() => { onRun(); void runCommand(channelId, { commandId: command.id, targetId: messageId }) }}>
            {icon ? <img className="exo-dc-menu-icon" src={icon} alt="" draggable={false} /> : null}{command.name}
          </button>
        )
      }) : null}
    </>
  )
}
