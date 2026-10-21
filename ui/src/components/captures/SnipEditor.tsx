import { useEffect, useRef, useState, type ComponentType, type PointerEvent as ReactPointerEvent } from 'react'
import { ArrowUpRight, Blur, Crop, Highlighter, type IconProps, TextIcon, Undo } from '../../brand/icons'
import { host } from '../../lib/host'
import type { CaptureItem } from '../../lib/capture'

type Tool = 'crop' | 'arrow' | 'highlight' | 'blur' | 'text'
type Rect = { x: number; y: number; w: number; h: number }
type Mark =
  | { tool: 'arrow'; from: [number, number]; to: [number, number] }
  | { tool: 'highlight' | 'blur'; rect: Rect }
  | { tool: 'text'; at: [number, number]; text: string }
  | { tool: 'crop'; rect: Rect }

const TOOLS: { tool: Tool; label: string; icon: ComponentType<IconProps> }[] = [
  { tool: 'crop', label: 'Crop', icon: Crop },
  { tool: 'arrow', label: 'Arrow', icon: ArrowUpRight },
  { tool: 'highlight', label: 'Highlight', icon: Highlighter },
  { tool: 'blur', label: 'Blur', icon: Blur },
  { tool: 'text', label: 'Text', icon: TextIcon },
]

const ANNOTATE = '#ff5c5c'

function rectOf(a: [number, number], b: [number, number]): Rect {
  return { x: Math.min(a[0], b[0]), y: Math.min(a[1], b[1]), w: Math.abs(a[0] - b[0]), h: Math.abs(a[1] - b[1]) }
}

/** Draws the snip and every mark; the last crop decides what is kept. */
function paint(canvas: HTMLCanvasElement, image: HTMLImageElement, marks: Mark[], draft: Mark | null, forExport: boolean) {
  const all = draft ? [...marks, draft] : marks
  const crop = [...all].reverse().find(mark => mark.tool === 'crop') as Extract<Mark, { tool: 'crop' }> | undefined
  const area = forExport && crop ? crop.rect : { x: 0, y: 0, w: image.naturalWidth, h: image.naturalHeight }
  canvas.width = Math.max(1, Math.round(area.w))
  canvas.height = Math.max(1, Math.round(area.h))
  const ctx = canvas.getContext('2d')!
  ctx.setTransform(1, 0, 0, 1, -area.x, -area.y)
  ctx.drawImage(image, 0, 0)
  const unit = Math.max(2, Math.round(Math.max(image.naturalWidth, image.naturalHeight) / 400))
  for (const mark of all) {
    if (mark.tool === 'blur') {
      // Pixelated, so the hidden text can't be read back.
      const { x, y, w, h } = mark.rect
      if (w < 2 || h < 2) continue
      const block = Math.max(8, Math.round(Math.max(w, h) / 24))
      const small = document.createElement('canvas')
      small.width = Math.max(1, Math.round(w / block))
      small.height = Math.max(1, Math.round(h / block))
      small.getContext('2d')!.drawImage(canvas, x - area.x, y - area.y, w, h, 0, 0, small.width, small.height)
      ctx.imageSmoothingEnabled = false
      ctx.drawImage(small, 0, 0, small.width, small.height, x, y, w, h)
      ctx.imageSmoothingEnabled = true
    } else if (mark.tool === 'highlight') {
      ctx.save()
      ctx.globalCompositeOperation = 'multiply'
      ctx.fillStyle = 'rgba(255, 214, 10, 0.55)'
      ctx.fillRect(mark.rect.x, mark.rect.y, mark.rect.w, mark.rect.h)
      ctx.restore()
    } else if (mark.tool === 'arrow') {
      const [x1, y1] = mark.from, [x2, y2] = mark.to
      const angle = Math.atan2(y2 - y1, x2 - x1)
      const head = unit * 7
      ctx.strokeStyle = ANNOTATE
      ctx.fillStyle = ANNOTATE
      ctx.lineWidth = unit * 2
      ctx.lineCap = 'round'
      ctx.beginPath()
      ctx.moveTo(x1, y1)
      ctx.lineTo(x2 - Math.cos(angle) * head * 0.8, y2 - Math.sin(angle) * head * 0.8)
      ctx.stroke()
      ctx.beginPath()
      ctx.moveTo(x2, y2)
      ctx.lineTo(x2 - head * Math.cos(angle - 0.45), y2 - head * Math.sin(angle - 0.45))
      ctx.lineTo(x2 - head * Math.cos(angle + 0.45), y2 - head * Math.sin(angle + 0.45))
      ctx.closePath()
      ctx.fill()
    } else if (mark.tool === 'text') {
      ctx.font = `600 ${unit * 11}px "Inter Variable", "Segoe UI Variable Text", sans-serif`
      ctx.textBaseline = 'top'
      ctx.lineWidth = unit * 1.5
      ctx.strokeStyle = '#000'
      ctx.strokeText(mark.text, mark.at[0], mark.at[1])
      ctx.fillStyle = '#eee'
      ctx.fillText(mark.text, mark.at[0], mark.at[1])
    } else if (mark.tool === 'crop' && !forExport) {
      // While editing, what the crop leaves out is dimmed.
      const { x, y, w, h } = mark.rect
      ctx.fillStyle = 'rgba(0, 0, 0, 0.6)'
      ctx.fillRect(0, 0, image.naturalWidth, y)
      ctx.fillRect(0, y + h, image.naturalWidth, image.naturalHeight - y - h)
      ctx.fillRect(0, y, x, h)
      ctx.fillRect(x + w, y, image.naturalWidth - x - w, h)
      ctx.strokeStyle = '#eee'
      ctx.lineWidth = unit / 2
      ctx.strokeRect(x, y, w, h)
    }
  }
}

/**
 * The snip editor: crop, arrow, highlight, blur (pixelated) and text, with undo.
 * Save writes the result over the snip and copies it.
 */
export function SnipEditor({ item, onDone }: { item: CaptureItem; onDone: (saved: CaptureItem | null) => void }) {
  const canvas = useRef<HTMLCanvasElement>(null)
  const [image, setImage] = useState<HTMLImageElement | null>(null)
  const [tool, setTool] = useState<Tool>('arrow')
  const [marks, setMarks] = useState<Mark[]>([])
  const [draft, setDraft] = useState<Mark | null>(null)
  const [typing, setTyping] = useState<{ at: [number, number]; left: number; top: number } | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const start = useRef<[number, number] | null>(null)
  // The text field that already ended: Enter, then the blur as it leaves, adds its text once; Escape adds none.
  const finished = useRef<typeof typing>(null)
  const finish = (session: typeof typing, value: string) => {
    if (!session || finished.current === session) return
    finished.current = session
    const text = value.trim()
    if (text) setMarks(list => [...list, { tool: 'text', at: session.at, text }])
    setTyping(current => current === session ? null : current)
  }

  useEffect(() => {
    const next = new Image()
    next.crossOrigin = 'anonymous'
    next.onload = () => setImage(next)
    next.onerror = () => setError('The snip could not be opened.')
    next.src = item.url
  }, [item.url])

  useEffect(() => {
    if (image && canvas.current) paint(canvas.current, image, marks, draft, false)
  }, [image, marks, draft])

  // Captured on the window, ahead of the sheet's own Escape: Escape leaves the editor (as Cancel does)
  // instead of closing the whole sheet with the edits. While text is typed, the field handles its keys.
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (typing) return
      if (event.key === 'Escape' && !event.defaultPrevented) {
        event.preventDefault()
        event.stopPropagation()
        onDone(null)
      } else if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'z') {
        event.preventDefault()
        setMarks(list => list.slice(0, -1))
      }
    }
    window.addEventListener('keydown', onKey, true)
    return () => window.removeEventListener('keydown', onKey, true)
  }, [typing, onDone])

  const point = (event: ReactPointerEvent<HTMLCanvasElement>): [number, number] => {
    const box = event.currentTarget.getBoundingClientRect()
    return [(event.clientX - box.left) * (event.currentTarget.width / box.width), (event.clientY - box.top) * (event.currentTarget.height / box.height)]
  }

  function draftFor(from: [number, number], to: [number, number]): Mark {
    if (tool === 'arrow') return { tool, from, to }
    if (tool === 'crop') return { tool, rect: rectOf(from, to) }
    return { tool: tool === 'blur' ? 'blur' : 'highlight', rect: rectOf(from, to) }
  }

  async function save() {
    if (!image || busy) return
    setBusy(true)
    setError(null)
    try {
      const out = document.createElement('canvas')
      paint(out, image, marks, null, true)
      const png = out.toDataURL('image/png').split(',')[1] ?? ''
      const result = await host.captureSaveImage(item.id, png)
      if (!result.ok) { setError(result.message ?? 'Could not save the snip.'); return }
      onDone(result.item ?? null)
    } catch (e) { setError(e instanceof Error ? e.message : 'Could not save the snip.') }
    finally { setBusy(false) }
  }

  return (
    <div className="exo-snip-editor exo-motion-switch">
      <div className="exo-snip-tools" role="toolbar" aria-label="Edit snip">
        {TOOLS.map(entry => (
          <button key={entry.tool} type="button" className={`exo-capture-chip${tool === entry.tool ? ' is-on' : ''}`} aria-pressed={tool === entry.tool} onClick={() => { setTool(entry.tool); setTyping(null) }}>
            <entry.icon size={14} />{entry.label}
          </button>
        ))}
        <button type="button" className="exo-capture-chip" disabled={!marks.length} onClick={() => setMarks(list => list.slice(0, -1))}><Undo size={14} />Undo</button>
        <span className="exo-capture-actions-gap" />
        <button type="button" className="exo-set-btn" onClick={() => onDone(null)}>Cancel</button>
        <button type="button" className="exo-set-btn is-primary" disabled={!image || busy} onClick={() => void save()}>{busy ? 'Saving' : 'Save and copy'}</button>
      </div>
      <div className="exo-snip-canvas">
        <canvas
          ref={canvas}
          className={`is-${tool}`}
          onPointerDown={event => {
            if (!image) return
            const at = point(event)
            if (tool === 'text') {
              // The field is placed in the canvas's frame, which centers the canvas: measure from the frame.
              const box = (event.currentTarget.parentElement ?? event.currentTarget).getBoundingClientRect()
              setTyping({ at, left: event.clientX - box.left, top: event.clientY - box.top })
              return
            }
            start.current = at
            event.currentTarget.setPointerCapture(event.pointerId)
          }}
          onPointerMove={event => { if (start.current) setDraft(draftFor(start.current, point(event))) }}
          onPointerUp={event => {
            if (!start.current) return
            const mark = draftFor(start.current, point(event))
            start.current = null
            setDraft(null)
            const big = mark.tool === 'arrow' ? Math.hypot(mark.to[0] - mark.from[0], mark.to[1] - mark.from[1]) > 6 : mark.tool !== 'text' && mark.rect.w > 4 && mark.rect.h > 4
            if (big) setMarks(list => [...list, mark])
          }}
        />
        {typing ? (
          <input
            className="exo-snip-text"
            style={{ left: typing.left, top: typing.top }}
            autoFocus
            aria-label="Text"
            onKeyDown={event => {
              if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); finish(typing, '') }
              else if (event.key === 'Enter') finish(typing, event.currentTarget.value)
            }}
            onBlur={event => finish(typing, event.currentTarget.value)}
          />
        ) : null}
      </div>
      {error ? <p className="exo-capture-note is-bad" role="alert">{error}</p> : null}
    </div>
  )
}
