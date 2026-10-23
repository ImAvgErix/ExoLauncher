import { useCallback, useEffect, useLayoutEffect, useRef, useState, type KeyboardEvent, type PointerEvent } from 'react'
import { Slider } from '../ui/Slider'
import { Cover } from '../../brand/icons'
import { host } from '../../lib/host'
import { report } from '../../lib/report'

/** A folder is a cover: 2 wide by 3 tall. */
const ASPECT = 2 / 3
/** The saved picture's width at most (1200 tall): twice the widest card on a 4K screen. */
const OUT_WIDTH = 800
const MAX_ZOOM = 4
/** The picture on screen is drawn at most this many pixels on its long side; Done crops the full picture. */
const PREVIEW_SIDE = 2048

/** Where the picture sits: its zoom over the fit that just covers the folder, and the crop's top-left in its pixels. */
type View = { zoom: number; x: number; y: number }
type Size = { width: number; height: number }

/**
 * The folder sheet's cropper: the picked picture in a frame of the folder's exact shape, with the folder's glass front
 * and name where they will be. Drag it (or the arrow keys, Shift for bigger steps) to move it; the wheel, the slider
 * or + and − zoom. The picture always covers the frame. Done crops it to at most 800 by 1200, and the host stores it
 * as the folder's picture (library.folderArt); Cancel or Escape goes back to the sheet.
 */
export function FolderCropper({ file, folderId, name, count, onDone, onCancel }: {
  file: File
  folderId: string
  name: string
  count: number
  onDone: (art: string) => void
  onCancel: () => void
}) {
  // Decoded here rather than shown from a blob: address, which the page's img-src does not allow.
  const [image, setImage] = useState<ImageBitmap | null>(null)
  const [view, setView] = useState<View>({ zoom: 1, x: 0, y: 0 })
  const [frameWidth, setFrameWidth] = useState(0)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const frame = useRef<HTMLDivElement>(null)
  const stage = useRef<HTMLDivElement>(null)
  const preview = useRef<HTMLCanvasElement>(null)
  const drag = useRef<{ id: number; x: number; y: number; from: View } | null>(null)

  useEffect(() => {
    let live = true
    let decoded: ImageBitmap | null = null
    createImageBitmap(file).then(picture => {
      decoded = picture
      if (!live) { picture.close(); return }
      if (picture.width < 64 || picture.height < 64) { setError('Choose a picture at least 64 pixels on each side.'); return }
      const { w, h } = cropSize(picture, 1)
      setView({ zoom: 1, x: (picture.width - w) / 2, y: (picture.height - h) / 2 })
      setImage(picture)
    }, () => { if (live) setError('Exo could not read that picture.') })
    return () => { live = false; decoded?.close() }
  }, [file])

  // The picture on screen: drawn once, at most PREVIEW_SIDE on its long side, then only moved and scaled.
  useLayoutEffect(() => {
    const canvas = preview.current
    if (!image || !canvas) return
    const fit = Math.min(1, PREVIEW_SIDE / Math.max(image.width, image.height))
    canvas.width = Math.round(image.width * fit)
    canvas.height = Math.round(image.height * fit)
    const context = canvas.getContext('2d')
    if (!context) return
    context.imageSmoothingQuality = 'high'
    context.drawImage(image, 0, 0, canvas.width, canvas.height)
  }, [image])

  useLayoutEffect(() => {
    const el = frame.current
    if (!el) return
    el.focus({ preventScroll: true })
    const measure = () => setFrameWidth(el.clientWidth)
    measure()
    const sizes = new ResizeObserver(measure)
    sizes.observe(el)
    return () => sizes.disconnect()
  }, [])

  /** The view moved and zoomed about a point of the frame (CSS px from its top-left), kept covering the frame. */
  const zoomAbout = useCallback((zoom: (current: number) => number, px?: number, py?: number) => {
    if (!image || !frameWidth) return
    setView(current => {
      const next = Math.min(MAX_ZOOM, Math.max(1, zoom(current.zoom)))
      const before = frameWidth / cropSize(image, current.zoom).w
      const after = frameWidth / cropSize(image, next).w
      const ax = px ?? frameWidth / 2
      const ay = py ?? frameWidth / ASPECT / 2
      return fit(image, { zoom: next, x: current.x + ax / before - ax / after, y: current.y + ay / before - ay / after })
    })
  }, [image, frameWidth])

  // The wheel zooms about the pointer. Not React's onWheel: that listener is passive, and Ctrl+wheel would zoom the page.
  useEffect(() => {
    const el = stage.current
    if (!el) return
    const wheel = (event: WheelEvent) => {
      event.preventDefault()
      const box = frame.current?.getBoundingClientRect()
      zoomAbout(zoom => zoom * Math.exp(-event.deltaY * 0.0015), box ? event.clientX - box.left : undefined, box ? event.clientY - box.top : undefined)
    }
    el.addEventListener('wheel', wheel, { passive: false })
    return () => el.removeEventListener('wheel', wheel)
  }, [zoomAbout])

  const scale = image && frameWidth ? frameWidth / cropSize(image, view.zoom).w : 0
  const move = (dx: number, dy: number) => { if (image) setView(current => fit(image, { ...current, x: current.x + dx, y: current.y + dy })) }

  function down(event: PointerEvent<HTMLDivElement>) {
    if (event.button !== 0 || !image) return
    event.currentTarget.setPointerCapture(event.pointerId)
    drag.current = { id: event.pointerId, x: event.clientX, y: event.clientY, from: view }
  }
  function moveTo(event: PointerEvent<HTMLDivElement>) {
    const start = drag.current
    if (!start || start.id !== event.pointerId || !image || !scale) return
    setView(fit(image, { ...start.from, x: start.from.x - (event.clientX - start.x) / scale, y: start.from.y - (event.clientY - start.y) / scale }))
  }
  function keys(event: KeyboardEvent<HTMLDivElement>) {
    if (!image) return
    const step = cropSize(image, view.zoom).w * (event.shiftKey ? 0.1 : 0.02)
    // The arrows move the picture, as dragging does (the crop goes the other way).
    const arrows: Record<string, [number, number]> = { ArrowLeft: [step, 0], ArrowRight: [-step, 0], ArrowUp: [0, step], ArrowDown: [0, -step] }
    if (arrows[event.key]) move(...arrows[event.key])
    else if (event.key === '+' || event.key === '=') zoomAbout(zoom => zoom * 1.1)
    else if (event.key === '-' || event.key === '_') zoomAbout(zoom => zoom / 1.1)
    else return
    event.preventDefault()
  }

  async function done() {
    if (!image || busy) return
    setBusy(true)
    setError(null)
    const { w, h } = cropSize(image, view.zoom)
    const width = Math.max(64, Math.min(OUT_WIDTH, Math.round(w)))
    const canvas = document.createElement('canvas')
    canvas.width = width
    canvas.height = Math.round(width / ASPECT)
    const context = canvas.getContext('2d')
    if (context) {
      context.imageSmoothingQuality = 'high'
      context.drawImage(image, view.x, view.y, w, h, 0, 0, canvas.width, canvas.height)
    }
    try {
      const saved = await host.libraryFolderArt(folderId, canvas.toDataURL('image/png').split(',')[1] ?? '')
      if (saved.ok && saved.art) { onDone(saved.art); return }
      setError(saved.message ?? 'Exo could not save that picture.')
    } catch (cause) {
      report(cause, 'FolderCropper.save')
      setError('Exo could not save that picture.')
    }
    setBusy(false)
  }

  return (
    <div
      className="exo-folder-crop"
      onKeyDown={event => {
        if (event.key !== 'Escape' || event.defaultPrevented) return
        event.preventDefault()
        event.stopPropagation()
        onCancel()
      }}
    >
      <h2 id="exo-folder-sheet-title">Crop picture</h2>
      <div ref={stage} className="exo-folder-crop-stage" onPointerDown={down} onPointerMove={moveTo}
        onPointerUp={() => { drag.current = null }} onPointerCancel={() => { drag.current = null }}>
        <div
          ref={frame}
          className="exo-folder-crop-frame"
          data-art=""
          role="group"
          tabIndex={0}
          aria-label="Picture position"
          aria-describedby="exo-folder-crop-hint"
          onKeyDown={keys}
        >
          <canvas
            ref={preview}
            className="exo-folder-crop-image"
            aria-hidden="true"
            style={image && scale ? { width: image.width * scale, height: image.height * scale, transform: `translate(${-view.x * scale}px, ${-view.y * scale}px)` } : { display: 'none' }}
          />
          <span className="exo-folder-crop-mask" aria-hidden="true" />
          <span className="exo-glass exo-folder-flap" aria-hidden="true">
            <span className="exo-folder-label">{name.trim() || 'Folder'}</span>
            <span className="exo-folder-count">{count} {count === 1 ? 'game' : 'games'}</span>
          </span>
        </div>
      </div>
      <div className="exo-folder-crop-zoom">
        <Cover size={14} />
        <Slider min={1} max={MAX_ZOOM} step={0.01} value={view.zoom} aria-label="Zoom" disabled={!image}
          onChange={event => { const zoom = Number(event.currentTarget.value); zoomAbout(() => zoom) }} />
        <Cover size={20} />
      </div>
      <footer className="exo-folder-sheet-foot">
        <span id="exo-folder-crop-hint" className={error ? 'exo-folder-sheet-error' : 'exo-folder-sheet-count'} role={error ? 'alert' : undefined}>
          {error ?? 'Drag to move it. Scroll or use the slider to zoom.'}
        </span>
        <button type="button" className="exo-ghost-btn" onClick={onCancel}>Cancel</button>
        <button type="button" className="exo-ghost-btn is-primary" disabled={!image || busy} onClick={() => void done()}>{busy ? 'Saving' : 'Done'}</button>
      </footer>
    </div>
  )
}

/** The crop in the picture's pixels at this zoom: at 1, the largest 2:3 that fits in it. */
function cropSize(image: Size, zoom: number) {
  const w = image.width / image.height > ASPECT ? image.height * ASPECT : image.width
  return { w: w / zoom, h: w / ASPECT / zoom }
}

/** The view kept on the picture: the crop never leaves it. */
function fit(image: Size, view: View): View {
  const { w, h } = cropSize(image, view.zoom)
  return {
    zoom: view.zoom,
    x: Math.min(Math.max(0, view.x), image.width - w),
    y: Math.min(Math.max(0, view.y), image.height - h),
  }
}
