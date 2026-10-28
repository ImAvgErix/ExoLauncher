import { useEffect, useMemo, useRef, useState } from 'react'
import { host, type DiscordReportKind, type DiscordReportMenu, type DiscordReportNode } from '../../lib/host'
import { openLink } from '../../lib/music'
import { useLayer } from './admin'
import { Icons } from './icons'
import { Markdown, type MarkdownContext } from './markdown'
import { ModalBack } from './SettingsLayer'
import { Switch } from '../Switch'
import { Spinner } from '../ui/Spinner'
import { ErrorNote } from '../ui/ErrorNote'

/** What's reported: Discord's kind and the ids it wants, how it shows on the steps, and who's behind it (Block, Ignore). */
export type ReportTarget = {
  kind: DiscordReportKind
  ids: Record<string, string>
  preview: { name: string; text?: string | null; image?: string | null }
  userId?: string | null
  /** A moderator's report of a message: the last step offers Delete Message. */
  canDelete?: boolean
}

/** Opens Discord's report for a message, a person, a server or an event, over wherever the room is. */
export function showReport(target: ReportTarget) {
  window.dispatchEvent(new CustomEvent('exo-dc-report', { detail: target }))
}

export function ReportHost() {
  const [target, setTarget] = useState<ReportTarget | null>(null)
  useEffect(() => {
    const show = (event: Event) => setTarget((event as CustomEvent<ReportTarget>).detail)
    window.addEventListener('exo-dc-report', show)
    return () => window.removeEventListener('exo-dc-report', show)
  }, [])
  return target ? <ReportForm key={JSON.stringify(target.ids)} target={target} onClose={() => setTarget(null)} /> : null
}

type Step = { node: number; picked?: string }
type Answers = Record<string, string[]>
const PREVIEW: Record<DiscordReportKind, string> = {
  message: 'message_preview', first_dm: 'message_preview', user: 'user_preview', guild: 'guild_preview',
  guild_scheduled_event: 'guild_scheduled_event_preview', application: 'app_preview',
}

/**
 * Discord's report as Discord asks it: its steps come from Discord (what happened, then more about it), each a list to
 * pick from or a few questions, then Report; the last step offers Block and Ignore for the person, as Discord's does.
 */
function ReportForm({ target, onClose }: { target: ReportTarget; onClose: () => void }) {
  const [menu, setMenu] = useState<DiscordReportMenu | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [path, setPath] = useState<Step[]>([])
  const [answers, setAnswers] = useState<Answers>({})
  const [sending, setSending] = useState(false)
  // sent or failed once the report went; a report goes once per dialog.
  const [result, setResult] = useState<'sent' | 'failed' | null>(null)
  const once = useRef(false)
  const box = useRef<HTMLDivElement>(null)
  useLayer(box, onClose)
  useEffect(() => {
    let live = true
    host.discordReport(target.kind, 'menu').then(result => {
      if (!live) return
      if (!result.ok || !result.menu?.nodes) { setError(result.message ?? "Discord didn't send its report questions. Try again."); return }
      setMenu(result.menu)
      setPath([{ node: result.menu.root_node_id }])
    }, () => { if (live) setError("Discord didn't send its report questions. Try again.") })
    return () => { live = false }
  }, [target.kind])

  const here = path.at(-1)
  const node = menu && here ? menu.nodes[String(here.node)] : undefined
  const ended = result !== null
  const go = (to: number | null | undefined, picked?: string) => {
    if (to == null || !menu?.nodes[String(to)]) return
    setPath(steps => [...steps.slice(0, -1), { ...steps[steps.length - 1], picked }, { node: to }])
  }

  async function send() {
    if (!menu || !node || once.current) return
    once.current = true
    setSending(true)
    setError(null)
    const breadcrumbs = path.map(step => step.node)
    const asked = new Set(path.flatMap(step => menu.nodes[String(step.node)]?.elements ?? []).filter(e => e.should_submit_data).map(e => e.name))
    const elements = Object.fromEntries(Object.entries(answers).filter(([name, values]) => asked.has(name) && values.length))
    const result = await host.discordReport(target.kind, 'send', {
      version: menu.version, variant: menu.variant, language: menu.language ?? 'en', breadcrumbs, elements, ids: target.ids,
    }).catch(() => ({ ok: false, message: null }))
    setSending(false)
    if (!result.ok) setError(result.message ?? "Discord didn't take the report.")
    setResult(result.ok ? 'sent' : 'failed')
    // A step that sends itself is its own thank-you (I don't like it): it stays, unless the report failed.
    if (!result.ok || !node.is_auto_submit) setPath(steps => [...steps, { node: result.ok ? menu.success_node_id : menu.fail_node_id }])
  }

  // A step Discord sends on its own (nothing to answer) goes as soon as it's reached.
  const auto = node?.is_auto_submit === true && !ended
  useEffect(() => { if (auto) void send() }, [auto]) // eslint-disable-line react-hooks/exhaustive-deps

  return (
    <ModalBack top onClose={onClose}>
      <div ref={box} className="exo-dc-modal exo-dc-app-form exo-dc-report" role="dialog" aria-modal="true" aria-label={node?.header ?? 'Report'} tabIndex={-1}
        onClick={event => event.stopPropagation()}>
        {!menu ? (error ? <ErrorNote className="is-bad" message={error} /> : <Spinner label="Loading" />) : node ? (
          <ReportStep key={`${path.length}-${here!.node}`} node={node} path={path} target={target} answers={answers} failed={result === 'failed'} ended={ended}
            sending={sending} error={error} onAnswer={(name, values) => setAnswers(a => ({ ...a, [name]: values }))} onPick={go} />
        ) : <ErrorNote className="is-bad" message="Discord's report has a step Exo can't show." />}
        <ReportButtons node={node} answers={answers} ended={ended} sending={sending} canGoBack={path.length > 1 && !ended && !sending}
          onBack={() => setPath(steps => steps.slice(0, -1).map((s, i, all) => i === all.length - 1 ? { node: s.node } : s))}
          onNext={() => go(node?.button?.target)} onSend={() => void send()} onClose={onClose} />
      </div>
    </ModalBack>
  )
}

function ReportStep({ node, path, target, answers, failed, ended, sending, error, onAnswer, onPick }: {
  node: DiscordReportNode
  path: Step[]
  target: ReportTarget
  answers: Answers
  failed: boolean
  ended: boolean
  sending: boolean
  error: string | null
  onAnswer: (name: string, values: string[]) => void
  onPick: (to: number, picked: string) => void
}) {
  const ctx = useMemo<MarkdownContext>(() => ({ users: new Map(), channelName: () => null, meId: null, guildId: null, colors: new Map() }), [])
  return (
    <>
      <header className="exo-dc-report-head">
        {ended ? <span className={`exo-dc-report-mark${failed ? ' is-bad' : ''}`} aria-hidden="true">{failed ? <Icons.Close size={22} /> : <Icons.Check size={22} />}</span> : null}
        <strong>{node.header}</strong>
        {node.subheader ? <p>{node.subheader}</p> : null}
      </header>
      {node.info ? <p className="exo-dc-report-info">{node.info}</p> : null}
      {node.elements.map((element, i) => {
        const key = `${element.type}-${i}`
        // A step names every kind's preview it could show (a message's and its app's): only what's reported shows.
        if (element.type === PREVIEW[target.kind]) return <ReportPreview key={key} preview={target.preview} />
        switch (element.type) {
          case 'breadcrumbs': return <Picked key={key} path={path} />
          case 'checkbox': return <Checks key={key} element={element} picked={answers[element.name] ?? []} onAnswer={values => onAnswer(element.name, values)} />
          case 'dropdown': return <Dropdown key={key} element={element} picked={answers[element.name]?.[0] ?? ''} onAnswer={value => onAnswer(element.name, value ? [value] : [])} />
          case 'free_text': return <FreeText key={key} element={element} said={answers[element.name]?.[0] ?? ''} onAnswer={text => onAnswer(element.name, text.trim() ? [text] : [])} />
          case 'text': return <TextBlock key={key} data={element.data} ctx={ctx} />
          case 'external_link': return <ExternalLink key={key} data={element.data} />
          case 'text_line_resource': return <TextLine key={key} data={element.data} />
          case 'block_users': case 'ignore_users': case 'delete_message': return <AfterAction key={key} kind={element.type} target={target} />
          default: return null
        }
      })}
      {node.children.length ? (
        <div className="exo-dc-options exo-dc-report-choices">
          {node.children.map(([name, to]) => (
            <button key={`${to}-${name}`} type="button" className="exo-dc-option" disabled={sending} onClick={() => onPick(to, name)}>
              <span>{name}</span><Icons.Next size={16} />
            </button>
          ))}
        </div>
      ) : null}
      {error ? <ErrorNote className="is-bad" message={error} /> : null}
    </>
  )
}

// The step's buttons: Back, then Discord's own (Next, Report, Done or Cancel); a step of choices has only Back and Cancel.
function ReportButtons({ node, answers, ended, sending, canGoBack, onBack, onNext, onSend, onClose }: {
  node: DiscordReportNode | undefined
  answers: Answers
  ended: boolean
  sending: boolean
  canGoBack: boolean
  onBack: () => void
  onNext: () => void
  onSend: () => void
  onClose: () => void
}) {
  const button = node?.button?.type
  // A step whose checkboxes need one ticked, or whose text has to match Discord's pattern, waits for it.
  const ready = !node || (!(node.is_multi_select_required && !node.elements.some(e => e.type === 'checkbox' && (answers[e.name]?.length ?? 0) > 0))
    && node.elements.every(e => e.type !== 'free_text' || fits(e.data, answers[e.name]?.[0] ?? '')))
  return (
    <div className="exo-dc-modal-row">
      {canGoBack ? <button type="button" className="exo-dc-tab" onClick={onBack}>Back</button> : null}
      {ended || button === 'done'
        ? <button type="button" className="exo-set-btn is-primary" onClick={onClose}>Done</button>
        : <button type="button" className="exo-dc-tab" onClick={onClose}>Cancel</button>}
      {!ended && button === 'next' ? <button type="button" className="exo-set-btn is-primary" disabled={!ready} onClick={onNext}>Next</button> : null}
      {!ended && button === 'submit' ? (
        <button type="button" className="exo-set-btn is-primary" disabled={!ready || sending} onClick={onSend}>
          {sending ? <Spinner label="Sending" /> : null}Report
        </button>
      ) : null}
    </div>
  )
}

function fits(data: unknown, said: string) {
  const free = data as { pattern?: string | null; character_limit?: number } | null
  if (free?.character_limit && said.length > free.character_limit) return false
  if (!said || !free?.pattern) return true
  try { return new RegExp(free.pattern).test(said) } catch { return true }
}

// What's reported, as Discord shows it at the top of a step: the message and who sent it, the person, or the server.
function ReportPreview({ preview }: { preview: ReportTarget['preview'] }) {
  return (
    <div className="exo-dc-report-preview">
      {preview.image ? <img src={preview.image} alt="" draggable={false} /> : null}
      <span>
        <strong>{preview.name}</strong>
        {preview.text ? <span>{preview.text.length > 300 ? `${preview.text.slice(0, 300)}…` : preview.text}</span> : null}
      </span>
    </div>
  )
}

// The choices picked on the way (Discord's summary before Report).
function Picked({ path }: { path: Step[] }) {
  const picked = path.map(step => step.picked).filter((p): p is string => !!p)
  if (!picked.length) return null
  return <ol className="exo-dc-report-picked">{picked.map((p, i) => <li key={i}>{p}</li>)}</ol>
}

function Checks({ element, picked, onAnswer }: { element: { name: string; data: unknown }; picked: string[]; onAnswer: (values: string[]) => void }) {
  const options = Array.isArray(element.data) ? (element.data as unknown[]).filter((o): o is string[] => Array.isArray(o) && typeof o[0] === 'string') : []
  const toggle = (name: string) => onAnswer(picked.includes(name) ? picked.filter(p => p !== name) : [...picked, name])
  return (
    <div className="exo-dc-options" role="group">
      {options.map(([name, label, description]) => (
        <span key={name} className="exo-dc-app-check exo-dc-report-switch">
          <span className="exo-dc-report-check">{label ?? name}{description ? <span>{description}</span> : null}</span>
          <Switch checked={picked.includes(name)} label={label ?? name} onChange={() => toggle(name)} />
        </span>
      ))}
    </div>
  )
}

function Dropdown({ element, picked, onAnswer }: { element: { name: string; data: unknown }; picked: string; onAnswer: (value: string) => void }) {
  const data = element.data as { title?: string; options?: { value: string; label: string }[] } | null
  const id = `exo-dc-report-${element.name}`
  return (
    <div className="exo-dc-app-field">
      {data?.title ? <label className="exo-dc-side-label" htmlFor={id}>{data.title}</label> : null}
      <select id={id} className="exo-dc-modal-input" value={picked} onChange={event => onAnswer(event.target.value)}>
        <option value="" />
        {(data?.options ?? []).map(o => <option key={o.value} value={o.value}>{o.label}</option>)}
      </select>
    </div>
  )
}

function FreeText({ element, said, onAnswer }: { element: { name: string; data: unknown }; said: string; onAnswer: (text: string) => void }) {
  const data = element.data as { title?: string | null; subtitle?: string | null; placeholder?: string | null; rows?: number; character_limit?: number } | null
  const id = `exo-dc-report-${element.name}`
  return (
    <div className="exo-dc-app-field">
      {data?.title ? <label className="exo-dc-side-label" htmlFor={id}>{data.title}</label> : null}
      {data?.subtitle ? <p className="exo-dc-report-sub">{data.subtitle}</p> : null}
      <textarea id={id} className="exo-dc-modal-input" rows={Math.min(Math.max(data?.rows ?? 3, 2), 8)} maxLength={data?.character_limit || 4000}
        placeholder={data?.placeholder ?? undefined} value={said} onChange={event => onAnswer(event.target.value)} />
    </div>
  )
}

function TextBlock({ data, ctx }: { data: unknown; ctx: MarkdownContext }) {
  const text = data as { header?: string; body?: string } | null
  if (!text?.header && !text?.body) return null
  return (
    <div className="exo-dc-report-text">
      {text.header ? <strong>{text.header}</strong> : null}
      {text.body ? <Markdown text={text.body} ctx={ctx} /> : null}
    </div>
  )
}

// A page of Discord's (Safety Center, a support line's site): it opens in the browser.
function ExternalLink({ data }: { data: unknown }) {
  const link = data as { url?: string; link_text?: string; link_description?: string | null } | null
  if (!link?.url || !/^https:\/\//i.test(link.url)) return null
  return (
    <button type="button" className="exo-dc-option exo-dc-report-link" onClick={() => openLink(link.url!)}>
      <span className="exo-dc-report-check">{link.link_text ?? link.url}{link.link_description ? <span>{link.link_description}</span> : null}</span>
      <Icons.Open size={16} />
    </button>
  )
}

// A support line to text (Discord's Crisis Text Line).
function TextLine({ data }: { data: unknown }) {
  const line = data as { title?: string; body?: string; sms?: string } | null
  if (!line?.title && !line?.body) return null
  return (
    <div className="exo-dc-report-text">
      {line.title ? <strong>{line.title}</strong> : null}
      {line.body ? <p>{line.body}</p> : null}
      {line.sms ? <p>Text {line.sms}</p> : null}
    </div>
  )
}

type After = 'block_users' | 'ignore_users' | 'delete_message'
const AFTER: Record<After, { label: string; done: string; busy: string }> = {
  block_users: { label: 'Block', done: 'Blocked', busy: 'Blocking' },
  ignore_users: { label: 'Ignore', done: 'Ignored', busy: 'Ignoring' },
  delete_message: { label: 'Delete message', done: 'Message deleted', busy: 'Deleting' },
}

// Block or Ignore the person behind it, or a moderator's Delete Message, from the last step, as Discord offers.
function AfterAction({ kind, target }: { kind: After; target: ReportTarget }) {
  const [state, setState] = useState<'idle' | 'busy' | 'done' | 'failed'>('idle')
  const deleting = kind === 'delete_message'
  if (deleting ? !target.canDelete : !target.userId) return null
  const run = () => {
    setState('busy')
    const work = deleting ? host.discordDelete(target.ids.channel_id, target.ids.message_id)
      : host.discordFriend(kind === 'block_users' ? 'block' : 'ignore', { userId: target.userId! })
    void work.then(r => setState(r.ok ? 'done' : 'failed'), () => setState('failed'))
  }
  const words = AFTER[kind]
  const name = target.kind === 'user' && !deleting ? ` ${target.preview.name}` : ''
  return (
    <button type="button" className={`exo-dc-option exo-dc-report-link${kind === 'ignore_users' ? '' : ' is-danger'}`} disabled={state === 'busy' || state === 'done'} onClick={run}>
      <span className="exo-dc-report-check">
        {state === 'done' ? words.done : `${words.label}${name}`}
        {state === 'failed' ? <span className="is-bad">Discord didn't take that. Try again.</span> : null}
      </span>
      {state === 'busy' ? <Spinner label={words.busy} /> : state === 'done' ? <Icons.Check size={16} /> : null}
    </button>
  )
}
