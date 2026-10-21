import { useEffect, useRef, useState } from 'react'
import type { DiscordChannel } from '../../lib/discord'
import { Switch } from '../Switch'
import { Choice } from '../settings/Choice'
import { admin, colorOf, Confirm, Field, ReadError, useAdmin } from './admin'

type Action = { type: number; metadata?: { custom_message?: string; channel_id?: string; duration_seconds?: number } }
type Meta = { keyword_filter?: string[]; regex_patterns?: string[]; allow_list?: string[]; presets?: number[]; mention_total_limit?: number; mention_raid_protection_enabled?: boolean }
type Rule = { id?: string; name: string; event_type: number; trigger_type: number; trigger_metadata?: Meta; actions: Action[]; enabled: boolean; exempt_roles: string[]; exempt_channels: string[] }
type Role = { id: string; name: string; color: number; position: number; managed: boolean }

// The rules Discord's AutoMod offers: their default names, how many a server may have, what each can do and does by default.
const KINDS: Record<number, { name: string; event: number; max: number; actions: number[]; meta?: Meta; about: string }> = {
  5: { name: 'Block Mention Spam', event: 1, max: 1, actions: [1, 2, 3], meta: { mention_total_limit: 20, mention_raid_protection_enabled: false }, about: 'Block messages with too many mentions of people or roles.' },
  3: { name: 'Block Suspected Spam Content', event: 1, max: 1, actions: [1, 2], about: 'Block messages Discord detects as spam.' },
  4: { name: 'Block Commonly Flagged Words', event: 1, max: 1, actions: [1, 2], meta: { allow_list: [], presets: [] }, about: 'Block words from Discord\'s lists of commonly flagged words.' },
  1: { name: 'Block Custom Words', event: 1, max: 6, actions: [1, 2, 3], meta: { keyword_filter: [], regex_patterns: [], allow_list: [] }, about: 'Block messages with words or phrases you choose.' },
  6: { name: 'Block Words in Member Profile Names', event: 2, max: 1, actions: [4, 2], meta: { keyword_filter: [], regex_patterns: [], allow_list: [] }, about: 'Stop members with these words in their names from chatting until they change them.' },
}
// Discord's order: what messages say (mention spam, spam, flagged words, your words), then members' names.
const ORDER = [5, 3, 4, 1, 6]
const ACTION_NAMES: Record<number, string> = { 1: 'Block message', 2: 'Send alert', 3: 'Timeout member', 4: 'Block member interactions' }
// Discord's lists: PROFANITY 1, SEXUAL_CONTENT 2, SLURS 3.
const PRESETS = [{ id: 1, name: 'Severe Profanity' }, { id: 3, name: 'Insults & Slurs' }, { id: 2, name: 'Sexual Content' }]
const DURATIONS = [{ value: '60', label: '60 seconds' }, { value: '300', label: '5 minutes' }, { value: '600', label: '10 minutes' }, { value: '3600', label: '1 hour' }, { value: '86400', label: '1 day' }, { value: '604800', label: '1 week' }]

// Words are separated by commas or lines; a regex only by lines (a pattern may hold a comma).
const lines = (text: string, regex = false) => text.split(regex ? /\n/ : /[\n,]/).map(s => s.trim()).filter(Boolean)

/**
 * Discord's AutoMod: each rule with its switch, edit and delete, and new rules of every kind Discord offers.
 * A rule opens for editing where it is (a new one under its kind), and what Discord refused is said there.
 */
export function AutoMod({ guildId, channels }: { guildId: string; channels: DiscordChannel[] }) {
  const rules = useAdmin<Rule[]>('automod', { guildId })
  const roles = useAdmin<{ roles: Role[] }>('roles', { guildId })
  const [editing, setEditing] = useState<Rule | null>(null)
  const [deleting, setDeleting] = useState<Rule | null>(null)
  const [busy, setBusy] = useState(false)
  // What Discord refused: the open editor's save (key "editor") or a rule's switch or delete (its id).
  const [failed, setFailed] = useState<{ key: string; message: string } | null>(null)
  const list = rules.data ?? []
  // One save at a time: a second click on Save Rule never makes a second rule.
  const save = async (rule: Rule, key: string) => {
    if (busy) return false
    setBusy(true); setFailed(null)
    const result = await admin<Rule>('saveAutomod', { guildId, rule })
    setBusy(false)
    if (!result.ok) { setFailed({ key, message: result.message ?? 'Discord did not save that rule.' }); return false }
    rules.reload()
    return true
  }
  const remove = (rule: Rule) => {
    setBusy(true)
    void admin('deleteAutomod', { guildId, ruleId: rule.id }).then(result => {
      setBusy(false); setDeleting(null)
      if (!result.ok) { setFailed({ key: rule.id ?? '', message: result.message ?? 'Discord did not delete that rule.' }); return }
      rules.reload()
    })
  }
  const open = (rule: Rule | null) => { setFailed(null); setEditing(rule) }
  const start = (kind: number) => {
    const k = KINDS[kind]
    open({ name: k.name, event_type: k.event, trigger_type: kind, trigger_metadata: k.meta ? structuredClone(k.meta) : undefined, actions: [{ type: k.actions[0], metadata: {} }], enabled: true, exempt_roles: [], exempt_channels: [] })
  }
  const roleList = (roles.data?.roles ?? []).filter(r => r.id !== guildId).sort((a, b) => b.position - a.position)
  const editor = editing ? (
    <RuleEditor key={editing.id ?? 'new'} rule={editing} roles={roleList} channels={channels} busy={busy} error={failed?.key === 'editor' ? failed.message : null}
      onCancel={() => open(null)} onSave={rule => void save(rule, 'editor').then(ok => { if (ok) setEditing(null) })} />
  ) : null
  return (
    <>
      <h1>AutoMod</h1>
      <p className="exo-dc-note">Keep your server safe with rules that act on their own, around the clock.</p>
      <ReadError error={rules.error} onRetry={rules.reload} />
      {!rules.data && !rules.error ? <p className="exo-dc-note">Loading</p> : null}
      {ORDER.map(type => {
        const kind = KINDS[type]
        const own = list.filter(r => r.trigger_type === type)
        return (
          <section key={type} className="exo-dc-perm-group" aria-label={kind.name}>
            <h3>{kind.name}</h3>
            <p className="exo-dc-note">{kind.about}</p>
            {own.map(rule => editing?.id === rule.id ? <div key={rule.id}>{editor}</div> : (
              <Field key={rule.id} label={rule.name} detail={rule.actions.map(a => ACTION_NAMES[a.type]).filter(Boolean).join(', ')} error={failed && failed.key === rule.id ? failed.message : null}>
                <span className="exo-dc-row-actions">
                  <button type="button" className="exo-dc-tab" onClick={() => open(structuredClone(rule))}>Edit</button>
                  <button type="button" className="exo-dc-tab is-danger" onClick={() => setDeleting(rule)}>Delete</button>
                  <Switch checked={rule.enabled} disabled={busy} label={`${rule.name} enabled`} onChange={enabled => void save({ ...rule, enabled }, rule.id ?? '')} />
                </span>
              </Field>
            ))}
            {editing && !editing.id && editing.trigger_type === type ? editor
              : own.length < kind.max && rules.data ? <button type="button" className="exo-dc-tab is-raised" onClick={() => start(type)}>{own.length ? 'Create another rule' : 'Set Up'}</button> : null}
          </section>
        )
      })}
      {deleting ? (
        <Confirm title="Delete Rule" action="Delete Rule" busy={busy} onConfirm={() => remove(deleting)} onClose={() => setDeleting(null)}>
          <p>Are you sure you want to delete {deleting.name}? AutoMod stops acting on it right away.</p>
        </Confirm>
      ) : null}
    </>
  )
}

function RuleEditor({ rule, roles, channels, busy, error, onCancel, onSave }: {
  rule: Rule
  roles: Role[]
  channels: DiscordChannel[]
  busy: boolean
  error: string | null
  onCancel: () => void
  onSave: (rule: Rule) => void
}) {
  const [draft, setDraft] = useState(rule)
  const box = useRef<HTMLDivElement>(null)
  // Opened where the rule is; on screen even when that is far down the page.
  useEffect(() => { box.current?.scrollIntoView({ block: 'nearest' }) }, [])
  const kind = KINDS[draft.trigger_type]
  const meta = draft.trigger_metadata ?? {}
  const setMeta = (change: Meta) => setDraft(d => ({ ...d, trigger_metadata: { ...d.trigger_metadata, ...change } }))
  const alert = draft.actions.find(a => a.type === 2)
  const ready = draft.name.trim().length > 0 && draft.actions.length > 0 && (!alert || !!alert.metadata?.channel_id)
  return (
    <div ref={box} className="exo-dc-rule-editor" role="group" aria-label={`Edit ${draft.name}`}>
      <Field label="Rule Name">
        <input className="exo-dc-modal-input exo-dc-field" value={draft.name} maxLength={100} aria-label="Rule Name" onChange={event => setDraft(d => ({ ...d, name: event.target.value }))} />
      </Field>
      <TriggerFields type={draft.trigger_type} meta={meta} setMeta={setMeta} />
      <ActionFields kind={kind.actions} actions={draft.actions} channels={channels} onChange={actions => setDraft(d => ({ ...d, actions }))} />
      <Exempt label="Allow these roles" ids={draft.exempt_roles} options={roles.map(r => ({ id: r.id, name: r.name, color: colorOf(r.color) }))}
        onChange={exempt_roles => setDraft(d => ({ ...d, exempt_roles }))} />
      {draft.event_type === 1 ? (
        <Exempt label="Allow in these channels" ids={draft.exempt_channels} options={channels.filter(c => c.kind !== 'category').map(c => ({ id: c.id, name: `#${c.name}` }))}
          onChange={exempt_channels => setDraft(d => ({ ...d, exempt_channels }))} />
      ) : null}
      {error ? <p className="exo-dc-field-error" role="alert">{error}</p> : null}
      <div className="exo-dc-modal-row">
        <button type="button" className="exo-dc-tab" onClick={onCancel}>Cancel</button>
        <button type="button" className="button button--primary" disabled={!ready || busy} onClick={() => onSave(draft)}>{busy ? 'Saving' : 'Save Rule'}</button>
      </div>
    </div>
  )
}

// The text stays as typed (it can be cleared and retyped); the rule takes 1 to 50.
function MentionLimit({ value, onChange }: { value: number; onChange: (limit: number) => void }) {
  const [text, setText] = useState(String(value))
  const clamp = (raw: string) => Math.max(1, Math.min(50, Number(raw) || 1))
  return (
    <input type="number" className="exo-dc-modal-input exo-dc-field is-number" min={1} max={50} value={text} aria-label="Mention limit"
      onChange={event => { setText(event.target.value); if (/^\d+$/.test(event.target.value)) onChange(clamp(event.target.value)) }}
      onBlur={() => { const limit = clamp(text); setText(String(limit)); onChange(limit) }} />
  )
}

// What a rule watches: words, patterns and exceptions; Discord's lists; or how many mentions.
function TriggerFields({ type, meta, setMeta }: { type: number; meta: Meta; setMeta: (change: Meta) => void }) {
  if (type === 5) {
    return (
      <>
        <Field label="Mention limit" detail="Block messages that mention more than this many people or roles (up to 50).">
          <MentionLimit value={meta.mention_total_limit ?? 20} onChange={limit => setMeta({ mention_total_limit: limit })} />
        </Field>
        <Field label="Mention raid protection" detail="Block raids of mentions across many messages too.">
          <Switch checked={meta.mention_raid_protection_enabled === true} label="Mention raid protection" onChange={on => setMeta({ mention_raid_protection_enabled: on })} />
        </Field>
      </>
    )
  }
  if (type === 3) return null
  return (
    <>
      {type === 4 ? PRESETS.map(preset => (
        <Field key={preset.id} label={preset.name}>
          <Switch checked={meta.presets?.includes(preset.id) === true} label={preset.name}
            onChange={on => setMeta({ presets: on ? [...(meta.presets ?? []), preset.id] : (meta.presets ?? []).filter(p => p !== preset.id) })} />
        </Field>
      )) : (
        <>
          <ListField label="Words" detail="Separate words or phrases with a comma. Use * for partial matches." values={meta.keyword_filter} onChange={keyword_filter => setMeta({ keyword_filter })} />
          <ListField label="Regex patterns" detail="One Rust-flavored regex per line, up to 10." values={meta.regex_patterns} regex onChange={regex_patterns => setMeta({ regex_patterns })} />
        </>
      )}
      <ListField label="Allowed words" detail="Words or phrases this rule never blocks, comma separated." values={meta.allow_list} onChange={allow_list => setMeta({ allow_list })} />
    </>
  )
}

function ListField({ label, detail, values, regex = false, onChange }: { label: string; detail: string; values?: string[]; regex?: boolean; onChange: (values: string[]) => void }) {
  const [text, setText] = useState((values ?? []).join(regex ? '\n' : ', '))
  return (
    <Field label={label} detail={detail} stacked>
      <textarea className="exo-dc-modal-input exo-dc-field is-wide" rows={3} value={text} aria-label={label}
        onChange={event => { setText(event.target.value); onChange(lines(event.target.value, regex)) }} />
    </Field>
  )
}

function ActionFields({ kind, actions, channels, onChange }: { kind: number[]; actions: Action[]; channels: DiscordChannel[]; onChange: (actions: Action[]) => void }) {
  const has = (type: number) => actions.find(a => a.type === type)
  const toggle = (type: number, on: boolean) => onChange(on ? [...actions, { type, metadata: type === 3 ? { duration_seconds: 60 } : {} }] : actions.filter(a => a.type !== type))
  const meta = (type: number, metadata: Action['metadata']) => onChange(actions.map(a => a.type === type ? { ...a, metadata: { ...a.metadata, ...metadata } } : a))
  return (
    <section className="exo-dc-perm-group" aria-label="Actions">
      <h3>Actions</h3>
      {kind.map(type => (
        <Field key={type} label={ACTION_NAMES[type]}>
          <span className="exo-dc-row-actions">
            {type === 1 && has(1) ? (
              <input className="exo-dc-modal-input exo-dc-field" placeholder="Custom response (optional)" maxLength={150} value={has(1)?.metadata?.custom_message ?? ''}
                aria-label="Custom response" onChange={event => meta(1, { custom_message: event.target.value || undefined })} />
            ) : null}
            {type === 2 && has(2) ? (
              <Choice label="Alert channel" value={has(2)?.metadata?.channel_id ?? 'none'}
                choices={[{ value: 'none', label: 'Pick a channel' }, ...channels.filter(c => c.kind === 'text').map(c => ({ value: c.id, label: `#${c.name}` }))]}
                onChange={id => meta(2, { channel_id: id === 'none' ? undefined : id })} />
            ) : null}
            {type === 3 && has(3) ? (
              <Choice label="Time-out length" value={String(has(3)?.metadata?.duration_seconds ?? 60)} choices={DURATIONS} onChange={s => meta(3, { duration_seconds: Number(s) })} />
            ) : null}
            <Switch checked={!!has(type)} label={ACTION_NAMES[type]} onChange={on => toggle(type, on)} />
          </span>
        </Field>
      ))}
    </section>
  )
}

function Exempt({ label, ids, options, onChange }: { label: string; ids: string[]; options: { id: string; name: string; color?: string }[]; onChange: (ids: string[]) => void }) {
  return (
    <section className="exo-dc-perm-group" aria-label={label}>
      <h3>{label}</h3>
      <div className="exo-dc-chip-list">
        {options.map(option => (
          <label key={option.id} className="exo-dc-check-chip">
            <input type="checkbox" checked={ids.includes(option.id)} onChange={event => onChange(event.target.checked ? [...ids, option.id] : ids.filter(id => id !== option.id))} />
            {option.color ? <span className="exo-dc-role-dot" style={{ background: option.color }} aria-hidden="true" /> : null}
            {option.name}
          </label>
        ))}
      </div>
    </section>
  )
}
