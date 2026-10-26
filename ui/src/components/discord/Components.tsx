import { useEffect, useRef, useState } from 'react'
import { host } from '../../lib/host'
import { avatarUrl, type DiscordAttachment, type DiscordMessage, type DiscordUser } from '../../lib/discord'
import { COMPONENTS_V2, failureLine, followInteraction, type AppComponent, type AppSelectOption } from '../../lib/discordApps'
import { useLayer } from './admin'
import { Icons } from './icons'
import { showImage } from './extras'
import { emojiUrl, Markdown, open, type MarkdownContext } from './markdown'
import { useDismiss } from '../../lib/dismiss'
import { Spinner } from '../ui/Spinner'

// Discord's component types.
const ROW = 1, BUTTON = 2, STRING_SELECT = 3, USER_SELECT = 5, ROLE_SELECT = 6, MENTIONABLE_SELECT = 7, CHANNEL_SELECT = 8,
  SECTION = 9, TEXT = 10, THUMBNAIL = 11, GALLERY = 12, FILE = 13, SEPARATOR = 14, CONTAINER = 17
const SELECTS = new Set([STRING_SELECT, USER_SELECT, ROLE_SELECT, MENTIONABLE_SELECT, CHANNEL_SELECT])
const LINK = 5, PREMIUM = 6
const STYLE = ['', 'is-primary', 'is-secondary', 'is-success', 'is-danger', 'is-link', 'is-secondary']

type Press = (component: AppComponent, values?: string[]) => void
type State = { busy: string | null; failed: string | null }

/**
 * An app's components on a message, drawn as Discord draws them: rows of buttons and selects, and the layout ones
 * (sections, text, thumbnails, galleries, files, separators, containers). A press or a choice is sent as Discord's
 * client sends it; the control waits until the app answers, and "This interaction failed" shows when it doesn't.
 */
export function MessageComponents({ message, ctx }: { message: DiscordMessage; ctx: MarkdownContext }) {
  const [state, setState] = useState<State>({ busy: null, failed: null })
  const stop = useRef<(() => void) | null>(null)
  useEffect(() => () => stop.current?.(), [])
  const press: Press = (component, values) => {
    if (!component.custom_id || state.busy) return
    const id = component.custom_id
    setState({ busy: id, failed: null })
    void host.discordApps(message.channel_id, 'component', {
      messageId: message.id, applicationId: message.application_id ?? message.author.id, messageFlags: message.flags ?? 0,
      componentType: component.type, customId: id, ...(values ? { values } : {}),
    }).catch(() => ({ ok: false, message: null, nonce: null })).then(result => {
      if (!result.ok || !result.nonce) { setState({ busy: null, failed: result.message ?? 'This interaction failed' }); return }
      stop.current = followInteraction(result.nonce, (ok, event) => setState({ busy: null, failed: ok ? null : failureLine(event) }))
    })
  }
  return (
    <div className={`exo-dc-components${(message.flags ?? 0) & COMPONENTS_V2 ? ' is-v2' : ''}`}>
      {message.components!.map((c, i) => <Component key={c.id ?? i} component={c} message={message} ctx={ctx} state={state} press={press} />)}
      {state.failed ? <p className="exo-dc-interaction-failed" role="alert">{state.failed}</p> : null}
    </div>
  )
}

type Props = { component: AppComponent; message: DiscordMessage; ctx: MarkdownContext; state: State; press: Press }

function Component(props: Props) {
  const { component, ctx, state, press } = props
  if (component.type === ROW) return <div className="exo-dc-cv-row">{children(props, component.components)}</div>
  if (component.type === BUTTON) return <AppButton component={component} busy={state.busy === component.custom_id} locked={state.busy !== null} press={press} />
  if (SELECTS.has(component.type)) {
    return <AppSelect component={component} guildId={ctx.guildId} disabled={state.busy !== null} busy={state.busy === component.custom_id}
      onCommit={values => press(component, values)} />
  }
  return <Layout {...props} />
}

function children(props: Props, list: AppComponent[] | undefined) {
  return list?.map((c, i) => <Component key={c.id ?? i} {...props} component={c} />)
}

// The layout components (Components V2): sections, text, thumbnails, galleries, files, separators and containers.
function Layout(props: Props) {
  const { component, message, ctx } = props
  switch (component.type) {
    case SECTION:
      return (
        <div className="exo-dc-cv-section">
          <div className="exo-dc-cv-section-text">{children(props, component.components)}</div>
          {component.accessory ? <div className="exo-dc-cv-accessory">{children(props, [component.accessory])}</div> : null}
        </div>
      )
    case TEXT:
      return <Markdown text={component.content ?? ''} ctx={ctx} />
    case THUMBNAIL:
      return component.media ? <Picture media={component.media} spoiler={component.spoiler} label={component.description} className="exo-dc-cv-thumb" /> : null
    case GALLERY:
      return (
        <div className={`exo-dc-cv-gallery is-${Math.min(component.items?.length ?? 1, 4)}`}>
          {component.items?.map((item, i) => <Picture key={i} media={item.media} spoiler={item.spoiler} label={item.description} />)}
        </div>
      )
    case FILE:
      return <FileRow file={component.file?.url} attachments={message.attachments} />
    case SEPARATOR:
      return <hr className={`exo-dc-cv-sep${component.divider === false ? ' is-blank' : ''}${component.spacing === 2 ? ' is-large' : ''}`} />
    case CONTAINER:
      return (
        <div className={`exo-dc-cv-container${component.spoiler ? ' is-spoiler' : ''}`}
          style={component.accent_color != null ? { borderLeftColor: `#${component.accent_color.toString(16).padStart(6, '0')}` } : undefined}>
          {children(props, component.components)}
        </div>
      )
    default:
      return null
  }
}

function ComponentEmoji({ emoji }: { emoji: AppComponent['emoji'] }) {
  if (!emoji) return null
  return emoji.id ? <img className="exo-dc-cv-emoji" src={emojiUrl(emoji.id, emoji.animated === true, 32)} alt={`:${emoji.name}:`} draggable={false} />
    : <span className="exo-dc-cv-emoji" aria-hidden="true">{emoji.name}</span>
}

// A button in its style; a link button opens its page in the browser, as Discord's does.
function AppButton({ component, busy, locked, press }: { component: AppComponent; busy: boolean; locked: boolean; press: Press }) {
  const style = component.style ?? 2
  const disabled = component.disabled === true || style === PREMIUM || (style !== LINK && locked)
  return (
    <button type="button" className={`exo-dc-cv-button ${STYLE[style] ?? 'is-secondary'}${busy ? ' is-busy' : ''}`} disabled={disabled}
      onClick={() => { if (style === LINK) { if (component.url) open(component.url) } else press(component) }}>
      {busy ? <Spinner label="Waiting for the app" /> : null}
      <ComponentEmoji emoji={component.emoji} />
      {component.label ? <span>{component.label}</span> : null}
      {style === LINK ? <Icons.Open size={14} /> : null}
    </button>
  )
}

function Picture({ media, spoiler, label, className }: { media: { url: string; proxy_url?: string; width?: number | null; height?: number | null; content_type?: string }; spoiler?: boolean; label?: string | null; className?: string }) {
  const [shown, setShown] = useState(!spoiler)
  const src = media.proxy_url ?? media.url
  const video = media.content_type?.startsWith('video/') ?? /\.(mp4|webm|mov)(\?|$)/i.test(media.url)
  return (
    <button type="button" className={`exo-dc-cv-media${className ? ` ${className}` : ''}${shown ? '' : ' is-hidden'}`} aria-label={shown ? label ?? 'Open image' : 'Spoiler, show'}
      onClick={() => { if (!shown) setShown(true); else showImage(src, media.url, video) }}>
      {video ? <video src={src} muted playsInline preload="metadata" /> : <img src={src} alt={label ?? ''} loading="lazy" draggable={false} />}
      {shown ? null : <span className="exo-dc-hidden-label">SPOILER</span>}
    </button>
  )
}

// A file component points to one of the message's attachments (attachment://name).
function FileRow({ file, attachments }: { file?: string; attachments: DiscordAttachment[] }) {
  const name = file?.replace(/^attachment:\/\//, '')
  const found = attachments.find(a => a.filename === name)
  if (!found) return null
  return (
    <button type="button" className="exo-dc-file" onClick={() => open(found.url)} aria-label={`${found.filename}, open in your browser`}>
      <span className="exo-dc-file-name">{found.filename}</span>
    </button>
  )
}

type Choice = { value: string; label: string; description?: string; image?: string | null; emoji?: AppSelectOption['emoji']; kind?: 'user' | 'role' | 'channel' }

// The people, roles or channels a select offers, matching what was typed (people are asked of Discord as you type).
function useChoices(component: AppComponent, guildId: string | null, query: string, open: boolean): Choice[] {
  const [found, setFound] = useState<Choice[]>([])
  useEffect(() => {
    if (!open || component.type === STRING_SELECT) return
    let live = true
    const q = query.trim().toLowerCase()
    const people = component.type === USER_SELECT || component.type === MENTIONABLE_SELECT
    const roles = component.type === ROLE_SELECT || component.type === MENTIONABLE_SELECT
    const load = async () => {
      const list: Choice[] = []
      if (people && guildId) {
        const users = await host.discordMentionable(guildId, q, q.length > 0).then(r => r.users).catch(() => [] as DiscordUser[])
        list.push(...users.map(u => ({ value: u.id, label: u.global_name || u.username, description: u.username, image: avatarUrl(u, 24), kind: 'user' as const })))
      }
      if ((roles || component.type === CHANNEL_SELECT) && guildId) {
        const place = await host.discordChannels(guildId).catch(() => null)
        if (roles) list.push(...Object.entries(place?.roles ?? {}).filter(([, r]) => r.name !== '@everyone' && r.name.toLowerCase().includes(q)).map(([id, r]) => ({ value: id, label: `@${r.name}`, kind: 'role' as const })))
        if (component.type === CHANNEL_SELECT) {
          const types = component.channel_types
          list.push(...(place?.channels ?? []).filter(c => c.kind !== 'category' && (!types?.length || types.includes(c.type)) && c.name.toLowerCase().includes(q))
            .map(c => ({ value: c.id, label: c.name, kind: 'channel' as const })))
        }
      }
      if (live) setFound(list.slice(0, 25))
    }
    const wait = window.setTimeout(() => void load(), q ? 250 : 0)
    return () => { live = false; window.clearTimeout(wait) }
  }, [component.type, component.channel_types, guildId, query, open])
  if (component.type !== STRING_SELECT) return found
  const q = query.trim().toLowerCase()
  return (component.options ?? []).filter(o => !q || o.label.toLowerCase().includes(q)).map(o => ({ value: o.value, label: o.label, description: o.description, emoji: o.emoji }))
}

/**
 * A select, Discord's way: the placeholder (or what's chosen) in a field that opens the choices; one choice is taken at once,
 * several when the list closes. In a form (onChange) the choice is only kept until Submit.
 */
export function AppSelect({ component, guildId, disabled, busy, onCommit, onChange }: {
  component: AppComponent
  guildId: string | null
  disabled?: boolean
  busy?: boolean
  onCommit?: (values: string[]) => void
  onChange?: (values: string[]) => void
}) {
  const initial = component.type === STRING_SELECT ? (component.options ?? []).filter(o => o.default).map(o => o.value) : (component.default_values ?? []).map(v => v.id)
  const [values, setValues] = useState<string[]>(initial)
  const [labels, setLabels] = useState<Map<string, string>>(() => new Map((component.options ?? []).map(o => [o.value, o.label])))
  const [open, setOpen] = useState(false)
  const [query, setQuery] = useState('')
  const box = useRef<HTMLDivElement>(null)
  const max = Math.max(1, component.max_values ?? 1)
  const choices = useChoices(component, guildId, query, open)
  const close = (commit: boolean) => {
    setOpen(false)
    setQuery('')
    if (commit && max > 1 && values.length >= (component.min_values ?? 1)) onCommit?.(values)
  }
  useLayer(box, () => close(true), open)
  useDismiss(box, () => close(true), { on: open })
  const choose = (choice: Choice) => {
    setLabels(map => new Map(map).set(choice.value, choice.label))
    const next = max === 1 ? [choice.value] : values.includes(choice.value) ? values.filter(v => v !== choice.value) : values.length < max ? [...values, choice.value] : values
    setValues(next)
    onChange?.(next)
    if (max === 1) { setOpen(false); setQuery(''); onCommit?.(next) }
  }
  const shown = values.map(v => labels.get(v) ?? v).join(', ')
  const searchable = component.type !== STRING_SELECT || (component.options?.length ?? 0) > 8
  return (
    <div className={`exo-dc-cv-select${open ? ' is-open' : ''}`} ref={box}>
      <button type="button" className="exo-dc-cv-select-field" aria-haspopup="listbox" aria-expanded={open} disabled={disabled || component.disabled}
        onClick={() => (open ? close(true) : setOpen(true))}>
        <span className={shown ? undefined : 'is-placeholder'}>{shown || component.placeholder || 'Make a selection'}</span>
        {busy ? <Spinner label="Waiting for the app" /> : <Icons.Crumb size={16} className="exo-dc-cv-select-arrow" />}
      </button>
      {open ? (
        <div className="exo-dc-cv-select-list exo-glass is-panel" role="listbox" aria-multiselectable={max > 1} aria-label={component.placeholder ?? 'Choices'}>
          {searchable ? <input className="exo-dc-modal-input" value={query} placeholder="Search" aria-label="Search" autoFocus onChange={event => setQuery(event.target.value)} /> : null}
          {choices.map(choice => (
            <button key={choice.value} type="button" role="option" aria-selected={values.includes(choice.value)} onClick={() => choose(choice)}>
              {choice.image ? <img src={choice.image} alt="" draggable={false} /> : choice.emoji ? <ComponentEmoji emoji={choice.emoji} /> : choice.kind === 'channel' ? <Icons.Hash size={16} /> : null}
              <span className="exo-dc-cv-option">
                <span>{choice.label}</span>
                {choice.description ? <span className="exo-dc-cv-option-note">{choice.description}</span> : null}
              </span>
              {values.includes(choice.value) ? <Icons.Check size={16} className="exo-dc-ctx-check" /> : null}
            </button>
          ))}
          {choices.length === 0 ? <p className="exo-dc-note">{guildId || component.type === STRING_SELECT ? 'No results' : 'Choose in a server'}</p> : null}
        </div>
      ) : null}
    </div>
  )
}
