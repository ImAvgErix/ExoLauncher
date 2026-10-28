import { useMemo, useState } from 'react'
import { ChevronLeft } from '../../brand/icons'
import type { DiscordChannel } from '../../lib/discord'
import { Switch } from '../Switch'
import { admin, colorOf, Field, PickChips, ReadError, SaveBar, useAdmin, useGuard } from './admin'
import { Icons } from './icons'
import { ReactionEmoji } from './Reactions'
import { Dialog } from './settingsAccess'
import { EmojiField, ServerGuide, guideState, type Guide } from './settingsGuide'
import { Skeleton } from '../ui/Skeleton'
import { ErrorNote } from '../ui/ErrorNote'

/**
 * Discord's Onboarding (a Community server's, for Manage Server and Manage Roles): on or off, Advanced Server Setup,
 * the Default Channels new members start with, the questions that give them channels and roles before or after they
 * join, and the Server Guide. Each part saves on its own, as Discord's page saves them; the requirement is Discord's
 * (one Default Channel @everyone can talk in, or with Advanced Server Setup one from Default Channels and required
 * questions together).
 */

type Emoji = { id?: string | null; name?: string | null; animated?: boolean | null }
type Answer = { id: string; title: string; description?: string | null; channel_ids?: string[] | null; role_ids?: string[] | null; emoji?: Emoji | null }
type Question = { id: string; title: string; options: Answer[]; single_select: boolean; required: boolean; in_onboarding: boolean }
type Setup = { prompts: Question[]; default_channel_ids: string[]; enabled: boolean; mode: number }
type Read = { onboarding: Setup; guide: Guide | null; seen: string[]; chats: string[]; me: string | null }
type Role = { id: string; name: string; color: number; position: number; managed: boolean }
type View = 'overview' | 'channels' | 'questions' | 'guide'
type QuestionErrors = { title?: string; options?: string; answers: (string | null)[] }

/** What the parts share: the server's channels with their categories, and which ones @everyone sees and talks in. */
export type OnboardingPlace = {
  guildId: string
  rows: { channel: DiscordChannel; parent: string | null }[]
  seen: Set<string>
  chats: Set<string>
  rules: string | null
}

// Discord's requirement and limits.
const NEEDED = 1, MAX_PREJOIN = 4, MAX_QUESTIONS = 15, MAX_ANSWERS = 50, DROPDOWN_FROM = 13, ADVANCED = 1
const plural = (n: number, one: string) => `${n} ${n === 1 ? one : `${one}s`}`
const NOT_MET = 'Your Default Channels must meet the latest requirements to be saved.'

// The channel list in order, each channel with its category (the list puts a category's channels right after it).
function rowsOf(channels: DiscordChannel[]) {
  let parent: string | null = null
  return channels.map(channel => {
    if (channel.kind === 'category') { parent = channel.id; return { channel, parent: null } }
    return { channel, parent }
  })
}

/** The public channels some ids give: each picked channel, and every channel of a picked category, as Discord counts. */
export function given(place: OnboardingPlace, ids: Set<string>) {
  return place.rows.filter(r => r.channel.kind !== 'category' && place.seen.has(r.channel.id) && (ids.has(r.channel.id) || (r.parent !== null && ids.has(r.parent))))
    .map(r => r.channel.id)
}
const chatty = (place: OnboardingPlace, ids: Set<string>) => given(place, ids).filter(id => place.chats.has(id))

// Advanced Server Setup's count: the Default Channels @everyone talks in, and for each required question the fewest new
// ones any answer gives.
function viaQuestions(place: OnboardingPlace, defaults: Set<string>, prompts: Question[]) {
  const result = chatty(place, defaults)
  for (const question of prompts.filter(q => q.required)) {
    let least: string[] | null = null
    for (const answer of question.options) {
      const got = chatty(place, new Set(answer.channel_ids ?? [])).filter(id => !result.includes(id))
      if (!least || got.length < least.length) least = got
    }
    result.push(...least ?? [])
  }
  return result
}

export function Onboarding({ guild, channels }: { guild: { id: string; rules_channel_id?: string | null; verification_level?: number }; channels: DiscordChannel[] }) {
  const read = useAdmin<Read>('onboarding', { guildId: guild.id })
  const [view, setView] = useState<View>('overview')
  const rows = useMemo(() => rowsOf(channels), [channels])
  const place = useMemo<OnboardingPlace | null>(() => read.data
    ? { guildId: guild.id, rows, seen: new Set(read.data.seen), chats: new Set(read.data.chats), rules: guild.rules_channel_id ?? null } : null,
  [read.data, guild.id, guild.rules_channel_id, rows])
  if (!read.data || !place) return <><h1>Onboarding</h1>{read.error ? <ReadError error={read.error} onRetry={read.reload} /> : <Skeleton label="Loading onboarding" />}</>
  const data = read.data
  const back = () => setView('overview')
  const saved = () => { read.reload(); setView('overview') }
  if (view === 'channels') return <DefaultChannels place={place} setup={data.onboarding} onBack={back} onSaved={saved} />
  if (view === 'questions') return <Questions place={place} setup={data.onboarding} onBack={back} onSaved={saved} />
  if (view === 'guide') return <ServerGuide place={place} guide={data.guide} meId={data.me} onBack={back} onSaved={saved} />
  return <Overview place={place} data={data} veryHigh={(guild.verification_level ?? 0) >= 4} onEdit={setView} onChanged={read.reload} />
}

// A part's row: how it stands (a check when it's set, a cross when it misses Discord's requirement) and Edit.
function Part({ label, detail, state, extra, onEdit }: { label: string; detail: string; state: 'good' | 'required' | null; extra?: React.ReactNode; onEdit: () => void }) {
  return (
    <Field label={label} detail={detail}>
      <span className="exo-dc-settings-buttons">
        {state ? <span className={`exo-dc-check-mark is-${state === 'good' ? 'good' : 'bad'}`} role="img" aria-label={state === 'good' ? 'Set' : 'Required'}>
          {state === 'good' ? <Icons.Check size={14} /> : <Icons.Close size={14} />}</span> : null}
        {extra}
        <button type="button" className="exo-set-btn is-compact" onClick={onEdit}>Edit</button>
      </span>
    </Field>
  )
}

function Overview({ place, data, veryHigh, onEdit, onChanged }: { place: OnboardingPlace; data: Read; veryHigh: boolean; onEdit: (view: View) => void; onChanged: () => void }) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const setup = data.onboarding
  const advanced = setup.mode === ADVANCED
  const defaults = new Set(setup.default_channel_ids)
  const regular = chatty(place, defaults).length
  const met = regular >= NEEDED || (advanced && viaQuestions(place, defaults, setup.prompts).length >= NEEDED)
  const guide = guideState(place, data.guide)
  async function run(work: () => Promise<{ ok: boolean; message?: string | null }>) {
    setBusy(true); setError(null)
    const result = await work()
    setBusy(false)
    if (result.ok) onChanged(); else setError(result.message ?? 'Failed to save changes')
  }
  // Turning onboarding on turns a finished Server Guide on with it, as Discord's Enable Onboarding does.
  const enable = () => run(async () => {
    const result = await admin('saveOnboarding', { guildId: place.guildId, enabled: true })
    if (result.ok && guide.started && guide.ready && data.guide && !data.guide.enabled) await admin('saveGuide', { guildId: place.guildId, guide: { ...data.guide, enabled: true } })
    return result
  })
  const mode = (on: boolean) => {
    if (!on && setup.enabled && regular < NEEDED) { setError(NOT_MET); return }
    void run(() => admin('saveOnboarding', { guildId: place.guildId, mode: on ? ADVANCED : 0 }))
  }
  return (
    <>
      <h1>Onboarding</h1>
      <p className="exo-dc-note">Give your members a simple starting experience with custom channels, roles, and first steps.</p>
      {setup.enabled ? (
        <Field label="Onboarding is enabled">
          <Switch checked disabled={busy} label="Onboarding is enabled" onChange={() => void run(() => admin('saveOnboarding', { guildId: place.guildId, enabled: false }))} />
        </Field>
      ) : (
        <Field label={met ? "You're ready to enable" : "You're missing a few items..."} detail={met ? 'Turn on your new member experience.' : 'To enable Onboarding, you must meet these requirements.'}>
          <button type="button" className="exo-set-btn is-primary" disabled={busy || !met} onClick={() => void enable()}>Enable onboarding</button>
        </Field>
      )}
      <Field label="Advanced server setup" detail="Meet the requirement with default channels and required questions combined.">
        <Switch checked={advanced} disabled={busy} label="Advanced server setup" onChange={mode} />
      </Field>
      <Part label={advanced ? 'Default channels & pre-join questions' : 'Default channels'} state={met ? 'good' : 'required'} onEdit={() => onEdit('channels')}
        detail={defaultsDetail(place, setup)} />
      <QuestionsPart place={place} setup={setup} onEdit={() => onEdit('questions')} />
      <GuidePart place={place} guide={data.guide} enabled={setup.enabled} busy={busy} onEdit={() => onEdit('guide')}
        onShow={on => void run(() => admin('saveGuide', { guildId: place.guildId, guide: { ...data.guide, enabled: on } }))} />
      {!setup.enabled && veryHigh ? <p className="exo-dc-note">Once enabled, getting a role will not bypass a member through verification and rules. To verify members yourself, use Verify member on someone or the Members page.</p> : null}
      {error ? <ErrorNote className="exo-dc-note is-bad" message={error} /> : null}
    </>
  )
}

// Default Channels' row: how many there are, or what Discord still wants.
function defaultsDetail(place: OnboardingPlace, setup: Setup) {
  const defaults = new Set(setup.default_channel_ids)
  const count = plural(given(place, defaults).length, 'default channel')
  const regular = chatty(place, defaults).length
  if (setup.mode === ADVANCED) return `You have ${count} and at least ${plural(Math.max(0, viaQuestions(place, defaults, setup.prompts).length - regular), 'channel')} assigned by questions.`
  return regular >= NEEDED ? `You have ${count}` : `You must have ${plural(NEEDED, 'channel')} new members can talk in`
}

// The Server Guide's row, with its switch once onboarding is on and the guide has something.
function GuidePart({ place, guide, enabled, busy, onEdit, onShow }: { place: OnboardingPlace; guide: Guide | null; enabled: boolean; busy: boolean; onEdit: () => void; onShow: (on: boolean) => void }) {
  const state = guideState(place, guide)
  return (
    <Part label="Server guide" state={state.started ? state.ready ? 'good' : 'required' : null} onEdit={onEdit}
      detail={!state.ready ? 'You must have a welcome message and at least 3 to-dos.' : state.started ? 'Your welcome message, to-dos and resources are all set up.' : 'Set up your Server Guide to help new members get started.'}
      extra={state.started && enabled && guide ? <Switch checked={guide.enabled === true} disabled={busy || !state.ready} label="Show the Server Guide" onChange={onShow} /> : null} />
  )
}

// Questions' row: how many of the public channels questions and Default Channels give (Discord wants 85% or more).
function QuestionsPart({ place, setup, onEdit }: { place: OnboardingPlace; setup: Setup; onEdit: () => void }) {
  const advanced = setup.mode === ADVANCED
  const total = place.rows.filter(r => r.channel.kind !== 'category' && r.channel.kind !== 'voice' && place.seen.has(r.channel.id)).length
  const ids = new Set([...setup.default_channel_ids, ...setup.prompts.flatMap(q => q.options.flatMap(o => o.channel_ids ?? []))])
  const assignable = given(place, ids).filter(id => place.rows.find(r => r.channel.id === id)?.channel.kind !== 'voice').length
  const state = setup.prompts.length && total && assignable / total >= 0.85 ? 'good' : null
  return <Part label={advanced ? 'Post-join questions' : 'Questions'} state={state} onEdit={onEdit} detail={`${assignable} of ${total} public channels are assignable through questions and default channels.`} />
}

function Back({ onBack }: { onBack: () => void }) {
  const guard = useGuard()
  return <button type="button" className="exo-dc-tab" onClick={() => guard(onBack)}><ChevronLeft size={14} />Back</button>
}

function DefaultChannels({ place, setup, onBack, onSaved }: { place: OnboardingPlace; setup: Setup; onBack: () => void; onSaved: () => void }) {
  const [ids, setIds] = useState(() => new Set(setup.default_channel_ids))
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const dirty = ids.size !== setup.default_channel_ids.length || setup.default_channel_ids.some(id => !ids.has(id))
  const chats = chatty(place, ids).length
  const known = new Set(place.rows.map(r => r.channel.id))
  const toggle = (id: string) => { setError(null); setIds(set => { const next = new Set(set); if (!next.delete(id)) next.add(id); return next }) }
  async function save() {
    if (setup.enabled && setup.mode !== ADVANCED && chats < NEEDED) { setError(NOT_MET); return }
    setBusy(true)
    const result = await admin('saveOnboarding', { guildId: place.guildId, defaultChannelIds: [...ids].filter(id => known.has(id)) })
    setBusy(false)
    if (result.ok) onSaved(); else setError(result.message ?? 'Failed to save changes')
  }
  return (
    <>
      <Back onBack={onBack} />
      <h1>Default channels</h1>
      <p className="exo-dc-note">Choose which channels new members start with: just the stuff they care about, without archives or bot spam.</p>
      <Field label={`${chats} chattable · ${given(place, ids).length} total`} detail={`Must include ${plural(NEEDED, 'channel')} where @everyone can read and send messages.`}>
        <span className={`exo-dc-check-mark is-${chats >= NEEDED ? 'good' : 'bad'}`} role="img" aria-label={chats >= NEEDED ? 'Met' : 'Not met'}>
          {chats >= NEEDED ? <Icons.Check size={14} /> : <Icons.Close size={14} />}
        </span>
      </Field>
      <div className="exo-dc-pick-list is-page" aria-label="Channels">
        {place.rows.map(row => <ChannelPick key={row.channel.id} place={place} row={row} ids={ids} onToggle={toggle} />)}
      </div>
      <SaveBar shown={dirty} busy={busy} error={error} onReset={() => { setIds(new Set(setup.default_channel_ids)); setError(null) }} onSave={() => void save()} />
    </>
  )
}

function ChannelPick({ place, row, ids, onToggle }: { place: OnboardingPlace; row: OnboardingPlace['rows'][number]; ids: Set<string>; onToggle: (id: string) => void }) {
  const { channel, parent } = row
  const category = channel.kind === 'category'
  const followed = parent !== null && ids.has(parent)
  const open = place.seen.has(channel.id)
  const checked = open && (ids.has(channel.id) || followed)
  // A picked category gives all its channels; its rows show so once, on the category.
  const why = !open ? '@everyone must have View permission for this channel.'
    : category && ids.has(channel.id) ? 'New members get all of its channels. Uncheck it to pick them one by one.'
      : channel.id === place.rules ? "We don't recommend adding the rules channel as a default channel." : null
  return (
    <button type="button" className={`exo-dc-pick-row${category ? ' is-category' : parent !== null ? ' is-child' : ''}`} aria-pressed={checked} disabled={followed || !open}
      onClick={() => onToggle(channel.id)}>
      <span className="exo-dc-pick-mark" aria-hidden="true">{checked ? <Icons.Check size={14} /> : null}</span>
      <span>{category ? `${channel.name} (category)` : channel.kind === 'voice' ? channel.name : `#${channel.name}`}</span>
      {why ? <small>{why}</small> : null}
    </button>
  )
}

// Discord's check of a question before it saves: a title, an answer, and each answer's channels or roles.
function check(prompts: Question[], question: Question, answer: Answer) {
  if (question.single_select) {
    const mine = new Set(answer.role_ids ?? [])
    if (prompts.some(q => q.id !== question.id && q.options.some(o => (o.role_ids ?? []).some(r => mine.has(r)))))
      return 'Roles used in single-select questions cannot be granted by other questions'
  }
  return (answer.channel_ids ?? []).length + (answer.role_ids ?? []).length === 0 ? 'At least one channel or role is required' : null
}

function errorsOf(prompts: Question[]): (QuestionErrors | null)[] {
  return prompts.map(q => {
    const errors: QuestionErrors = { answers: q.options.map(o => check(prompts, q, o)) }
    if (!q.title.trim()) errors.title = 'Question is required'
    if (!q.options.length) errors.options = 'At least one answer is required'
    return errors.title || errors.options || errors.answers.some(Boolean) ? errors : null
  })
}

// Pre-join questions first, as Discord keeps them.
const ordered = (prompts: Question[]) => [...prompts.filter(q => q.in_onboarding), ...prompts.filter(q => !q.in_onboarding)]

function Questions({ place, setup, onBack, onSaved }: { place: OnboardingPlace; setup: Setup; onBack: () => void; onSaved: () => void }) {
  const roles = useAdmin<{ roles: Role[] }>('roles', { guildId: place.guildId })
  const [list, setList] = useState(() => ordered(setup.prompts))
  const [errors, setErrors] = useState<(QuestionErrors | null)[]>([])
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [editing, setEditing] = useState<{ question: string; answer: number } | null>(null)
  const dirty = JSON.stringify(list) !== JSON.stringify(ordered(setup.prompts))
  const prejoin = list.filter(q => q.in_onboarding).length
  const change = (next: Question[]) => { setError(null); setErrors([]); setList(ordered(next)) }
  const put = (id: string, part: Partial<Question>) => change(list.map(q => q.id === id ? { ...q, ...part } : q))
  const add = (before: boolean) => change([...list, { id: String(Date.now()), title: '', options: [], single_select: false, required: false, in_onboarding: before }])
  function move(question: Question, to: number) {
    const next = [...list]
    next.splice(to, 0, ...next.splice(list.indexOf(question), 1))
    change(next)
  }
  function switchSide(question: Question) {
    if (!question.in_onboarding && prejoin >= MAX_PREJOIN) { setError(`You cannot have more than ${MAX_PREJOIN} pre-join questions`); return }
    put(question.id, { in_onboarding: !question.in_onboarding, required: question.in_onboarding ? false : question.required })
  }
  async function save() {
    const found = errorsOf(list)
    if (found.some(Boolean)) { setErrors(found); return }
    setBusy(true)
    const result = await admin('saveOnboarding', { guildId: place.guildId, prompts: list })
    setBusy(false)
    if (result.ok) onSaved(); else setError(result.message ?? 'Failed to save changes')
  }
  const question = editing ? list.find(q => q.id === editing.question) : undefined
  const card = (q: Question) => (
    <QuestionCard key={q.id} question={q} index={list.indexOf(q)} first={list.indexOf(q) === 0 || list[list.indexOf(q) - 1].in_onboarding !== q.in_onboarding}
      errors={errors[list.indexOf(q)] ?? null} onChange={part => put(q.id, part)} onMoveUp={() => move(q, list.indexOf(q) - 1)} onSwitch={() => switchSide(q)}
      onDelete={() => change(list.filter(x => x.id !== q.id))} onAnswer={answer => setEditing({ question: q.id, answer })} />
  )
  return (
    <>
      <Back onBack={onBack} />
      <h1>Questions</h1>
      <h3>Pre-join questions</h3>
      <p className="exo-dc-note">Members will be asked these questions before they join your server. Use them to assign channels and important roles. Pre-join questions are also on the Channels & Roles page.</p>
      {list.filter(q => q.in_onboarding).map(card)}
      {prejoin < MAX_PREJOIN && list.length < MAX_QUESTIONS ? <span className="exo-dc-settings-buttons"><button type="button" className="exo-set-btn" onClick={() => add(true)}>Add a question</button></span> : null}
      <h3>Post-join questions</h3>
      <p className="exo-dc-note">Members will be asked these questions after they join your server, on the Channels & Roles page. Use them to assign roles that members can pick later, like vanity roles.</p>
      {list.filter(q => !q.in_onboarding).map(card)}
      {list.length < MAX_QUESTIONS ? <span className="exo-dc-settings-buttons"><button type="button" className="exo-set-btn" onClick={() => add(false)}>Add a question</button></span> : null}
      {error ? <ErrorNote className="exo-dc-note is-bad" message={error} /> : null}
      {editing && question ? (
        <AnswerDialog index={list.indexOf(question)} question={question} answer={question.options[editing.answer]} place={place} roles={roles.data?.roles ?? []} prompts={list}
          onSave={answer => put(question.id, { options: editing.answer < 0 ? [...question.options, answer] : question.options.map((o, i) => i === editing.answer ? answer : o) })}
          onRemove={() => put(question.id, { options: question.options.filter((_, i) => i !== editing.answer) })} onClose={() => setEditing(null)} />
      ) : null}
      <SaveBar shown={dirty} busy={busy} error={null} onReset={() => { setList(ordered(setup.prompts)); setErrors([]); setError(null) }} onSave={() => void save()} />
    </>
  )
}

function QuestionCard({ question, index, first, errors, onChange, onMoveUp, onSwitch, onDelete, onAnswer }: {
  question: Question
  index: number
  first: boolean
  errors: QuestionErrors | null
  onChange: (part: Partial<Question>) => void
  onMoveUp: () => void
  onSwitch: () => void
  onDelete: () => void
  onAnswer: (index: number) => void
}) {
  const n = question.options.length
  return (
    <section className="exo-dc-perm-group" aria-label={`Question ${index + 1}`}>
      <Field label={`Question ${index + 1}`} stacked error={errors?.title}>
        <input className="exo-dc-modal-input exo-dc-field is-wide" value={question.title} maxLength={100} placeholder="Enter a question..." aria-label={`Question ${index + 1}`}
          onChange={event => onChange({ title: event.target.value })} />
      </Field>
      <h3>{n >= DROPDOWN_FROM ? `Available answers — ${n} of ${MAX_ANSWERS} shown in a dropdown list` : `Available answers — ${n} of ${MAX_ANSWERS}`}</h3>
      {errors?.options ? <ErrorNote className="exo-dc-note is-bad" message={errors.options} /> : null}
      <ol className="exo-dc-rule-list">
        {question.options.map((answer, i) => (
          <li key={answer.id}>
            {answer.emoji?.name ? <ReactionEmoji reaction={{ emoji: { id: answer.emoji.id ?? null, name: answer.emoji.name, animated: answer.emoji.animated === true } }} size={20} /> : null}
            <span className="exo-dc-question">
              <strong>{answer.title}</strong>
              <span className={errors?.answers[i] ? 'is-bad' : undefined}>{errors?.answers[i] ?? (answer.description || gives(answer))}</span>
            </span>
            <button type="button" className="exo-set-btn is-compact" onClick={() => onAnswer(i)}>Edit</button>
          </li>
        ))}
      </ol>
      {n < MAX_ANSWERS ? <span className="exo-dc-settings-buttons"><button type="button" className="exo-set-btn" onClick={() => onAnswer(-1)}>Add an answer</button></span> : null}
      {n === DROPDOWN_FROM - 1 ? <p className="exo-dc-note">From {DROPDOWN_FROM} answers, members see them as a dropdown list without descriptions.</p> : null}
      <Field label="Allow multiple answers"><Switch checked={!question.single_select} label="Allow multiple answers" onChange={on => onChange({ single_select: !on })} /></Field>
      {question.in_onboarding ? <Field label="Required"><Switch checked={question.required} label="Required" onChange={required => onChange({ required })} /></Field> : null}
      <span className="exo-dc-settings-buttons">
        {first ? null : <button type="button" className="exo-set-btn is-compact" onClick={onMoveUp}>Move up</button>}
        <button type="button" className="exo-set-btn is-compact" onClick={onSwitch}>{question.in_onboarding ? 'Move to post-join' : 'Move to pre-join'}</button>
        <button type="button" className="exo-set-btn is-compact" onClick={onDelete}>Delete this question</button>
      </span>
    </section>
  )
}

const gives = (answer: Answer) => [plural((answer.channel_ids ?? []).length, 'channel'), plural((answer.role_ids ?? []).length, 'role')].join(' · ')

// One answer, Discord's three steps: its title and description, the channels and roles it gives, an emoji.
function AnswerDialog({ index, question, answer, place, roles, prompts, onSave, onRemove, onClose }: {
  index: number
  question: Question
  answer: Answer | undefined
  place: OnboardingPlace
  roles: Role[]
  prompts: Question[]
  onSave: (answer: Answer) => void
  onRemove: () => void
  onClose: () => void
}) {
  const [draft, setDraft] = useState<Answer>(() => answer ?? { id: String(Date.now()), title: '', description: '', channel_ids: [], role_ids: [] })
  const [errors, setErrors] = useState<{ title?: string; roles?: string }>({})
  const set = (part: Partial<Answer>) => { setErrors({}); setDraft(d => ({ ...d, ...part })) }
  const channels = place.rows.filter(r => r.channel.kind !== 'category' && place.seen.has(r.channel.id))
    .map(r => ({ id: r.channel.id, name: r.channel.kind === 'voice' ? r.channel.name : `#${r.channel.name}` }))
  const picks = roles.filter(r => r.id !== place.guildId && !r.managed).sort((a, b) => b.position - a.position)
    .map(r => ({ id: r.id, name: r.name, color: colorOf(r.color) }))
  function save() {
    const next = { ...draft, title: draft.title.trim() }
    const found = { title: next.title ? undefined : 'Title is required', roles: check(prompts, question, next) ?? undefined }
    if (found.title || found.roles) { setErrors(found); return }
    onSave(next); onClose()
  }
  return (
    <Dialog title={`Question ${index + 1}`} onClose={onClose} actions={<>
      {answer ? <button type="button" className="exo-set-btn is-danger" onClick={() => { onRemove(); onClose() }}>Remove</button> : null}
      <button type="button" className="exo-set-btn is-primary" onClick={save}>Save</button>
    </>}>
      <p>{question.title || 'What do you want to do in this community?'}</p>
      <Field label="1. Add an answer" stacked error={errors.title}>
        <input className="exo-dc-modal-input" value={draft.title} maxLength={50} autoFocus placeholder="Enter an answer..." aria-label="Answer" onChange={event => set({ title: event.target.value })} />
        <input className="exo-dc-modal-input" value={draft.description ?? ''} maxLength={100} placeholder="Enter a description... (optional)" aria-label="Description"
          onChange={event => set({ description: event.target.value })} />
      </Field>
      <PickChips label="2. Assign channels and/or roles" detail="@everyone must have View permission for channels used in answers." ids={draft.channel_ids ?? []} options={channels}
        onChange={channel_ids => set({ channel_ids })} />
      <PickChips label="Roles" detail="Members will receive all channels granted by these roles." ids={draft.role_ids ?? []} options={picks} onChange={role_ids => set({ role_ids })} />
      {errors.roles ? <ErrorNote className="exo-dialog-error" message={errors.roles} /> : null}
      <Field label="3. Pick an emoji" detail="Optional, but why not add some fun?"><EmojiField emoji={draft.emoji ?? null} onChange={emoji => set({ emoji })} /></Field>
    </Dialog>
  )
}
