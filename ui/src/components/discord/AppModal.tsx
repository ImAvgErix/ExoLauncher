import { useEffect, useMemo, useRef, useState } from 'react'
import { host, onHostEvent } from '../../lib/host'
import { appIcon, failureLine, followInteraction, type AppComponent, type AppModal, type ModalAnswers } from '../../lib/discordApps'
import { useLayer } from './admin'
import { AppSelect } from './Components'
import { uploadFile } from './Chat'
import { Markdown, type MarkdownContext } from './markdown'

// Form component types.
const ROW = 1, TEXT_INPUT = 4, TEXT = 10, LABEL = 18, FILE_UPLOAD = 19, RADIO = 21, CHECKS = 22, CHECKBOX = 23

/** An app's form opens wherever the user is in the room, one at a time, as Discord's does. */
export function AppModalHost({ guildId }: { guildId: string | null }) {
  const [modal, setModal] = useState<AppModal | null>(null)
  useEffect(() => onHostEvent('discord.interaction', event => { if (event.kind === 'modal') setModal(event.modal) }), [])
  if (!modal) return null
  return <AppForm key={modal.id} modal={modal} guildId={guildId} onClose={() => setModal(null)} />
}

type Field = { component: AppComponent; label: string | null; description: string | null }

// The form's fields in order, each with the label Discord shows over it (a label component's, or a text input's own).
function fieldsOf(components: AppComponent[]): (Field | { text: string })[] {
  const out: (Field | { text: string })[] = []
  for (const c of components) {
    if (c.type === ROW) for (const inner of c.components ?? []) out.push({ component: inner, label: inner.label ?? null, description: null })
    else if (c.type === LABEL && c.component) out.push({ component: c.component, label: c.label ?? null, description: c.description ?? null })
    else if (c.type === TEXT) out.push({ text: c.content ?? '' })
  }
  return out
}

/**
 * The form an app opened (Discord's modal) as an Exo dialog: its title and app, its fields (text, choices, people,
 * roles, channels, files, checkboxes), Cancel and Submit. Submit waits for the app; the form closes when it answers.
 */
function AppForm({ modal, guildId, onClose }: { modal: AppModal; guildId: string | null; onClose: () => void }) {
  const fields = useMemo(() => fieldsOf(modal.components), [modal.components])
  const [answers, setAnswers] = useState<ModalAnswers>(() => {
    const start: ModalAnswers = {}
    for (const f of fields) if ('component' in f && f.component.custom_id && f.component.type === TEXT_INPUT) start[f.component.custom_id] = { value: f.component.value ?? '' }
    return start
  })
  const [files, setFiles] = useState<Record<string, File[]>>({})
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const box = useRef<HTMLFormElement>(null)
  const stop = useRef<(() => void) | null>(null)
  useEffect(() => () => stop.current?.(), [])
  useLayer(box, onClose)
  const ctx = useMemo<MarkdownContext>(() => ({ users: new Map(), channelName: () => null, meId: null, guildId, colors: new Map() }), [guildId])
  const set = (id: string, answer: ModalAnswers[string]) => setAnswers(a => ({ ...a, [id]: answer }))

  async function submit() {
    if (busy) return
    setBusy(true)
    setError(null)
    // Files go up first; a file field's values are their places among this form's files, as Discord numbers them.
    const uploads: string[] = []
    const withFiles: ModalAnswers = { ...answers }
    for (const [id, list] of Object.entries(files)) {
      const values: string[] = []
      for (const file of list) {
        const up = await uploadFile(file)
        if (typeof up !== 'string') { setBusy(false); setError(up.message); return }
        values.push(String(uploads.length))
        uploads.push(up)
      }
      withFiles[id] = { values }
    }
    const result = await host.discordApps(modal.channel_id, 'modal', { interactionId: modal.id, answers: withFiles, ...(uploads.length ? { uploads } : {}) })
      .catch(() => ({ ok: false, message: null, nonce: null }))
    if (!result.ok || !result.nonce) { setBusy(false); setError(result.message ?? 'Something went wrong. Try again.'); return }
    stop.current = followInteraction(result.nonce, (ok, event) => {
      if (ok) onClose()
      else { setBusy(false); setError(failureLine(event)) }
    })
  }

  const icon = appIcon({ id: modal.application.id, name: modal.application.name ?? '', icon: modal.application.icon })
  return (
    <div className="exo-dc-modal-back is-top" onClick={onClose}>
      <form ref={box} className="exo-dc-modal exo-dc-app-form" noValidate role="dialog" aria-modal="true" aria-label={modal.title} tabIndex={-1}
        onClick={event => event.stopPropagation()} onSubmit={event => { event.preventDefault(); void submit() }}>
        <header className="exo-dc-app-form-head">
          {icon ? <img src={icon} alt="" draggable={false} /> : null}
          <strong>{modal.title}</strong>
        </header>
        {fields.map((f, i) => 'text' in f
          ? <Markdown key={i} text={f.text} ctx={ctx} />
          : <FormField key={f.component.custom_id ?? i} field={f} guildId={guildId} answer={answers[f.component.custom_id ?? '']} files={files[f.component.custom_id ?? ''] ?? []}
            first={i === fields.findIndex(x => 'component' in x)} onAnswer={answer => set(f.component.custom_id!, answer)}
            onFiles={list => setFiles(all => ({ ...all, [f.component.custom_id!]: list }))} />)}
        {error ? <p className="is-bad" role="alert">{error}</p> : null}
        <div className="exo-dc-modal-row">
          <button type="button" className="exo-dc-tab" onClick={onClose}>Cancel</button>
          <button type="submit" className="button button--primary" disabled={busy}>{busy ? 'Submitting…' : 'Submit'}</button>
        </div>
      </form>
    </div>
  )
}

type FieldProps = {
  field: Field
  guildId: string | null
  answer?: ModalAnswers[string]
  files: File[]
  first: boolean
  onAnswer: (answer: ModalAnswers[string]) => void
  onFiles: (files: File[]) => void
}

function FormField(props: FieldProps) {
  const { field, answer, onAnswer } = props
  if (field.component.type === CHECKBOX) {
    return (
      <label className="exo-dc-app-check">
        <input type="checkbox" checked={answer?.checked ?? false} onChange={event => onAnswer({ checked: event.target.checked })} />
        <span>{field.label}</span>
      </label>
    )
  }
  return <LabeledField {...props} />
}

// A field under its label (and description): text, choices, people, roles, channels or files.
function LabeledField({ field, guildId, answer, files, first, onAnswer, onFiles }: FieldProps) {
  const c = field.component
  const id = `exo-dc-form-${c.custom_id}`
  const required = c.required === true && c.type !== CHECKBOX
  const head = (
    <>
      {field.label ? <label className="exo-dc-side-label" htmlFor={id}>{field.label}{required ? <span className="exo-dc-required" aria-hidden="true"> *</span> : null}</label> : null}
      {field.description ? <p className="exo-dc-note">{field.description}</p> : null}
    </>
  )
  if (c.type === TEXT_INPUT) {
    const props = {
      id, className: 'exo-dc-modal-input', value: answer?.value ?? '', placeholder: c.placeholder, required, minLength: c.min_length, maxLength: c.max_length ?? 4000,
      autoFocus: first, onChange: (event: React.ChangeEvent<HTMLInputElement | HTMLTextAreaElement>) => onAnswer({ value: event.target.value }),
    }
    return <div className="exo-dc-app-field">{head}{c.style === 2 ? <textarea rows={4} {...props} /> : <input {...props} />}</div>
  }
  if (c.type === RADIO || c.type === CHECKS) return <div className="exo-dc-app-field">{head}<ChoiceGroup component={c} label={field.label} answer={answer} onAnswer={onAnswer} /></div>
  if (c.type === FILE_UPLOAD) {
    return (
      <div className="exo-dc-app-field">
        {head}
        <input id={id} type="file" multiple={(c.max_values ?? 1) > 1} className="exo-dc-app-file"
          onChange={event => onFiles(Array.from(event.target.files ?? []).slice(0, c.max_values ?? 1))} />
        {files.length ? <p className="exo-dc-note">{files.map(f => f.name).join(', ')}</p> : null}
      </div>
    )
  }
  return <div className="exo-dc-app-field">{head}<AppSelect component={c} guildId={guildId} onChange={values => onAnswer({ values })} /></div>
}

// A radio group (one choice) or a checkbox group (several), Discord's newer form fields.
function ChoiceGroup({ component, label, answer, onAnswer }: { component: AppComponent; label: string | null; answer?: ModalAnswers[string]; onAnswer: (answer: ModalAnswers[string]) => void }) {
  const many = component.type === CHECKS
  const picked = many ? answer?.values ?? [] : answer?.value ? [answer.value] : []
  const toggle = (value: string) => onAnswer(many ? { values: picked.includes(value) ? picked.filter(v => v !== value) : [...picked, value] } : { value })
  return (
    <div className="exo-dc-options" role={many ? 'group' : 'radiogroup'} aria-label={label ?? undefined}>
      {(component.options ?? []).map(o => (
        <button key={o.value} type="button" role={many ? 'checkbox' : 'radio'} aria-checked={picked.includes(o.value)}
          className={`exo-dc-option${picked.includes(o.value) ? ' is-current' : ''}`} onClick={() => toggle(o.value)}>
          <span className="exo-dc-option-dot" aria-hidden="true" /><span>{o.label}</span>
        </button>
      ))}
    </div>
  )
}
