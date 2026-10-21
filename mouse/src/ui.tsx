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
