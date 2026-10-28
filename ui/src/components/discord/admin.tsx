import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState } from 'react'
import { host } from '../../lib/host'
import { useLayer } from '../../lib/layer'
import { report } from '../../lib/report'
import { Spinner } from '../ui/Spinner'
import { ConfirmDialog } from '../ui/Dialog'
import { CopyLogs, ErrorNote } from '../ui/ErrorNote'

/** Discord wants the person to verify first: the check's id (the ticket stays in Exo's host) and the ways Discord offers. */
export type MfaAsk = { id: string; methods: string[] }

/** One discord.admin reply: data is Discord's own (or Exo's summary for the person menu and Edit Channel). */
export type AdminReply<T> = { ok: boolean; message?: string | null; data?: T | null; mfa?: MfaAsk | null }

type MfaShow = (ask: MfaAsk, done: (reply: AdminReply<unknown>) => void) => void
let mfaShow: MfaShow | null = null

/** The room's verify dialog (MfaHost) takes Discord's checks while it is mounted. */
export function onMfaAsk(show: MfaShow) {
  mfaShow = show
  return () => { if (mfaShow === show) mfaShow = null }
}

export async function admin<T = unknown>(action: string, params: Record<string, unknown>): Promise<AdminReply<T>> {
  try {
    const reply = await host.discordAdmin(action, params) as AdminReply<T>
    // Discord's "it's really you" check: its dialog, then the same request's own answer (or the cancel).
    const show = mfaShow
    if (reply.mfa && show) return await new Promise<AdminReply<unknown>>(resolve => show(reply.mfa!, resolve)) as AdminReply<T>
    return reply
  } catch (error) {
    report(error, 'discord.admin.' + action)
    return { ok: false, message: 'Discord did not answer. Try again.' }
  }
}

/** One of discord.admin's reads, kept while open; reload asks Discord again, set changes it in place. */
export function useAdmin<T>(action: string, params: Record<string, unknown>) {
  const key = JSON.stringify(params)
  const [data, setData] = useState<T | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [tick, setTick] = useState(0)
  useEffect(() => {
    let live = true
    void admin<T>(action, JSON.parse(key) as Record<string, unknown>).then(result => {
      if (!live) return
      if (result.ok) { setData(result.data ?? null); setError(null) } else setError(result.message ?? 'Discord did not answer.')
    })
    return () => { live = false }
  }, [action, key, tick])
  return { data, error, set: setData, reload: useCallback(() => setTick(t => t + 1), []) }
}

/** Lets the host drop a picture a save didn't take, so it stops counting toward the upload limit. */
export function dropUpload(uploadId: string | undefined) {
  if (uploadId) void host.discordUpload({ uploadId, cancel: true }).catch(() => { })
}

/** A read that failed: why, and Try Again. A page keeps showing what it had. */
export function ReadError({ error, onRetry }: { error: string | null; onRetry: () => void }) {
  if (!error) return null
  return <p className="exo-dc-note is-bad" role="alert">{error} <button type="button" className="exo-dc-tab is-raised" onClick={onRetry}>Try again</button> <CopyLogs error={error} /></p>
}

/** The page's layers (lib/layer.ts): kept here too, where the Discord room has always found it. */
export { useLayer }

type Unsaved = { mark: (dirty: boolean) => void; nudge: number; guard: (leave: () => void) => void }
const UnsavedContext = createContext<Unsaved | null>(null)
export const UnsavedProvider = UnsavedContext.Provider

/** An overlay's unsaved changes, Discord's way: leaving while its save bar shows shakes the bar red instead. */
export function useUnsaved(): Unsaved {
  const dirty = useRef(false)
  const [nudge, setNudge] = useState(0)
  const mark = useCallback((on: boolean) => { dirty.current = on; if (!on) setNudge(0) }, [])
  const guard = useCallback((leave: () => void) => { if (dirty.current) setNudge(n => n + 1); else leave() }, [])
  return useMemo(() => ({ mark, nudge, guard }), [mark, nudge, guard])
}

/** Leave this page only when nothing is unsaved (else the save bar shakes). */
export function useGuard() {
  return useContext(UnsavedContext)?.guard ?? ((leave: () => void) => leave())
}

/** A settings row, Discord's label and line over its control, in Exo's rows; an error shows under the control. */
export function Field({ label, detail, children, stacked, error }: { label: string; detail?: string; children?: React.ReactNode; stacked?: boolean; error?: string | null }) {
  return (
    <div className={`exo-dc-settings-row${stacked ? ' is-stacked' : ''}`}>
      <span className="exo-dc-settings-copy"><strong>{label}</strong>{detail ? <span>{detail}</span> : null}</span>
      {error ? <span className="exo-dc-control">{children}<ErrorNote as="span" className="exo-dc-field-error" message={error} /></span> : children}
    </div>
  )
}

/** Choices to pick any of, as chips: AutoMod's exemptions, an onboarding answer's channels and roles. */
export function PickChips({ label, detail, ids, options, onChange }: {
  label: string
  detail?: string
  ids: string[]
  options: { id: string; name: string; color?: string }[]
  onChange: (ids: string[]) => void
}) {
  return (
    <section className="exo-dc-perm-group" aria-label={label}>
      <h3>{label}</h3>
      {detail ? <p className="exo-dc-note">{detail}</p> : null}
      <div className="exo-dc-chip-list">
        {options.map(option => (
          <button key={option.id} type="button" className="exo-shelf-chip" aria-pressed={ids.includes(option.id)}
            onClick={() => onChange(ids.includes(option.id) ? ids.filter(id => id !== option.id) : [...ids, option.id])}>
            {option.color ? <span className="exo-dc-role-dot" style={{ background: option.color }} aria-hidden="true" /> : null}
            {option.name}
          </button>
        ))}
      </div>
    </section>
  )
}

/** Discord's bar for unsaved changes: Reset and Save Changes, with the error under it when Discord refused. */
export function SaveBar({ shown, busy, error, onReset, onSave }: { shown: boolean; busy: boolean; error: string | null; onReset: () => void; onSave: () => void }) {
  const unsaved = useContext(UnsavedContext)
  const mark = unsaved?.mark
  useEffect(() => {
    mark?.(shown)
    return () => mark?.(false)
  }, [mark, shown])
  if (!shown) return null
  const nudge = unsaved?.nudge ?? 0
  // A new key each nudge plays the shake again.
  return (
    <div key={nudge} className={`exo-dc-savebar exo-glass${nudge ? ' is-nudged' : ''}`} role="status">
      <span>{error ?? 'Careful — you have unsaved changes!'}</span>
      <button type="button" className="exo-dc-tab" disabled={busy} onClick={onReset}>Reset</button>
      <button type="button" className="exo-set-btn is-primary" disabled={busy} onClick={onSave}>
        {busy ? <Spinner label="Saving changes" /> : null}Save changes
      </button>
    </div>
  )
}

/** A question before something that can't be taken back, in Discord's room: Exo's one dialog (ui/Dialog.tsx), drawn in
 * the room so its words keep Discord's colors. */
export function Confirm({ title, children, action, danger = true, busy, disabled, error, form, onConfirm, onClose }: {
  title: string
  children?: React.ReactNode
  action: string
  danger?: boolean
  /** The action is running: its ring shows before its words. */
  busy?: boolean
  /** The action can't be taken yet (a field still empty); nothing is running. */
  disabled?: boolean
  error?: string | null
  /** Its body holds fields (a name, a reason). */
  form?: boolean
  onConfirm: () => void
  onClose: () => void
}) {
  return (
    <ConfirmDialog title={title} action={action} danger={danger} busy={busy} disabled={disabled} error={error} form={form} portal={false}
      onConfirm={onConfirm} onClose={onClose}>{children}</ConfirmDialog>
  )
}

/** Discord's ask before accepting a request from someone you share no server with (it refuses the plain accept, 80013). */
export function StrangerConfirm({ name, busy, error, onConfirm, onClose }: { name: string; busy?: boolean; error?: string | null; onConfirm: () => void; onClose: () => void }) {
  return (
    <Confirm title="Accept friend request?" action="Accept" danger={false} busy={busy} error={error} onConfirm={onConfirm} onClose={onClose}>
      <p>You don't share a server with <strong>{name}</strong>. Only accept friend requests from people you know.</p>
    </Confirm>
  )
}

/** Opens Windows' file picker for one file of these types. */
export function pickFile(accept: string): Promise<File | null> {
  return new Promise(resolve => {
    const input = document.createElement('input')
    input.type = 'file'
    input.accept = accept
    input.onchange = () => resolve(input.files?.[0] ?? null)
    input.oncancel = () => resolve(null)
    input.click()
  })
}

/** A picture or a file of yours, as a data URL to show before it's saved. */
export function previewOf(file: File): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader()
    reader.onload = () => resolve(String(reader.result))
    reader.onerror = () => reject(reader.error)
    reader.readAsDataURL(file)
  })
}

/** A Discord color number as CSS (the muted gray for no color). */
export const colorOf = (color: number | null | undefined) => color ? `#${color.toString(16).padStart(6, '0')}` : 'var(--dc-muted)'

/** A name as Discord shows people: server nickname, else display name, else username. */
export const personName = (user: { username: string; global_name?: string | null } | null | undefined, nick?: string | null) =>
  nick || user?.global_name || user?.username || 'Someone'

/** "3 days", "5 hours": how long until a time, for invite expiry. */
export function untilLabel(iso: string | null | undefined) {
  if (!iso) return 'Never'
  const ms = Date.parse(iso) - Date.now()
  if (ms <= 0) return 'Expired'
  const minutes = Math.round(ms / 60_000)
  if (minutes < 60) return `${minutes} min`
  const hours = Math.round(minutes / 60)
  return hours < 48 ? `${hours} hours` : `${Math.round(hours / 24)} days`
}
