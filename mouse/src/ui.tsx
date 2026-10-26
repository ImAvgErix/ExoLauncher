import { Fragment, useEffect, useId, useRef, useState, type CSSProperties, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { useGlide } from './glide'
import { groupOf } from './section'

/** A section: its name is the only bold type, its rows ruled by hairlines; it rises in when it appears. */
export function Panel({ title, actions, children }: { title: string; children?: ReactNode; /** Buttons on the title's own line, for a panel that is only a few actions. */ actions?: ReactNode }) {
  const id = useId()
  return (
    <section className={`m-panel m-arrive${actions ? ' has-actions' : ''}`} aria-labelledby={id} data-group={groupOf(title)}>
      {actions ? (
        <div className="m-panel-head">
          <h2 id={id} className="m-panel-title">{title}</h2>
          <span className="m-actions">{actions}</span>
        </div>
      ) : <h2 id={id} className="m-panel-title">{title}</h2>}
      {children}
    </section>
  )
}

/** Label left, control on the shared right edge, a hairline above. */
export function Row({ label, value, children }: { label: string; value?: string; children: ReactNode }) {
  return (
    <div className="m-row">
      <span className="m-row-label">{label}{value ? <span className="m-row-value">{value}</span> : null}</span>
      <span className="m-row-control">{children}</span>
    </div>
  )
}

/** One pill; the current option filled, the fill gliding to a new choice. */
export function Segmented<T extends string | number>({ label, options, value, onChange, disabled = false }: {
  label: string
  /** color: a dot before the label (a DPI stage's LED color). */
  options: readonly { value: T; label: string; disabled?: boolean; color?: string }[]
  value: T | null | undefined
  onChange: (value: T) => void
  disabled?: boolean
}) {
  const glide = useGlide<HTMLSpanElement>()
  return (
    <span ref={glide} className="m-seg m-glide" role="radiogroup" aria-label={label}>
      {options.map(o => (
        <button
          key={String(o.value)}
          type="button"
          role="radio"
          aria-checked={o.value === value}
          className={`m-seg-item${o.value === value ? ' is-on' : ''}`}
          disabled={disabled || o.disabled}
          onClick={() => o.value !== value && onChange(o.value)}
        >
          {o.color ? <span className="m-seg-dot" style={{ background: o.color }} /> : null}
          {o.label}
        </button>
      ))}
    </span>
  )
}

/** Exo's switch: a dark track, white when on. */
export function Switch({ label, checked, onChange, disabled = false }: { label: string; checked: boolean; onChange: (on: boolean) => void; disabled?: boolean }) {
  return (
    <button type="button" role="switch" aria-checked={checked} aria-label={label} className="m-switch" disabled={disabled} onClick={() => onChange(!checked)}>
      <span className="m-switch-knob" />
    </button>
  )
}

/**
 * Exo's bar (Exo's components/ui/Progress.tsx: the same props and markup; mouse.css › Progress), only for work with a
 * measure (a timed test): a glass track holding the ink's fill with a sheen and a lit head at its end; with no number,
 * a lit segment glides along the track. A wait without a measure is the ring (Spinner) or the shine (mouse.css › Waits).
 */
export function Progress({ value, label, size, edge, tone, className }: {
  /** How far along, 0 to 1; null or undefined while that isn't known. */
  value?: number | null
  /** What is happening, for screen readers. */
  label: string
  /** small: on a card or art; large: a window's one bar. */
  size?: 'small' | 'large'
  /** Along the inside foot of what holds it (a button, the mouse's picture). */
  edge?: boolean
  /** done: it finished (green); bad: it stopped short (red). */
  tone?: 'done' | 'bad'
  className?: string
}) {
  const known = typeof value === 'number' && Number.isFinite(value)
  const fraction = known ? Math.min(1, Math.max(0, value)) : 0
  const classes = ['m-progress', !known && 'is-unknown', size && `is-${size}`, edge && 'is-edge', tone && `is-${tone}`, className]
  return (
    <span
      role="progressbar"
      aria-label={label}
      aria-valuemin={known ? 0 : undefined}
      aria-valuemax={known ? 100 : undefined}
      aria-valuenow={known ? Math.round(fraction * 100) : undefined}
      className={classes.filter(Boolean).join(' ')}
      style={{ '--m-progress': fraction } as CSSProperties}
    >
      <span className="m-progress-track"><span className="m-progress-fill" /></span>
      <span className="m-progress-head" aria-hidden="true" />
    </span>
  )
}

/**
 * Exo's ring (Exo's components/ui/Spinner.tsx; mouse.css › Waits): a short wait inside a control of words, before
 * them, in their color; a status with its label for screen readers.
 */
export function Spinner({ size = 14, label }: { size?: number; label?: string }) {
  return (
    <svg className="m-spinner" width={size} height={size} viewBox="0 0 16 16" fill="none"
      role={label ? 'status' : undefined} aria-label={label} aria-hidden={label ? undefined : true}>
      <circle cx="8" cy="8" r="6.25" stroke="currentColor" strokeOpacity="0.22" strokeWidth="1.5" />
      <path d="M8 1.75a6.25 6.25 0 0 1 6.25 6.25" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" />
    </svg>
  )
}

/**
 * Exo's one question (Exo's components/ui/Dialog.tsx › ConfirmDialog; mouse.css › Dialog), asked only before what
 * can't be taken back: the title asks it, a line or two say what goes and what stays, then equal capsules, Cancel
 * first and focused so Enter alone never confirms, and the action named for what it does, red when it deletes.
 * Escape or the scrim cancels, Tab stays inside, and focus goes back where it was when it closes.
 */
export function Confirm({ title, body, action, danger = false, onConfirm, onClose }: {
  title: string
  body?: ReactNode
  action: string
  danger?: boolean
  onConfirm: () => void
  onClose: () => void
}) {
  const box = useRef<HTMLDivElement>(null)
  const cancel = useRef<HTMLButtonElement>(null)
  const id = useId()
  const close = useRef(onClose)
  close.current = onClose
  useEffect(() => {
    const back = document.activeElement instanceof HTMLElement ? document.activeElement : null
    cancel.current?.focus()
    const key = (e: KeyboardEvent) => {
      if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); close.current() }
      if (e.key !== 'Tab' || !box.current) return
      e.preventDefault()
      const buttons = [...box.current.querySelectorAll<HTMLElement>('button:not(:disabled)')]
      const at = buttons.indexOf(document.activeElement as HTMLElement)
      buttons[(at + (e.shiftKey ? -1 : 1) + buttons.length) % buttons.length]?.focus()
    }
    window.addEventListener('keydown', key, true)
    return () => {
      window.removeEventListener('keydown', key, true)
      if (back?.isConnected) back.focus()
    }
  }, [])
  // On the page itself: a panel's arrive animation leaves a transform that would hold a fixed overlay inside the panel.
  return createPortal(
    <div className="m-dialog-back" onMouseDown={e => { if (e.target === e.currentTarget) onClose() }}>
      <div ref={box} className="m-dialog" role="dialog" aria-modal="true" aria-labelledby={id} aria-describedby={body ? `${id}-body` : undefined}>
        <h2 id={id}>{title}</h2>
        {body ? <p id={`${id}-body`} className="m-dialog-body">{body}</p> : null}
        <div className="m-dialog-actions">
          <button ref={cancel} type="button" className="m-button" onClick={onClose}>Cancel</button>
          <button type="button" className={`m-button ${danger ? 'is-danger' : 'is-primary'}`} onClick={onConfirm}>{action}</button>
        </div>
      </div>
    </div>,
    document.body,
  )
}

/**
 * A color well that writes once, when the picker closes, so dragging through
 * colors does not send the mouse a command for each one.
 */
export function ColorField({ label, value, onCommit, disabled = false }: { label: string; value: string; onCommit: (hex: string) => void; disabled?: boolean }) {
  const [draft, setDraft] = useState(value)
  const ref = useRef<HTMLInputElement>(null)
  const committed = useRef(value)
  const commit = useRef(onCommit)
  commit.current = onCommit
  useEffect(() => { setDraft(value); committed.current = value }, [value])
  useEffect(() => {
    const input = ref.current
    if (!input) return
    const done = () => {
      const next = input.value.toLowerCase()
      if (next !== committed.current.toLowerCase()) commit.current(next)
    }
    input.addEventListener('change', done)
    return () => input.removeEventListener('change', done)
  }, [])
  return <input ref={ref} type="color" className="m-color" aria-label={label} value={draft} disabled={disabled} onChange={e => setDraft(e.target.value)} />
}

/**
 * A slider that follows the pointer and writes once, on release (or on the
 * last arrow key), so dragging does not send the mouse a command per pixel.
 */
export function Slider({ label, min, max, step, value, format, onCommit, disabled = false }: {
  label: string
  min: number
  max: number
  step: number
  value: number
  format?: (v: number) => string
  onCommit: (v: number) => void
  disabled?: boolean
}) {
  const [draft, setDraft] = useState(value)
  const [held, setHeld] = useState(false)
  useEffect(() => { if (!held) setDraft(value) }, [value, held])
  const commit = () => {
    setHeld(false)
    if (draft !== value) onCommit(draft)
  }
  return (
    <span className="m-slider">
      <input
        type="range"
        aria-label={label}
        min={min}
        max={max}
        step={step}
        value={draft}
        disabled={disabled}
        style={{ '--fill': `${((draft - min) / (max - min || 1)) * 100}%` } as CSSProperties}
        onPointerDown={() => setHeld(true)}
        onChange={e => setDraft(Number(e.target.value))}
        onPointerUp={commit}
        onKeyUp={commit}
        onBlur={() => held && commit()}
      />
      {format ? <span className="m-slider-value">{format(draft)}</span> : null}
    </span>
  )
}

/** A number typed in: written on Enter or when the field loses focus, clamped and snapped to the step. */
export function NumberField({ label, min, max, step, value, suffix, onCommit, disabled = false }: {
  label: string
  min: number
  max: number
  step: number
  value: number
  suffix?: string
  onCommit: (v: number) => void
  disabled?: boolean
}) {
  const [text, setText] = useState(String(value))
  useEffect(() => setText(String(value)), [value])
  const commit = () => {
    const n = Number(text)
    if (!Number.isFinite(n)) { setText(String(value)); return }
    const snapped = Math.min(max, Math.max(min, Math.round(n / step) * step))
    setText(String(snapped))
    if (snapped !== value) onCommit(snapped)
  }
  return (
    <span className="m-number">
      <input
        aria-label={label}
        inputMode="numeric"
        value={text}
        disabled={disabled}
        onChange={e => setText(e.target.value.replace(/[^\d]/g, ''))}
        onBlur={commit}
        onKeyDown={e => { if (e.key === 'Enter') (e.target as HTMLInputElement).blur() }}
      />
      {suffix ? <span className="m-number-suffix">{suffix}</span> : null}
    </span>
  )
}

export function Select({ label, options, value, onChange, disabled = false }: {
  label: string
  options: readonly string[]
  value: string
  onChange: (v: string) => void
  disabled?: boolean
}) {
  return <Dropdown label={label} options={options.map(o => ({ value: o, label: o }))} value={value} onChange={onChange} disabled={disabled} placeholder={value} />
}

/** One choice in a Dropdown: what is sent, what is shown, and the heading it sits under. */
export type DropOption<T> = { value: T; label: string; group?: string; disabled?: boolean }

/**
 * Exo's dropdown: a pill like the room's other controls that opens Exo's own
 * menu (never Windows' list), below it or above when the room ends first.
 * Arrow keys move, Enter picks, Escape or a click elsewhere closes. A current
 * value that is not one of the options shows as `placeholder`.
 */
export function Dropdown<T extends string | number>({ label, options, value, onChange, disabled = false, placeholder }: {
  label: string
  options: readonly DropOption<T>[]
  value: T | null | undefined
  onChange: (value: T) => void
  disabled?: boolean
  placeholder?: string
}) {
  const button = useRef<HTMLButtonElement>(null)
  const menu = useRef<HTMLUListElement>(null)
  const listId = useId()
  const [open, setOpen] = useState(false)
  const [active, setActive] = useState(0)
  const [place, setPlace] = useState<CSSProperties>({})
  const current = options.find(o => o.value === value)
  const shown = current?.label ?? placeholder ?? (value == null ? '' : String(value))
  const pickable = options.map((o, i) => (o.disabled ? -1 : i)).filter(i => i >= 0)

  const show = () => {
    const box = button.current?.getBoundingClientRect()
    if (!box) return
    const below = window.innerHeight - box.bottom - 8
    const above = box.top - 8
    const height = Math.min(320, Math.max(below, above))
    setPlace({
      left: Math.max(8, Math.min(box.right - Math.max(box.width, 180), window.innerWidth - 8 - Math.max(box.width, 180))),
      minWidth: Math.max(box.width, 180),
      maxHeight: height,
      ...(below >= Math.min(320, options.length * 36 + 8) || below >= above ? { top: box.bottom + 4 } : { bottom: window.innerHeight - box.top + 4 }),
    })
    const at = options.findIndex(o => o.value === value)
    setActive(at >= 0 ? at : pickable[0] ?? 0)
    setOpen(true)
  }
  const close = (focus = true) => { setOpen(false); if (focus) button.current?.focus() }
  const pick = (i: number) => {
    const o = options[i]
    if (!o || o.disabled) return
    close()
    if (o.value !== value) onChange(o.value)
  }
  const step = (by: number) => {
    const at = pickable.indexOf(active)
    const next = pickable[Math.max(0, Math.min(pickable.length - 1, (at < 0 ? 0 : at) + by))]
    if (next !== undefined) setActive(next)
  }

  useEffect(() => {
    if (!open) return
    const away = (e: MouseEvent) => {
      if (!menu.current?.contains(e.target as Node) && !button.current?.contains(e.target as Node)) close(false)
    }
    const shut = () => close(false)
    document.addEventListener('mousedown', away)
    window.addEventListener('resize', shut)
    window.addEventListener('blur', shut)
    return () => {
      document.removeEventListener('mousedown', away)
      window.removeEventListener('resize', shut)
      window.removeEventListener('blur', shut)
    }
  }, [open])
  useEffect(() => {
    if (open) menu.current?.querySelector<HTMLElement>(`[data-i="${active}"]`)?.scrollIntoView({ block: 'nearest' })
  }, [open, active])

  const onKey = (e: React.KeyboardEvent) => {
    if (!open) {
      if (['ArrowDown', 'ArrowUp', 'Enter', ' '].includes(e.key)) { e.preventDefault(); show() }
      return
    }
    if (e.key === 'ArrowDown') { e.preventDefault(); step(1) }
    else if (e.key === 'ArrowUp') { e.preventDefault(); step(-1) }
    else if (e.key === 'Home') { e.preventDefault(); setActive(pickable[0] ?? 0) }
    else if (e.key === 'End') { e.preventDefault(); setActive(pickable[pickable.length - 1] ?? 0) }
    else if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); pick(active) }
    else if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); close() }
    else if (e.key === 'Tab') close(false)
  }

  return (
    <>
      <button ref={button} type="button" className="m-select" aria-label={label} aria-haspopup="listbox" aria-expanded={open}
        aria-controls={open ? listId : undefined} aria-activedescendant={open ? `${listId}-${active}` : undefined}
        disabled={disabled} onClick={() => (open ? close() : show())} onKeyDown={onKey}>
        <span className="m-select-value">{shown}</span>
        <svg className="m-select-chevron" viewBox="0 0 24 24" aria-hidden="true"><path d="M6 9l6 6l6 -6" /></svg>
      </button>
      {open ? createPortal(
        <ul ref={menu} id={listId} className="m-menu" role="listbox" aria-label={label} style={place}>
          {options.map((o, i) => (
            <Fragment key={`${String(o.value)}-${i}`}>
              {o.group && o.group !== options[i - 1]?.group ? <li className="m-menu-group" role="presentation">{o.group}</li> : null}
              <li id={`${listId}-${i}`} data-i={i} role="option" aria-selected={o.value === value} aria-disabled={o.disabled || undefined}
                className={`m-menu-item${i === active ? ' is-active' : ''}${o.value === value ? ' is-on' : ''}`}
                onMouseEnter={() => { if (!o.disabled) setActive(i) }} onMouseDown={e => e.preventDefault()} onClick={() => pick(i)}>
                {o.label}
              </li>
            </Fragment>
          ))}
        </ul>,
        document.body,
      ) : null}
    </>
  )
}
