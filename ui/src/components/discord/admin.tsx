import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type RefObject } from 'react'
import { host } from '../../lib/host'
import { focusableSelector, trapTab } from '../../lib/focusTrap'
import { report } from '../../lib/report'
import { ModalBack } from './SettingsLayer'

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
  return <p className="exo-dc-note is-bad" role="alert">{error} <button type="button" className="exo-dc-tab is-raised" onClick={onRetry}>Try again</button></p>
}

// Open dialogs and menus, the newest last: Escape and Tab belong to the top one only.
const layers: object[] = []

/**
 * One layer over the room (a dialog, Server Settings, a menu): Escape runs onEscape, Tab stays inside it,
 * focus starts in it and goes back where it was when it closes. A layer under another
 * waits, and Escape in an open dropdown closes only the dropdown.
 */
export function useLayer(ref: RefObject<HTMLElement | null>, onEscape: () => void, on = true) {
  const escape = useRef(onEscape)
  useEffect(() => { escape.current = onEscape })
  useEffect(() => {
    if (!on) return
    const me = {}
    layers.push(me)
    const back = document.activeElement instanceof HTMLElement ? document.activeElement : null
    const frame = window.requestAnimationFrame(() => {
      const box = ref.current
      if (!box || box.contains(document.activeElement)) return
      // A field that asks for it, else the dialog itself (Tab goes on from there), else a menu's first row.
      const first = box.querySelector<HTMLElement>('[autofocus], [data-autofocus]') ?? (box.tabIndex === -1 ? box : box.querySelector<HTMLElement>(focusableSelector))
      ;(first ?? box).focus({ preventScroll: true })
    })
    const key = (event: KeyboardEvent) => {
      if (layers.at(-1) !== me || !ref.current) return
      if (event.key === 'Tab') trapTab(ref.current, event)
      if (event.key !== 'Escape' || event.defaultPrevented || (event.target as Element | null)?.closest?.(':popover-open')) return
      event.preventDefault()
      event.stopPropagation()
      escape.current()
    }
    window.addEventListener('keydown', key, true)
    return () => {
      window.cancelAnimationFrame(frame)
      window.removeEventListener('keydown', key, true)
      layers.splice(layers.indexOf(me), 1)
      if (back?.isConnected) back.focus({ preventScroll: true })
    }
  }, [ref, on])
}

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
      {error ? <span className="exo-dc-control">{children}<span className="exo-dc-field-error" role="alert">{error}</span></span> : children}
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
      <button type="button" className="exo-set-btn is-primary" disabled={busy} onClick={onSave}>Save changes</button>
    </div>
  )
}

/** A question before something that can't be taken back, Discord's way: the title, why, Cancel and the action. */
export function Confirm({ title, children, action, danger = true, busy, error, onConfirm, onClose }: {
  title: string
  children?: React.ReactNode
  action: string
  danger?: boolean
  busy?: boolean
  error?: string | null
  onConfirm: () => void
  onClose: () => void
}) {
  const box = useRef<HTMLDivElement>(null)
  useLayer(box, onClose)
  return (
    <ModalBack top onClose={onClose}>
      <div ref={box} className="exo-dc-modal exo-dc-confirm" role="dialog" aria-modal="true" aria-label={title} tabIndex={-1} onClick={event => event.stopPropagation()}>
        <strong>{title}</strong>
        {children}
        {error ? <p className="is-bad" role="alert">{error}</p> : null}
        <div className="exo-dc-modal-row">
          <button type="button" className="exo-dc-tab" onClick={onClose}>Cancel</button>
          <button type="button" className={`exo-set-btn ${danger ? 'is-danger' : 'is-primary'}`} disabled={busy} onClick={onConfirm}>{action}</button>
        </div>
      </div>
    </ModalBack>
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
