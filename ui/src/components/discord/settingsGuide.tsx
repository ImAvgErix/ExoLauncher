import { useState } from 'react'
import { ChevronLeft } from '../../brand/icons'
import { Choice } from '../settings/Choice'
import { admin, Field, SaveBar, useAdmin, useGuard } from './admin'
import { ReactionEmoji } from './Reactions'
import { Dialog } from './settingsAccess'
import type { OnboardingPlace } from './settingsOnboarding'

/**
 * Discord's Server Guide editor (Onboarding's last part): the welcome sign (its author and message), 3 to 5 tasks for
 * new members (a channel, what to do there and an emoji) and up to 7 resource pages (read-only channels with a title and
 * description). A thumbnail Discord already has stays as it is.
 */

type Emoji = { id?: string | null; name?: string | null; animated?: boolean | null }
type Item = { channel_id: string; title: string; description?: string | null; emoji?: Emoji | null; icon?: string | null; action_type?: number }
export type Guide = { welcome_message?: { author_ids: string[]; message: string } | null; new_member_actions?: Item[] | null; resource_channels?: Item[] | null; enabled?: boolean }

// Discord's limits: the welcome message 7 to 300, a task's title 7 to 60, 3 to 5 tasks, 7 resources with titles to 30.
const MIN_WELCOME = 7, MAX_WELCOME = 300, MIN_TASK = 7, MAX_TASK = 60, MIN_TASKS = 3, MAX_TASKS = 5, MAX_RESOURCES = 7, MAX_RESOURCE = 30, MAX_DESCRIPTION = 200
const VISIT = 0, TALK = 1

// What keeps a task from counting: Discord wants a title of 7 and a channel @everyone can see (and talk in, to talk).
function taskProblem(place: OnboardingPlace, task: Item) {
  if (task.title.trim().length < MIN_TASK) return `Actions must be at least ${MIN_TASK} characters.`
  if (!place.seen.has(task.channel_id)) return 'The selected channel does not have view channel permissions for @everyone.'
  return task.action_type === TALK && !place.chats.has(task.channel_id) ? 'The selected channel does not have send message permissions for @everyone.' : null
}

/** Whether the guide has anything yet, and whether it's whole as Discord wants it (an empty one counts as whole). */
export function guideState(place: OnboardingPlace, guide: Guide | null) {
  const welcome = guide?.welcome_message
  const tasks = guide?.new_member_actions ?? []
  const started = !!(welcome?.message || welcome?.author_ids.length || tasks.length || guide?.resource_channels?.length)
  const ready = !started || ((welcome?.message.length ?? 0) >= MIN_WELCOME && !!welcome?.author_ids.length && tasks.length >= MIN_TASKS && tasks.every(t => taskProblem(place, t) === null))
  return { started, ready }
}

const channelName = (place: OnboardingPlace, id: string) => {
  const channel = place.rows.find(r => r.channel.id === id)?.channel
  return channel ? `#${channel.name}` : 'a channel'
}

export function ServerGuide({ place, guide, meId, onBack, onSaved }: { place: OnboardingPlace; guide: Guide | null; meId: string | null; onBack: () => void; onSaved: () => void }) {
  const original: Guide = guide ?? { welcome_message: { author_ids: [], message: '' }, new_member_actions: [], resource_channels: [], enabled: false }
  const [draft, setDraft] = useState<Guide>(original)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [editing, setEditing] = useState<{ list: 'new_member_actions' | 'resource_channels'; index: number } | null>(null)
  const guard = useGuard()
  const welcome = draft.welcome_message ?? { author_ids: [], message: '' }
  const tasks = draft.new_member_actions ?? []
  const resources = draft.resource_channels ?? []
  const author = welcome.author_ids[0] ?? null
  const dirty = JSON.stringify(draft) !== JSON.stringify(original)
  const set = (part: Partial<Guide>) => { setError(null); setDraft(d => ({ ...d, ...part })) }
  const setList = (list: 'new_member_actions' | 'resource_channels', items: Item[]) => set(list === 'new_member_actions' ? { new_member_actions: items } : { resource_channels: items })
  async function save() {
    // Someone writing the welcome sign signs it, unless it names its author already.
    const next = { ...draft, welcome_message: { message: welcome.message, author_ids: welcome.author_ids.length || !welcome.message || !meId ? welcome.author_ids : [meId] } }
    if (next.welcome_message.message && next.welcome_message.message.length < MIN_WELCOME) { setError(`Welcome messages require a message of at least ${MIN_WELCOME} characters.`); return }
    const state = guideState(place, next)
    if (next.enabled && !state.ready) { setError(`To show the Server Guide, it needs a welcome message and at least ${MIN_TASKS} to-dos.`); return }
    setBusy(true)
    const result = await admin('saveGuide', { guildId: place.guildId, guide: next })
    setBusy(false)
    if (result.ok) onSaved(); else setError(result.message ?? 'Failed to save changes')
  }
  const item = editing ? (editing.list === 'new_member_actions' ? tasks : resources)[editing.index] : undefined
  return (
    <>
      <button type="button" className="exo-dc-tab" onClick={() => guard(onBack)}><ChevronLeft size={14} />Back</button>
      <h1>Server guide</h1>
      <h3>Welcome sign</h3>
      <p className="exo-dc-note">Tell new members what's special about your community and why you're excited to see them!</p>
      <Author place={place} author={author} meId={meId} onMine={() => meId && set({ welcome_message: { ...welcome, author_ids: [meId] } })} />
      <Field label="Welcome message" detail="Use [@username] to mention the member by name." stacked>
        <textarea className="exo-dc-modal-input exo-dc-field is-wide" rows={4} maxLength={MAX_WELCOME} value={welcome.message} aria-label="Welcome message"
          placeholder="Hi [@username]! I made this server so that people like us could have a safe space to be ourselves."
          onChange={event => set({ welcome_message: { ...welcome, message: event.target.value } })} />
      </Field>
      <h3>New member to-dos</h3>
      <p className="exo-dc-note">Set {MIN_TASKS}–{MAX_TASKS} tasks for your new members to do. Get them to talk and engage in your channels.</p>
      <Entries items={tasks} detail={task => `in ${channelName(place, task.channel_id)} · ${task.action_type === TALK ? 'Member sends a message' : 'Member visits the channel'}`}
        problem={task => taskProblem(place, task)} onEdit={index => setEditing({ list: 'new_member_actions', index })} />
      {tasks.length < MAX_TASKS ? <span className="exo-dc-settings-buttons"><button type="button" className="exo-set-btn" onClick={() => setEditing({ list: 'new_member_actions', index: -1 })}>Add a task</button></span> : null}
      <h3>Resource pages</h3>
      <p className="exo-dc-note">Turn read-only channels into resource pages in your Server Guide. Members start at the top of a page, without the chat bar, and the channel leaves the channel list unless they show all channels.</p>
      <Entries items={resources} detail={resource => resource.description || channelName(place, resource.channel_id)}
        problem={resource => place.seen.has(resource.channel_id) && !place.chats.has(resource.channel_id) ? null : 'Assigned channel must be readonly for @everyone'}
        onEdit={index => setEditing({ list: 'resource_channels', index })} />
      {resources.length < MAX_RESOURCES ? <span className="exo-dc-settings-buttons"><button type="button" className="exo-set-btn" onClick={() => setEditing({ list: 'resource_channels', index: -1 })}>Add a resource</button></span> : null}
      {editing ? (
        <ItemDialog place={place} kind={editing.list} item={item} taken={(editing.list === 'new_member_actions' ? tasks : resources).filter((_, i) => i !== editing.index).map(i => i.channel_id)}
          onSave={next => {
            const list = editing.list === 'new_member_actions' ? tasks : resources
            setList(editing.list, editing.index < 0 ? [...list, next] : list.map((x, i) => i === editing.index ? next : x))
          }}
          onRemove={() => setList(editing.list, (editing.list === 'new_member_actions' ? tasks : resources).filter((_, i) => i !== editing.index))}
          onClose={() => setEditing(null)} />
      ) : null}
      <SaveBar shown={dirty} busy={busy} error={error} onReset={() => { setDraft(original); setError(null) }} onSave={() => void save()} />
    </>
  )
}

// The welcome sign's author: you, or the name Discord has for them, with Make me the author.
function Author({ place, author, meId, onMine }: { place: OnboardingPlace; author: string | null; meId: string | null; onMine: () => void }) {
  const person = useAdmin<{ name?: string }>('person', { guildId: place.guildId, userId: author && author !== meId ? author : '' })
  const name = !author ? 'You, when you save' : author === meId ? 'You' : (person.data?.name ?? 'Someone else')
  return (
    <Field label="Message author" detail={name}>
      {author && author !== meId && meId ? <button type="button" className="exo-set-btn is-compact" onClick={onMine}>Make me the author</button> : null}
    </Field>
  )
}

function Entries({ items, detail, problem, onEdit }: { items: Item[]; detail: (item: Item) => string; problem: (item: Item) => string | null; onEdit: (index: number) => void }) {
  return (
    <ol className="exo-dc-rule-list">
      {items.map((item, i) => (
        <li key={item.channel_id}>
          {item.emoji?.name ? <ReactionEmoji reaction={{ emoji: { id: item.emoji.id ?? null, name: item.emoji.name, animated: item.emoji.animated === true } }} size={20} /> : null}
          <span className="exo-dc-question">
            <strong>{item.title}</strong>
            <span className={problem(item) ? 'is-bad' : undefined}>{problem(item) ?? detail(item)}</span>
          </span>
          <button type="button" className="exo-set-btn is-compact" onClick={() => onEdit(i)}>Edit</button>
        </li>
      ))}
    </ol>
  )
}

// A task or a resource page, as Discord's dialogs ask for them.
function ItemDialog({ place, kind, item, taken, onSave, onRemove, onClose }: {
  place: OnboardingPlace
  kind: 'new_member_actions' | 'resource_channels'
  item: Item | undefined
  taken: string[]
  onSave: (item: Item) => void
  onRemove: () => void
  onClose: () => void
}) {
  const task = kind === 'new_member_actions'
  // A task's channel: one @everyone can see; a resource's: one it can read but not write in.
  const choices = place.rows.filter(r => r.channel.kind !== 'category' && r.channel.kind !== 'voice' && place.seen.has(r.channel.id) && !taken.includes(r.channel.id)
    && (task || !place.chats.has(r.channel.id))).map(r => ({ value: r.channel.id, label: `#${r.channel.name}` }))
  const [draft, setDraft] = useState<Item>(() => item ?? { channel_id: choices[0]?.value ?? '', title: '', description: '', emoji: null, ...(task ? { action_type: VISIT } : {}) })
  const [error, setError] = useState<string | null>(null)
  const set = (part: Partial<Item>) => { setError(null); setDraft(d => ({ ...d, ...part })) }
  const talk = place.chats.has(draft.channel_id)
  function save() {
    const next = { ...draft, title: draft.title.trim() }
    if (!next.channel_id) { setError(task ? 'Channels must be viewable by @everyone' : 'You can only select channels that are readonly for @everyone.'); return }
    if (task && next.title.length < MIN_TASK) { setError(`Actions must be at least ${MIN_TASK} characters.`); return }
    if (!next.title) { setError('Give this resource a name.'); return }
    onSave(task && next.action_type === TALK && !talk ? { ...next, action_type: VISIT } : next); onClose()
  }
  return (
    <Dialog title={item ? (task ? 'Edit task' : 'Edit resource') : (task ? 'Add a task' : 'Add a resource')} onClose={onClose} actions={<>
      {item ? <button type="button" className="exo-set-btn is-danger" onClick={() => { onRemove(); onClose() }}>Remove</button> : null}
      <button type="button" className="exo-set-btn is-primary" onClick={save}>Save</button>
    </>}>
      <Field label={task ? 'What should the new member do?' : 'Give this resource a name'} stacked>
        <input className="exo-dc-modal-input" value={draft.title} autoFocus maxLength={task ? MAX_TASK : MAX_RESOURCE} aria-label={task ? 'Task' : 'Name'}
          placeholder={task ? 'Ex: Post a photo of your pet' : '#rules might be Rules'} onChange={event => set({ title: event.target.value })} />
      </Field>
      <Field label={task ? 'Where should they do it?' : 'Choose a resource channel'} detail={task ? 'Channels must be viewable by @everyone' : 'You can only select channels that are readonly for @everyone.'}>
        <Choice label="Channel" value={draft.channel_id} choices={choices.length || !draft.channel_id ? choices : [{ value: draft.channel_id, label: channelName(place, draft.channel_id) }]}
          onChange={channel_id => set({ channel_id })} />
      </Field>
      {task ? (
        <Field label="This task is complete when:">
          <div className="exo-shelf-chips" role="radiogroup" aria-label="This task is complete when">
            {[{ v: VISIT, label: 'Member visits the channel' }, { v: TALK, label: 'Member sends a message in the channel' }].map(o => (
              <button key={o.v} type="button" role="radio" className="exo-shelf-chip" disabled={o.v === TALK && !talk} aria-checked={draft.action_type === o.v} aria-pressed={draft.action_type === o.v}
                onClick={() => set({ action_type: o.v })}>{o.label}</button>
            ))}
          </div>
        </Field>
      ) : (
        <Field label="Give this resource a description" stacked>
          <textarea className="exo-dc-modal-input" rows={2} value={draft.description ?? ''} maxLength={MAX_DESCRIPTION} placeholder="Ex: Rules for the server" aria-label="Description"
            onChange={event => set({ description: event.target.value })} />
        </Field>
      )}
      <Field label="Emoji" detail="Optional, shown beside it."><EmojiField emoji={draft.emoji ?? null} onChange={emoji => set({ emoji })} /></Field>
      {error ? <p className="exo-dialog-error" role="alert">{error}</p> : null}
    </Dialog>
  )
}

/** An emoji to show beside something: a server emoji Discord has stays (or goes); otherwise type one. */
export function EmojiField({ emoji, onChange }: { emoji: Emoji | null; onChange: (emoji: Emoji | null) => void }) {
  if (emoji?.id) {
    return (
      <span className="exo-dc-settings-buttons">
        <ReactionEmoji reaction={{ emoji: { id: emoji.id, name: emoji.name ?? '', animated: emoji.animated === true } }} size={24} />
        <button type="button" className="exo-set-btn is-compact" onClick={() => onChange(null)}>Remove</button>
      </span>
    )
  }
  return (
    <input className="exo-dc-modal-input exo-dc-field is-number" value={emoji?.name ?? ''} maxLength={8} placeholder="Emoji" aria-label="Emoji"
      onChange={event => onChange(event.target.value.trim() ? { name: event.target.value.trim() } : null)} />
  )
}
