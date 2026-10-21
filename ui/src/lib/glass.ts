import { useLayoutEffect, type RefObject } from 'react'

/*
 * The lens of Exo's Liquid Glass (styles/glass.css, DESIGN.md › Glass). Small glass (the Bar's capsules,
 * Music's player, a button over art) bends what is behind it at its edge, as the thick rim of a pane of
 * glass does: a displacement map computed from the element's own shape, a rounded rectangle whose edge is
 * a squircle bezel, each pixel shifted toward the inside by Snell refraction through glass of index 1.5.
 * The filter has two branches, as light through real glass does: the middle, where the words are, is the
 * soft blur every glass has (index.html › exo-glass-soft); the rim sees the art nearly sharp and bent
 * (the map's blue channel says how much of the rim each pixel is). glass.css then presses the result into
 * the glass's legible band, rim included, so the bend never costs a word its contrast. Where the map isn't
 * ready the rim is empty and the glass is simply soft. Chromium only (WebView2).
 *
 * Chromium measures an SVG backdrop filter against the element's ink overflow, so glass with a lens never
 * casts an outer shadow or an outer focus ring: an inset darkened edge separates it instead. Maps are
 * cached by size and rebuilt once a resize settles.
 */

const SVG_NS = 'http://www.w3.org/2000/svg'
const maps = new Map<string, Promise<string>>()
let defs: SVGDefsElement | null = null
let serial = 0

function definitions(): SVGDefsElement {
  if (defs?.isConnected) return defs
  const svg = document.createElementNS(SVG_NS, 'svg')
  svg.setAttribute('width', '0')
  svg.setAttribute('height', '0')
  svg.setAttribute('aria-hidden', 'true')
  svg.style.position = 'absolute'
  defs = document.createElementNS(SVG_NS, 'defs')
  svg.appendChild(defs)
  document.body.appendChild(svg)
  return defs
}

// The bezel's height profile (a squircle) and its slope.
const profile = (t: number) => Math.pow(1 - Math.pow(1 - t, 4), 0.25)
const slope = (t: number) => (profile(Math.min(1, t + 1e-3)) - profile(t)) / 1e-3

function buildMap(w: number, h: number, radius: number, bezel: number): Promise<string> {
  const canvas = new OffscreenCanvas(w, h)
  const ctx = canvas.getContext('2d')
  if (!ctx) return Promise.resolve('')
  const image = ctx.createImageData(w, h)
  const data = image.data
  const hx = w / 2, hy = h / 2, n = 1.5
  const r = Math.min(radius, hx, hy)
  const vx = new Float32Array(w * h), vy = new Float32Array(w * h), rim = new Float32Array(w * h)
  let max = 1e-6
  for (let y = 0; y < h; y++) {
    for (let x = 0; x < w; x++) {
      // Distance inside the rounded rectangle, and the edge's outward normal.
      const px = x + 0.5 - hx, py = y + 0.5 - hy
      const qx = Math.abs(px) - (hx - r), qy = Math.abs(py) - (hy - r)
      const inside = r - Math.hypot(Math.max(qx, 0), Math.max(qy, 0)) - Math.min(Math.max(qx, qy), 0)
      if (inside <= 0 || inside >= bezel) continue
      let nx: number, ny: number
      if (qx > 0 && qy > 0) { const l = Math.hypot(qx, qy); nx = qx / l; ny = qy / l }
      else if (qx > qy) { nx = 1; ny = 0 }
      else { nx = 0; ny = 1 }
      nx *= Math.sign(px) || 1
      ny *= Math.sign(py) || 1
      const t = inside / bezel
      const a1 = Math.atan(slope(t)), a2 = Math.asin(Math.sin(a1) / n)
      const shift = profile(t) * Math.tan(a1 - a2)
      const i = y * w + x
      vx[i] = -nx * shift
      vy[i] = -ny * shift
      // The rim: all of the outer bezel, easing out toward its inner edge.
      rim[i] = t < 0.55 ? 1 : 1 - (t - 0.55) / 0.45
      if (shift > max) max = shift
    }
  }
  for (let i = 0; i < w * h; i++) {
    data[i * 4] = 128 + (vx[i] / max) * 127
    data[i * 4 + 1] = 128 + (vy[i] / max) * 127
    data[i * 4 + 2] = rim[i] * 255
    data[i * 4 + 3] = 255
  }
  ctx.putImageData(image, 0, 0)
  return canvas.convertToBlob().then(blob => new Promise<string>(resolve => {
    const reader = new FileReader()
    reader.onload = () => resolve(String(reader.result))
    reader.onerror = () => resolve('')
    reader.readAsDataURL(blob)
  }))
}

function lensFilter(id: string, href: string, w: number, h: number) {
  const filter = document.createElementNS(SVG_NS, 'filter')
  filter.id = id
  // A region well past the element: the blur needs room, and if the reference box ever shifts, the soft branch still covers it.
  filter.setAttribute('x', '-50%')
  filter.setAttribute('y', '-50%')
  filter.setAttribute('width', '200%')
  filter.setAttribute('height', '200%')
  filter.setAttribute('color-interpolation-filters', 'sRGB')
  // The bend grows with the glass, as a thicker pane bends more: up to about a third of its height at the edge.
  const strength = Math.round(Math.min(h, w) * 0.75)
  filter.innerHTML =
    `<feGaussianBlur in="SourceGraphic" stdDeviation="6" result="soft"/>` +
    `<feGaussianBlur in="SourceGraphic" stdDeviation="2" result="sharp"/>` +
    `<feImage href="${href}" x="0" y="0" width="${w}" height="${h}" preserveAspectRatio="none" result="map"/>` +
    `<feDisplacementMap in="sharp" in2="map" scale="${strength}" xChannelSelector="R" yChannelSelector="G" result="bent"/>` +
    `<feColorMatrix in="map" type="matrix" values="0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 1 0 0" result="rim"/>` +
    `<feComposite in="bent" in2="rim" operator="in" result="edge"/>` +
    `<feComposite in="edge" in2="soft" operator="over"/>`
  return filter
}

// Light from the press: glass lights from within where it is pressed (glass.css › ::after). One listener for all glass.
function followPresses() {
  document.addEventListener('pointerdown', event => {
    const glass = (event.target as Element | null)?.closest?.<HTMLElement>('.exo-glass')
    if (!glass) return
    const box = glass.getBoundingClientRect()
    glass.style.setProperty('--glass-x', `${event.clientX - box.left}px`)
    glass.style.setProperty('--glass-y', `${event.clientY - box.top}px`)
  }, { capture: true, passive: true })
}

let following = false

/**
 * Gives the element (which carries `exo-glass`) a lens sized to it. Panels (`is-panel`) take no lens.
 * `mounted` is for glass that comes and goes after its component mounts (a Bar capsule).
 */
export function useLens(ref: RefObject<HTMLElement | null>, mounted = true) {
  useLayoutEffect(() => {
    if (!following) { following = true; followPresses() }
    const el = ref.current
    if (!mounted || !el || typeof OffscreenCanvas === 'undefined') return
    const id = `exo-lens-${++serial}`
    let filter: SVGFilterElement | null = null
    let timer = 0
    let live = true
    const apply = () => {
      const w = Math.round(el.offsetWidth), h = Math.round(el.offsetHeight)
      if (w < 8 || h < 8) return
      const radius = Math.min(parseFloat(getComputedStyle(el).borderTopLeftRadius) || h / 2, h / 2, w / 2)
      const bezel = Math.max(6, Math.min(18, Math.min(w, h) * 0.32))
      const key = `${w}x${h}x${Math.round(radius)}`
      if (!maps.has(key)) maps.set(key, buildMap(w, h, radius, bezel))
      void maps.get(key)!.then(href => {
        if (!live || !href) return
        const next = lensFilter(id, href, w, h)
        if (filter) filter.replaceWith(next)
        else definitions().appendChild(next)
        filter = next
        el.style.setProperty('--exo-lens', `url(#${id})`)
      })
    }
    apply()
    const sizes = new ResizeObserver(() => {
      window.clearTimeout(timer)
      timer = window.setTimeout(apply, 120)
    })
    sizes.observe(el)
    return () => {
      live = false
      window.clearTimeout(timer)
      sizes.disconnect()
      filter?.remove()
      el.style.removeProperty('--exo-lens')
    }
  }, [ref, mounted])
}

/*
 * Light or dark: small glass that bars a view (the Bar, Music's player) turns light over bright content and
 * back over dark, as Apple's small glass does, and its words with it (glass.css › data-tone). Either look
 * keeps its words legible on its own, so this only chooses the one that suits what is behind: it samples
 * points under the glass, reads the art there from a small grid of its brightness (art Exo can't read
 * counts as unknown and the glass stays as it is), and turns past 0.5 or back under 0.32 of full
 * brightness. Glass in one group (the Bar's capsules) shares one look, as one toolbar does.
 */

type Tone = 'light' | 'dark'
const GRID = 24
const grids = new Map<string, Float32Array | null | 'loading'>()
const groups = new Map<string, { members: Set<HTMLElement>; tone: Tone; changed: number }>()
let ticking = 0
let timer = 0

const linear = (c: number) => { c /= 255; return c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4 }

// The cover cache's thumbnails are read from the cover they were made from.
const readable = (src: string) => src.startsWith('https://thumbs.exo-launcher.local/')
  ? src.replace('https://thumbs.exo-launcher.local/', 'https://covers.exo-launcher.local/').replace(/\?.*$/, '')
  : src

function measure(src: string) {
  if (grids.size > 300) grids.delete(grids.keys().next().value!)
  grids.set(src, 'loading')
  const img = new Image()
  img.crossOrigin = 'anonymous'
  img.decoding = 'async'
  img.src = src
  img.decode().then(() => {
    const canvas = new OffscreenCanvas(GRID, GRID)
    const ctx = canvas.getContext('2d', { willReadFrequently: true })
    if (!ctx) throw new Error('no canvas')
    ctx.drawImage(img, 0, 0, GRID, GRID)
    const data = ctx.getImageData(0, 0, GRID, GRID).data
    const grid = new Float32Array(GRID * GRID)
    for (let i = 0; i < grid.length; i++) grid[i] = 0.2126 * linear(data[i * 4]) + 0.7152 * linear(data[i * 4 + 1]) + 0.0722 * linear(data[i * 4 + 2])
    grids.set(src, grid)
    schedule()
  }).catch(() => grids.set(src, null))
}

// What dims an element as drawn: its own and its ancestors' opacity and brightness().
function dimming(el: Element) {
  let factor = 1
  for (let node: Element | null = el; node && node !== document.body; node = node.parentElement) {
    const style = getComputedStyle(node)
    factor *= Number(style.opacity)
    const bright = /brightness\(([\d.]+)\)/.exec(style.filter)
    if (bright) factor *= Number(bright[1])
  }
  return factor
}

function imageLuma(img: HTMLImageElement, x: number, y: number): number | undefined {
  const src = readable(img.currentSrc || img.src)
  const grid = grids.get(src)
  if (grid === undefined) { measure(src); return undefined }
  if (grid === 'loading' || grid === null) return undefined
  const box = img.getBoundingClientRect()
  const style = getComputedStyle(img)
  let w = box.width, h = box.height, ox = 0, oy = 0
  if (style.objectFit === 'cover' || style.objectFit === 'contain') {
    const scale = (style.objectFit === 'cover' ? Math.max : Math.min)(box.width / img.naturalWidth, box.height / img.naturalHeight)
    const [px = '50%', py = '50%'] = style.objectPosition.split(' ')
    w = img.naturalWidth * scale
    h = img.naturalHeight * scale
    ox = (box.width - w) * (px.endsWith('%') ? parseFloat(px) / 100 : 0.5)
    oy = (box.height - h) * (py.endsWith('%') ? parseFloat(py) / 100 : 0.5)
  }
  const u = (x - box.left - ox) / w, v = (y - box.top - oy) / h
  if (u < 0 || u >= 1 || v < 0 || v >= 1) return 0
  return grid[Math.floor(v * GRID) * GRID + Math.floor(u * GRID)] * dimming(img)
}

// The brightness behind the glass at one point: the first picture or solid fill under it (words are ignored).
function pointLuma(x: number, y: number): number | undefined {
  for (const el of document.elementsFromPoint(x, y)) {
    if (el.closest('.exo-glass, .exo-titlebar')) continue
    if (el instanceof HTMLImageElement) {
      if (!el.complete || !el.naturalWidth) continue
      return imageLuma(el, x, y)
    }
    if (el instanceof HTMLVideoElement) return undefined
    const m = /rgba?\(([\d.]+)[,\s]+([\d.]+)[,\s]+([\d.]+)(?:[,/\s]+([\d.]+))?/.exec(getComputedStyle(el).backgroundColor)
    if (m && (m[4] === undefined || Number(m[4]) > 0.85)) {
      return (0.2126 * linear(+m[1]) + 0.7152 * linear(+m[2]) + 0.0722 * linear(+m[3])) * dimming(el)
    }
  }
  return 0
}

// Windows' more contrast or forced colors: glass draws no backdrop and never turns light.
const plain = typeof matchMedia === 'function'
  ? matchMedia('(prefers-contrast: more), (forced-colors: active)')
  : null

// Glass drawn solid has no tone to read: Settings' Solid, or Windows' more contrast or forced colors.
function solid() {
  const root = document.documentElement.dataset
  return root.exoGlass === 'solid' || !!plain?.matches
}

function tick() {
  ticking = 0
  lastTick = performance.now()
  if (document.hidden || solid()) return
  // Mid view transition the page hit-tests as the root alone, so it would read as black: the look holds,
  // and is read again just after.
  if ('exoVt' in document.documentElement.dataset) { window.setTimeout(schedule, 120); return }
  const now = performance.now()
  for (const group of groups.values()) {
    let sum = 0, count = 0
    for (const el of group.members) {
      const box = el.getBoundingClientRect()
      if (box.width < 8 || box.height < 8) continue
      for (let i = 0; i < 6; i++) {
        const l = pointLuma(box.left + box.width * (0.1 + 0.16 * i), box.top + box.height * (i % 2 ? 0.35 : 0.65))
        if (l !== undefined) { sum += l; count++ }
      }
    }
    if (!count) continue
    const luma = sum / count
    const next: Tone = luma > 0.5 ? 'light' : luma < 0.32 ? 'dark' : group.tone
    if (next === group.tone || now - group.changed < 250) continue
    group.tone = next
    group.changed = now
    // Everything on the glass turns in the same frame as the glass (glass.css › data-tone-flip): a word
    // easing its color across would pass through gray on the new glass.
    const members = [...group.members]
    for (const el of members) { el.dataset.toneFlip = ''; el.dataset.tone = next }
    requestAnimationFrame(() => requestAnimationFrame(() => { for (const el of members) delete el.dataset.toneFlip }))
  }
}

// A look costs about 30 hit tests, and a tone changes at most every 250ms: so one look per 250ms at most. A
// look on every scroll frame dropped 1 to 6% of the Library's frames at 165 Hz; this way it drops none.
let lastTick = -Infinity
let deferred = 0
function schedule() {
  if (ticking || deferred) return
  const wait = 250 - (performance.now() - lastTick)
  if (wait > 0) { deferred = window.setTimeout(() => { deferred = 0; schedule() }, wait); return }
  ticking = requestAnimationFrame(tick)
}

function watch() {
  document.addEventListener('scroll', schedule, { capture: true, passive: true })
  window.addEventListener('resize', schedule, { passive: true })
  // Content also changes without a scroll (a room opens, art arrives, the song changes behind an Exo that isn't
  // in front): a slow look keeps up. Hidden (minimized), the frame never comes and nothing is read.
  timer = window.setInterval(schedule, 700)
}

/** Lets small glass that bars a view turn light over bright content; glass in one `group` shares one look. */
export function useGlassTone(ref: RefObject<HTMLElement | null>, group: string, mounted = true) {
  useLayoutEffect(() => {
    const el = ref.current
    if (!mounted || !el) return
    if (!timer) watch()
    const entry = groups.get(group) ?? { members: new Set<HTMLElement>(), tone: 'dark' as Tone, changed: 0 }
    groups.set(group, entry)
    entry.members.add(el)
    el.dataset.tone = entry.tone
    schedule()
    return () => {
      entry.members.delete(el)
      delete el.dataset.tone
    }
  }, [ref, group, mounted])
}
