import { useLayoutEffect, useRef, useState } from 'react'
import { GROUPS } from './section'

/** The gap between columns and between panels in a column, as in mouse.css. */
const GAP = 12
/** A settings column, times text size: never so narrow a row's choices wrap, nor so wide its rows drift apart. */
const COLUMN_MIN = 420
const COLUMN_MAX = 520
/** The mouse keeps at least this width, and the columns leave it this much when they can; it takes the rest. */
const HERO_MIN = 240
const HERO_WANT = 400
/** The most the mouse takes: past this the room adds a column of settings instead of empty space around the picture. */
const HERO_MAX = 900
const MAX_COLUMNS = 4
/** Row heights, roomiest first: rows are squeezed before a column is added. */
const ROWS = [48, 44, 40, 36]
/** The most a row grows to fill a short column. */
const ROOMY = 64
const STACKED = '(max-width: 860px)'

const scale = () => parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--exo-scale')) || 1

/**
 * Contiguous runs of blocks, in order, into exactly `count` columns (or fewer
 * blocks), keeping the tallest column as short as it can be. Returns each
 * block's column and the tallest column's height.
 */
export function partition(heights: number[], count: number, gap = GAP): { column: number[]; tallest: number } {
  const n = heights.length
  const k = Math.max(1, Math.min(count, n))
  const sum = (a: number, b: number) => heights.slice(a, b).reduce((t, h) => t + h, 0) + gap * (b - a - 1)
  const best = Array.from({ length: k + 1 }, () => new Array<number>(n + 1).fill(Infinity))
  const cut = Array.from({ length: k + 1 }, () => new Array<number>(n + 1).fill(0))
  best[0][0] = 0
  for (let c = 1; c <= k; c++) {
    for (let i = 1; i <= n; i++) {
      for (let j = c - 1; j < i; j++) {
        const tallest = Math.max(best[c - 1][j], sum(j, i))
        if (tallest < best[c][i]) { best[c][i] = tallest; cut[c][i] = j }
      }
    }
  }
  const column = new Array<number>(n).fill(0)
  for (let i = n, c = k; c > 0; c--) {
    const j = cut[c][i]
    for (let p = j; p < i; p++) column[p] = c - 1
    i = j
  }
  return { column, tallest: n ? best[k][n] : 0 }
}

/**
 * The settings beside the mouse with no scrolling: the fewest equal columns
 * that hold every panel in the room's height, so no column stands half empty.
 * At each count, sections (Sensor, Buttons, Device, Profiles) stay whole if
 * they can, in order, the roomiest rows first; then a section may continue
 * at the top of the next column. Columns are as wide as the room allows
 * (420 to 520 px), leaving the mouse its picture; only when nothing fits do
 * the columns scroll. The last panel of each column reaches
 * the foot, so the columns end level with the mouse.
 *
 * The stack holds the panels (`.m-panel`) and empty `.m-break` spans that end
 * a column; this sets each one's `order` and the room's `--settings-w`, before
 * paint, whenever the room's size or its set of panels changes.
 */
export function useSections() {
  const box = useRef<HTMLDivElement>(null)
  const stack = useRef<HTMLDivElement>(null)
  const [size, setSize] = useState('')
  const planned = useRef('')

  useLayoutEffect(() => {
    const b = box.current
    const room = b?.parentElement
    if (!b || !room) return
    const observer = new ResizeObserver(() => setSize(`${room.clientWidth}x${b.clientHeight}`))
    observer.observe(room)
    observer.observe(b)
    // A panel's own content changing (profiles loaded, a row added) plans again, once per frame.
    let frame = 0
    const changed = new MutationObserver(() => {
      cancelAnimationFrame(frame)
      frame = requestAnimationFrame(() => setSize(`${room.clientWidth}x${b.clientHeight}|${performance.now()}`))
    })
    if (stack.current) changed.observe(stack.current, { childList: true, subtree: true, characterData: true })
    return () => { observer.disconnect(); changed.disconnect(); cancelAnimationFrame(frame) }
  }, [])

  useLayoutEffect(() => {
    const s = stack.current
    const b = box.current
    const room = b?.parentElement
    if (!s || !b || !room) return
    const panels = [...s.querySelectorAll<HTMLElement>(':scope > .m-panel')]
    panels.forEach((panel, i) => { if (!panel.style.getPropertyValue('--i')) panel.style.setProperty('--i', String(i + 1)) })
    const key = `${size}|${scale()}|${panels.map(p => `${p.dataset.group}:${p.childElementCount}`).join(',')}`
    if (key === planned.current) return
    planned.current = key
    const breaks = [...s.querySelectorAll<HTMLElement>(':scope > .m-break')]

    if (window.matchMedia(STACKED).matches) {
      room.style.removeProperty('--settings-w')
      s.classList.remove('is-columns')
      panels.forEach(p => { p.style.order = ''; p.classList.remove('is-last') })
      breaks.forEach(br => { br.hidden = true })
      return
    }

    const k = scale()
    const gap = GAP
    const pad = parseFloat(getComputedStyle(room).paddingLeft) + parseFloat(getComputedStyle(room).paddingRight)
    const width = room.clientWidth - pad
    const columnFor = (count: number) =>
      Math.round(Math.min(COLUMN_MAX * k, Math.max(COLUMN_MIN * k, (width - HERO_WANT * k - count * gap) / count)))
    let most = 1
    while (most < MAX_COLUMNS && (most + 1) * (columnFor(most + 1) + gap) <= width - HERO_MIN * k) most++
    // On a wide room, start at the column count that keeps the mouse within HERO_MAX.
    let start = 1
    while (start < most && width - start * (columnFor(start) + gap) > HERO_MAX) start++
    const height = b.clientHeight

    // Sections in order; each is its panels.
    const ordered = GROUPS.flatMap(g => panels.filter(p => p.dataset.group === g))
    const sectionOf = ordered.map(p => GROUPS.indexOf(p.dataset.group as (typeof GROUPS)[number]))
    const sections = [...new Set(sectionOf)]

    s.classList.add('is-measuring')
    ordered.forEach(p => p.style.removeProperty('--row-min'))
    const measure = (column: number, rows: number) => {
      s.style.setProperty('--col-w', `${column}px`)
      s.style.setProperty('--row-min', `${rows}px`)
      return ordered.map(p => p.offsetHeight)
    }
    type Choice = { columnOf: number[]; column: number; rows: number; tallest: number; heights: number[] }
    let choice: Choice | null = null
    let fallback: Choice | null = null
    for (let count = start; count <= most && !choice; count++) {
      const column = columnFor(count)
      const tried = ROWS.map(rows => ({ rows, heights: measure(column, rows) }))
      for (const { rows, heights } of tried) {
        if (choice || count > sections.length) break
        const blockHeights = sections.map(sec => {
          const hs = heights.filter((_, i) => sectionOf[i] === sec)
          return hs.reduce((t, h) => t + h, 0) + gap * (hs.length - 1)
        })
        const { column: blockColumn, tallest } = partition(blockHeights, count, gap)
        if (tallest <= height) choice = { columnOf: sectionOf.map(sec => blockColumn[sections.indexOf(sec)]), column, rows, tallest, heights }
      }
      for (const { rows, heights } of tried) {
        if (choice) break
        const { column: panelColumn, tallest } = partition(heights, count, gap)
        if (tallest <= height) choice = { columnOf: panelColumn, column, rows, tallest, heights }
        else if (count === most && (!fallback || tallest < fallback.tallest)) fallback = { columnOf: panelColumn, column, rows, tallest, heights }
      }
    }
    s.classList.remove('is-measuring')
    const plan = choice ?? fallback ?? { columnOf: ordered.map(() => 0), column: columnFor(1), rows: ROWS[0], tallest: height, heights: ordered.map(() => 0) }
    const count = Math.max(1, ...plan.columnOf.map(c => c + 1))
    s.style.setProperty('--col-w', `${plan.column}px`)

    s.style.setProperty('--row-min', `${plan.rows}px`)
    s.style.setProperty('--grid-h', `${Math.max(height, plan.tallest)}px`)
    s.classList.add('is-columns')
    room.style.setProperty('--settings-w', `${count * plan.column + (count - 1) * gap}px`)
    // A column shorter than the room spreads its spare height over its rows (up to ROOMY each), so
    // every column ends level without one card standing half empty; the last panel takes what is left.
    const roomy = ROOMY * k
    for (let c = 0; c < count; c++) {
      const mine = ordered.filter((_, i) => plan.columnOf[i] === c)
      const natural = plan.heights.filter((_, i) => plan.columnOf[i] === c).reduce((t, h) => t + h, 0) + gap * (mine.length - 1)
      const rows = mine.reduce((t, p) => t + p.querySelectorAll('.m-row').length, 0)
      const extra = rows ? Math.max(0, Math.min(roomy - plan.rows, Math.floor((height - natural) / rows))) : 0
      mine.forEach(p => p.style.setProperty('--row-min', `${plan.rows + extra}px`))
    }
    ordered.forEach((panel, i) => {
      const c = plan.columnOf[i]
      panel.style.order = String(c * 100 + i)
      panel.classList.toggle('is-last', plan.columnOf[i + 1] !== c)
    })
    breaks.forEach((br, i) => {
      br.hidden = i >= count - 1
      br.style.order = String(i * 100 + 99)
    })
  })

  return { box, stack }
}
