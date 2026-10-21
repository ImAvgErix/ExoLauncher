// UI half of the Exo end-to-end run. Invoke-ExoE2E.ps1 launches the installed
// app with a loopback CDP port and calls one suite at a time:
//
//   node exo-e2e.mjs <suite> --port <n> --out <dir> [--settings <settings.json>] [--cache <library-cache.json>] [--arg <value>]
//
// Each suite prints one JSON object { checks: [...], data: {...} } on stdout.
// A check is { id, name, status: PASS|FAIL|SKIP, reason, durationMs, evidence, screenshots }.
// The page is driven by real CDP input (mouse/keyboard), found by role, aria-label and text.

import { readFile } from 'node:fs/promises'
import { join } from 'node:path'
import { openFrame, openPage, openShell, sleep } from './cdp.mjs'

const argv = process.argv.slice(2)
const suite = argv[0]
const opt = (name, fallback = null) => {
  const i = argv.indexOf('--' + name)
  return i >= 0 && i + 1 < argv.length ? argv[i + 1] : fallback
}
const port = Number(opt('port'))
const outDir = opt('out', '.')
const shotsDir = join(outDir, 'screenshots')

const checks = []
const data = {}

async function check(id, name, fn) {
  const started = Date.now()
  const entry = { id, name, status: 'PASS', reason: '', durationMs: 0, evidence: {}, screenshots: [] }
  const ctx = {
    evidence: entry.evidence,
    shot: async (sh, label) => {
      const file = `${id}${label ? '-' + label : ''}.png`.replace(/[^a-z0-9.-]+/gi, '-')
      await sh.eval(REDACT_PAGE)
      await sh.screenshot(join(shotsDir, file))
      entry.screenshots.push('screenshots/' + file)
    },
    clip: async (sh, label, rect) => {
      const file = `${id}-${label}.png`.replace(/[^a-z0-9.-]+/gi, '-')
      await sh.eval(REDACT_PAGE)
      await sh.screenshotClip(join(shotsDir, file), rect)
      entry.screenshots.push('screenshots/' + file)
    },
    skip: (reason) => { const e = new Error(reason); e.skip = true; throw e },
  }
  try {
    const failures = (await fn(ctx)) ?? []
    if (failures.length) { entry.status = 'FAIL'; entry.reason = failures.join('; ') }
  } catch (error) {
    entry.status = error.skip ? 'SKIP' : 'FAIL'
    entry.reason = error.skip ? error.message : 'error: ' + error.message
  }
  entry.evidence = redact(entry.evidence)
  entry.reason = redact(entry.reason)
  entry.durationMs = Date.now() - started
  checks.push(entry)
  return entry
}

// Artifacts must not carry personal identifiers: e-mail addresses are masked in evidence
// and in the page's visible text right before each screenshot.
const EMAIL = /[A-Z0-9._%+-]+@[A-Z0-9.-]+.[A-Z]{2,}/gi
const redact = (value) => JSON.parse(JSON.stringify(value).replace(EMAIL, '<email>'))
const REDACT_PAGE = `(() => { const re = ${EMAIL}; const w = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT); let n; while ((n = w.nextNode())) if (re.test(n.nodeValue)) { re.lastIndex = 0; n.nodeValue = n.nodeValue.replace(re, '<email>') } return true })()`

const readJson = async (path) => JSON.parse((await readFile(path, 'utf8')).replace(/^﻿/, ''))

// ---------- page-side finders (serialized into the page) ----------
const roomButton = (name) => [...document.querySelectorAll('nav[aria-label="Rooms"] button')].find((b) => b.textContent.trim() === name)
const byLabel = (label) => [...document.querySelectorAll('button,[role=button],[role=switch]')].find((b) => b.getAttribute('aria-label') === label && b.getBoundingClientRect().width > 0)

/**
 * Mirrors ExoLauncher/Services/CatalogHygiene.cs IsOwnedExtraTitle + IsDemoTitle:
 * phrases on word boundaries, suffix words only at the end, any "demo" word.
 */
const OWNED_EXTRA_PHRASES = [
  'bonus content', 'artbook', 'art book', 'soundtrack', 'original soundtrack',
  'sdk', 'dev kit', 'devkit', 'development kit', 'level editor', 'mod tools',
  'modding tools', 'mod kit', 'modkit', 'mod uploader', 'benchmark', 'test server',
  'public test', 'playtest', 'dedicated server', 'dedserver', 'closed beta', 'open beta',
  'beta test', 'remote console', 'redistributable', 'redistributables', 'demo',
  'digital extras', 'making of', 'strategy guide',
]
const OWNED_EXTRA_SUFFIX = ['ost', 'editor', 'server', 'trailer', 'trailers', 'wallpaper', 'wallpapers', 'tools']
export function ownedExtraReason(title) {
  const words = (title || '').toLowerCase().split(/[^\p{L}\p{N}]+/u).filter(Boolean)
  if (!words.length) return 'empty title'
  if (OWNED_EXTRA_SUFFIX.includes(words.at(-1))) return `ends with "${words.at(-1)}"`
  const packed = ' ' + words.join(' ') + ' '
  const hit = OWNED_EXTRA_PHRASES.find((p) => packed.includes(' ' + p + ' '))
  return hit ? `contains "${hit}"` : null
}

// ---------- suites ----------

async function startup(sh) {
  await check('startup.covers', 'Library painted with covers', async (c) => {
    const result = await sh.waitFor(() => {
      if (!performance.getEntriesByName('exo.library.usable').length) return null
      const tiles = [...document.querySelectorAll('.exo-tile-hit')].filter((b) => { const r = b.getBoundingClientRect(); return r.bottom > 0 && r.top < innerHeight && r.width > 0 })
      if (!tiles.length) return null
      const imgs = tiles.map((b) => b.querySelector('img.exo-cover-front:not([data-under])')).filter(Boolean)
      const painted = imgs.filter((i) => i.complete && i.naturalWidth > 0 && getComputedStyle(i).opacity === '1')
      if (painted.length < imgs.length) return null
      return { visibleTiles: tiles.length, coverImages: imgs.length, painted: painted.length, usableMs: Math.round(performance.getEntriesByName('exo.library.usable')[0].startTime), sinceNavigationMs: Math.round(performance.now()), viewport: `${innerWidth}x${innerHeight}@${devicePixelRatio}` }
    }, { timeoutMs: 60000, interval: 200 })
    await c.shot(sh)
    if (!result) return ['first-screen covers did not all paint within 60 s']
    Object.assign(c.evidence, result)
    return result.painted > 0 ? [] : ['no cover image on the first screen']
  })

  // Liquid Glass (DESIGN.md › Glass): the shared filters are in the page (a backdrop-filter naming a
  // missing filter draws nothing), every Bar capsule's filter chain resolves, and the bands hold. With a pure
  // white sheet under the Bar, the Bar turns light (its words dark) and the Rooms capsule, words hidden, keeps
  // its ink at 4.5:1 or more against its worst pixel; three probes with a fixed look (dark small glass, light
  // small glass, large glass) keep theirs over the same white. Solid glass (the setting, a running game,
  // Windows' more contrast or forced colors) must draw no backdrop at all; Windows' Transparency effects switch
  // doesn't count (Exo follows its own Glass setting).
  await check('glass.legible', 'Liquid Glass: filters present, Bar capsules resolve, the Bar turns light over white, every look keeps its words at 4.5:1 or more', async (c) => {
    const state = await sh.eval(`(() => {
      const root = document.documentElement
      const missing = ['exo-glass-soft', 'exo-glass-tone', 'exo-glass-tone-light', 'exo-glass-tone-tinted', 'exo-glass-tone-light-tinted', 'exo-glass-panel', 'exo-glass-panel-tinted'].filter((id) => !document.getElementById(id))
      const solid = root.dataset.exoGlass === 'solid' || matchMedia('(prefers-contrast: more), (forced-colors: active)').matches
      const capsules = [...document.querySelectorAll('.exo-titlebar .exo-glass')].map((el) => {
        const filter = getComputedStyle(el).backdropFilter
        const ids = [...filter.matchAll(/url\\("?#([\\w-]+)"?\\)/g)].map((m) => m[1])
        return { el: String(el.className).split(' ').filter((x) => x !== 'exo-glass')[0], tone: el.dataset.tone ?? null, filter, unresolved: ids.filter((id) => !document.getElementById(id)) }
      })
      return { glass: root.dataset.exoGlass ?? null, inactive: root.dataset.exoInactive ?? null, solid, missing, capsules }
    })()`)
    Object.assign(c.evidence, state)
    const f = []
    if (state.missing.length) f.push(`shared glass filters missing: ${state.missing.join(', ')}`)
    if (!state.capsules.length) f.push('no glass capsule in the Bar')
    for (const cap of state.capsules) {
      if (state.solid && cap.filter !== 'none') f.push(`${cap.el} draws a backdrop while glass is solid`)
      if (!state.solid && !/#exo-glass-tone/.test(cap.filter)) f.push(`${cap.el} backdrop-filter is "${cap.filter}"`)
      if (cap.unresolved.length) f.push(`${cap.el} names missing filters: ${cap.unresolved.join(', ')}`)
    }
    if (state.solid) return f
    const rects = await sh.eval(`(() => {
      const app = document.querySelector('.exo-app')
      const sheet = document.createElement('div')
      sheet.id = 'exo-e2e-white'
      sheet.style.cssText = 'position:fixed;inset:0;z-index:39;background:#fff'
      sheet.innerHTML = '<div class="exo-glass" data-tone="dark" style="position:absolute;left:40px;bottom:200px;width:300px;height:36px;border-radius:999px"></div>'
        + '<div class="exo-glass" data-tone="light" style="position:absolute;left:40px;bottom:140px;width:300px;height:36px;border-radius:999px"></div>'
        + '<div class="exo-glass is-panel" style="position:absolute;left:400px;bottom:140px;width:300px;height:96px;border-radius:14px"></div>'
      app.appendChild(sheet)
      const words = document.createElement('style')
      words.id = 'exo-e2e-words'
      words.textContent = '.exo-titlebar .exo-glass * { visibility: hidden !important }'
      document.head.appendChild(words)
      const box = (el, x, y) => { const r = el.getBoundingClientRect(); return { x: Math.round(r.left + x), y: Math.round(r.top + y), width: Math.round(r.width - 2 * x), height: Math.round(r.height - 2 * y) } }
      const probes = [...sheet.children]
      return { rooms: box(document.querySelector('nav[aria-label="Rooms"]'), 10, 8), dark: box(probes[0], 10, 8), light: box(probes[1], 10, 8), panel: box(probes[2], 10, 10) }
    })()`)
    try {
      await sleep(1500)
      const look = await sh.eval(`(() => { const nav = document.querySelector('nav[aria-label="Rooms"]'); return { tone: nav.dataset.tone ?? null, ink: getComputedStyle(nav.querySelector('button')).color } })()`)
      await c.clip(sh, 'white', { x: rects.rooms.x - 10, y: rects.rooms.y - 8, width: rects.rooms.width + 20, height: rects.rooms.height + 16 })
      const linear = (v) => { v /= 255; return v <= 0.04045 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4 }
      const lum = (r, g, b) => 0.2126 * linear(r) + 0.7152 * linear(g) + 0.0722 * linear(b)
      const hex = (h) => lum(parseInt(h.slice(1, 3), 16), parseInt(h.slice(3, 5), 16), parseInt(h.slice(5, 7), 16))
      const ratio = (a, b) => (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05)
      const range = async (rect) => {
        const px = await sh.pixelsClip(rect)
        let lo = 1, hi = 0
        for (let i = 0; i < px.data.length; i += 4) { const l = lum(px.data[i], px.data[i + 1], px.data[i + 2]); lo = Math.min(lo, l); hi = Math.max(hi, l) }
        return { lo, hi }
      }
      const worst = (inkL, r) => Math.min(ratio(inkL, r.lo), ratio(inkL, r.hi))
      const rgb = /(\d+)\D+(\d+)\D+(\d+)/.exec(look.ink)
      const barInk = rgb ? lum(+rgb[1], +rgb[2], +rgb[3]) : hex('#ffffff')
      const measured = {
        bar: worst(barInk, await range(rects.rooms)),
        darkInk: worst(hex('#ffffff'), await range(rects.dark)),
        darkSecondary: worst(hex('#dbdbdb'), await range(rects.dark)),
        lightInk: worst(hex('#0a0a0a'), await range(rects.light)),
        lightSecondary: worst(hex('#383838'), await range(rects.light)),
        panelInk: worst(hex('#ffffff'), await range(rects.panel)),
        panelSecondary: worst(hex('#bdbdbd'), await range(rects.panel)),
      }
      c.evidence.barTone = look.tone
      c.evidence.contrast = Object.fromEntries(Object.entries(measured).map(([k, v]) => [k, Number(v.toFixed(2))]))
      if (look.tone !== 'light') f.push(`the Bar stayed ${look.tone} over a white sheet`)
      for (const [name, value] of Object.entries(measured)) if (value < 4.5) f.push(`${name} ${value.toFixed(2)}:1 over white (needs 4.5:1)`)
    } finally {
      await sh.eval(`(() => { document.getElementById('exo-e2e-white')?.remove(); document.getElementById('exo-e2e-words')?.remove(); return true })()`)
    }
    return f
  })

  // What the shell loaded to get here: Exo's own script files, sized by fetching each again (the
  // virtual host sends no timing sizes). The first paint needs only index.html's own script and its
  // preloads (750 KB); the rooms Exo warms behind the splash come on top (1500 KB in all). A trailer
  // player (dash.js, hls.js) loads only when a trailer plays.
  await check('startup.js', 'Script loaded at startup is small, and no trailer player is among it', async (c) => {
    const files = await sh.call(async () => {
      const urls = [...new Set([...document.querySelectorAll('script[src]')].map((s) => s.src).concat(performance.getEntriesByType('resource').map((r) => r.name)).filter((u) => { try { const p = new URL(u); return p.origin === location.origin && p.pathname.endsWith('.js') } catch { return false } }))]
      // index.html as shipped: the build adds preload tags at runtime for rooms warmed later, which are not first paint.
      const shipped = new DOMParser().parseFromString(await (await fetch(location.href, { cache: 'force-cache' })).text(), 'text/html')
      const entry = new Set([...shipped.querySelectorAll('script[src], link[rel=modulepreload]')].map((e) => new URL(e.getAttribute('src') || e.getAttribute('href'), location.href).href))
      const out = []
      for (const u of urls) out.push({ file: new URL(u).pathname.replace(/^\/assets\//, ''), entry: entry.has(u), bytes: (await (await fetch(u, { cache: 'force-cache' })).arrayBuffer()).byteLength })
      return out
    })
    const total = files.reduce((a, x) => a + x.bytes, 0)
    const first = files.filter((x) => x.entry).reduce((a, x) => a + x.bytes, 0)
    const players = files.filter((x) => /dash|hls|shaka|Trailer/i.test(x.file))
    Object.assign(c.evidence, { files: files.length, totalKB: Math.round(total / 1024), largest: [...files].sort((a, b) => b.bytes - a.bytes).slice(0, 6).map((x) => `${x.file} ${Math.round(x.bytes / 1024)} KB`), trailerPlayers: players.map((x) => x.file), firstPaintKB: Math.round(first / 1024), firstPaintBudgetKB: 750, budgetKB: 1500 })
    const f = []
    if (players.length) f.push(`a trailer player loaded at startup: ${players.map((x) => x.file).join(', ')}`)
    if (first > 750 * 1024) f.push(`${Math.round(first / 1024)} KB of script before the first paint (budget 750 KB)`)
    if (total > 1500 * 1024) f.push(`${Math.round(total / 1024)} KB of script at startup with the warmed rooms (budget 1500 KB)`)
    return f
  })
}

/** Page side: the Library's one grid (All games): its heading, its count and the grid element. */
const ALL_GAMES = () => {
  const h = [...document.querySelectorAll('main.exo-library-pane h2.exo-shelf-title')].find((x) => /^All games/.test(x.textContent.trim()))
  const section = h?.closest('section')
  return h ? { h, section, grid: section?.querySelector('.exo-windowed-game-grid') ?? null, count: Number(h.querySelector('.exo-shelf-count')?.textContent ?? 0) } : null
}

/**
 * Page side: scrolls the Library from the top until the card of `id` is in the windowed grid, then
 * centres it. True when it is on screen. The pane is left where the card is.
 */
const REVEAL_TILE = `async (id) => {
  const pane = document.querySelector('main.exo-library-pane')
  if (!pane) return false
  const find = () => pane.querySelector('.exo-tile-hit[data-game-id="' + CSS.escape(id) + '"]')
  const frame = () => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r)))
  if (!find()) { pane.scrollTop = 0; await frame() }
  for (let i = 0; i < 400 && !find(); i++) {
    if (pane.scrollTop + pane.clientHeight >= pane.scrollHeight - 2) break
    pane.scrollTop += Math.max(200, pane.clientHeight * 0.6)
    await frame()
  }
  const b = find()
  if (!b) return false
  b.scrollIntoView({ block: 'center' })
  await frame()
  return true
}`
const revealTile = (sh, id) => sh.eval(`(${REVEAL_TILE})(${JSON.stringify(id)})`)
const libraryTop = (sh) => sh.eval(`(async () => { const p = document.querySelector('main.exo-library-pane'); if (p) p.scrollTop = 0; await new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r))); return true })()`)

/** The Library's sort menu: its trigger in the All games header ("Sort library, Name"). */
const sortTrigger = () => document.querySelector('main.exo-library-pane button[aria-label^="Sort library"]')
const sortItem = (label) => [...document.querySelectorAll('[role=menu]:popover-open [role^=menuitem]')].find((i) => i.textContent.trim() === label)
const SORT_LABELS = ['Name', 'Installed first', 'Last launched', 'Time played', 'Size', 'Store']

/** Picks one sort through the real menu; true once the trigger names it. */
async function pickSort(sh, label) {
  if (!await sh.click(sortTrigger)) return false
  if (!await sh.waitFor(() => document.querySelector('[role=menu]:popover-open') ? true : null, { timeoutMs: 3000 })) return false
  if (!await sh.click(sortItem, label)) { await sh.key('Escape'); return false }
  return !!(await sh.waitFor((l) => document.querySelector('main.exo-library-pane button[aria-label^="Sort library"]')?.getAttribute('aria-label') === 'Sort library, ' + l, { timeoutMs: 3000, args: [label] }))
}

/** Page side: the cards the All games grid renders, in order (the window from the top of the pane). */
const GRID_ORDER = () => [...(document.querySelector('main.exo-library-pane .exo-windowed-game-grid')?.querySelectorAll('.exo-tile-hit') ?? [])].map((b) => b.dataset.gameId)

async function library(sh, cachePath) {
  const cache = cachePath ? await readJson(cachePath).catch(() => null) : null
  const cacheById = new Map((cache?.games ?? []).map((g) => [g.id, g]))
  const hostLibrary = await sh.eval(hostCall('library.get')).catch(() => null)
  const byId = new Map((hostLibrary?.games ?? []).map((g) => [g.id, g]))
  await sh.click(navButton, 'Library')
  await sh.waitFor(() => document.querySelectorAll('main.exo-library-pane .exo-tile-hit').length > 0, { timeoutMs: 15000 })
  await sh.eval('window.__exoAllGames = ' + ALL_GAMES.toString())
  await libraryTop(sh)

  // The Library is Continue (what you are playing), Friends are playing (when friends play a game
  // you own) and one grid of all your games; the Store's shelves live in the Store room.
  const layout = await sh.eval(`(() => {
    const pane = document.querySelector('main.exo-library-pane')
    if (!pane) return { heads: [], continueCards: 0, friendsPlaying: 0, storeShelvesInLibrary: 0, noPane: true }
    const heads = [...pane.querySelectorAll('h2')].filter(h => !h.classList.contains('sr-only')).map(h => [...h.childNodes].filter(n => n.nodeType === 3).map(n => n.textContent).join('').trim())
    return {
      heads,
      continueCards: pane.querySelectorAll('.exo-continue-card').length,
      friendsPlaying: pane.querySelectorAll('.exo-friend-pill').length,
      storeShelvesInLibrary: pane.querySelectorAll('[id^=exo-store-]').length,
    }
  })()`)
  const all = await sh.call(() => { const a = window.__exoAllGames(); return a ? { count: a.count, rendered: a.grid?.querySelectorAll('.exo-tile-hit').length ?? 0, columns: Number(a.grid?.dataset.columnCount ?? 0) } : null })
  data.shelves = [{ name: 'All games', count: all?.count ?? 0 }]

  await check('library.shelves', 'Library: Continue, Friends are playing and one All games grid with cards; no store shelves', async (c) => {
    Object.assign(c.evidence, layout, { allGames: all })
    await c.shot(sh)
    const f = []
    if (!all) return ['no All games heading']
    if (!(all.count > 0)) f.push(`All games count is ${all.count}`)
    if (!(all.rendered > 0)) f.push('the All games grid renders no cards')
    const allowed = ['Continue', 'Friends are playing', 'All games']
    for (const h of layout.heads) if (!allowed.includes(h)) f.push(`unexpected heading "${h}" in the Library`)
    if (layout.heads.includes('Continue') !== layout.continueCards > 0) f.push(`Continue heading ${layout.heads.includes('Continue')} with ${layout.continueCards} cards`)
    if (layout.storeShelvesInLibrary) f.push(`${layout.storeShelvesInLibrary} store shelves in the Library`)
    // Every installed game the host lists is a card (tools and soundtracks aside).
    if (hostLibrary) {
      const installed = hostLibrary.games.filter((g) => g.installed && !g.isAddPortable && g.id !== 'local:add' && !ownedExtraReason(g.title)).length
      c.evidence.hostInstalled = installed
      if (all.count < installed) f.push(`All games shows ${all.count} cards, the host has ${installed} installed games`)
    }
    return f
  })

  // The grid is windowed: scroll the pane and collect every card it renders (used by the picks below).
  const collected = await sh.eval(`(async () => {
    const a = window.__exoAllGames()
    if (!a?.section) return []
    const pane = a.section.closest('main')
    const seen = new Map()
    const take = () => { for (const b of a.section.querySelectorAll('.exo-tile-hit')) seen.set(b.dataset.gameId, b.getAttribute('aria-label')) }
    pane.scrollTop = 0
    for (let i = 0; i < 600; i++) {
      await new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r)))
      take()
      if (pane.scrollTop + pane.clientHeight >= pane.scrollHeight - 2) break
      pane.scrollTop += Math.max(200, pane.clientHeight * 0.6)
    }
    await new Promise(r => setTimeout(r, 150)); take()
    pane.scrollTop = 0
    return [...seen.entries()]
  })()`)

  await check('library.owned-hygiene', 'All games has no extras among the games you own but have not installed (bonus/soundtrack/SDK/server/test/demo)', async (c) => {
    if (!all) c.skip('no All games grid')
    const settings = await sh.eval(hostCall('settings.get')).catch(() => null)
    if (settings?.showLibraryTools === true) c.skip('Show tools and soundtracks is on')
    const notInstalled = collected.filter(([id]) => (byId.get(id) ?? cacheById.get(id))?.installed === false)
    const offenders = notInstalled.map(([id, title]) => ({ id, title, rule: ownedExtraReason(title) })).filter((x) => x.rule)
    Object.assign(c.evidence, { gridCount: all.count, collected: collected.length, notInstalled: notInstalled.length, offenders })
    const f = []
    if (collected.length !== all.count) f.push(`collected ${collected.length} cards but the grid says ${all.count}`)
    if (offenders.length) f.push(`${offenders.length} extras in All games: ` + offenders.slice(0, 8).map((o) => `${o.title} (${o.rule})`).join(', '))
    if (cache) c.evidence.cacheOwnedExtras = cache.games.filter((g) => !g.installed && g.owned && ownedExtraReason(g.title)).map((g) => g.title)
    return f
  })

  await check('library.sort-menu', 'The sort menu in the All games header opens with every sort, the current one checked, and Escape closes it', async (c) => {
    const label0 = await sh.eval(`document.querySelector('main.exo-library-pane button[aria-label^="Sort library"]')?.getAttribute('aria-label') ?? null`)
    if (!await sh.click(sortTrigger)) return ['no "Sort library" button in the All games header']
    const open = await sh.waitFor(() => {
      const t = document.querySelector('main.exo-library-pane button[aria-label^="Sort library"]')
      const m = document.getElementById(t.getAttribute('aria-controls'))
      if (!m || !m.matches(':popover-open') || t.getAttribute('aria-expanded') !== 'true') return null
      return { items: [...m.querySelectorAll('[role^=menuitem]')].map((i) => i.textContent.trim()), checked: m.querySelector('[aria-checked=true]')?.textContent.trim() ?? null, inHeader: !!t.closest('.exo-shelf-head') }
    }, { timeoutMs: 3000 })
    await c.shot(sh, 'open')
    if (!open) return ['menu did not open (popover-open + aria-expanded=true)']
    Object.assign(c.evidence, open, { trigger: label0 })
    await sh.key('Escape')
    const closed = await sh.waitFor(() => {
      const t = document.querySelector('main.exo-library-pane button[aria-label^="Sort library"]')
      const m = document.getElementById(t.getAttribute('aria-controls'))
      return (!m || !m.matches(':popover-open')) && t.getAttribute('aria-expanded') !== 'true' ? { focusReturned: document.activeElement === t } : null
    }, { timeoutMs: 3000 })
    const f = []
    if (open.items.join('|') !== SORT_LABELS.join('|')) f.push(`items ${open.items.join(', ')} (expected ${SORT_LABELS.join(', ')})`)
    if (!open.checked) f.push('no sort option is checked')
    else if (label0 !== `Sort library, ${open.checked}`) f.push(`the trigger says "${label0}" but "${open.checked}" is checked`)
    if (!open.inHeader) f.push('the sort is not in the All games header')
    if (!closed) f.push('Escape did not close the menu')
    else if (!closed.focusReturned) f.push('focus did not return to the sort button')
    return f
  })

  await check('library.grid-keys', 'All games keyboard: PageDown x3 then End moves focus down to a row end, on screen', async (c) => {
    await libraryTop(sh)
    const first = await sh.call(() => {
      const a = window.__exoAllGames()
      const b = a?.grid?.querySelector('.exo-tile-hit')
      if (!b) return null
      b.focus()
      return { id: b.dataset.gameId, columns: Number(a.grid.dataset.columnCount), count: a.count }
    })
    if (!first) c.skip('no card in All games')
    // cdp.key sends no virtual key code for these; send the real ones.
    const press = async (key, vk) => {
      const base = { key, code: key, windowsVirtualKeyCode: vk, nativeVirtualKeyCode: vk, modifiers: 0 }
      await sh.s.send('Input.dispatchKeyEvent', { type: 'rawKeyDown', ...base })
      await sh.s.send('Input.dispatchKeyEvent', { type: 'keyUp', ...base })
      await sleep(120)
    }
    for (let i = 0; i < 3; i++) await press('PageDown', 34)
    await press('End', 35)
    const at = await sh.waitFor(() => {
      const b = document.activeElement
      if (!b?.matches('.exo-tile-hit')) return null
      const pane = document.querySelector('main.exo-library-pane').getBoundingClientRect()
      const r = b.getBoundingClientRect()
      return { id: b.dataset.gameId, position: Number(b.closest('[aria-posinset]')?.getAttribute('aria-posinset')), onScreen: r.top >= pane.top - 1 && r.bottom <= pane.bottom + 1 && r.width > 0 }
    }, { timeoutMs: 3000 })
    await c.shot(sh)
    Object.assign(c.evidence, { first, landed: at })
    await sh.eval(`(async () => { document.activeElement?.blur(); document.querySelector('main.exo-library-pane').scrollTop = 0; await new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r))) })()`)
    if (!at) return ['focus left the cards']
    const { columns, count } = first
    const lastRow = Math.ceil(count / columns) - 1
    const row = Math.floor((at.position - 1) / columns)
    const f = []
    if (at.id === first.id && count > 1) f.push('focus did not move')
    if (row < Math.min(lastRow, 3)) f.push(`landed on row ${row}, expected at least ${Math.min(lastRow, 3)} after three PageDowns`)
    if (at.position % columns !== 0 && at.position !== count) f.push(`End landed on card ${at.position}, not the end of its row (${columns} columns)`)
    if (!at.onScreen) f.push('the focused card is not on screen')
    return f
  })

  // Deterministic picks: the first card in grid order of each kind (the host says which is installed).
  const kindOf = (id) => (byId.get(id) ?? cacheById.get(id))
  const installedPick = collected.map(([id, label]) => ({ id, label })).find((t) => kindOf(t.id)?.installed && !kindOf(t.id)?.isRunning && !/\((update|playing)\)$/.test(t.label))
  const ownedPick = collected.map(([id, label]) => ({ id, label })).find((t) => kindOf(t.id)?.installed === false && kindOf(t.id)?.canInstall !== false && !/\(queued\)$/.test(t.label))
  data.libraryPicks = { installed: installedPick ?? null, notInstalled: ownedPick ?? null }
  for (const [kind, tile] of [['installed', installedPick], ['not-installed', ownedPick]]) {
    await check(`library.sheet-${kind}`, `Game page opens for a${kind === 'installed' ? 'n installed' : ' not-installed'} game from All games${kind === 'installed' ? ' with Remove and Properties beside Play' : ''}; Escape closes`, async (c) => {
      if (!tile) c.skip(`no ${kind} game in All games`)
      c.evidence.gameId = tile.id
      c.evidence.card = tile.label
      if (!await revealTile(sh, tile.id)) return ['card not found in the grid']
      const label = await sh.click((id) => document.querySelector(`main.exo-library-pane .exo-tile-hit[data-game-id="${CSS.escape(id)}"]`), tile.id)
      if (!label) return ['card not clickable']
      const sheet = await sh.waitFor(() => {
        const d = document.querySelector('[role=dialog][aria-modal=true]:not([hidden] *)')
        const primary = d?.querySelector('.exo-primary-action')
        // The button stacks an idle and a busy copy; the accessible name is what the user gets.
        const name = primary && (primary.getAttribute('aria-label') || primary.querySelector('[aria-hidden=false] .exo-action-copy')?.textContent || primary.textContent).trim()
        const beside = [...(d?.querySelectorAll('.exo-game-actions > button:not(.exo-primary-action)') ?? [])].map((b) => b.getAttribute('aria-label') || b.textContent.trim())
        const tools = [...(d?.querySelectorAll('button') ?? [])].map((b) => b.textContent.trim()).filter((t) => /^(Open folder|Verify files|Repair|Replace cover|Refresh art|Reset cover)$/.test(t))
        // Upscalers and Shaders are one row each: the row is the only button on its line.
        const lines = [...(d?.querySelectorAll('.exo-game-line') ?? [])].map((s) => ({ line: s.getAttribute('aria-label'), buttons: s.querySelectorAll('button').length }))
        return d && primary ? { title: d.getAttribute('aria-label'), primary: name, primaryDisabled: primary.disabled, beside, tools, lines, heights: [primary, ...d.querySelectorAll('.exo-game-actions > .exo-game-side')].map((b) => Math.round(b.getBoundingClientRect().height)) } : null
      }, { timeoutMs: 5000 })
      await c.shot(sh)
      if (!sheet) return ['game page (role=dialog with a primary action) did not open']
      Object.assign(c.evidence, sheet)
      const f = []
      const g = cacheById.get(tile.id)
      const expected = kind === 'not-installed' ? /^Install\b/ : /^Play$/
      if (g) c.evidence.cache = { installed: g.installed, updateAvailable: g.updateAvailable, owned: g.owned, canInstall: g.canInstall }
      c.evidence.expectedPrimary = expected.source
      if (!expected.test(sheet.primary ?? '')) f.push(`primary action "${sheet.primary}", expected ${kind === 'installed' ? 'Play' : 'Install'}`)
      // Installed: Remove and Properties beside Play at its height; the folder, files and cover tools live in Properties.
      if (kind === 'installed') {
        if (sheet.beside.join('|') !== 'Remove|Properties') f.push(`beside Play: ${sheet.beside.join(', ') || 'nothing'} (expected Remove, Properties)`)
        if (sheet.tools.length) f.push(`the page still shows ${sheet.tools.join(', ')}`)
        for (const l of sheet.lines) if (l.buttons !== 1) f.push(`the ${l.line} line has ${l.buttons} buttons, not one row`)
        if (new Set(sheet.heights).size > 1) f.push(`Play and its neighbors differ in height (${sheet.heights.join(', ')} px)`)
      }
      if (sheet.title !== tile.label.replace(/ \((update|queued)\)$/, '').trim()) c.evidence.titleNote = `page "${sheet.title}" vs card "${tile.label}"`
      await sh.key('Escape')
      const closed = await sh.waitFor(() => !document.querySelector('[role=dialog][aria-modal=true]:not([hidden] *)'), { timeoutMs: 3000 })
      if (!closed) { f.push('Escape did not close the game page'); await sh.click(() => document.querySelector('[role=dialog] button[aria-label=Close]')) }
      return f
    })
  }
  await libraryTop(sh)
}

// ---------- covers: identity, paint and sharpness of library art ----------
// Cards draw Exo's cached posters from covers.exo-launcher.local (the file) or
// thumbs.exo-launcher.local (a scaled copy of that same file), or a vendor CDN.

const COVER_ORIGIN = 'https://covers.exo-launcher.local'
const THUMB_ORIGIN = 'https://thumbs.exo-launcher.local'

/** Page side: every library card on screen, the image it draws and the size it draws it at. */
const COVER_AUDIT = `(() => {
  const pane = document.querySelector('main.exo-library-pane')
  if (!pane) return null
  const view = pane.getBoundingClientRect()
  const fileOf = (u) => { try { const p = new URL(u); return p.origin === '${COVER_ORIGIN}' || p.origin === '${THUMB_ORIGIN}' ? decodeURIComponent(p.pathname.slice(1)) : p.origin + p.pathname } catch { return u } }
  const onScreen = (b) => { const r = b.getBoundingClientRect(); return r.width > 0 && r.bottom > view.top + 4 && r.top < view.bottom - 4 && r.right > view.left && r.left < view.right }
  return [...pane.querySelectorAll('.exo-tile-hit[data-game-id]')].filter(onScreen).map((b) => {
    const media = b.querySelector('.exo-tile-media')
    const img = media?.querySelector('img.exo-cover-front:not([data-under])') ?? null
    const r = (media ?? b).getBoundingClientRect()
    const src = img ? (img.currentSrc || img.src) : null
    return {
      id: b.dataset.gameId,
      title: b.querySelector('.exo-card-title')?.textContent.trim() ?? null,
      src, file: src ? fileOf(src) : null, thumb: !!src && src.startsWith('${THUMB_ORIGIN}/'),
      painted: !!img && img.complete && img.naturalWidth > 0 && getComputedStyle(img).opacity === '1',
      naturalWidth: img?.naturalWidth ?? 0,
      cssWidth: Math.round(r.width * 10) / 10,
      devicePx: Math.ceil(r.width * devicePixelRatio),
      rect: [Math.round(r.left), Math.round(r.top), Math.round(r.width), Math.round(r.height)],
    }
  })
})()`

/** Page side: pixel width of each cached file at full size (0 when it does not exist). */
const probeCoverFiles = (files) => `(async () => {
  const out = {}
  await Promise.all(${JSON.stringify(files)}.map((f) => new Promise((done) => {
    const i = new Image()
    const t = setTimeout(() => { out[f] = 0; done() }, 5000)
    i.onload = () => { clearTimeout(t); out[f] = i.naturalWidth; done() }
    i.onerror = () => { clearTimeout(t); out[f] = 0; done() }
    i.src = '${COVER_ORIGIN}/' + encodeURIComponent(f)
  })))
  return out
})()`

const steamAppOf = (id, game) => {
  const texts = [id, game?.launchTarget, ...(game?.variants ?? []).flatMap((v) => [v.id, v.launchTarget])]
  for (const t of texts) { const m = /^steam:(\d+)/i.exec(t ?? '') ?? /steam:\/\/(?:rungameid|run)\/(\d+)/i.exec(t ?? ''); if (m) return m[1] }
  return null
}
const coverFileOf = (url) => { try { const u = new URL(url); return u.origin === COVER_ORIGIN ? decodeURIComponent(u.pathname.slice(1)) : null } catch { return null } }

/**
 * What a card must show: its cache cover file, or for a Steam title its 2x
 * poster. Null when the art comes from a vendor CDN (nothing of Exo's to compare).
 */
function expectedArt(tile, game) {
  const own = game?.coverUrl ? coverFileOf(game.coverUrl) : null
  const app = steamAppOf(tile.id, game)
  if (own) return { file: own, app }
  if (game?.coverUrl) return { file: null, app }
  return { file: app ? `${app}_2x.jpg` : null, app }
}

/** Whether the file a card draws belongs to that card's game. */
function drawsOwnArt(tile, expect) {
  if (!tile.file) return true
  if (tile.file.startsWith('custom-cover-')) return true
  if (expect.file && tile.file === expect.file) return true
  if (expect.app) return new RegExp(`(^|[/_])${expect.app}([_./]|$)`).test(tile.file)
  return !expect.file
}

/** Checks one audit: identity (every painted card), then paint and sharpness once settled. */
async function judgeCovers(sh, tiles, cacheById, { settled }) {
  const f = []
  const rows = tiles.map((t) => ({ tile: t, expect: expectedArt(t, cacheById.get(t.id)) }))
  const probeFiles = [...new Set(rows.flatMap(({ tile, expect }) => [expect.file, tile.thumb ? tile.file : null]).filter(Boolean))]
  const sizes = probeFiles.length ? await sh.eval(probeCoverFiles(probeFiles)) : {}
  const wrong = rows.filter(({ tile, expect }) => tile.painted && !drawsOwnArt(tile, expect))
  for (const { tile, expect } of wrong) f.push(`"${tile.title}" draws ${tile.file} (expected ${expect.file ?? 'art for steam ' + expect.app})`)
  // The same bitmap on two different games' cards is art that moved between cards.
  const byFile = new Map()
  for (const { tile } of rows) if (tile.painted && tile.file) byFile.set(tile.file, [...(byFile.get(tile.file) ?? []), tile])
  for (const [file, list] of byFile) if (new Set(list.map((t) => t.id)).size > 1) f.push(`${file} is drawn on ${list.length} cards: ${list.map((t) => t.title).join(', ')}`)
  const evidence = { visible: tiles.length, painted: tiles.filter((t) => t.painted).length, wrongArt: wrong.map(({ tile }) => ({ id: tile.id, title: tile.title, file: tile.file })) }
  if (!settled) return { failures: f, evidence }

  const blank = rows.filter(({ tile, expect }) => !tile.painted && expect.file && sizes[expect.file] > 0)
  for (const { tile } of blank) f.push(`"${tile.title}" shows no cover though its art is cached`)
  const downgraded = rows.filter(({ tile, expect }) => tile.painted && expect.file && sizes[expect.file] > 0 && tile.file !== expect.file && !tile.file?.startsWith('custom-cover-'))
  for (const { tile, expect } of downgraded) f.push(`"${tile.title}" fell back to ${tile.file} although ${expect.file} is cached`)
  // Sharpness: never fewer pixels than the card has, and a thumbnail carries
  // twice the card's device pixels (or the whole source when that is smaller).
  const soft = []
  for (const { tile } of rows) {
    if (!tile.painted) continue
    const source = tile.thumb ? sizes[tile.file] || tile.naturalWidth : tile.naturalWidth
    const need = tile.thumb ? Math.min(2 * tile.devicePx, source) : Math.min(tile.devicePx, source)
    if (tile.naturalWidth + 2 < need) soft.push({ title: tile.title, file: tile.file, thumb: tile.thumb, served: tile.naturalWidth, source, cardDevicePx: tile.devicePx, need })
  }
  for (const s of soft) f.push(`"${s.title}" serves ${s.served} px for a ${s.cardDevicePx} px card (needs ${s.need}; source ${s.source})`)
  Object.assign(evidence, {
    blank: blank.map(({ tile }) => tile.title),
    downgraded: downgraded.map(({ tile, expect }) => ({ title: tile.title, drawn: tile.file, cached: expect.file })),
    soft,
    sizes: rows.slice(0, 12).map(({ tile }) => ({ title: tile.title, file: tile.file, thumb: tile.thumb, served: tile.naturalWidth, source: tile.thumb ? sizes[tile.file] ?? null : tile.naturalWidth, cssWidth: tile.cssWidth, devicePx: tile.devicePx })),
  })
  return { failures: f, evidence }
}

/** Waits until every card on screen has painted (or the deadline), then audits it. */
async function settledCovers(sh, timeoutMs = 10000) {
  const deadline = Date.now() + timeoutMs
  let tiles = null
  while (Date.now() < deadline) {
    tiles = await sh.eval(COVER_AUDIT)
    if (tiles?.length && tiles.every((t) => t.painted)) break
    await sleep(250)
  }
  return tiles ?? []
}

async function covers(sh, phase, cachePath) {
  const cache = cachePath ? await readJson(cachePath).catch(() => null) : null
  const cacheById = new Map((cache?.games ?? []).map((g) => [g.id, g]))
  await sh.click(roomButton, 'Library')
  await sh.waitFor(() => document.querySelectorAll('main.exo-library-pane .exo-tile-hit').length > 0, { timeoutMs: 15000 })
  const where = await sh.eval(`(() => { const p = document.querySelector('main.exo-library-pane').getBoundingClientRect(); return { x: Math.round(p.left + p.width / 2), y: Math.round(p.top + p.height / 2), dpr: devicePixelRatio, viewport: innerWidth + 'x' + innerHeight, screen: screen.width + 'x' + screen.height } })()`)
  data.display = where

  if (phase === 'scroll') {
    await check('covers.fast-scroll', 'Fast wheel scrolling: every card draws its own game\'s art, never another card\'s', async (c) => {
      await sh.eval(`document.querySelector('main.exo-library-pane').scrollTop = 0`)
      await sleep(400)
      const f = []
      const mid = []
      // Four fling cycles down and back up with real wheel input; audit mid-flight each time.
      for (let cycle = 0; cycle < 4; cycle++) {
        for (const direction of [1, -1]) {
          for (let i = 0; i < 28; i++) { await sh.wheel(where.x, where.y, direction * 420); await sleep(6) }
          const tiles = await sh.eval(COVER_AUDIT)
          const judged = await judgeCovers(sh, tiles ?? [], cacheById, { settled: false })
          mid.push({ cycle, direction, scrollTop: await sh.eval(`Math.round(document.querySelector('main.exo-library-pane').scrollTop)`), ...judged.evidence })
          f.push(...judged.failures.map((x) => `mid-scroll ${cycle}${direction > 0 ? 'v' : '^'}: ${x}`))
          if (cycle === 0) await c.shot(sh, `mid-${direction > 0 ? 'down' : 'up'}`)
        }
      }
      // Park mid-library and let it settle: every card must now be painted with its own art.
      for (let i = 0; i < 10; i++) { await sh.wheel(where.x, where.y, 420); await sleep(6) }
      const tiles = await settledCovers(sh)
      const judged = await judgeCovers(sh, tiles, cacheById, { settled: true })
      f.push(...judged.failures)
      Object.assign(c.evidence, { display: where, midScroll: mid, settled: judged.evidence })
      await c.shot(sh, 'settled')
      return [...new Set(f)].slice(0, 40)
    })

    await check('covers.bottom-nudge', 'Small wheel nudges at the end of the library: the page keeps its height and every card keeps its own art', async (c) => {
      const pane = `document.querySelector('main.exo-library-pane')`
      for (let i = 0; i < 60 && await sh.eval(`${pane}.scrollTop + ${pane}.clientHeight < ${pane}.scrollHeight - 1`); i++) { await sh.wheel(where.x, where.y, 1200); await sleep(20) }
      await settledCovers(sh)
      const bottom = await sh.eval(`(() => { const p = ${pane}; return { scrollTop: Math.round(p.scrollTop), scrollHeight: p.scrollHeight, clientHeight: p.clientHeight } })()`)
      if (bottom.scrollHeight <= bottom.clientHeight) c.skip('the library fits on one screen; there is no end of list to nudge')
      await c.shot(sh, 'bottom')
      // Every frame while nudging: a library that changes height at the end of
      // its scroll range clamps the scroll and jumps the cards by a row.
      await sh.eval(`(() => { const p = ${pane}; const r = window.__exoNudge = { heights: new Set([p.scrollHeight]), frames: 0, on: true }; const tick = () => { if (!r.on) return; r.heights.add(p.scrollHeight); r.frames++; requestAnimationFrame(tick) }; requestAnimationFrame(tick); return true })()`)
      const f = []
      const samples = []
      // Up 120 / down 60 drifts ~600 px off the end, then down 120 / up 60 walks
      // back: the mounted rows change several times within a screen of the end.
      for (let i = 0; i < 20; i++) {
        const delta = (i < 10 ? -1 : 1) * (i % 2 ? -60 : 120)
        await sh.wheel(where.x, where.y, delta)
        await sleep(90)
        const tiles = await sh.eval(COVER_AUDIT)
        const judged = await judgeCovers(sh, tiles ?? [], cacheById, { settled: false })
        samples.push({ i, delta, scrollTop: await sh.eval(`Math.round(${pane}.scrollTop)`), ...judged.evidence })
        f.push(...judged.failures.map((x) => `nudge ${i}: ${x}`))
        if (i === 9) await c.shot(sh, 'mid-nudge')
      }
      const frames = await sh.eval(`(() => { const r = window.__exoNudge; r.on = false; return { frames: r.frames, heights: [...r.heights] } })()`)
      if (frames.heights.length > 1) f.push(`the library changed height while nudging (${frames.heights.join(', ')} px over ${frames.frames} frames): rows and spacers swapped out of step`)
      const tiles = await settledCovers(sh)
      const judged = await judgeCovers(sh, tiles, cacheById, { settled: true })
      // Pixels belong to covers.sharpness.
      f.push(...judged.failures.filter((x) => !/ serves \d+ px/.test(x)))
      Object.assign(c.evidence, { display: where, bottom, frames, nudges: samples, settled: judged.evidence })
      await c.shot(sh, 'settled')
      return [...new Set(f)].slice(0, 40)
    })

    await check('covers.sharpness', 'Cover pixels: served width >= card device pixels (thumbnails >= 2x); crop of the top-left cards', async (c) => {
      await sh.eval(`document.querySelector('main.exo-library-pane').scrollTop = 0`)
      const tiles = await settledCovers(sh)
      const judged = await judgeCovers(sh, tiles, cacheById, { settled: true })
      Object.assign(c.evidence, { display: where, ...judged.evidence })
      // A 1:1 crop of the first cards, for eyeballing against another build's crop.
      const first = tiles.slice(0, 4)
      if (first.length) {
        const x = Math.min(...first.map((t) => t.rect[0])), y = Math.min(...first.map((t) => t.rect[1]))
        const right = Math.max(...first.map((t) => t.rect[0] + t.rect[2])), bottom = Math.max(...first.map((t) => t.rect[1] + t.rect[3]))
        await c.clip(sh, 'crop', { x, y, width: right - x, height: bottom - y })
      }
      await c.shot(sh)
      // Identity and paint belong to covers.fast-scroll; this check owns pixels.
      return judged.evidence.soft.map((s) => `"${s.title}" serves ${s.served} px for a ${s.cardDevicePx} px card (needs ${s.need}; source ${s.source})`)
    })
    return
  }

  if (phase === 'rooms') {
    await check('covers.after-rooms', 'Back from Discord, Music, Devices and Settings, every card shows its own art within 1.5 s', async (c) => {
      const before = await settledCovers(sh, 15000)
      if (!before.length) return ['no library cards on screen']
      const tour = []
      for (const name of ['Discord', 'Music', 'Devices']) {
        const t0 = Date.now()
        if (!await sh.click(roomButton, name)) { tour.push({ room: name, clicked: false }); continue }
        const shown = await sh.waitFor((n) => [...document.querySelectorAll('nav[aria-label="Rooms"] button')].find((b) => b.textContent.trim() === n)?.getAttribute('aria-current') === 'page', { timeoutMs: 5000, args: [name] })
        await sleep(2000)
        tour.push({ room: name, shown: !!shown, ms: Date.now() - t0 })
      }
      await sh.click(byLabel, 'Open settings')
      tour.push({ room: 'Settings', shown: !!await sh.waitFor(() => document.querySelector('main.exo-settings')?.getBoundingClientRect().width > 0, { timeoutMs: 5000 }) })
      await sleep(1500)
      await sh.click(roomButton, 'Library')
      const t0 = Date.now()
      let tiles = []
      let paintedAtMs = null
      while (Date.now() - t0 < 1500) {
        tiles = (await sh.eval(COVER_AUDIT)) ?? []
        if (tiles.length && tiles.every((t) => t.painted || !before.find((b) => b.id === t.id)?.painted)) { paintedAtMs = Date.now() - t0; break }
        await sleep(100)
      }
      await c.shot(sh)
      const judged = await judgeCovers(sh, tiles, cacheById, { settled: false })
      const f = [...judged.failures]
      const was = new Map(before.map((t) => [t.id, t]))
      const lost = tiles.filter((t) => was.get(t.id)?.painted && !t.painted)
      for (const t of lost) f.push(`"${t.title}" showed ${was.get(t.id).file} before the rooms and nothing 1.5 s after coming back (${t.src ? t.file : 'no image'})`)
      Object.assign(c.evidence, { tour, visibleBefore: before.length, paintedBefore: before.filter((t) => t.painted).length, visibleAfter: tiles.length, paintedAfter: tiles.filter((t) => t.painted).length, paintedAtMs, lost: lost.map((t) => t.title), ...judged.evidence })
      if (!tiles.length) f.push('no library cards on screen after coming back')
      return f
    })
    return
  }

  const names = { restore: 'after minimize and restore', refresh: 'after a library refresh' }
  if (!names[phase]) throw new Error('unknown covers phase ' + phase)
  await check(`covers.${phase}`, `Covers still painted ${names[phase]}`, async (c) => {
    if (phase === 'refresh') {
      // The live-library hook rescans on "online"; the host logs the scan (the runner checks it).
      await sh.eval(`window.dispatchEvent(new Event('online'))`)
      await sleep(5000)
    }
    const tiles = await settledCovers(sh, 15000)
    const judged = await judgeCovers(sh, tiles, cacheById, { settled: true })
    Object.assign(c.evidence, judged.evidence)
    await c.shot(sh)
    if (!tiles.length) return ['no library cards on screen']
    return judged.failures.filter((x) => !/ serves \d+ px/.test(x))
  })
}

async function settings(sh, settingsPath) {
  const before = await readJson(settingsPath)
  await sh.click(byLabel, 'Open settings')
  const ready = await sh.waitFor(() => document.querySelector('main.exo-settings h2[id^=set-]') && document.querySelectorAll('main.exo-settings h2[id^=set-]').length, { timeoutMs: 10000 })

  // The second open is the everyday one: Settings stays mounted, so its content
  // must be on screen in the first frames after the click, with nothing left saying Checking.
  await check('settings.open-fast', 'Opened a second time, Settings shows its content within 150 ms of the click', async (c) => {
    if (!ready) return ['settings did not open the first time']
    await sh.waitFor(() => !document.querySelector('main.exo-settings')?.textContent.includes('Checking…'), { timeoutMs: 15000 })
    await sh.key('Escape')
    const closed = await sh.waitFor(() => !(document.querySelector('main.exo-settings')?.getBoundingClientRect().width > 0), { timeoutMs: 3000 })
    if (!closed) return ['Escape did not close Settings']
    await sleep(1500)
    const armed = await sh.eval(`(() => {
      const gear = [...document.querySelectorAll('button')].find((b) => b.getAttribute('aria-label') === 'Open settings' && b.getBoundingClientRect().width > 0)
      if (!gear) return false
      const r = window.__exoOpenFast = { clickAt: null, contentAt: null, frames: 0 }
      gear.addEventListener('click', () => {
        r.clickAt = performance.now()
        const tick = () => {
          r.frames++
          const main = document.querySelector('main.exo-settings')
          const box = main?.getBoundingClientRect()
          const shown = box && box.width > 0 && main.querySelectorAll('section[id^=set-] .exo-set-item').length > 0
          if (shown && !main.textContent.includes('Checking…')) { r.contentAt = performance.now(); return }
          if (performance.now() - r.clickAt < 5000) requestAnimationFrame(tick)
        }
        requestAnimationFrame(tick)
      }, { capture: true, once: true })
      return true
    })()`)
    if (!armed) return ['no "Open settings" button']
    await sh.click(byLabel, 'Open settings')
    const timing = await sh.waitFor(() => { const r = window.__exoOpenFast; return r?.contentAt ? { ms: Math.round(r.contentAt - r.clickAt), frames: r.frames } : null }, { timeoutMs: 6000, interval: 50 })
    await c.shot(sh)
    c.evidence.secondOpen = timing ?? { contentMs: null }
    if (!timing) return ['Settings content was not on screen 5 s after the second click']
    return timing.ms <= 150 ? [] : [`Settings content took ${timing.ms} ms after the second click (${timing.frames} frames)`]
  })

  // Exo pauses animations while it is not the focused window (a hosted Discord
  // window or a game has focus). A room opened then must still fade all the way in.
  await check('rooms.visible-unfocused', 'A room opened while Exo is unfocused becomes fully visible', async (c) => {
    const previous = await sh.eval(`document.documentElement.dataset.exoInactive ?? null`)
    try {
      await sh.key('Escape')
      await sh.waitFor(() => !(document.querySelector('main.exo-settings')?.getBoundingClientRect().width > 0), { timeoutMs: 3000 })
      await sh.eval(`(() => { document.documentElement.dataset.exoInactive = 'true'; document.dispatchEvent(new Event('exo:activity')); return true })()`)
      await sh.click(byLabel, 'Open settings')
      await sleep(600)
      const opacity = await sh.eval(`(() => { const room = document.querySelector('.exo-set-room'); return room ? Number(getComputedStyle(room).opacity) : null })()`)
      c.evidence.roomOpacity = opacity
      await c.shot(sh)
      return opacity === 1 ? [] : [`Settings opened unfocused sits at opacity ${opacity}`]
    } finally {
      await sh.eval(`(() => { const p = ${JSON.stringify(previous)}; if (p === null) delete document.documentElement.dataset.exoInactive; else document.documentElement.dataset.exoInactive = p; document.dispatchEvent(new Event('exo:activity')); return true })()`)
    }
  })

  await check('settings.sections', 'Every settings section heading is present', async (c) => {
    const found = await sh.eval(`(() => { const nav = [...document.querySelectorAll('main.exo-settings nav button')].map(b => b.textContent.trim()); const heads = [...document.querySelectorAll('main.exo-settings section[id^=set-]')].map(s => ({ id: s.id, heading: document.getElementById(s.id + '-title')?.textContent.trim() ?? null })); return { nav, heads } })()`)
    Object.assign(c.evidence, found)
    await c.shot(sh)
    const f = []
    if (!ready) f.push('settings did not open')
    for (const s of found.heads) if (!s.heading) f.push(`#${s.id} has no #${s.id}-title heading`)
    for (const n of found.nav) if (!found.heads.some((h) => h.heading === n)) f.push(`nav "${n}" has no section heading`)
    const expected = ['General', 'Downloads', 'Music', 'Lucid', 'Capture', 'Game stores', 'Optional keys', 'About']
    const headings = found.heads.map((h) => h.heading)
    if (headings.join('|') !== expected.join('|')) f.push(`sections ${JSON.stringify(headings)} (expected ${expected.join(', ')})`)
    if (found.nav.length !== expected.length) f.push(`${found.nav.length} nav entries (expected ${expected.length})`)
    return f
  })

  await check('settings.nav', 'Settings nav jumps to each section', async (c) => {
    const names = await sh.eval(`[...document.querySelectorAll('main.exo-settings nav button')].map(b => b.textContent.trim())`)
    const f = []
    c.evidence.jumps = []
    for (const name of [...names.slice(1), names[0]]) {
      await sh.click((n) => [...document.querySelectorAll('main.exo-settings nav button')].find((b) => b.textContent.trim() === n), name)
      const r = await sh.waitFor((n) => {
        const sc = document.querySelector('.exo-settings-scroll')
        const sec = [...document.querySelectorAll('main.exo-settings section[id^=set-]')].find((s) => document.getElementById(s.id + '-title')?.textContent.trim() === n)
        if (!sec) return { missing: true }
        const a = sc.getBoundingClientRect(); const b = sec.getBoundingClientRect()
        const headingInView = b.top >= a.top - 2 && b.top < a.bottom - 20
        const settled = Math.abs(sc.scrollTop - (sc.__lastTop ?? -1)) < 1; sc.__lastTop = sc.scrollTop
        if (!headingInView || !settled) return null
        const cur = document.querySelector('main.exo-settings nav button[aria-current=true]')?.textContent.trim() ?? null
        return { heading: n, offsetFromTop: Math.round(b.top - a.top), scrollTop: Math.round(sc.scrollTop), ariaCurrent: cur }
      }, { timeoutMs: 4000, interval: 150, args: [name] })
      c.evidence.jumps.push(r ?? { heading: name, inView: false })
      if (!r || r.missing) f.push(`"${name}" heading not scrolled into view`)
      else if (r.ariaCurrent !== name) f.push(`after jumping to "${name}" the nav marks "${r.ariaCurrent}" current`)
    }
    await c.shot(sh, 'after')
    return f
  })

  await check('settings.no-scrollbar', 'No visible scrollbar in Settings', async (c) => {
    const s = await sh.eval(`[...document.querySelectorAll('main.exo-settings, main.exo-settings *')].filter(e => e.scrollHeight > e.clientHeight + 1 && /(auto|scroll)/.test(getComputedStyle(e).overflowY) && e.offsetWidth > 0).map(e => ({ el: e.tagName.toLowerCase() + '.' + String(e.className).split(' ')[0], scrollbarWidth: getComputedStyle(e).scrollbarWidth, gutterPx: e.offsetWidth - e.clientWidth - parseFloat(getComputedStyle(e).borderLeftWidth) - parseFloat(getComputedStyle(e).borderRightWidth) }))`)
    c.evidence.scrollers = s
    const f = []
    if (!s.length) f.push('no scrolling container found in Settings')
    for (const x of s) if (x.scrollbarWidth !== 'none' || x.gutterPx > 0) f.push(`${x.el} scrollbar-width=${x.scrollbarWidth} gutter=${x.gutterPx}px`)
    return f
  })

  await check('settings.key-fields', 'Key fields are password inputs', async (c) => {
    const k = await sh.eval(`(() => { const sec = document.getElementById('set-keys'); if (!sec) return null; return [...sec.querySelectorAll('.exo-set-item')].map(row => ({ label: row.querySelector('.exo-set-item-label')?.textContent.trim(), inputs: [...row.querySelectorAll('input')].map(i => i.type), saved: /\\bSaved\\b/.test(row.querySelector('.exo-set-state')?.textContent ?? '') })) })()`)
    if (!k) return ['#set-keys section missing']
    c.evidence.rows = k
    const f = []
    const keyRows = k.filter((r) => /\b(key|token|secret)\b/i.test(r.label))
    if (!keyRows.length) f.push('no key rows')
    for (const r of keyRows) {
      if (r.saved && !r.inputs.length) continue
      if (!r.inputs.length) f.push(`"${r.label}" has neither a field nor a Saved state`)
      for (const t of r.inputs) if (t !== 'password') f.push(`"${r.label}" input type=${t}`)
    }
    return f
  })

  // Exo optimizes every hosted app itself (AppOptimization.IsOn is always on): Settings has no
  // Optimize row, no Run or Undo, and no retired Clean up Discord switch.
  await check('settings.no-optimize-rows', 'Settings has no Optimize rows: Exo optimizes every app itself', async (c) => {
    const rows = await sh.eval(`[...document.querySelectorAll('main.exo-settings .exo-set-item')].map(row => ({ label: row.querySelector('.exo-set-item-label')?.textContent.trim() ?? '', buttons: [...row.querySelectorAll('button')].map(b => b.textContent.trim()) })).filter(r => /^Optimize |^Clean up Discord$/.test(r.label) || r.buttons.some(b => b === 'Undo'))`)
    const apps = await sh.eval(`document.querySelectorAll('#set-apps .exo-set-item').length`)
    Object.assign(c.evidence, { rows, appRows: apps })
    await c.shot(sh)
    const f = rows.map((r) => `Settings still shows "${r.label}" (${r.buttons.join(', ')})`)
    if (!apps) f.push('Music shows no app rows')
    return f
  })
  await check('settings.copy', "Settings: the Steam key says it fills in achievements; nothing of OpenMouse or the Exo account is left", async (c) => {
    const text = await sh.eval(`document.querySelector('main.exo-settings')?.innerText ?? ''`)
    const lines = text.split('\n').map((l) => l.trim()).filter(Boolean)
    const steam = await sh.eval(`(() => { const row = [...document.querySelectorAll('#set-keys .exo-set-item')].find(r => /Steam/.test(r.querySelector('.exo-set-item-label')?.textContent ?? '')); return row ? { label: row.querySelector('.exo-set-item-label')?.textContent.trim(), detail: row.querySelector('.exo-set-item-detail')?.textContent.trim() ?? '' } : null })()`)
    const remnants = lines.filter((l) => /OpenMouse|Exo account|Sign in to Exo|Sign up|Delete account|Download account/i.test(l))
    Object.assign(c.evidence, { steamKey: steam, remnants, mentionsStore: lines.filter((l) => /\bStore\b/.test(l) && !/Microsoft Store/.test(l)), mentionsExoMouse: lines.filter((l) => /Exo Mouse/.test(l)) })
    const f = []
    if (!steam) f.push('no Steam key row in Optional keys')
    else if (!/achievements/i.test(steam.detail)) f.push(`the Steam key says "${steam.detail}" (no achievements)`)
    for (const r of remnants) f.push(`left over: "${r.slice(0, 100)}"`)
    return f
  })

  await check('settings.check-updates', '"Check for updates" button present', async (c) => {
    const b = await sh.eval(`(() => { const b = [...document.querySelectorAll('#set-about button')].find(b => /^(Check for updates|Checking…)$/.test(b.textContent.trim())); return b ? { text: b.textContent.trim(), disabled: b.disabled } : null })()`)
    if (!b) return ['no "Check for updates" button in About']
    Object.assign(c.evidence, b)
    return []
  })

  // Reversible round trip through the host: Reduce motion changes only the UI.
  await check('settings.toggle-roundtrip', 'Reduce motion toggles, persists, and restores byte-for-byte', async (c) => {
    const { createHash } = await import('node:crypto')
    const hash = async () => createHash('sha256').update(await readFile(settingsPath)).digest('hex')
    const h0 = await hash()
    const original = (await readJson(settingsPath)).reduceMotion === true
    const sw = () => { const l = [...document.querySelectorAll('.exo-set-item-label')].find((x) => x.textContent.trim() === 'Reduce motion'); return l?.closest('.exo-set-item')?.querySelector('[role=switch]') }
    const state = async () => sh.call(() => { const l = [...document.querySelectorAll('.exo-set-item-label')].find((x) => x.textContent.trim() === 'Reduce motion'); return l?.closest('.exo-set-item')?.querySelector('[role=switch]')?.getAttribute('aria-checked') ?? null })
    const f = []
    const s0 = await state()
    if (s0 === null) return ['no Reduce motion switch']
    if ((s0 === 'true') !== original) f.push(`switch ${s0} vs settings.json reduceMotion ${original}`)
    const waitFile = async (want) => { for (let i = 0; i < 50; i++) { try { if (((await readJson(settingsPath)).reduceMotion === true) === want) return true } catch { /* mid-write */ } await sleep(100) } return false }
    await sh.click(sw)
    const flipped = await waitFile(!original)
    const s1 = await state()
    if (!flipped) f.push('settings.json did not change after the toggle')
    if ((s1 === 'true') === original) f.push('switch did not flip')
    await sh.click(sw)
    const back = await waitFile(original)
    const s2 = await state()
    if (!back) f.push('settings.json did not return after the second toggle')
    if (s2 !== s0) f.push('switch did not return')
    await sleep(300)
    const h2 = await hash()
    c.evidence.sha256Before = h0
    c.evidence.sha256After = h2
    c.evidence.switchStates = [s0, s1, s2]
    if (h0 !== h2) f.push('settings.json bytes differ after the round trip (the script restores it at the end)')
    return f
  })

  data.musicService = await sh.eval(`(() => { const l = [...document.querySelectorAll('.exo-set-item-label')].find(x => x.textContent.trim() === 'Music service'); const row = l?.closest('.exo-set-item'); const on = row && [...row.querySelectorAll('button')].find(b => b.getAttribute('aria-checked') === 'true' || b.getAttribute('aria-pressed') === 'true' || b.classList.contains('is-on') || b.getAttribute('data-selected') === 'true'); return on?.textContent.trim() ?? null })()`)
}

/**
 * Exo's own Spotify or Apple Music room: every read view answers through the
 * hidden engine (home, library, a search, the queue, the player): Spotify's app,
 * or Apple's web player (music.apple.com) through its own MusicKit. Read-only:
 * nothing plays.
 */
async function nativeMusic(sh, service) {
  const rpc = service === 'apple' ? 'applemusic' : 'spotify'
  const name = service === 'apple' ? 'Apple Music' : 'Spotify'
  await check(`music.${service}.native`, `Exo's ${name} room reads ${name}'s own home, library, search and queue`, async (c) => {
    const start = await poll(async () => { const r = await sh.eval(hostCall(`${rpc}.start`, {}, 60000)); return r.state === 'loading' ? null : r }, 60000, 1000)
    c.evidence.start = start
    if (!start) return [`${name} never finished opening`]
    if (start.state === 'notInstalled' || start.state === 'signedOut' || start.state === 'notPremium') c.skip(`${name} is ${start.state}`)
    if (start.state !== 'running') return [`${rpc}.start says ${start.state}: ${start.message ?? ''}`]
    const f = []
    const views = { home: {}, library: {}, search: { query: 'a' }, queue: {}, player: {} }
    // Apple Music's own New and Radio pages, History and the web player's quality (read only).
    if (service === 'apple') Object.assign(views, { browse: {}, radio: {}, history: {}, audio: {} })
    for (const [view, options] of Object.entries(views)) {
      const t0 = Date.now()
      const r = await sh.eval(hostCall(`${rpc}.view`, { view, ...options }, 30000)).catch((error) => ({ ok: false, message: error.message }))
      const d = r.data ?? {}
      const size = d.sections?.length ?? d.items?.length ?? d.tracks?.length ?? (d.current ? 1 : 0) + (d.next?.length ?? 0) ?? null
      c.evidence[view] = { ok: r.ok, ms: Date.now() - t0, size, message: r.message }
      if (!r.ok) f.push(`${view}: ${r.message}`)
      else if (!['queue', 'player', 'history', 'audio'].includes(view) && !size) f.push(`${view} answered empty`)
    }
    if (service === 'spotify') await sh.eval(hostCall('spotify.leave')).catch(() => { })
    return f
  })
}

async function room(sh, name) {
  const id = name.toLowerCase()
  if (id === 'discord') return discordRoom(sh)
  await check(`room.${id}.open`, `${name} room opens`, async (c) => {
    if (!await sh.click(roomButton, name)) return [`no "${name}" room button`]
    const cur = await sh.waitFor((n) => [...document.querySelectorAll('nav[aria-label="Rooms"] button')].find((b) => b.textContent.trim() === n)?.getAttribute('aria-current') === 'page', { timeoutMs: 5000, args: [name] })
    // Wait for the host to settle: either a state line (opening/installing/closed/failed) or an empty surface (the client window is over it).
    const t0 = Date.now()
    let state = null
    // Rooms stay mounted once opened; the current one is the surface with a size.
    // Spotify is Exo's own room (main.exo-music) once its hidden client is signed in.
    await sh.waitFor(() => [...document.querySelectorAll('main.exo-hosted-room .exo-hosted-room-surface, main.exo-music')].some((s) => s.getBoundingClientRect().width > 0), { timeoutMs: 10000 })
    if (id === 'music') {
      const native = await sh.waitFor(() => { const m = document.querySelector('main.exo-music'); return !!m && m.getBoundingClientRect().width > 0 }, { timeoutMs: 1000 }).catch(() => false)
      if (native) {
        data.spotifyNative = true
        c.evidence.native = true
        c.evidence.libraryRows = await sh.waitFor(() => document.querySelectorAll('.exo-music-library-row').length || null, { timeoutMs: 15000 }).catch(() => 0)
        await c.shot(sh)
        return cur ? [] : [`${name} nav button not aria-current=page`]
      }
    }
    for (;;) {
      state = await sh.eval(`(() => { const s = [...document.querySelectorAll('main.exo-hosted-room .exo-hosted-room-surface')].find(s => s.getBoundingClientRect().width > 0); if (!s) return { surface: false }; const st = s.querySelector('[role=status],[role=alert]'); const r = s.getBoundingClientRect(); return { surface: true, role: st?.getAttribute('role') ?? null, text: st ? st.innerText.trim().replace(/\\s+/g, ' ') : null, rect: [Math.round(r.left), Math.round(r.top), Math.round(r.width), Math.round(r.height)] } })()`)
      if (!state.surface || !state.text || !/^Opening|^Installing/.test(state.text) || Date.now() - t0 > 30000) break
      await sleep(250)
    }
    state.settleMs = Date.now() - t0
    state.dpr = await sh.eval('devicePixelRatio')
    // Exo speaks only while the provider's window is absent (opening, installing, closed, failed).
    state.providerState = state.text ?? 'running: no Exo state line, the provider window covers the room (host checks it)'
    Object.assign(c.evidence, state)
    data.surface = state.rect
    data.dpr = state.dpr
    await c.shot(sh)
    const f = []
    if (!cur) f.push(`${name} nav button not aria-current=page`)
    if (!state.surface) f.push('no hosted room surface')
    if (state.role === 'alert') f.push(`room shows: ${state.text}`)
    return f
  })
  if (id !== 'music') return
  await check('room.youtube.optimized', "YouTube Music's page carries Exo's optimized mode (always on)", async (c) => {
    const page = await openPage(port, 'music.youtube.com', 8000).catch(() => null)
    if (!page) c.skip('YouTube Music is not the hosted music app')
    try {
      // Loaded well before this check: the mark must survive the page's own boot.
      const marked = await page.eval(`document.documentElement.hasAttribute('data-exo-optimize')`)
      Object.assign(c.evidence, { pageMarked: marked })
      return marked ? [] : ["YouTube Music's page is not marked optimized"]
    } finally { page.close() }
  })
}

/**
 * The Discord room is Exo's own client (nothing hosted): it opens on the client (or Discord's QR
 * sign-in when signed out), Ctrl+K finds a channel by name and opens it, and Ctrl+1..5 leave the
 * room from its message box. Nothing is typed into or
 * sent from the message box; the channel is the test place, Uhh #mod-chat.
 */
const TEST_CHANNEL = 'mod-chat'
const TEST_SERVER = 'Uhh'
const DISCORD_STATE = () => {
  const dc = document.querySelector('main.exo-dc')
  if (dc && dc.getBoundingClientRect().width > 0) return { state: 'ready', servers: dc.querySelectorAll('.exo-dc-rail [data-guild-id], .exo-dc-rail button').length, chat: dc.querySelector('section.exo-dc-chat')?.getAttribute('aria-label') ?? null }
  const signin = document.querySelector('main.exo-dc-signin')
  if (signin && signin.getBoundingClientRect().width > 0) return { state: 'signedOut' }
  const st = [...document.querySelectorAll('main.exo-hosted-room .exo-hosted-room-state')].find((s) => s.getBoundingClientRect().width > 0)
  if (st) return { state: st.getAttribute('role') === 'alert' ? 'error' : 'connecting', text: st.innerText.trim().replace(/\s+/g, ' ') }
  return null
}

async function discordRoom(sh) {
  let ready = false
  await check('room.discord.open', "Discord room opens on Exo's own client (or Discord's sign-in), no hosted window", async (c) => {
    if (!await sh.click(navButton, 'Discord')) return ['no "Discord" room button']
    const cur = await sh.waitFor(isCurrent, { timeoutMs: 5000, args: ['Discord'] })
    await sh.eval('window.__exoDiscordState = ' + DISCORD_STATE.toString())
    const t0 = Date.now()
    const state = await sh.waitFor(() => { const s = window.__exoDiscordState(); return s && s.state !== 'connecting' ? s : null }, { timeoutMs: 30000, interval: 250 })
      ?? await sh.call(DISCORD_STATE)
    Object.assign(c.evidence, state ?? { state: null }, { settleMs: Date.now() - t0, hostedSurface: await sh.eval(`!!document.querySelector('main.exo-hosted-room .exo-hosted-room-surface')`) })
    await c.shot(sh)
    const f = []
    if (!cur) f.push('Discord nav button not aria-current=page')
    if (!state) f.push('the room showed neither the client nor the sign-in within 30 s')
    else if (state.state === 'error') f.push(`the room says: ${state.text}`)
    else if (state.state === 'connecting') f.push('still connecting after 30 s')
    if (c.evidence.hostedSurface) f.push('the room hosts a window surface')
    ready = state?.state === 'ready'
    return f
  })

  await check('discord.palette-channel', `Ctrl+K finds the Discord channel #${TEST_CHANNEL} by name and opens it in the Discord room`, async (c) => {
    if (!ready) c.skip('Discord is not signed in')
    await sh.click(navButton, 'Library')
    await sh.key('k', { ctrl: true })
    const input = await sh.waitFor(() => { const i = document.querySelector('[role=dialog] input[role=combobox]'); return i && document.activeElement === i ? true : null }, { timeoutMs: 3000 })
    if (!input) return ['Ctrl+K did not open the command menu with its field focused']
    await sh.type(TEST_CHANNEL)
    const option = (n, server) => [...document.querySelectorAll('[role=dialog] [role=listbox] [role=option]')].find((o) => { const t = o.querySelectorAll('span span'); return t[0]?.textContent.trim() === n && (!server || t[1]?.textContent.trim() === server) })
    const found = await sh.waitFor((n, server) => {
      const opts = [...document.querySelectorAll('[role=dialog] [role=listbox] [role=option]')]
      const hit = opts.find((o) => { const t = o.querySelectorAll('span span'); return t[0]?.textContent.trim() === n && t[1]?.textContent.trim() === server })
      return hit ? { options: opts.slice(0, 6).map((o) => o.innerText.replace(/\s+/g, ' ').trim()) } : null
    }, { timeoutMs: 8000, args: [TEST_CHANNEL, TEST_SERVER] })
    await c.shot(sh, 'found')
    c.evidence.found = found
    if (!found) { await sh.key('Escape'); return [`no "${TEST_CHANNEL}" (${TEST_SERVER}) among the results`] }
    await sh.click(option, TEST_CHANNEL, TEST_SERVER)
    const opened = await sh.waitFor((n) => {
      const room = [...document.querySelectorAll('nav[aria-label="Rooms"] button')].find((b) => b.textContent.trim().startsWith('Discord'))?.getAttribute('aria-current') === 'page'
      const chat = document.querySelector('main.exo-dc section.exo-dc-chat')?.getAttribute('aria-label')
      return room && chat === n ? { room, chat, menuClosed: !document.querySelector('[role=dialog] input[role=combobox]') } : null
    }, { timeoutMs: 8000, args: [TEST_CHANNEL] })
    await c.shot(sh, 'opened')
    c.evidence.opened = opened
    if (!opened) return [`choosing it did not open #${TEST_CHANNEL} in the Discord room`]
    // The menu fades out after the choice.
    c.evidence.menuClosed = !!(await sh.waitFor(() => ![...document.querySelectorAll('[role=dialog] input[role=combobox]')].some((i) => i.getBoundingClientRect().width > 0), { timeoutMs: 2000 }))
    return c.evidence.menuClosed ? [] : ['the command menu stayed open']
  })

  await check('discord.rail-drag', 'A server in the Discord rail lifts under a mouse drag (dims, its picture follows the pointer, a drop mark shows past the next icon); dropped back on itself nothing moves or is saved', async (c) => {
    if (!ready) c.skip('Discord is not signed in')
    await sh.click(navButton, 'Discord')
    await sh.waitFor(isCurrent, { timeoutMs: 3000, args: ['Discord'] })
    const order = () => [...document.querySelectorAll('nav.exo-dc-rail [data-dc-node]')].map((n) => n.dataset.dcNode)
    // The first server alone in the list and the icon after it.
    const pair = await sh.waitFor(() => {
      const roots = [...document.querySelectorAll('nav.exo-dc-rail [data-dc-node]:not([data-dc-within])')]
      const at = roots.findIndex((n) => !n.dataset.dcNode.startsWith('folder:'))
      if (at < 0 || at + 1 >= roots.length) return null
      return [roots[at], roots[at + 1]].map((n) => { const b = n.getBoundingClientRect(); return { key: n.dataset.dcNode, x: b.left + b.width / 2, y: b.top + b.height / 2, bottom: b.bottom } })
    }, { timeoutMs: 5000 })
    if (!pair) c.skip('no server with another icon after it in the rail')
    const [a, b] = pair
    const before = await sh.call(order)
    const mouse = (type, y) => sh.s.send('Input.dispatchMouseEvent', { type, x: a.x, y, button: 'left', buttons: type === 'mouseReleased' ? 0 : 1, clickCount: type === 'mouseMoved' ? 0 : 1 })
    await mouse('mouseMoved', a.y)
    await mouse('mousePressed', a.y)
    for (const y of [a.y + 8, b.y, b.bottom + 2]) { await mouse('mouseMoved', y); await sleep(40) }
    const dragging = await sh.waitFor(() => {
      const mark = document.querySelector('nav.exo-dc-rail [class*="is-drop-"]')
      const lifted = document.querySelector('nav.exo-dc-rail .is-lifted')?.dataset.dcNode ?? null
      const ghost = document.querySelector('.exo-dc-rail-ghost')
      return mark && lifted && ghost && !ghost.hidden ? { lifted, mark: mark.className.match(/is-drop-\w+/)?.[0], ghost: ghost.getBoundingClientRect().height > 0 } : null
    }, { timeoutMs: 2000 })
    await c.shot(sh, 'dragging')
    for (const y of [b.y, a.y]) { await mouse('mouseMoved', y); await sleep(40) }
    const home = await sh.call(() => !document.querySelector('nav.exo-dc-rail [class*="is-drop-"]'))
    await mouse('mouseReleased', a.y)
    await sleep(300)
    const after = await sh.call(order)
    Object.assign(c.evidence, { source: a.key, next: b.key, dragging, noMarkOverItself: home, unchanged: JSON.stringify(before) === JSON.stringify(after) })
    const f = []
    if (!dragging) f.push('dragging the server showed no lifted icon, picture or drop mark')
    else if (dragging.lifted !== a.key) f.push(`the lifted icon is ${dragging.lifted}, not the dragged server`)
    if (!home) f.push('a drop mark showed over the dragged server itself')
    if (!c.evidence.unchanged) f.push('dropping the server back on itself changed the rail')
    return f
  })

  await check('rooms.shortcuts', 'Ctrl+1..5 switch rooms even from the Discord message box (nothing is typed into it)', async (c) => {
    if (!ready) c.skip('Discord is not signed in')
    const order = ['Library', 'Store', 'Discord', 'Music', 'System']
    const f = []
    c.evidence.keys = []
    for (let i = 0; i < order.length; i++) {
      await sh.click(navButton, 'Discord')
      await sh.waitFor(isCurrent, { timeoutMs: 3000, args: ['Discord'] })
      const focused = await sh.waitFor(() => { const t = document.querySelector('main.exo-dc section.exo-dc-chat textarea'); if (!t) return null; t.focus(); return document.activeElement === t ? { draft: t.value.length } : null }, { timeoutMs: 5000 })
      if (!focused) { f.push('no message box to focus'); break }
      await sh.key(String(i + 1), { ctrl: true })
      const ok = await sh.waitFor((n) => [...document.querySelectorAll('nav[aria-label="Rooms"] button')].find((b) => b.textContent.trim().startsWith(n))?.getAttribute('aria-current') === 'page', { timeoutMs: 3000, args: [order[i]] })
      const typed = await sh.eval(`document.querySelector('main.exo-dc section.exo-dc-chat textarea')?.value.length ?? 0`)
      c.evidence.keys.push({ key: `Ctrl+${i + 1}`, room: order[i], opened: !!ok, draftBefore: focused.draft, draftAfter: typed })
      if (!ok) f.push(`Ctrl+${i + 1} from the message box did not open ${order[i]}`)
      if (typed !== focused.draft) f.push(`Ctrl+${i + 1} typed into the message box`)
    }
    await sh.click(navButton, 'Library')
    return f
  })
}

/** OpenMouse Bridge's loopback port: Exo no longer answers on it (the OpenMouse Bridge app may). */
const OPENMOUSE_BRIDGE = 'http://127.0.0.1:17846'

/**
 * Devices is Exo Mouse, its own program in a frame. The room fills with it and
 * settles on a connected mouse (its name and panels) or "No supported mouse
 * found"; Exo relays only HID calls and the installed-games read for it, so a call to anything else of
 * Exo's from the frame gets no answer. Read-only: nothing is written to the
 * mouse, and nothing of the retired web panel or Exo's own bridge is left.
 */
async function devices(sh) {
  await sh.click(roomButton, 'Devices')
  await sh.waitFor((n) => [...document.querySelectorAll('nav[aria-label="Rooms"] button')].find((b) => b.textContent.trim() === n)?.getAttribute('aria-current') === 'page', { timeoutMs: 5000, args: ['Devices'] })
  await check('room.devices.mouse', 'Devices shows Exo Mouse: the room fills with it and it settles on your mouse or on no supported mouse', async (c) => {
    // Exo Mouse is on its own origin: read inside it through its own frame, and its size from the shell.
    const mouse = await openFrame(sh, 'mouse.exo-launcher.local', 15000).catch(() => null)
    const inner = mouse && await mouse.waitFor(() => {
      const name = document.querySelector('.m-name')?.textContent.trim() ?? null
      const center = document.querySelector('.m-center-title')?.textContent.trim() ?? null
      if (!name && (!center || /^(Looking|Connecting)/.test(center))) return null
      return {
        name, center,
        line: document.querySelector('.m-center-line')?.textContent.trim() ?? null,
        meta: document.querySelector('.m-meta')?.textContent.trim() ?? null,
        battery: document.querySelector('.m-battery-text')?.textContent.trim() ?? null,
        panels: [...document.querySelectorAll('.m-panel-title')].map(h => h.textContent.trim()),
      }
    }, { timeoutMs: 20000 })
    const box = await sh.eval(`(() => {
      const frame = [...document.querySelectorAll('.exo-devices-frame')].find(f => f.getBoundingClientRect().width > 0)
      if (!frame) return null
      const r = frame.getBoundingClientRect(), p = frame.parentElement.getBoundingClientRect()
      return { frame: [Math.round(r.width), Math.round(r.height)], room: [Math.round(p.width), Math.round(p.height)] }
    })()`)
    const view = inner && box ? { ...inner, ...box } : null
    await c.shot(sh)
    data.devicesState = view?.name ? 'connected' : view?.center ?? null
    Object.assign(c.evidence, view ?? { settled: false })
    if (!view) return ['Exo Mouse did not settle within 20 s (still looking or connecting)']
    const f = []
    if (view.frame[0] < view.room[0] - 2 || view.frame[1] < view.room[1] - 2) f.push(`the frame ${view.frame.join('x')} does not fill the room ${view.room.join('x')}`)
    if (!view.name && view.center !== 'No supported mouse found') f.push(`Exo Mouse says "${view.center}"${view.line ? `: ${view.line}` : ''}`)
    if (view.name && view.panels.length === 0) f.push(`${view.name} is connected but no settings show`)
    // A Starlight X shows each of its sections on every firmware, a value it did not report saying why;
    // on its cable there is no receiver, so its Bhop mode sits under Scroll wheel.
    const slxPanels = ['Sensitivity', 'Performance', 'Main switches', /Cable/.test(view.meta ?? '') ? 'Scroll wheel' : 'Receiver']
    const slxMissing = /Starlight X/.test(view.name ?? '') ? slxPanels.filter((p) => !view.panels.includes(p)) : []
    if (slxMissing.length) f.push(`${view.name} is missing ${slxMissing.join(', ')}`)
    return f
  })
  // Every HID arrival or departure anywhere on the PC makes the module list again, often several
  // listings at once that answer out of order: a mouse that stays must stay connected through them.
  await check('devices.relist', 'A connected mouse stays connected while Exo Mouse lists devices again: 60 device-change notices, in bursts, never drop it', async (c) => {
    const mouse = await openFrame(sh, 'mouse.exo-launcher.local', 15000).catch(() => null)
    const name = mouse && await mouse.eval(`document.querySelector('.m-name')?.textContent.trim() ?? null`).catch(() => null)
    if (!name) { c.evidence.skipped = 'no mouse connected'; mouse?.close(); return [] }
    try {
      await mouse.eval(`(() => {
        window.__exoRelist = { views: [], lists: 0 }
        window.addEventListener('message', e => { if (e.source === window.parent && e.data?.result?.devices) window.__exoRelist.lists++ })
        let last = null
        window.__exoRelistTimer = setInterval(() => {
          const v = document.querySelector('.m-name')?.textContent.trim() ?? document.querySelector('.m-center-title')?.textContent.trim() ?? ''
          if (v !== last) { last = v; window.__exoRelist.views.push(v) }
        }, 20)
      })()`)
      await sh.eval(`(async () => {
        const frame = [...document.querySelectorAll('.exo-devices-frame')].find(f => f.getBoundingClientRect().width > 0)?.contentWindow
        const changed = () => frame.postMessage({ exo: 'mouse', event: 'hid.changed', data: {} }, 'https://mouse.exo-launcher.local')
        for (let burst = 0; burst < 6; burst++) {
          for (let i = 0; i < 10; i++) changed()
          await new Promise(r => setTimeout(r, 400))
        }
      })()`)
      await sleep(4000)
      const seen = await mouse.eval(`(() => { clearInterval(window.__exoRelistTimer); return window.__exoRelist })()`)
      Object.assign(c.evidence, { mouse: name, listingsAnswered: seen.lists, views: seen.views })
      const left = seen.views.filter((v) => v !== name)
      return left.length ? [`${name} dropped during the listings: ${left.join(' → ')}`] : seen.lists < 60 ? [`only ${seen.lists} of 60 listings answered`] : []
    } finally { mouse.close() }
  })
  // Exo Mouse is on its own origin, so the relay is driven from inside its frame the way the module
  // calls it: window.parent.postMessage to Exo's origin, and the reply read back in the frame.
  const SHELL_ORIGIN = 'https://app.exo-launcher.local'
  const askFromFrame = async (calls) => {
    const mouse = await openFrame(sh, 'mouse.exo-launcher.local', 15000).catch(() => null)
    if (!mouse) return null
    try {
      return await mouse.call((list, shell) => Promise.all(list.map(({ id, method, wait }) => new Promise((resolve) => {
        const on = (e) => {
          if (e.source !== window.parent || e.data?.exo !== 'mouse' || e.data.id !== id) return
          window.removeEventListener('message', on)
          resolve({ from: e.origin, result: e.data.result ?? null, error: e.data.error ?? null })
        }
        window.addEventListener('message', on)
        window.parent.postMessage({ exo: 'mouse', id, method, params: {} }, shell)
        setTimeout(() => { window.removeEventListener('message', on); resolve('silent') }, wait ?? 2500)
      }))), calls, SHELL_ORIGIN)
    } finally { mouse.close() }
  }
  await check('devices.relay', 'Exo relays only HID, the games list and profiles for Exo Mouse: hid.list and exo.games answer the frame on Exo\'s origin, anything else from the frame, and the same call from any other window, does not', async (c) => {
    const answers = await askFromFrame([
      { id: 990001, method: 'hid.list' },
      { id: 990004, method: 'exo.games', wait: 5000 },
      { id: 990002, method: 'settings.get' },
      { id: 990003, method: 'toString' },
    ])
    if (!answers) return ['no Exo Mouse frame']
    const [hid, games, settings, proto] = answers
    // The same allowed call posted by the shell's own window (not the frame) must not be relayed.
    const mouse = await openFrame(sh, 'mouse.exo-launcher.local', 15000)
    const waiting = mouse.call(() => new Promise((resolve) => {
      const on = (e) => { if (e.data?.exo === 'mouse' && e.data.id === 990005) { window.removeEventListener('message', on); resolve('answered') } }
      window.addEventListener('message', on)
      setTimeout(() => { window.removeEventListener('message', on); resolve('silent') }, 2500)
    }))
    await sleep(200)
    await sh.eval(`window.postMessage({ exo: 'mouse', id: 990005, method: 'hid.list', params: {} }, location.origin)`)
    const foreign = await waiting.finally(() => mouse.close())
    const gameList = games?.result?.games ?? null
    Object.assign(c.evidence, {
      hid: hid === 'silent' ? 'silent' : hid.result ? 'answered' : 'error',
      replyOrigin: hid === 'silent' ? null : hid.from,
      games: gameList ? { count: gameList.length, sample: gameList.slice(0, 3).map((g) => ({ id: g.id, title: g.title, cover: g.cover ? g.cover.slice(0, 40) : null })) } : games,
      settings: settings === 'silent' ? 'silent' : 'answered',
      proto: proto === 'silent' ? 'silent' : 'answered',
      fromShellWindow: foreign,
    })
    const f = []
    if (hid === 'silent' || !hid.result) f.push(`hid.list from the frame was ${hid === 'silent' ? 'silent' : 'an error: ' + hid.error}`)
    else if (hid.from !== SHELL_ORIGIN) f.push(`the reply came from ${hid.from}, not ${SHELL_ORIGIN}`)
    if (!gameList) f.push(`exo.games from the frame was ${games === 'silent' ? 'silent' : 'an error: ' + games.error}`)
    else if (!gameList.length) f.push('exo.games answered with no installed games')
    if (settings !== 'silent') f.push('settings.get from the frame got an answer')
    if (proto !== 'silent') f.push('an inherited method name from the frame got an answer')
    if (foreign !== 'silent') f.push('hid.list posted by the shell\'s own window was relayed to the frame')
    return f
  })
  await check('devices.profiles', 'Mouse profiles read through Exo: Desktop is the default, one game profile is linked to VALORANT', async (c) => {
    const host = await sh.eval(hostCall('mouse.profiles.get')).catch((e) => ({ ok: false, message: e.message }))
    // The module's own read, through the relay.
    const [read] = (await askFromFrame([{ id: 990010, method: 'exo.profiles.read', wait: 4000 }])) ?? ['no frame']
    const relay = read === 'silent' || read === 'no frame' ? read : read.result ? 'answered' : 'error'
    const devices = host?.data && typeof host.data === 'object' ? Object.entries(host.data) : []
    const profiles = devices.flatMap(([device, list]) => (Array.isArray(list) ? list : []).map((p) => ({ device, name: p.name, isDefault: !!p.isDefault, games: p.games ?? [], steps: (p.steps ?? []).length })))
    Object.assign(c.evidence, { ok: host?.ok ?? null, relay, devices: devices.length, profiles })
    const f = []
    if (!host?.ok) f.push(`mouse.profiles.get: ${host?.message ?? 'no answer'}`)
    if (relay !== 'answered') f.push(`exo.profiles.read from the frame was ${relay}`)
    if (!profiles.length) return [...f, 'no saved profiles']
    for (const [device, list] of devices) if ((list ?? []).filter((p) => p.isDefault).length !== 1) f.push(`${device} has ${(list ?? []).filter((p) => p.isDefault).length} default profiles`)
    if (!profiles.some((p) => p.name === 'Desktop' && p.isDefault)) f.push('no default Desktop profile')
    // The game profile's name is the user's (FPS, TMR, ...): one non-default profile carries VALORANT.
    const linked = profiles.filter((p) => p.games.includes('riot:valorant'))
    c.evidence.valorantProfile = linked.map((p) => p.name)
    if (linked.length !== 1) f.push(`${linked.length} profiles are linked to riot:valorant (want one)`)
    else if (linked[0].isDefault) f.push(`VALORANT is linked to the default profile ${linked[0].name}`)
    return f
  })

  await check('devices.battery-chip', "The title bar's battery chip shows exactly while the mouse is low and not charging", async (c) => {
    const mouse = await openFrame(sh, 'mouse.exo-launcher.local', 15000).catch(() => null)
    const inside = mouse ? await mouse.eval(`({
      name: document.querySelector('.m-name')?.textContent.trim() ?? null,
      battery: document.querySelector('.m-battery-text')?.textContent.trim() ?? null,
      charging: !!document.querySelector('.m-battery.is-charging, [data-charging=true]') || /charging/i.test(document.querySelector('.m-battery')?.textContent ?? ''),
    })`) : { name: null, battery: null, charging: false }
    const chip = await sh.eval(`(() => {
      const chip = document.querySelector('.exo-titlebar .exo-mouse-battery, .exo-mouse-battery')
      return chip ? { text: chip.textContent.trim(), label: chip.getAttribute('aria-label') } : null
    })()`)
    const state = { ...inside, chip }
    Object.assign(c.evidence, state)
    if (!state.name) c.skip('no mouse connected')
    const pct = Number(/(\d+)\s*%/.exec(state.battery ?? '')?.[1] ?? NaN)
    c.evidence.percent = Number.isNaN(pct) ? null : pct
    const f = []
    if (Number.isNaN(pct)) { if (state.chip) f.push(`the chip shows ${state.chip.text} while the mouse reports no battery`); return f }
    const low = pct <= 20 && !state.charging
    if (low && !state.chip) f.push(`the mouse is at ${pct}% and not charging but there is no chip`)
    if (!low && state.chip) f.push(`the chip shows ${state.chip.text} while the mouse is at ${pct}%${state.charging ? ' and charging' : ''}`)
    if (state.chip && !state.chip.text.includes(`${pct}%`)) f.push(`the chip says ${state.chip.text}, Exo Mouse ${pct}%`)
    return f
  })

  await check('devices.retired', "No OpenMouse web panel in Exo and no Exo answer on OpenMouse Bridge's port", async (c) => {
    const targets = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json().catch(() => [])
    const panels = targets.filter(t => /control\.openmouse\.app/.test(t.url)).map(t => t.url)
    let bridge = null
    try {
      const response = await fetch(OPENMOUSE_BRIDGE + '/v1/status', { signal: AbortSignal.timeout(3000) })
      bridge = response.ok ? await response.json() : { status: response.status }
    } catch { /* nothing listens */ }
    Object.assign(c.evidence, { panelTargets: panels, portAnswers: !!bridge, portVersion: bridge?.version ?? null, exoMarker: bridge?.exo ?? null })
    const f = []
    if (panels.length) f.push(`Exo still loads the OpenMouse web panel: ${panels.join(', ')}`)
    if (bridge && (typeof bridge.exo === 'string' || /\+exo\./.test(bridge.version ?? ''))) f.push(`Exo still answers on OpenMouse Bridge's port (${bridge.version})`)
    return f
  })
}

async function home(sh) {
  await sh.click(roomButton, 'Library')
  const ok = await sh.waitFor(() => [...document.querySelectorAll('nav[aria-label="Rooms"] button')].find((b) => b.textContent.trim() === 'Library')?.getAttribute('aria-current') === 'page' && document.querySelectorAll('.exo-tile-hit').length > 0, { timeoutMs: 10000 })
  data.onLibrary = !!ok
}

async function minimize(sh) {
  data.clicked = await sh.click(byLabel, 'Minimize')
}

/**
 * Points the host drag check uses (CSS px; the host multiplies by the DPR):
 * the middle of the widest run of title-band points no control covers, the
 * current room's tab and the maximize button.
 */
async function chrome(sh) {
  await sleep(400) // let a fresh layout reach the host's caption regions
  Object.assign(data, await sh.eval(`(() => {
    const bar = document.querySelector('.exo-titlebar')
    if (!bar) return { band: null }
    const controls = 'button,a,input,select,textarea,[role="button"],[role="slider"]'
    const pill = (bar.querySelector('.exo-chrome-pill') ?? bar).getBoundingClientRect()
    const y = Math.round(pill.top + pill.height / 2)
    let best = null, start = null
    for (let x = Math.ceil(pill.left) + 12; x <= pill.right - 12; x += 4) {
      const el = document.elementFromPoint(x, y)
      const free = !!el && bar.contains(el) && !el.closest(controls)
      const last = x + 4 > pill.right - 12
      if (free && start === null) start = x
      if ((!free || last) && start !== null) {
        const end = free ? x : x - 4
        if (!best || end - start > best.len) best = { len: end - start, x: Math.round((start + end) / 2) }
        start = null
      }
    }
    const center = (el) => { if (!el) return null; const r = el.getBoundingClientRect(); return [Math.round(r.left + r.width / 2), Math.round(r.top + r.height / 2)] }
    return {
      dpr: devicePixelRatio,
      viewport: innerWidth + 'x' + innerHeight,
      band: best ? [best.x, y] : null,
      bandRunPx: best ? best.len : 0,
      tab: center([...document.querySelectorAll('nav[aria-label="Rooms"] button')].find((b) => b.getAttribute('aria-current') === 'page')),
      maximize: center(bar.querySelector('[data-exo-maximize]')),
      miniPlayer: !!bar.querySelector('.exo-music-mini'),
    }
  })()`))
}

/** One host RPC from the shell page, the way host.ts sends it; resolves with the result. */
const hostCall = (method, params = {}, timeoutMs = 10000) => `new Promise((resolve, reject) => {
  const wv = window.chrome.webview, id = crypto.randomUUID()
  const timer = setTimeout(() => { wv.removeEventListener('message', on); reject(new Error('host did not answer ${method}')) }, ${timeoutMs})
  function on(e) {
    let d = e.data
    try { if (typeof d === 'string') d = JSON.parse(d) } catch { return }
    if (!d || d.id !== id) return
    clearTimeout(timer); wv.removeEventListener('message', on)
    d.ok ? resolve(d.result) : reject(new Error(d.error || 'host error'))
  }
  wv.addEventListener('message', on)
  wv.postMessage(JSON.stringify({ version: 1, id, method: ${JSON.stringify(method)}, params: ${JSON.stringify(params)} }))
})`

/**
 * Page side: holds back the shell's own requests for the named host methods and answers each
 * with an error, so a click can be followed to its host call without the call happening (a
 * launch, a store page opening). window.__exoHold.calls records them; window.__exoHold.release()
 * puts the bridge's own postMessage back. Always released in a finally.
 */
const holdHost = (methods) => `(() => {
  const wv = window.chrome.webview
  if (window.__exoHold) window.__exoHold.release()
  const own = wv.postMessage
  const hold = window.__exoHold = { calls: [], release: () => { wv.postMessage = own; window.__exoHold = null; return true } }
  const methods = ${JSON.stringify(methods)}
  wv.postMessage = function (message) {
    let d = null
    try { d = typeof message === 'string' ? JSON.parse(message) : message } catch { }
    if (d && methods.includes(d.method)) {
      hold.calls.push({ method: d.method, params: d.params ?? null, at: performance.now() })
      setTimeout(() => wv.dispatchEvent(new MessageEvent('message', { data: JSON.stringify({ id: d.id, ok: false, error: 'Held by the E2E run.' }) })), 30)
      return
    }
    return own.call(wv, message)
  }
  return true
})()`
const releaseHost = `(window.__exoHold ? window.__exoHold.release() : true)`

async function poll(fn, timeoutMs, interval = 100) {
  const deadline = Date.now() + timeoutMs
  for (;;) {
    const value = await fn().catch(() => null)
    if (value !== null && value !== undefined) return value
    if (Date.now() > deadline) return null
    await sleep(interval)
  }
}

/**
 * The mini player's volume is Spotify's own: a level set through the host moves
 * Spotify's own volume bar, and Spotify's own control (a wheel notch on its bar)
 * reaches the shell as a music.volume event within 1.5 s. Nothing plays; the
 * original level is put back.
 */
async function volume(sh, spotifyPort) {
  await check('music.volume.linked', "Mini player volume is Spotify's own volume, both ways", async (c) => {
    if (!spotifyPort) c.skip("Spotify's page is reachable only over Exo's private DevTools pipe")
    const sp = await openPage(Number(spotifyPort), 'xpui', 15000).catch(() => null)
    // Exo freezes Spotify while it is idle; a frozen page never answers, so the check skips instead of hanging.
    const awake = sp && await Promise.race([sp.eval('1').then(() => true).catch(() => false), new Promise((r) => setTimeout(() => r(false), 5000))])
    if (!awake) {
      try { sp?.close() } catch { }
      c.skip('Spotify is frozen while idle (Exo saves its memory); its page does not answer')
    }
    // Spotify's own volume through the reader Exo installs in its page; the
    // bar's range input only moves in 0.1 steps, so it is the fallback.
    // Exo can freeze Spotify mid-check; every read of its page gives up after 8 s.
    const guard = (promise) => Promise.race([promise, new Promise((_, reject) => setTimeout(() => reject(new Error('SPOTIFY_FROZEN')), 8000))])
    const spotifyLevel = () => guard(sp.eval(`(async () => { if (window.__exoVolume) { const v = await window.__exoVolume.read(); if (typeof v === 'number') return v } const i = document.querySelector('[data-testid="volume-bar"] input[type=range]'); return i ? Number(i.value) / Number(i.max || 1) : null })()`))
    const hostVolume = (level) => sh.eval(hostCall('music.volume', level === undefined ? {} : { level }))
    let original = null
    try {
      const first = await poll(async () => { const r = await hostVolume(); return r?.source === 'app' ? r : null }, 15000, 500)
      c.evidence.first = first ?? await hostVolume().catch((error) => ({ error: error.message }))
      if (!first) return [`the host answers with the ${c.evidence.first?.source ?? 'no'} volume, not Spotify's own`]
      original = first.level
      c.evidence.spotifyBefore = await spotifyLevel()
      if (c.evidence.spotifyBefore === null) c.skip("Spotify's volume bar is not on screen")
      const f = []
      if (Math.abs(c.evidence.spotifyBefore - original) > 0.02) f.push(`host reads ${original}, Spotify's bar shows ${c.evidence.spotifyBefore}`)

      const target = Math.round((original > 0.5 ? original - 0.15 : original + 0.15) * 100) / 100
      await hostVolume(target)
      const moved = await poll(async () => { const v = await spotifyLevel(); return v !== null && Math.abs(v - target) <= 0.02 ? v : null }, 4000)
      c.evidence.exoToSpotify = { target, spotify: moved }
      if (moved === null) f.push(`Spotify's own bar did not move to ${target} (shows ${await spotifyLevel()})`)

      await sh.eval(`(() => { window.__e2eVolume = []; window.chrome.webview.addEventListener('message', (e) => { let d = e.data; try { if (typeof d === 'string') d = JSON.parse(d) } catch { return } if (d && d.event === 'music.volume') window.__e2eVolume.push(d.data.level) }); return true })()`)
      const bar = await guard(sp.eval(`(() => { const b = document.querySelector('[data-testid="volume-bar"]'); if (!b) return null; const r = b.getBoundingClientRect(); return { x: r.left + r.width / 2, y: r.top + r.height / 2 } })()`))
      // A hidden Spotify window lays out no player bar; the wheel direction is then unexercised, not failed.
      if (bar) await guard(sp.s.send('Input.dispatchMouseEvent', { type: 'mouseWheel', x: bar.x, y: bar.y, deltaX: 0, deltaY: 120 }))
      const spotifyNow = !bar ? null : await poll(async () => { const v = await spotifyLevel(); return v !== null && Math.abs(v - target) > 0.02 ? v : null }, 1500)
      const t0 = Date.now()
      const followed = spotifyNow === null ? null : await poll(async () => { const v = await sh.eval('window.__e2eVolume.at(-1) ?? null'); return v !== null && Math.abs(v - spotifyNow) <= 0.02 ? v : null }, 1500)
      c.evidence.spotifyToExo = { spotify: spotifyNow, shellEvent: followed, afterMs: Date.now() - t0 }
      // Only a Spotify that ignored the synthetic wheel leaves this direction unproven.
      if (spotifyNow === null) c.evidence.spotifyToExo.note = bar ? "Spotify's bar ignored the wheel notch; direction not exercised" : "Spotify's volume bar is not laid out; direction not exercised"
      else if (followed === null) f.push(`Spotify moved to ${spotifyNow} but no music.volume event reached the shell within 1.5 s`)
      return f
    } catch (error) {
      if (error?.message === 'SPOTIFY_FROZEN') c.skip('Spotify stopped answering mid-check: Exo froze it while idle')
      throw error
    } finally {
      if (original !== null) {
        await hostVolume(original).catch(() => { })
        c.evidence.restored = await poll(async () => { const v = await spotifyLevel().catch(() => null); return v !== null && Math.abs(v - original) <= 0.02 ? v : null }, 2000).catch(() => null)
      }
      try { sp.close() } catch { }
    }
  })
}

// The provider page's own now-playing, read independently of Exo's reader.
// YouTube Music: Media Session metadata (or the player bar) and the time its own player bar shows
// (whole seconds). Never the <video> clock: queued songs share one media source, so it runs on across tracks.
const YOUTUBE_OWN = `(() => {
  const p = document.getElementById('movie_player'), v = p?.querySelector('video')
  const title = navigator.mediaSession?.metadata?.title || document.querySelector('ytmusic-player-bar .title')?.textContent.trim() || ''
  const slider = document.querySelector('ytmusic-player-bar #progress-bar')
  const shown = slider ? Number(slider.value ?? slider.getAttribute('aria-valuenow')) : NaN
  return { title, positionMs: isFinite(shown) ? shown * 1000 : null, granularityMs: 1000, ad: !!p?.classList.contains('ad-showing'), at: Date.now() }
})()`
// Spotify: its now-playing widget and the elapsed time its own player bar prints (whole seconds).
const SPOTIFY_OWN = `(() => {
  const text = (q) => document.querySelector(q)?.textContent.trim() || ''
  const title = text('[data-testid="context-item-info-title"]') || text('[data-testid="now-playing-widget"] [data-testid="context-item-link"]')
  const m = /^(?:(\\d+):)?(\\d+):(\\d{2})$/.exec(text('[data-testid="playback-position"]'))
  const positionMs = m ? ((Number(m[1] || 0) * 60 + Number(m[2])) * 60 + Number(m[3])) * 1000 : null
  return { title, positionMs, granularityMs: 1000, ad: false, at: Date.now() }
})()`
const MINI_SHOWN = `(() => {
  const el = document.querySelector('.exo-titlebar .exo-music-mini')
  if (!el) return { visible: false }
  const r = el.getBoundingClientRect(), seek = el.querySelector('input.exo-mini-seek')
  return { visible: r.width > 0 && r.height > 0, title: el.querySelector('.exo-mini-title')?.textContent.trim() ?? '', time: el.querySelector('.exo-mini-time')?.textContent.trim() ?? '', positionMs: seek ? Number(seek.value) : null, playing: el.querySelector('.exo-mini-play')?.getAttribute('aria-label') === 'Pause music', at: Date.now() }
})()`

/**
 * The mini player is the provider's song on its own clock: with a track loaded it
 * shows in the top bar outside the Music room, its title is the provider page's
 * own, its position is within ±1 s of the page's clock (plus the page's and the
 * seek bar's rounding), and over 3 s it advances as the page does, or stays put
 * while paused. Read-only: nothing is played, paused or seeked.
 */
async function miniplayer(sh, arg) {
  const [named, spotifyPort] = String(arg ?? '').split(':')
  const provider = named === 'youtubemusic' ? 'youtube' : named
  await check('music.miniplayer.sync', "Mini player shows the provider's song and follows its clock", async (c) => {
    c.evidence.provider = provider
    if (provider !== 'youtube' && !(provider === 'spotify' && spotifyPort)) c.skip(`${provider || 'the provider'} has no page this run can reach (Spotify's is private to Exo)`)
    const page = provider === 'youtube' ? await openPage(port, 'music.youtube.com', 10000).catch(() => null) : await openPage(Number(spotifyPort), 'xpui', 10000).catch(() => null)
    if (!page) c.skip(`the ${provider} page is not reachable`)
    const own = () => page.eval(provider === 'youtube' ? YOUTUBE_OWN : SPOTIFY_OWN)
    const current = () => sh.eval(`[...document.querySelectorAll('nav[aria-label="Rooms"] button')].find((b) => b.getAttribute('aria-current') === 'page')?.textContent.trim() ?? null`)
    const startRoom = await current().catch(() => null)
    try {
      const first = await own()
      c.evidence.page = first
      if (!first?.title || first.positionMs === null) c.skip('the provider has no track loaded')
      if (first.ad) c.skip('an ad is playing')
      if (startRoom === 'Music') await sh.click(roomButton, 'Library')
      const shown = await poll(async () => { const m = await sh.eval(MINI_SHOWN); return m.visible && m.title ? m : null }, 6000, 200)
      await c.shot(sh)
      if (!shown) return ['no mini player in the top bar while the provider has a track loaded']
      // Sample both clocks twice, 3 s apart.
      const sample = async () => { const mini = await sh.eval(MINI_SHOWN); const pg = await own(); return { mini, page: pg } }
      const a = await sample()
      await sleep(3000)
      const b = await sample()
      c.evidence.samples = [a, b]
      const f = []
      const norm = (s) => String(s ?? '').normalize('NFKC').replace(/\s+/g, ' ').trim().toLowerCase()
      if (norm(b.mini.title) !== norm(b.page.title)) f.push(`mini player shows "${b.mini.title}", the provider shows "${b.page.title}"`)
      if (b.page.ad || norm(a.page.title) !== norm(b.page.title)) { c.evidence.note = 'the track changed or an ad started while sampling; clock not compared'; return f }
      const elapsed = b.mini.at - a.mini.at
      const pageMoved = b.page.positionMs - a.page.positionMs, miniMoved = b.mini.positionMs - a.mini.positionMs
      // Whole-second page clocks (Spotify) can tick once while paused-looking; only clear cases are judged.
      const playing = pageMoved > 1500, paused = Math.abs(pageMoved) < 50
      c.evidence.clock = { elapsedMs: elapsed, pageMovedMs: Math.round(pageMoved), miniMovedMs: Math.round(miniMoved), playing, paused, miniSaysPlaying: b.mini.playing }
      // The seek bar rounds to 0.5 s; the page may print whole seconds.
      const tolerance = 1000 + 250 + b.page.granularityMs
      for (const s of [a, b]) {
        const pageAtMini = s.page.positionMs - (playing ? s.page.at - s.mini.at : 0)
        const off = s.mini.positionMs - pageAtMini
        if (!(Math.abs(off) <= tolerance)) f.push(`mini player at ${Math.round(s.mini.positionMs)} ms, provider at ${Math.round(pageAtMini)} ms`)
      }
      if (playing && Math.abs(miniMoved - pageMoved) > 1000 + b.page.granularityMs) f.push(`over ${elapsed} ms the provider advanced ${Math.round(pageMoved)} ms, the mini player ${Math.round(miniMoved)} ms`)
      if (paused && Math.abs(miniMoved) > 500) f.push(`the provider is paused but the mini player moved ${Math.round(miniMoved)} ms`)
      if (playing && !b.mini.playing) f.push('the provider is playing, the mini player shows paused')
      if (paused && b.mini.playing) f.push('the provider is paused, the mini player shows playing')
      return f
    } finally {
      if (startRoom === 'Music') await sh.click(roomButton, 'Music').catch(() => { })
      page.close()
    }
  })
}

// The provider page's own volume, read through its own player (0–1).
const YOUTUBE_VOLUME = `(() => { const p = document.getElementById('movie_player'); if (!p || typeof p.getVolume !== 'function') return null; return { level: p.getVolume() / 100, muted: !!(p.isMuted && p.isMuted()) } })()`
const SPOTIFY_VOLUME = `(async () => { let v = null; if (window.__exoVolume) v = await window.__exoVolume.read(); if (typeof v !== 'number') { const i = document.querySelector('[data-testid="volume-bar"] input[type=range]'); v = i ? Number(i.value) / Number(i.max || 1) : null } return typeof v === 'number' ? { level: v, muted: v <= 0 } : null })()`
const MINI_VOLUME = `(() => {
  const el = document.querySelector('.exo-titlebar .exo-music-mini .exo-mini-volume')
  const input = el?.querySelector('input[type=range]')
  if (!input) return null
  return { known: !input.disabled, percent: Number(input.value), shown: el.querySelector('.exo-mini-volume-value')?.textContent.trim() ?? '', mutedIcon: el.querySelector('button')?.getAttribute('aria-label') === 'Unmute music' }
})()`

/**
 * On the fresh shell this run started, with a song loaded, the mini player's volume is
 * the provider's own from the start: known within 8 s, equal to the provider page's
 * level within 2%, and never 0 or the muted icon unless the provider is muted. A provider
 * with no page level must still show a level, and
 * 0 only when the host reports the session muted. Read-only: nothing is moved.
 */
async function miniVolume(sh, arg) {
  const [named, spotifyPort] = String(arg ?? '').split(':')
  const provider = named === 'youtubemusic' ? 'youtube' : named
  await check('music.miniplayer.volume', "Mini player volume is the provider's level from the start", async (c) => {
    c.evidence.provider = provider
    const page = provider === 'youtube' ? await openPage(port, 'music.youtube.com', 10000).catch(() => null)
      : provider === 'spotify' && spotifyPort ? await openPage(Number(spotifyPort), 'xpui', 10000).catch(() => null) : null
    if ((provider === 'youtube' || provider === 'spotify') && !page) c.skip(`the ${provider} page is not reachable`)
    const current = () => sh.eval(`[...document.querySelectorAll('nav[aria-label="Rooms"] button')].find((b) => b.getAttribute('aria-current') === 'page')?.textContent.trim() ?? null`)
    const startRoom = await current().catch(() => null)
    try {
      if (startRoom === 'Music') await sh.click(roomButton, 'Library')
      const mini = await poll(async () => { const m = await sh.eval(MINI_VOLUME); return m ? m : null }, 6000, 200)
      if (!mini) c.skip('no song is loaded, so the mini player is not shown')
      const known = await poll(async () => { const m = await sh.eval(MINI_VOLUME); return m?.known ? m : null }, 8000, 250)
      c.evidence.mini = known ?? mini
      await c.shot(sh)
      const own = page ? await page.eval(provider === 'youtube' ? YOUTUBE_VOLUME : SPOTIFY_VOLUME).catch(() => null) : null
      c.evidence.provider = { provider, own }
      if (!known) return [`the mini player volume is still unknown 8 s after the song showed (shows "${mini.shown}")`]
      const f = []
      if (page) {
        if (!own) c.skip("the provider's player has no volume to compare with yet")
        const ownPercent = own.muted ? 0 : Math.round(own.level * 100)
        if (Math.abs(known.percent - ownPercent) > 2) f.push(`mini player shows ${known.percent}%, ${provider} is at ${ownPercent}%`)
        if ((known.percent === 0 || known.mutedIcon) && ownPercent > 0) f.push(`mini player shows muted while ${provider} plays at ${ownPercent}%`)
      } else {
        const host = await sh.eval(hostCall('music.volume')).catch((error) => ({ error: error.message }))
        c.evidence.host = host
        if ((known.percent === 0 || known.mutedIcon) && host?.muted !== true) f.push(`mini player shows muted, the ${provider || 'provider'} session is not muted (${JSON.stringify(host)})`)
      }
      return f
    } finally {
      if (startRoom === 'Music') await sh.click(roomButton, 'Music').catch(() => { })
      page?.close()
    }
  })
}

async function onboarding(sh) {
  await check('onboarding.walk', 'Onboarding steps render (stores, then music; no account step)', async (c) => {
    const first = await sh.waitFor(() => document.querySelector('#exo-ob-title')?.textContent.trim() || null, { timeoutMs: 30000 })
    if (!first) return ['onboarding did not appear (no #exo-ob-title)']
    const steps = []
    const f = []
    for (let i = 0; i < 5; i++) {
      // Let the step's own data arrive (store check, providers).
      await sh.waitFor(() => !document.querySelector('.exo-ob-body [role=status]')?.textContent.includes('Looking for stores'), { timeoutMs: 20000 })
      await sleep(1500)
      const s = await sh.eval(`(() => {
        const title = document.querySelector('#exo-ob-title')?.textContent.trim()
        const progress = document.querySelector('.exo-ob-progress')?.getAttribute('aria-label') ?? null
        const body = document.querySelector('.exo-ob-body')
        const stores = [...(body?.querySelectorAll('ul[aria-label="Stores on this PC"] > li') ?? [])].map(li => li.innerText.trim().split('\\n')[0])
        const music = [...(body?.querySelectorAll('[role=radiogroup][aria-label="Music service"] [role=radio]') ?? [])].map(r => r.innerText.trim().split('\\n')[0] + (r.getAttribute('aria-checked') === 'true' ? ' *' : ''))
        const apps = [...(body?.querySelectorAll('ul[aria-label="Apps Exo hosts"] > li') ?? [])].map(li => li.innerText.trim().split('\\n')[0])
        const primary = document.querySelector('.exo-ob-primary')?.textContent.trim()
        const alert = body?.querySelector('[role=alert]')?.innerText.trim() ?? null
        const lede = document.querySelector('.exo-ob-lede')?.textContent.trim() ?? null
        return { title, progress, stores, music, apps, primary, alert, lede, bodyText: body?.innerText.trim().replace(/\\s+/g, ' ').slice(0, 160) }
      })()`)
      steps.push(s)
      await c.shot(sh, String(steps.length))
      if (s.primary !== 'Continue') break
      await sh.click(() => document.querySelector('.exo-ob-primary'))
      await sh.waitFor((t) => document.querySelector('#exo-ob-title')?.textContent.trim() !== t, { timeoutMs: 5000, args: [s.title] })
    }
    c.evidence.steps = steps
    const titles = steps.map((s) => s.title)
    if (titles[0] !== 'Your game stores') f.push(`first step "${titles[0]}"`)
    if (!steps[0]?.stores.length) f.push('stores step lists no stores' + (steps[0]?.alert ? ': ' + steps[0].alert : ''))
    const musicStep = steps.find((s) => s.title === 'Music and Discord')
    if (!musicStep) f.push('no music step')
    else if (!musicStep.music.length) f.push('music step has no service choices')
    else if (!musicStep.music.some((m) => m.endsWith(' *'))) f.push('music step has no selected service')
    if (titles.join('|') !== 'Your game stores|Music and Discord') f.push(`steps ${JSON.stringify(titles)} (expected Your game stores, Music and Discord)`)
    // First run introduces the Store and Exo Mouse, and nothing of OpenMouse or the Exo account.
    const all = steps.map((s) => s.lede ?? '').join(' ')
    c.evidence.ledes = steps.map((s) => s.lede)
    if (!/\bStore\b/.test(all)) f.push('setup does not mention the Store')
    if (!/Exo Mouse/.test(all)) f.push('setup does not mention Exo Mouse')
    if (/OpenMouse|Exo account|sign in to Exo/i.test(all + ' ' + steps.map((s) => s.bodyText).join(' '))) f.push('setup mentions OpenMouse or an Exo account')
    const last = steps.at(-1)
    if (last?.primary !== 'Start using Exo') f.push(`last step button "${last?.primary}"`)
    if (last?.progress && !last.progress.endsWith(`of ${steps.length}`)) f.push(`progress "${last.progress}" vs ${steps.length} steps walked`)
    return f
  })
}

/**
 * Mirrors the title half of ExoLauncher/Services/DlssSwapService.AntiCheat.cs
 * for games whose anti-cheat lives outside the install folder. An installed
 * game with one of these titles must come back from dlss.status as protected.
 */
const ANTI_CHEAT_TITLES = /(?<![\p{L}\p{N}])(valorant|league of legends|call of duty|warzone|battlefield|fortnite|apex legends|the finals|pubg|rainbow six|escape from tarkov|destiny 2|counter-strike|marvel rivals|overwatch)(?![\p{L}\p{N}])/iu

/**
 * Upscalers, read only: dlss.status for every installed game lists the upscaler
 * DLLs it ships with versions, anti-cheat games come back with a reason and no
 * action unless the user allowed changes for that game (never for Riot), cached
 * reads are fast, and one game sheet shows the section, opening the anti-cheat
 * confirmation and cancelling it. Nothing is allowed, updated, restored or
 * downloaded; the runner checks app.log and settings.json for that.
 */
async function upscalers(sh, settingsPath) {
  const library = await sh.eval(hostCall('library.get')).catch(() => null)
  const installed = (library?.games ?? []).filter((g) => g.installed)
  const statuses = []
  await check('upscalers.status', 'Upscaler status lists each installed game\'s DLLs with versions', async (c) => {
    if (!installed.length) c.skip('no installed games')
    for (const game of installed) {
      const started = performance.now()
      const status = await sh.eval(hostCall('dlss.status', { id: game.id })).catch((e) => ({ ok: false, message: e.message }))
      statuses.push({ game, status, firstMs: Math.round(performance.now() - started) })
    }
    const f = []
    const withFiles = statuses.filter((s) => s.status.items?.length)
    c.evidence.installedGames = installed.length
    c.evidence.gamesWithUpscalers = withFiles.map((s) => ({
      title: s.game.title,
      antiCheat: s.status.antiCheat ?? null,
      files: s.status.items.map((i) => `${i.displayName}${i.folder ? ' (' + i.folder + ')' : ''}: ${i.currentVersion ?? '—'}${i.state === 'outdated' ? ' → ' + i.latestVersion : ''} [${i.state}, ${i.origin}${i.skipReason ? ', ' + i.skipReason : ''}]`),
    }))
    for (const s of statuses) {
      if (s.status.ok === false && !/install folder is unavailable/i.test(s.status.message ?? '')) f.push(`${s.game.title}: ${s.status.message}`)
      for (const i of s.status.items ?? []) {
        if (/^[A-Za-z]:[\\/]/.test(i.key ?? '') || /^[A-Za-z]:[\\/]/.test(i.folder ?? '')) f.push(`${s.game.title}: an absolute path reached the page`)
        if (!i.currentVersion && !i.skipReason) f.push(`${s.game.title} ${i.fileName}: no version read and no reason`)
        if (!i.latestVersion && !i.skipReason) f.push(`${s.game.title} ${i.fileName}: no newest version`)
      }
    }
    data.upscalers = { games: withFiles.length }
    if (!withFiles.length) c.skip('no installed game ships an upscaler DLL Exo knows')
    return f
  })

  await check('upscalers.anticheat', 'Anti-cheat games change nothing without the user\'s consent for that game; Riot never', async (c) => {
    const excluded = statuses.filter((s) => s.status.antiCheat)
    const saved = (await readJson(settingsPath).catch(() => ({}))).upscalerAntiCheatConsent ?? {}
    const consentFor = (id) => Object.entries(saved).find(([key]) => key.toLowerCase() === id.toLowerCase())?.[1] ?? null
    c.evidence.excluded = excluded.map((s) => ({ title: s.game.title, antiCheat: s.status.antiCheat, consent: s.status.consent ?? null, files: s.status.items?.length ?? 0 }))
    c.evidence.savedConsents = Object.keys(saved).length
    const f = []
    for (const s of statuses) {
      if (ANTI_CHEAT_TITLES.test(s.game.title) && !s.status.antiCheat && s.status.ok !== false) f.push(`${s.game.title} is not marked as anti-cheat`)
    }
    for (const s of excluded) {
      const riot = s.game.store === 'riot' || s.status.antiCheat === 'Riot Vanguard'
      const consent = s.status.consent
      if (!['never', 'available', 'given'].includes(consent)) f.push(`${s.game.title}: no consent state`)
      if (riot && consent !== 'never') f.push(`${s.game.title}: a Riot game or Vanguard is open to consent (${consent})`)
      if (riot && consentFor(s.game.id)) f.push(`${s.game.title}: settings.json holds a consent for a Riot game`)
      // Consent exists only where the user gave it: the page's "given" must be backed by settings.json naming this anti-cheat.
      if (consent === 'given' && consentFor(s.game.id)?.toLowerCase() !== s.status.antiCheat.toLowerCase()) f.push(`${s.game.title}: shows consent that settings.json does not hold`)
      const reason = consent === 'never' ? /never changes Riot|could not check/i : consent === 'given' ? /You allowed Exo/i : /unless you allow it/i
      if (!reason.test(s.status.message ?? '')) f.push(`${s.game.title}: the reason does not match its consent (${consent})`)
      if (consent === 'given') continue
      for (const i of s.status.items ?? []) {
        if (i.canUpdate) f.push(`${s.game.title} ${i.fileName}: offered an update without consent`)
        if (i.choices?.length) f.push(`${s.game.title} ${i.fileName}: offered versions without consent`)
      }
    }
    if (!excluded.length && !f.length) c.skip('no installed game with anti-cheat')
    return f
  })

  // The newest DLSS Exo pins, read from the source this run's commit builds (DlssSwapService.Releases.cs).
  await check('upscalers.dlss-latest', 'A DLSS game\'s status names the newest pinned DLSS (310.9.16 today) as latest', async (c) => {
    const src = await readFile(new URL('../../ExoLauncher/Services/DlssSwapService.Releases.cs', import.meta.url), 'utf8').catch(() => '')
    const pins = [...src.matchAll(/new\("nvngx_dlss\.dll", "dlss-[^"]+", "DLSS [^"]+", "[^"]+", "(\d+(?:\.\d+)+)"/g)].map((m) => m[1])
    const newest = pins.sort((a, b) => { const x = a.split('.').map(Number), y = b.split('.').map(Number); for (let i = 0; i < Math.max(x.length, y.length); i++) if ((x[i] ?? 0) !== (y[i] ?? 0)) return (y[i] ?? 0) - (x[i] ?? 0); return 0 })[0]
    c.evidence.pinnedNewest = newest ?? null
    if (!newest) return ['no nvngx_dlss.dll pin found in DlssSwapService.Releases.cs']
    const dlss = statuses.filter((s) => (s.status.items ?? []).some((i) => i.fileName?.toLowerCase() === 'nvngx_dlss.dll' && !i.skipReason))
    if (!dlss.length) c.skip('no installed game ships DLSS Super Resolution')
    c.evidence.games = dlss.map((s) => { const i = s.status.items.find((x) => x.fileName.toLowerCase() === 'nvngx_dlss.dll'); return `${s.game.title}: ${i.currentVersion} (latest ${i.latestVersion}, ${i.state})` })
    const f = []
    for (const s of dlss) for (const i of s.status.items.filter((x) => x.fileName.toLowerCase() === 'nvngx_dlss.dll' && !x.skipReason)) if (i.latestVersion !== newest) f.push(`${s.game.title}: latest ${i.latestVersion}, Exo pins ${newest}`)
    return f
  })

  await check('upscalers.speed', 'Upscaler status answers from the scan cache', async (c) => {
    const timed = []
    for (const s of statuses.filter((x) => x.status.items?.length)) {
      const started = performance.now()
      await sh.eval(hostCall('dlss.status', { id: s.game.id })).catch(() => null)
      timed.push(Math.round(performance.now() - started))
    }
    if (!timed.length) c.skip('no game with upscaler DLLs')
    timed.sort((a, b) => a - b)
    const p95 = timed[Math.min(timed.length - 1, Math.floor(timed.length * 0.95))]
    Object.assign(c.evidence, { reads: timed.length, medianMs: timed[Math.floor(timed.length / 2)], p95Ms: p95, firstReadMaxMs: Math.max(...statuses.map((s) => s.firstMs)) })
    return p95 > 150 ? [`cached dlss.status p95 ${p95} ms (budget 150 ms)`] : []
  })

  await check('upscalers.sheet', 'A game page\'s Upscalers is one row that says where its files stand and opens System > Graphics; consent and the originals are in Properties', async (c) => {
    const withFiles = statuses.filter((s) => s.status.items?.length)
    const pick = withFiles.find((s) => s.status.consent === 'available') ?? withFiles.find((s) => s.status.antiCheat) ?? withFiles[0]
    if (!pick) c.skip('no game with upscaler DLLs')
    c.evidence.game = pick.game.title
    await sh.click(navButton, 'Library')
    const opened = await revealTile(sh, pick.game.id) && await sh.click((id) => document.querySelector(`main.exo-library-pane .exo-tile-hit[data-game-id="${CSS.escape(id)}"]`), pick.game.id)
    if (!opened) c.skip(`${pick.game.title} has no card in the Library to open`)
    const section = await sh.waitFor(() => {
      const s = document.querySelector('[role=dialog] section[aria-label="Upscalers"]')
      if (!s) return null
      s.scrollIntoView({ block: 'center' })
      const row = s.querySelector('button.exo-game-line-row')
      return {
        word: s.querySelector('.exo-game-line-word')?.textContent.trim() ?? null,
        buttons: s.querySelectorAll('button').length,
        name: row?.getAttribute('aria-label') ?? null,
        height: Math.round(s.getBoundingClientRect().height),
        text: s.textContent,
      }
    }, { timeoutMs: 8000 })
    await c.shot(sh)
    const f = []
    if (!section) f.push('the sheet has no Upscalers line')
    else {
      Object.assign(c.evidence, section)
      // One row: the families with an update ("2 updates"), else "Up to date"; an anti-cheat game
      // without consent names its anti-cheat. Versions, folders and APIs live on System > Graphics.
      const families = new Set(pick.status.items.filter((i) => i.state === 'outdated').map((i) => ['DLSS', 'FSR', 'XeSS'].find((x) => (i.displayName || i.fileName).startsWith(x)) ?? (i.displayName || i.fileName)))
      const locked = pick.status.antiCheat && pick.status.consent !== 'given'
      const expected = pick.status.consent === 'never' ? 'Never changed' : locked ? `Uses ${pick.status.antiCheat}`
        : families.size > 0 ? (families.size === 1 ? '1 update' : `${families.size} updates`) : 'Up to date'
      if (section.word !== expected) f.push(`the line says "${section.word}", the files say "${expected}"`)
      if (section.buttons !== 1) f.push(`the Upscalers line has ${section.buttons} buttons, not one row`)
      if (section.name !== `Upscalers, ${section.word}, open in System`) f.push(`the row is named "${section.name}"`)
      if (section.height > 60) f.push(`the Upscalers line is ${section.height}px tall`)
      for (const i of pick.status.items) {
        if (i.folder && section.text.includes(i.folder)) f.push(`the sheet shows the folder ${i.folder}`)
        if (i.api && section.text.includes(i.api)) f.push(`the sheet shows the API ${i.api}`)
      }
      const consent = pick.status.consent
      const restorable = pick.status.items.filter((i) => i.canRestore).length
      const askable = !!pick.status.antiCheat && (consent === 'available' || consent === 'given')
      if (restorable > 0 || pick.status.antiCheat) {
        // Properties > Installed files: Restore originals, and the anti-cheat consent (read; Allow is opened and canceled).
        await sh.click(() => document.querySelector('[role=dialog] .exo-game-side[aria-label=Properties]'))
        await sh.waitFor(() => !!document.querySelector('.exo-props-nav button'), { timeoutMs: 5000 })
        // Sections past General appear once Properties has read the game.
        await sh.waitFor(() => [...document.querySelectorAll('.exo-props-nav button')].some((b) => b.textContent.trim() === 'Installed files') || null, { timeoutMs: 20000 })
        await sh.click(() => [...document.querySelectorAll('.exo-props-nav button')].find((b) => b.textContent.trim() === 'Installed files'))
        // Properties reads the game through the store first: Installed files can take a while to fill.
        const read = () => [...document.querySelectorAll('.exo-props-pane button')].map((b) => b.textContent.trim())
        await sh.waitFor((want) => want.every((w) => [...document.querySelectorAll('.exo-props-pane button')].some((b) => w.includes(b.textContent.trim()))) || null,
          { timeoutMs: 20000, args: [[...(restorable > 0 ? [['Restore originals']] : []), ...(askable ? [['Allow changes', 'Stop allowing']] : [])]] })
        const pane = await sh.call(read)
        c.evidence.properties = pane
        if (restorable > 0 && !pane?.includes('Restore originals')) f.push('originals are saved but Properties has no Restore originals')
        const offersAllow = !!pane?.includes('Allow changes')
        if (consent === 'available' && !offersAllow) f.push('an anti-cheat game has no Allow changes in Properties')
        if (!askable && (offersAllow || pane?.includes('Stop allowing'))) f.push('a Riot or Vanguard game offers consent in Properties')
        if (consent === 'given' && !pane?.includes('Stop allowing')) f.push('an allowed game has no Stop allowing in Properties')
        if (consent === 'available' && offersAllow) {
          // Open the confirmation, read it, and cancel. Nothing is allowed.
          await sh.click(() => [...document.querySelectorAll('.exo-props-pane button')].find((b) => b.textContent.trim() === 'Allow changes'))
          const confirm = await sh.waitFor(() => {
            const g = document.querySelector('.exo-props-pane .exo-props-confirm')
            return g ? { text: g.textContent, buttons: [...g.querySelectorAll('button')].map((b) => b.textContent.trim()) } : null
          }, { timeoutMs: 3000 })
          await c.shot(sh, 'consent')
          c.evidence.confirmation = confirm
          if (!confirm) f.push('Allow changes opened no confirmation')
          else {
            if (!confirm.text.includes(pick.status.antiCheat)) f.push('the confirmation does not name the anti-cheat')
            if (!/banned/i.test(confirm.text) || !/risk is yours/i.test(confirm.text)) f.push('the confirmation does not state the ban risk is the user\'s')
            if (!confirm.buttons.includes('Allow, at my risk')) f.push(`the confirmation offers ${confirm.buttons.join(', ')}`)
            await sh.click(() => [...document.querySelectorAll('.exo-props-pane .exo-props-confirm button')].find((b) => b.textContent.trim() === 'Cancel'))
            const closed = await sh.waitFor(() => !document.querySelector('.exo-props-pane .exo-props-confirm'), { timeoutMs: 3000 })
            if (!closed) f.push('Cancel did not close the confirmation')
          }
        }
        await sh.key('Escape')
        await sh.waitFor(() => !document.querySelector('.exo-props'), { timeoutMs: 3000 })
      }
      // The row opens System > Graphics, where Update and Check again are.
      await sh.click(() => document.querySelector('[role=dialog] section[aria-label="Upscalers"] button.exo-game-line-row'))
      const landed = await sh.waitFor(() => [...document.querySelectorAll('section.exo-system [role=tab]')].find((b) => b.textContent.trim() === 'Graphics')?.getAttribute('aria-selected') === 'true'
        && !document.querySelector('[role=dialog][aria-modal=true]:not([hidden] *)') && document.querySelector('section.exo-system').getBoundingClientRect().width > 0, { timeoutMs: 5000 })
      if (!landed) f.push('the Upscalers row did not open System > Graphics')
      await sh.click(navButton, 'Library')
    }
    await sh.key('Escape')
    await sh.waitFor(() => !document.querySelector('[role=dialog][aria-modal=true]:not([hidden] *)'), { timeoutMs: 3000 })
    return f
  })
}

/**
 * Shaders, read only: System > Shaders is Exo Shaders (the shader add-on)
 * and Exo hosts no shader app's window. shaders.status for every installed game
 * answers from the engine's list and records; a game's page says Compiled, or
 * where it stands, in one row that opens System > Shaders; without the add-on
 * System > Shaders says so. Nothing is compiled, recorded or downloaded.
 */
async function shaders(sh) {
  const library = await sh.eval(hostCall('library.get')).catch(() => null)
  const installed = (library?.games ?? []).filter((g) => g.installed && !g.isAddPortable)
  const statuses = []
  const STATES = ['noApp', 'warmed', 'stale', 'ready', 'record', 'unsupported']

  await check('shaders.status', 'Shader status answers for every installed game from the Exo Shaders engine', async (c) => {
    if (!installed.length) c.skip('no installed games')
    for (const game of installed) {
      const started = performance.now()
      const status = await sh.eval(hostCall('shaders.status', { id: game.id }, 30000)).catch((e) => ({ ok: false, state: 'error', reason: e.message }))
      statuses.push({ game, status, ms: Math.round(performance.now() - started) })
    }
    c.evidence.games = statuses.map((s) => ({ title: s.game.title, state: s.status.state, reason: s.status.reason ?? null, ms: s.ms }))
    const f = []
    for (const s of statuses) {
      const { state, reason } = s.status
      if (!STATES.includes(state)) f.push(`${s.game.title}: state ${state}${reason ? ' (' + reason + ')' : ''}`)
      if (/[A-Za-z]:[\\/]/.test(reason ?? '')) f.push(`${s.game.title}: a path reached the page`)
      if (state === 'warmed' && !s.status.warmed) f.push(`${s.game.title}: compiled without when or which driver`)
      if (s.ms > 2000) f.push(`${s.game.title}: status took ${s.ms} ms`)
    }
    const noApp = statuses.filter((s) => s.status.state === 'noApp').length
    if (noApp && noApp !== statuses.length) f.push('some games say Exo Shaders is missing and others do not')
    data.shaders = { games: statuses.length, compiled: statuses.filter((s) => s.status.state === 'warmed').length }
    return f
  })

  const openSheet = async (game) => {
    await sh.click(navButton, 'Library')
    await sh.waitFor(() => document.querySelectorAll('main.exo-library-pane .exo-tile-hit').length > 0, { timeoutMs: 10000 })
    if (!await revealTile(sh, game.id)) return null
    const opened = await sh.click((id) => document.querySelector(`main.exo-library-pane .exo-tile-hit[data-game-id="${CSS.escape(id)}"]`), game.id)
    if (!opened) return null
    // A press within half a second of the page opening is held back as part of the double-click that opened it (GameSheet STREAK_MS).
    await new Promise((resolve) => setTimeout(resolve, 600))
    return sh.waitFor(() => {
      const s = document.querySelector('[role=dialog] section[aria-label="Shaders"]')
      if (!s || !s.dataset.state || s.dataset.state === 'loading') return null
      s.scrollIntoView({ block: 'center' })
      return {
        state: s.dataset.state,
        word: s.querySelector('.exo-game-line-word')?.textContent.trim() ?? null,
        buttons: s.querySelectorAll('button').length,
        name: s.querySelector('button.exo-game-line-row')?.getAttribute('aria-label') ?? null,
      }
    }, { timeoutMs: 30000 })
  }
  const closeSheet = async () => {
    await sh.key('Escape')
    await sh.waitFor(() => !document.querySelector('[role=dialog][aria-modal=true]:not([hidden] *)'), { timeoutMs: 3000 })
  }

  // The game page's Shaders is one row: Compiled, or where it stands, opening System > Shaders,
  // where the compile is started (the row is pressed; nothing is compiled).
  await check('shaders.sheet', 'A game page\'s Shaders is one row that says Compiled or where it stands and opens System > Shaders', async (c) => {
    const pick = statuses.find((s) => s.status.state === 'warmed') ?? statuses.find((s) => ['ready', 'stale', 'record'].includes(s.status.state))
    if (!pick) c.skip('no installed game Exo Shaders can compile on this PC')
    c.evidence.game = pick.game.title
    c.evidence.hostState = pick.status.state
    const section = await openSheet(pick.game)
    if (!section) c.skip(`${pick.game.title} has no card in the Library to open`)
    await c.shot(sh)
    Object.assign(c.evidence, section)
    const f = []
    if (section.state !== pick.status.state) f.push(`the page shows ${section.state}, shaders.status said ${pick.status.state}`)
    if (pick.status.state === 'warmed' && section.word !== 'Compiled') f.push(`compiled game shows "${section.word}"`)
    if (['ready', 'record'].includes(pick.status.state) && section.word !== 'Not compiled') f.push(`a game not compiled shows "${section.word}"`)
    if (section.buttons !== 1) f.push(`the Shaders line has ${section.buttons} buttons, not one row`)
    if (section.name !== `Shaders, ${section.word}, open in System`) f.push(`the row is named "${section.name}"`)
    await sh.click(() => document.querySelector('[role=dialog] section[aria-label="Shaders"] button.exo-game-line-row'))
    const landed = await sh.waitFor(() => [...document.querySelectorAll('section.exo-system [role=tab]')].find((b) => b.textContent.trim() === 'Shaders')?.getAttribute('aria-selected') === 'true'
      && !document.querySelector('[role=dialog][aria-modal=true]:not([hidden] *)') && document.querySelector('section.exo-system').getBoundingClientRect().width > 0, { timeoutMs: 5000 })
    if (!landed) { f.push('the Shaders row did not open System > Shaders'); await closeSheet() }
    await sh.click(navButton, 'Library')
    return f
  })

  // With the shader add-on installed, System > Shaders is its board.
  const addon = await sh.eval(hostCall('shaders.addon')).catch(() => null)
  if (addon?.present) {
    await shaderAddon(sh, addon, statuses, openSheet, closeSheet)
    return
  }

  await check('shaders.page', 'Without Exo Shaders, System > Shaders says it is not installed and hosts no window', async (c) => {
    if (!(await openSystemPage(sh, 'Shaders'))) return ['no System room with a Shaders page']
    const page = await sh.waitFor(() => {
      const p = [...document.querySelectorAll('section.exo-system .exo-system-page')].find((x) => !x.hidden && x.querySelector('.exo-shaders-empty, .exo-shaders-board'))
      const empty = p?.querySelector('.exo-shaders-empty')
      return empty ? { text: empty.textContent.trim(), hosted: !!document.querySelector('section.exo-system .exo-hosted-room-surface, section.exo-system iframe') } : null
    }, { timeoutMs: 10000 })
    await c.shot(sh)
    await sh.click(navButton, 'Library')
    if (!page) return ['System > Shaders did not say Exo Shaders is not installed']
    Object.assign(c.evidence, page)
    const f = []
    if (!/not installed/.test(page.text)) f.push(`it says "${page.text}"`)
    if (page.hosted) f.push('System > Shaders hosts a window or frame')
    return f
  })
}

/**
 * Exo Shaders in System > Shaders, read only: Exo draws the engine's board itself (no frame, no
 * window): a bar with what is happening, Scan games and Compile (or Pause and Stop while it
 * compiles), then a poster per game the engine knows, with Exo's cover where Exo has the game. Exo's
 * own game page says Compiled for the games the engine compiled. Nothing is compiled, queued or scanned.
 */
async function shaderAddon(sh, addon, statuses, openSheet, closeSheet) {
  await check('shaders.addon.frame', 'System > Shaders is Exo\'s board for the engine: the bar (its line, Scan games, Compile or Pause/Stop) and a poster per engine game, with covers; no frame', async (c) => {
    c.evidence.addon = addon
    const snap = await sh.eval(hostCall('shaders.call', { method: 'snapshot', params: {} }, 30000)).catch((e) => ({ ok: false, message: e.message }))
    const engineGames = snap?.ok ? (snap.result?.games ?? []) : null
    c.evidence.engine = snap?.ok ? { games: engineGames.length, scanning: !!snap.result?.app?.scanning, running: !!snap.result?.queue?.running } : { error: snap?.message ?? 'no answer' }
    if (!(await openSystemPage(sh, 'Shaders'))) return ['no System room with a Shaders page']
    const board = await sh.waitFor(() => {
      const b = [...document.querySelectorAll('section.exo-system .exo-shaders-board')].find((x) => x.getBoundingClientRect().width > 0 && !x.hasAttribute('aria-busy'))
      if (!b) return null
      const bar = b.querySelector('.exo-shaders-bar')
      return {
        line: bar?.querySelector('.exo-shaders-line')?.textContent.trim() ?? null,
        lineAlert: bar?.querySelector('.exo-shaders-line[role=alert]') ? true : false,
        buttons: [...(bar?.querySelectorAll('button') ?? [])].map((x) => x.textContent.trim()),
        posters: b.querySelectorAll('.exo-shaders-poster').length,
        names: [...b.querySelectorAll('.exo-shaders-name')].slice(0, 8).map((n) => n.textContent.trim()),
        frames: document.querySelectorAll('section.exo-system iframe, section.exo-system .exo-hosted-room-surface').length,
      }
    }, { timeoutMs: 30000 })
    // Covers load lazily: bring each poster on screen, then count.
    const covers = board ? await sh.eval(`(async () => {
      const list = [...document.querySelectorAll('section.exo-system .exo-shaders-poster')]
      for (const p of list) { p.scrollIntoView({ block: 'center' }); await new Promise(r => setTimeout(r, 30)) }
      const deadline = Date.now() + 8000
      while (Date.now() < deadline && list.some(p => { const i = p.querySelector('.exo-shaders-art img'); return i && !i.complete })) await new Promise(r => setTimeout(r, 200))
      list[0]?.scrollIntoView({ block: 'center' })
      return list.map(p => { const i = p.querySelector('.exo-shaders-art img'); return { name: p.querySelector('.exo-shaders-name')?.textContent.trim(), cover: i ? (i.naturalWidth > 0 ? 'loaded' : 'broken') : 'none' } })
    })()`) : []
    await c.shot(sh)
    await sh.click(navButton, 'Library')
    if (!board) return ['System > Shaders never showed the board (engine not answering?)']
    Object.assign(c.evidence, board, { covers: { loaded: covers.filter((x) => x.cover === 'loaded').length, broken: covers.filter((x) => x.cover === 'broken').map((x) => x.name), none: covers.filter((x) => x.cover === 'none').length } })
    const f = []
    if (board.lineAlert) f.push(`the board says "${board.line}"`)
    if (!board.buttons.some((b) => /^(Scan games|Scanning)$/.test(b))) f.push(`no Scan games (bar: ${board.buttons.join(', ')})`)
    if (!board.buttons.some((b) => /^(Compile|Pause|Resume)$/.test(b))) f.push(`no Compile or Pause (bar: ${board.buttons.join(', ')})`)
    if (board.frames) f.push('System > Shaders hosts a frame or window')
    if (engineGames && board.posters !== engineGames.length) f.push(`${board.posters} posters, the engine lists ${engineGames.length} games`)
    if (c.evidence.covers.broken.length) f.push(`${c.evidence.covers.broken.length} covers failed: ${c.evidence.covers.broken.slice(0, 5).join(', ')}`)
    if (covers.length && c.evidence.covers.loaded < covers.length / 2) f.push(`only ${c.evidence.covers.loaded} of ${covers.length} posters have a cover`)
    return f
  })

  await check('shaders.compiled', 'Exo\'s game page says Compiled for games the engine compiled (Mortal Shell, Little Nightmares Enhanced Edition)', async (c) => {
    const wanted = statuses.filter((s) => /^(Mortal Shell|Little Nightmares Enhanced Edition)$/i.test(s.game.title))
    const warmed = statuses.filter((s) => s.status.state === 'warmed')
    c.evidence.compiledByStatus = warmed.map((s) => s.game.title)
    const picks = wanted.length ? wanted : warmed.slice(0, 2)
    if (!picks.length) c.skip('no game compiled by the engine')
    const f = []
    c.evidence.sheets = []
    for (const s of picks) {
      if (s.status.state !== 'warmed') f.push(`${s.game.title}: shaders.status says ${s.status.state} (${s.status.reason ?? ''})`)
      const section = await openSheet(s.game)
      if (!section) { f.push(`${s.game.title} has no card in the Library`); continue }
      c.evidence.sheets.push({ game: s.game.title, ...section })
      await c.shot(sh, s.game.title.replace(/\W+/g, '-').slice(0, 20))
      if (section.word !== 'Compiled') f.push(`${s.game.title}'s page says "${section.word}"`)
      await closeSheet()
    }
    return f
  })
}

/**
 * Opt-in (-UpscalerCycle): a real Restore and Update all on one game without anti-cheat whose
 * originals Exo saved. Restore puts the game's own DLSS back, the Upscalers sheet lists it old →
 * new, Update all brings it to the newest again: the game ends on the files it had.
 */
async function upscalerCycle(sh) {
  await check('upscalers.cycle', 'Restore one game\'s upscalers, then System > Graphics > Upscalers > Update all brings them back to the newest', async (c) => {
    const library = await sh.eval(hostCall('library.get'))
    const versions = (s) => s.items.map((i) => `${i.displayName || i.fileName}: ${i.currentVersion} [${i.state}]`)
    let pick = null
    let restorable = null
    for (const g of library.games.filter((x) => x.installed && x.store === 'steam' && !x.canStop)) {
      const s = await sh.eval(hostCall('dlss.status', { id: g.id }, 30000)).catch(() => null)
      if (!s || s.antiCheat || !s.items?.length) continue
      // Already behind (an earlier run stopped half way): Update all alone brings it back.
      if (s.items.some((i) => i.state === 'outdated' && i.canUpdate)) { pick = { game: g, status: s, restore: false }; break }
      if (!restorable && s.items.every((i) => i.state === 'current') && s.items.some((i) => i.canRestore)) restorable = { game: g, status: s, restore: true }
    }
    pick ??= restorable
    if (!pick) c.skip('no game without anti-cheat whose upscalers are current and restorable')
    c.evidence.game = pick.game.title
    c.evidence.before = versions(pick.status)
    const f = []
    await sh.click(navButton, 'Library')
    if (pick.restore) {
      // Restore from the game's Properties, the way a person does it.
      if (!await revealTile(sh, pick.game.id) || !await sh.click((id) => document.querySelector(`main.exo-library-pane .exo-tile-hit[data-game-id="${CSS.escape(id)}"]`), pick.game.id)) c.skip(`${pick.game.title} has no card on screen`)
      // Properties > Installed files > Restore originals, then its confirm.
      await sh.click(() => document.querySelector('[role=dialog] .exo-game-side[aria-label=Properties]'))
      await sh.waitFor(() => !!document.querySelector('.exo-props-nav button'), { timeoutMs: 5000 })
      await sh.click(() => [...document.querySelectorAll('.exo-props-nav button')].find((b) => b.textContent.trim() === 'Installed files'))
      const button = () => [...document.querySelectorAll('.exo-props-pane button')].find((b) => b.textContent.trim() === 'Restore originals' && !b.disabled)
      if (!await sh.waitFor(() => !![...document.querySelectorAll('.exo-props-pane button')].find((b) => b.textContent.trim() === 'Restore originals' && !b.disabled), { timeoutMs: 10000 })) { await sh.key('Escape'); await sh.key('Escape'); return ['Properties offers no Restore originals'] }
      await sh.click(button)
      await sh.click(() => document.querySelector('.exo-props-confirm button[data-autofocus]'))
      const restored = await poll(async () => { const s = await sh.eval(hostCall('dlss.status', { id: pick.game.id }, 30000)); return s.items.some((i) => i.state === 'outdated') ? s : null }, 120000, 1000)
      await c.shot(sh, 'restored')
      await sh.key('Escape')
      await sh.waitFor(() => !document.querySelector('.exo-props'), { timeoutMs: 3000 })
      await sh.key('Escape')
      await sh.waitFor(() => !document.querySelector('[role=dialog][aria-modal=true]:not([hidden] *)'), { timeoutMs: 3000 })
      if (!restored) return ['Restore originals in Properties left nothing outdated']
      c.evidence.restored = versions(restored)
    }
    if (!await openSystemPage(sh, 'Graphics')) return [...f, 'no System room with a Graphics page']
    const listed = await sh.waitFor((t) => {
      const s = document.querySelector('section.exo-system .exo-upscalers')
      if (!s || s.querySelector('[role=progressbar]')) return null
      const g = [...s.querySelectorAll('.exo-upscalers-card')].find((x) => x.querySelector('.exo-upscalers-card-title')?.textContent.trim() === t)
      return { game: !!g, rows: g ? [...g.querySelectorAll('.exo-upscalers-jump')].map((j) => `${j.querySelector('.exo-upscalers-from')?.textContent.trim() ?? ''} → ${j.querySelector('.exo-upscalers-to')?.textContent.trim() ?? ''}`) : [], button: [...s.querySelectorAll('.exo-gpu-bar button')].map((b) => b.textContent.trim()).find((x) => /^Update/.test(x)) ?? null }
    }, { timeoutMs: 90000, interval: 250, args: [pick.game.title] })
    await c.shot(sh, 'listed')
    c.evidence.sheet = listed
    if (!listed?.game) { await sh.click(navButton, 'Library'); return [...f, 'the restored game is not listed in Upscalers'] }
    if (!listed.rows.every((r) => / → /.test(r))) f.push(`rows ${listed.rows.join(', ')} are not old → new`)
    if (!listed.button) { await sh.click(navButton, 'Library'); return [...f, 'no Update all'] }
    await sh.click(() => [...document.querySelectorAll('section.exo-system .exo-upscalers .exo-gpu-bar button')].find((b) => /^Update/.test(b.textContent.trim())))
    const done = await sh.waitFor((t) => {
      const g = [...document.querySelectorAll('section.exo-system .exo-upscalers .exo-upscalers-card')].find((x) => x.querySelector('.exo-upscalers-card-title')?.textContent.trim() === t)
      const note = g?.querySelector('.exo-upscalers-card-note')?.textContent.trim()
      // Update all reads every game again when it ends: an updated game leaves the list (or the list
      // gives way to "Everything is on the newest release") once that read lands.
      if (!g) return document.querySelector('section.exo-system .exo-upscalers [role=progressbar]') ? null : 'Updated'
      return note && note !== 'Updating…' ? note : null
    }, { timeoutMs: 600000, interval: 500, args: [pick.game.title] })
    await c.shot(sh, 'updated')
    c.evidence.result = done
    if (done !== 'Updated') f.push(`Update all ended with "${done}"`)
    await sh.click(navButton, 'Library')
    const end = await sh.eval(hostCall('dlss.status', { id: pick.game.id }, 30000))
    c.evidence.after = versions(end)
    if (!end.items.every((i) => i.state === 'current')) f.push('after Update all the game is not current: ' + versions(end).join(', '))
    return f
  })
}

/**
 * A game started outside Exo reaches game.runState. A temporary portable game is added through
 * the same call as Library > Add portable game (game.install local:add with a folder): a copy of
 * Windows' ping.exe in a folder under the user profile. The exe is started by the run, not by
 * Exo; game.runState must say running within 15 s and stopped within 15 s of it ending (it ends
 * by itself). The entry is removed from Exo (its files are left; the run deletes its folder).
 */
async function runState(sh) {
  const { mkdirSync, copyFileSync, rmSync } = await import('node:fs')
  const { spawn } = await import('node:child_process')
  const root = join(process.env.USERPROFILE ?? 'C:\\Users\\Public', 'exo-e2e-portable-' + Date.now())
  const folder = join(root, 'Exo E2E Game')
  await check('runstate.outside', 'A game started outside Exo reaches game.runState within 15 s and clears within 15 s of closing', async (c) => {
    const f = []
    let id = null
    try {
      mkdirSync(folder, { recursive: true })
      copyFileSync(join(process.env.SystemRoot ?? 'C:\\Windows', 'System32', 'PING.EXE'), join(folder, 'ExoE2EGame.exe'))
      await sh.eval(`(() => { window.__e2eRun = []; window.chrome.webview.addEventListener('message', (e) => { let d = e.data; try { if (typeof d === 'string') d = JSON.parse(d) } catch { return } if (d && d.event === 'game.runState') window.__e2eRun.push({ ...d.data, at: Date.now() }) }); return true })()`)
      const added = await sh.eval(hostCall('game.install', { id: 'local:add', path: folder }, 60000))
      c.evidence.added = { ok: added.ok, message: added.message ?? null }
      if (!added.ok) return [`adding the portable game failed: ${added.message}`]
      const game = await poll(async () => (await sh.eval(hostCall('library.get'))).games.find((g) => g.title === 'Exo E2E Game' && g.installed) ?? null, 20000, 500)
      if (!game) return ['the portable game did not appear in the library']
      id = game.id
      c.evidence.game = { id: game.id, store: game.store }
      // The poll reads the library every 5 s; let it see the new game before the exe starts.
      await sleep(6000)
      const started = Date.now()
      const child = spawn(join(folder, 'ExoE2EGame.exe'), ['-n', '22', '127.0.0.1'], { stdio: 'ignore', windowsHide: true })
      const exited = new Promise((resolve) => child.on('exit', () => resolve(Date.now())))
      const events = () => sh.eval('window.__e2eRun')
      const on = await poll(async () => (await events()).find((e) => e.id === id && e.isRunning && e.at >= started) ?? null, 15000, 250)
      c.evidence.runningAfterMs = on ? on.at - started : null
      c.evidence.canStop = on?.canStop ?? null
      if (!on) f.push('no game.runState running within 15 s of the start')
      const sheet = on ? await sh.eval(hostCall('game.get', { id })).then((r) => ({ isRunning: r.game?.isRunning ?? null, canStop: r.game?.canStop ?? null })).catch(() => null) : null
      c.evidence.gameGet = sheet
      const endedAt = await exited
      const off = await poll(async () => (await events()).find((e) => e.id === id && !e.isRunning && e.at >= endedAt - 500) ?? null, 15000, 250)
      c.evidence.stoppedAfterMs = off ? off.at - endedAt : null
      if (!off) f.push('no game.runState stopped within 15 s of the exe ending')
      c.evidence.events = (await events()).filter((e) => e.id === id).map((e) => ({ isRunning: e.isRunning, canStop: e.canStop, ms: e.at - started }))
    } finally {
      if (id) {
        const removed = await sh.eval(hostCall('game.uninstall', { id }, 30000)).catch((e) => ({ ok: false, message: e.message }))
        c.evidence.removed = { ok: removed.ok, message: removed.message ?? null }
        if (!removed.ok) f.push(`removing the portable game failed: ${removed.message}`)
        const gone = await poll(async () => (await sh.eval(hostCall('library.get'))).games.some((g) => g.id === id) ? null : true, 15000, 500)
        if (!gone) f.push('the portable game is still in the library after Remove')
      }
      try { rmSync(root, { recursive: true, force: true }) } catch (error) { f.push('could not delete the temporary folder: ' + error.message) }
    }
    return f
  })
}

/**
 * Opt-in (-RealGame <id>): a real installed game, started outside Exo through its store's own
 * link (steam://rungameid/<app>), reaches game.runState within 15 s of its process appearing;
 * a mouse profile linked to it comes on (Exo Mouse's notice, sent once the mouse has it);
 * Exo's Stop closes it; game.runState clears and the default profile comes back. Never a Riot
 * game or one with kernel anti-cheat: the caller picks the game.
 */
async function realGame(sh, id) {
  const { spawn } = await import('node:child_process')
  const { execFileSync } = await import('node:child_process')
  await check('realgame.run', `A real game (${id}) started outside Exo: game.runState, its mouse profile, Exo's Stop, and back to Desktop`, async (c) => {
    const app = /^steam:(\d+)$/.exec(id ?? '')?.[1]
    if (!app) c.skip('-RealGame takes a Steam game id (steam:<app>)')
    if (/^riot:/i.test(id)) c.skip('never a Riot game')
    const library = await sh.eval(hostCall('library.get'))
    const game = library.games.find((g) => g.id === id)
    if (!game?.installed) c.skip(`${id} is not installed`)
    if (game.updateAvailable) c.skip(`${game.title} has an update waiting; the run downloads nothing`)
    const profiles = (await sh.eval(hostCall('mouse.profiles.get')).catch(() => null))?.data ?? {}
    const linked = Object.values(profiles).flat().find((p) => (p.games ?? []).includes(id))
    const fallback = Object.values(profiles).flat().find((p) => p.isDefault)
    Object.assign(c.evidence, { game: game.title, linkedProfile: linked?.name ?? null, defaultProfile: fallback?.name ?? null })
    await sh.eval(`(() => {
      const r = window.__e2eReal = { run: [], notices: [] }
      window.chrome.webview.addEventListener('message', (e) => { let d = e.data; try { if (typeof d === 'string') d = JSON.parse(d) } catch { return } if (d && d.event === 'game.runState' && d.data.id === ${JSON.stringify(id)}) r.run.push({ ...d.data, at: Date.now() }) })
      window.addEventListener('message', (e) => { const m = e.data; if (m && m.exo === 'mouse' && m.id === undefined && typeof m.notice === 'string') r.notices.push({ text: m.notice, at: Date.now() }) })
      return true
    })()`)
    const procs = () => { try { return execFileSync('tasklist', ['/fo', 'csv', '/nh'], { encoding: 'utf8' }) } catch { return '' } }
    const before = new Set(procs().split('\n').map((l) => l.split('","')[1]))
    const f = []
    let profileSkip = null
    const t0 = Date.now()
    spawn('cmd.exe', ['/c', 'start', '', `steam://rungameid/${app}`], { stdio: 'ignore', windowsHide: true }).unref()
    // The game's own process (a new image under its install folder) appears once Steam hands off.
    const started = await poll(async () => {
      const ps = await sh.eval(hostCall('game.get', { id })).catch(() => null)
      return ps?.game?.isRunning ? Date.now() : null
    }, 120000, 1000)
    const ev = () => sh.eval('window.__e2eReal')
    const run = (await ev()).run.find((e) => e.isRunning)
    c.evidence.startClickToRunningMs = run ? run.at - t0 : null
    c.evidence.gameGetRunningAfterMs = started ? started - t0 : null
    if (!run) { f.push('no game.runState running within 2 minutes of the start'); return f }
    // When did its process appear? The newest image names that were not running before.
    c.evidence.newProcesses = [...new Set(procs().split('\n').map((l) => l.split('","')[1]).filter((n) => n && !before.has(n)))].slice(0, 8)
    if (linked) {
      const on = await poll(async () => (await ev()).notices.find((n) => n.text.startsWith(`${linked.name} mouse profile on`)) ?? null, 60000, 500)
      c.evidence.profileOn = on ? { text: on.text, afterRunStateMs: on.at - run.at } : null
      // Exo Mouse says so only once the mouse has the profile; a sleeping wireless mouse drops writes until it wakes.
      if (!on) { const mouse = await openFrame(sh, 'mouse.exo-launcher.local', 5000).catch(() => null); const active = !mouse ? [] : await mouse.eval(`[...document.querySelectorAll('.m-row')].filter(r => r.querySelector('.m-row-value.is-good')).map(r => r.querySelector('.m-row-label')?.childNodes[0]?.textContent.trim())`).catch(() => []); c.evidence.activeProfileWithoutNotice = active; profileSkip = `${linked.name} did not land within 60 s (Exo Mouse still shows ${active.join(', ') || 'no profile'} active): the mouse took no write, as a sleeping wireless mouse does` }
    }
    await sleep(5000)
    const stop = await sh.eval(hostCall('game.stop', { id }, 60000)).catch((e) => ({ ok: false, message: e.message }))
    c.evidence.stop = { ok: stop.ok, message: stop.message ?? null }
    if (!stop.ok) f.push(`Exo's Stop: ${stop.message}`)
    const stopAt = Date.now()
    const off = await poll(async () => (await ev()).run.find((e) => !e.isRunning && e.at >= stopAt - 1000) ?? null, 30000, 500)
    c.evidence.stoppedAfterMs = off ? off.at - stopAt : null
    if (!off) f.push('no game.runState stopped within 30 s of Stop')
    else if (off.at - stopAt > 15000) f.push(`game.runState cleared ${off.at - stopAt} ms after Stop (over 15 s)`)
    if (linked && fallback) {
      const back = await poll(async () => (await ev()).notices.find((n) => n.text.startsWith(`${fallback.name} mouse profile on`) && n.at >= stopAt - 1000) ?? null, 60000, 500)
      c.evidence.profileBack = back ? { text: back.text, afterStopMs: back.at - stopAt } : null
      if (!back && !profileSkip) f.push(`${fallback.name} did not come back (no notice within 60 s of Stop)`)
    }
    c.evidence.notices = (await ev()).notices.map((n) => n.text)
    if (!f.length && profileSkip) c.skip('game.runState and Stop passed; ' + profileSkip)
    return f
  })
}

// ---------- Library: sheets, shortcuts, sort, achievements, tools ----------

const SHELF_COUNTS = () => [...document.querySelectorAll('main.exo-library-pane h2.exo-shelf-title')].map((h) => ({ name: [...h.childNodes].filter((n) => n.nodeType === 3).map((n) => n.textContent).join('').trim(), count: Number(h.querySelector('.exo-shelf-count')?.textContent ?? 0) }))

/**
 * Library additions: System > Graphics > Upscalers (read; Update all is never pressed here), the
 * System room's pages, a double-click on a card opening its page and never playing (game.launch is
 * held, so nothing could start), the right-click game menu (read, never chosen), Ctrl+1..5 and
 * Ctrl+, , the sort menu's Last launched and Installed first (put back), the achievements stat on a
 * played game, and Show tools and soundtracks (toggled and put back byte for byte).
 */
async function libraryPlus(sh, settingsPath) {
  const library = await sh.eval(hostCall('library.get')).catch(() => null)
  const games = library?.games ?? []
  const toLibrary = async () => { await sh.click(navButton, 'Library'); await sh.waitFor(() => document.querySelectorAll('main.exo-library-pane .exo-tile-hit').length > 0, { timeoutMs: 10000 }) }
  await toLibrary()
  const safe = (g) => g.store !== 'riot' && !g.isAddPortable && g.id !== 'local:add' && !/valorant|league|counter-strike|rainbow|bodycam|wuthering/i.test(g.title)

  await check('library.upscalers-sheet', 'System > Graphics > Upscalers shows a card for each game with older upscalers (family old → new) and Update all, or that everything is current', async (c) => {
    if (!await openSystemPage(sh, 'Graphics')) return ['no System room with a Graphics page']
    const sheet = await sh.waitFor(() => {
      const s = document.querySelector('section.exo-system .exo-system-panel[aria-label="Upscalers"] .exo-upscalers')
      if (!s || s.querySelector('[role=progressbar]')) return null
      return {
        note: s.querySelector('.exo-gpu-bar-note')?.textContent.trim() ?? '',
        clear: s.querySelector('.exo-upscalers-clear-title')?.textContent.trim() ?? null,
        games: [...s.querySelectorAll('.exo-upscalers-card')].map((g) => ({ title: g.querySelector('.exo-upscalers-card-title')?.textContent.trim(), jumps: [...g.querySelectorAll('.exo-upscalers-jump')].map((j) => ({ family: j.querySelector('.exo-upscalers-family')?.textContent.trim(), from: j.querySelector('.exo-upscalers-from')?.textContent.trim() ?? null, to: j.querySelector('.exo-upscalers-to')?.textContent.trim() ?? null })), note: g.querySelector('.exo-upscalers-card-note')?.textContent.trim() ?? null })),
        actions: [...s.querySelectorAll('.exo-gpu-bar button')].map((b) => b.textContent.trim() || b.getAttribute('aria-label')),
      }
    }, { timeoutMs: 90000, interval: 250 })
    await c.shot(sh)
    if (!sheet) { await toLibrary(); return ['Upscalers did not finish checking within 90 s'] }
    Object.assign(c.evidence, sheet)
    const f = []
    const riot = games.filter((g) => g.store === 'riot').map((g) => g.title)
    const ver = /^\d+(\.\d+)+$/
    const older = (a, b) => { const l = a.split('.').map(Number), r = b.split('.').map(Number); for (let k = 0; k < Math.max(l.length, r.length); k++) { const d = (l[k] || 0) - (r[k] || 0); if (d) return d < 0 } return false }
    for (const g of sheet.games) {
      if (riot.includes(g.title)) f.push(`a Riot game is listed: ${g.title}`)
      if (!g.jumps.length) f.push(`${g.title} lists no upscaler`)
      for (const j of g.jumps) {
        if (!['DLSS', 'FSR', 'XeSS'].includes(j.family)) f.push(`${g.title}: "${j.family}" is not an upscaler family`)
        // A family already current shows only its version.
        if (j.from === null) { if (!ver.test(j.to ?? '')) f.push(`${g.title} ${j.family}: "${j.to}" is not a version`); continue }
        if (!ver.test(j.from) || !ver.test(j.to ?? '')) f.push(`${g.title} ${j.family}: "${j.from} → ${j.to}" is not old → new`)
        else if (!older(j.from, j.to)) f.push(`${g.title} ${j.family}: ${j.from} is not older than ${j.to}`)
      }
    }
    if (sheet.games.length) {
      if (!sheet.actions.some((a) => /^(Update (all|\d+)|Check again|Updating…)$/.test(a))) f.push(`games are listed but there is no Update all (${sheet.actions.join(', ')})`)
      if (sheet.clear) f.push(`games are listed and it also says "${sheet.clear}"`)
    } else {
      if (sheet.clear !== 'Everything is on the newest release') f.push(`nothing listed but it says "${sheet.clear ?? ''}"`)
      if (sheet.actions.some((a) => /^Update/.test(a))) f.push('nothing listed but Update is offered')
      if (sheet.note) f.push(`nothing listed but the bar says "${sheet.note}"`)
      // Nothing listed must mean the host has no outdated file in any game Exo may change.
      const outdated = []
      for (const g of games.filter((x) => x.installed && x.store !== 'riot' && !x.isAddPortable)) {
        const st = await sh.eval(hostCall('dlss.status', { id: g.id })).catch(() => null)
        if (st && st.consent !== 'never' && (st.items ?? []).some((i) => i.state === 'outdated')) outdated.push(g.title)
      }
      c.evidence.hostOutdated = outdated
      if (outdated.length) f.push(`nothing listed but dlss.status has outdated files in ${outdated.join(', ')}`)
    }
    await toLibrary()
    return f
  })

  await check('library.shaders-escape', 'System\'s tabs switch its pages: Shaders shows the board alone, Display its panel, Graphics its three panels again', async (c) => {
    const f = []
    const shown = () => [...document.querySelectorAll('section.exo-system .exo-system-page')].filter((p) => !p.hidden).map((p) => ({ panels: [...p.querySelectorAll('.exo-system-panel')].map((x) => x.getAttribute('aria-label')), board: !!p.querySelector('.exo-shaders-board, .exo-shaders-empty') }))
    c.evidence.pages = {}
    for (const [tab, want] of [['Shaders', (p) => p.length === 1 && p[0].board], ['Display', (p) => p.length === 1 && p[0].panels.join() === 'Display'], ['Graphics', (p) => p.length === 1 && p[0].panels.join('|') === 'Driver and profile|HDR|Upscalers']]) {
      if (!await openSystemPage(sh, tab)) { f.push(`no ${tab} tab in System`); continue }
      await sleep(300)
      const pages = await sh.call(shown)
      c.evidence.pages[tab] = pages
      if (tab === 'Shaders') await c.shot(sh, 'shaders')
      if (!pages || !want(pages)) f.push(`${tab} shows ${JSON.stringify(pages)}`)
    }
    await toLibrary()
    return f
  })

  await check('library.double-click-play', 'Double-clicking an installed card opens its page and never plays it (no game.launch; launch is held anyway)', async (c) => {
    const target = games.find((g) => g.installed && g.store === 'steam' && /thymesia/i.test(g.title)) ?? games.find((g) => g.installed && !g.isRunning && safe(g))
    if (!target) c.skip('no installed game to double-click')
    c.evidence.game = target.title
    if (!await revealTile(sh, target.id)) return ['card not found in the grid']
    await sh.eval(holdHost(['game.launch', 'game.install', 'game.update']))
    let calls = []
    let page = null
    try {
      if (!await sh.doubleClick((id) => document.querySelector(`main.exo-library-pane .exo-tile-hit[data-game-id="${CSS.escape(id)}"]`), target.id)) return ['card not on screen']
      page = await sh.waitFor((t) => [...document.querySelectorAll('[role=dialog][aria-modal=true]:not([hidden] *)')].map((d) => d.getAttribute('aria-label')).find((l) => l === t) ?? null, { timeoutMs: 3000, args: [target.title] })
      await sleep(1200)
      calls = await sh.eval('window.__exoHold?.calls ?? []')
    } finally { await sh.eval(releaseHost) }
    Object.assign(c.evidence, { calls, page })
    await c.shot(sh)
    await sh.key('Escape')
    await sh.waitFor(() => !document.querySelector('[role=dialog][aria-modal=true]:not([hidden] *)'), { timeoutMs: 3000 })
    const f = []
    if (!page) f.push('the double-click did not open the game page')
    if (calls.length) f.push(`the double-click asked the host for ${calls.map((x) => x.method).join(', ')}`)
    return f
  })

  // A card's right-click menu: Play (or Install) and Open folder for an installed game; never Pin.
  // Read only: the menu is opened and closed with Escape, nothing in it is chosen.
  await check('library.game-menu', 'Right-clicking a card opens its menu (Play and Open folder installed, Install otherwise; no Pin); Escape closes it', async (c) => {
    const picks = [
      // Games in Continue aren't in the grid: each kind takes its first game whose card the grid has.
      ['installed', games.filter((g) => g.installed && !g.isRunning && !g.canStop && !g.updateAvailable && safe(g))],
      ['not-installed', games.filter((g) => !g.installed && g.owned && safe(g))],
    ].filter(([, g]) => g.length)
    if (!picks.length) c.skip('no game to right-click')
    const f = []
    c.evidence.menus = []
    await sh.eval(holdHost(['game.launch', 'game.install', 'game.update', 'game.openFolder', 'shell.openPath']))
    try {
      for (const [kind, candidates] of picks) {
        let g = null
        for (const candidate of candidates.slice(0, 6)) if (await revealTile(sh, candidate.id)) { g = candidate; break }
        if (!g) { f.push(`${kind}: no card found in the grid`); continue }
        await sh.eval(`window.__exoPagesBefore = new Set(document.querySelectorAll('[role=dialog][aria-modal=true]:not([hidden] *)'))`)
        if (!await sh.rightClick((id) => document.querySelector(`main.exo-library-pane .exo-tile-hit[data-game-id="${CSS.escape(id)}"]`), g.id)) { f.push(`${g.title}: card not on screen`); continue }
        const menu = await sh.waitFor(() => { const m = document.querySelector('.exo-game-menu:popover-open'); return m ? { label: m.getAttribute('aria-label'), items: [...m.querySelectorAll('[role=menuitem]')].map((i) => i.textContent.trim()), focused: document.activeElement?.getAttribute('role') === 'menuitem', page: [...document.querySelectorAll('[role=dialog][aria-modal=true]:not([hidden] *)')].filter((d) => !window.__exoPagesBefore.has(d)).map((d) => d.getAttribute('aria-label') ?? d.className)[0] ?? null, stale: [...window.__exoPagesBefore].map((d) => ({ label: d.getAttribute('aria-label'), presence: d.parentElement?.className ?? null, opacity: getComputedStyle(d.parentElement ?? d).opacity, width: d.getBoundingClientRect().width })) } : null }, { timeoutMs: 3000 })
        await c.shot(sh, kind)
        c.evidence.menus.push({ kind, game: g.title, menu })
        if (!menu) { f.push(`${g.title}: right-click opened no menu`); continue }
        const want = kind === 'installed' ? ['Play', 'Open folder', 'Properties'] : null
        if (want && menu.items.join('|') !== want.join('|')) f.push(`${g.title}: menu ${menu.items.join(', ')} (expected ${want.join(', ')})`)
        if (!want && !(menu.items.length === 1 && /^Install\b/.test(menu.items[0]))) f.push(`${g.title}: menu ${menu.items.join(', ')} (expected Install)`)
        if (menu.items.some((i) => /\bPin\b/i.test(i))) f.push(`${g.title}: the menu offers Pin`)
        if (menu.label !== g.title) f.push(`menu named "${menu.label}" for ${g.title}`)
        if (!menu.focused) f.push(`${g.title}: focus is not on the menu`)
        if (menu.page) f.push(`${g.title}: the right-click also opened a page (${menu.page})`)
        await sh.key('Escape')
        const closed = await sh.waitFor((id) => !document.querySelector('.exo-game-menu:popover-open') ? { focusOnCard: document.activeElement?.dataset?.gameId === id } : null, { timeoutMs: 3000, args: [g.id] })
        if (!closed) f.push(`${g.title}: Escape did not close the menu`)
        else if (!closed.focusOnCard) f.push(`${g.title}: focus did not return to the card`)
      }
      const calls = await sh.eval('window.__exoHold?.calls ?? []')
      if (calls.length) f.push(`opening the menus asked the host for ${calls.map((x) => x.method).join(', ')}`)
    } finally { await sh.eval(releaseHost) }
    await libraryTop(sh)
    return f
  })

  await check('library.shortcuts', 'Ctrl+1..5 open Library, Store, Discord, Music and System; Ctrl+, opens Settings', async (c) => {
    const rooms = await sh.eval(`[...document.querySelectorAll('nav[aria-label="Rooms"] button')].map(b => b.textContent.trim())`)
    const f = []
    const order = ['Library', 'Store', 'Discord', 'Music', 'System']
    c.evidence.rooms = []
    for (let i = 0; i < order.length; i++) {
      if (!rooms[i]?.startsWith(order[i])) f.push(`tab ${i + 1} is "${rooms[i]}", expected ${order[i]}`)
      await sh.key(String(i + 1), { ctrl: true })
      const ok = await sh.waitFor((n) => [...document.querySelectorAll('nav[aria-label="Rooms"] button')][n]?.getAttribute('aria-current') === 'page', { timeoutMs: 3000, args: [i] })
      c.evidence.rooms.push({ key: `Ctrl+${i + 1}`, room: rooms[i], opened: !!ok })
      if (!ok) f.push(`Ctrl+${i + 1} did not open ${rooms[i]}`)
      await sleep(400)
    }
    await sh.key(',', { ctrl: true })
    const settings = await sh.waitFor(() => document.querySelector('main.exo-settings')?.getBoundingClientRect().width > 0 ? true : null, { timeoutMs: 3000 })
    c.evidence.settings = !!settings
    if (!settings) f.push('Ctrl+, did not open Settings')
    await c.shot(sh)
    await sh.key('Escape')
    await sh.waitFor(() => !(document.querySelector('main.exo-settings')?.getBoundingClientRect().width > 0), { timeoutMs: 3000 })
    await sh.key('1', { ctrl: true })
    await sh.waitFor(() => [...document.querySelectorAll('nav[aria-label="Rooms"] button')][0]?.getAttribute('aria-current') === 'page', { timeoutMs: 3000 })
    return f
  })
  await toLibrary()

  await check('library.sort-recent', 'Sort > Last launched orders All games by last launch, Installed first puts installed games first (the sort is put back)', async (c) => {
    await libraryTop(sh)
    const before = await sh.eval(`document.querySelector('main.exo-library-pane button[aria-label^="Sort library"]')?.getAttribute('aria-label')?.replace(/^Sort library, /, '') ?? null`)
    c.evidence.sortBefore = before
    if (!before) return ['no "Sort library" button in the All games header']
    const byId = new Map(games.map((g) => [g.id, g]))
    const f = []
    try {
      if (!await pickSort(sh, 'Last launched')) return ['Last launched could not be picked']
      await sleep(500)
      const order = await sh.call(GRID_ORDER)
      await c.shot(sh, 'recent')
      const last = (id) => { const g = byId.get(id); return g?.lastPlayedUtc ? Date.parse(g.lastPlayedUtc) : 0 }
      const outOfOrder = []
      for (let i = 1; i < order.length; i++) if (last(order[i]) > last(order[i - 1]) + 1000) outOfOrder.push(`${byId.get(order[i])?.title} after ${byId.get(order[i - 1])?.title}`)
      c.evidence.recentFirst = order.slice(0, 6).map((id) => `${byId.get(id)?.title}: ${byId.get(id)?.lastPlayedUtc ?? '—'}`)
      if (!order.length) f.push('All games renders no cards')
      if (outOfOrder.length) f.push(`${outOfOrder.length} cards out of last-launched order: ${outOfOrder.slice(0, 4).join('; ')}`)

      if (!await pickSort(sh, 'Installed first')) { f.push('Installed first could not be picked'); return f }
      await sleep(500)
      const order2 = await sh.call(GRID_ORDER)
      await c.shot(sh, 'installed')
      const installed = order2.map((id) => byId.get(id)?.installed === true)
      const firstOut = installed.indexOf(false)
      c.evidence.installedFirst = order2.slice(0, 6).map((id) => `${byId.get(id)?.title}: ${byId.get(id)?.installed ? 'installed' : 'not installed'}`)
      if (firstOut >= 0 && installed.slice(firstOut).some(Boolean)) f.push('an installed game comes after a game that is not installed')
      if (!installed[0] && games.some((g) => g.installed && byId.has(g.id))) f.push('the first card is not an installed game')
    } finally {
      await libraryTop(sh)
      if (!await pickSort(sh, before)) f.push(`the sort could not be put back to ${before}`)
    }
    return f
  })

  await check('library.achievements', 'A played game with achievements shows them on its sheet, as the host counts them', async (c) => {
    const played = games.filter((g) => g.installed && g.store !== 'riot' && (g.playtimeMinutes ?? 0) > 0)
    let pick = null
    for (const g of played) {
      const extras = await sh.eval(hostCall('game.extras', { id: g.id }, 20000)).catch(() => null)
      if (extras?.achievementsTotal > 0) { pick = { game: g, extras }; break }
    }
    if (!pick) c.skip('no played game with achievement data')
    c.evidence.game = pick.game.title
    c.evidence.host = { unlocked: pick.extras.achievementsUnlocked, total: pick.extras.achievementsTotal }
    if (!await revealTile(sh, pick.game.id) || !await sh.click((id) => document.querySelector(`main.exo-library-pane .exo-tile-hit[data-game-id="${CSS.escape(id)}"]`), pick.game.id)) c.skip(`${pick.game.title} has no card in the Library`)
    const stat = await sh.waitFor(() => [...document.querySelectorAll('[role=dialog] .exo-game-stat')].map((s) => [s.querySelector('.exo-game-stat-label')?.textContent.trim(), s.querySelector('.exo-game-stat-value')?.textContent.trim()]).find(([l]) => l === 'Achievements')?.[1] ?? null, { timeoutMs: 15000 })
    await c.shot(sh)
    c.evidence.sheet = stat
    await sh.key('Escape')
    await sh.waitFor(() => !document.querySelector('[role=dialog][aria-modal=true]:not([hidden] *)'), { timeoutMs: 3000 })
    const want = `${pick.extras.achievementsUnlocked} of ${pick.extras.achievementsTotal}`
    if (!stat) return ['no Achievements on the sheet']
    return stat === want ? [] : [`the sheet says ${stat}, the host ${want}`]
  })

  await check('library.show-tools', 'Show tools and soundtracks changes the shelves and is put back byte for byte', async (c) => {
    const { createHash } = await import('node:crypto')
    const hash = async () => createHash('sha256').update(await readFile(settingsPath)).digest('hex')
    const h0 = await hash()
    const original = (await readJson(settingsPath)).showLibraryTools === true
    const total = async () => (await sh.call(SHELF_COUNTS)).reduce((a, s) => a + s.count, 0)
    const before = await total()
    const sw = () => document.querySelector('#set-tools [role=switch], [id="set-tools"][role=switch]') ?? [...document.querySelectorAll('.exo-set-item-label')].find((x) => x.textContent.trim() === 'Show tools and soundtracks')?.closest('.exo-set-item')?.querySelector('[role=switch]')
    const f = []
    const flip = async (want) => {
      await sh.click(byLabel, 'Open settings')
      await sh.waitFor(() => document.querySelector('main.exo-settings')?.getBoundingClientRect().width > 0 ? true : null, { timeoutMs: 5000 })
      if (!await sh.click(sw)) return false
      for (let i = 0; i < 50; i++) { try { if (((await readJson(settingsPath)).showLibraryTools === true) === want) break } catch { } await sleep(100) }
      await sh.key('Escape')
      await toLibrary()
      return true
    }
    if (!await flip(!original)) { await sh.key('Escape'); return ['no Show tools and soundtracks switch'] }
    const changed = await poll(async () => { const n = await total(); return n !== before ? n : null }, 8000, 250)
    const toolTitles = await sh.eval(`[...document.querySelectorAll('main.exo-library-pane .exo-tile-hit')].map(b => b.getAttribute('aria-label'))`)
    await c.shot(sh, 'flipped')
    await flip(original)
    const back = await poll(async () => { const n = await total(); return n === before ? n : null }, 8000, 250)
    await sleep(300)
    const h1 = await hash()
    const extras = games.filter((g) => (g.owned || g.installed) && ownedExtraReason(g.title)).map((g) => g.title)
    Object.assign(c.evidence, { showToolsBefore: original, cardsBefore: before, cardsFlipped: changed ?? before, cardsAfter: back ?? await total(), extrasInLibrary: extras.slice(0, 10), sha256Before: h0, sha256After: h1, visibleSample: toolTitles.slice(0, 5) })
    if (back === null) f.push('the shelves did not return to their count after switching back')
    if (h0 !== h1) f.push('settings.json bytes differ after the round trip')
    // The host already leaves non-games out; with nothing it calls a tool, the count may not move.
    if (changed === null && extras.length) f.push(`the shelves did not change though the library has ${extras.length} tools or extras`)
    if (changed === null && !extras.length) c.evidence.note = 'the library has no tools or soundtracks to show; the switch round trip is what was checked'
    return f
  })
}

// ---------- Store room ----------

const navButton = (name) => [...document.querySelectorAll('nav[aria-label="Rooms"] button')].find((b) => b.textContent.trim().startsWith(name))
/**
 * System > Graphics > Driver and profile, read only (never Use, Undo or Import): the card row shows the
 * action the host's own state calls for (Use, Applied or the change since); on NVIDIA the Sync menu names
 * the host's variant (G-SYNC or No G-SYNC) and there are no per-setting rows; AMD and Intel show one row
 * per setting to set by hand.
 */
async function gpuProfile(sh) {
  await check('gpu.profile', "System > Graphics shows Exo's profile for this card with its sync choice and the right action", async (c) => {
    const f = []
    const host = await sh.eval(hostCall('gpu.profile', {}, 30000)).catch((e) => ({ ok: false, message: e.message }))
    c.evidence.host = { ok: host.ok, vendor: host.vendor, gpu: host.gpu, series: host.series, screen: host.screen, status: host.status, gsync: host.gsync, items: host.items?.map((i) => `${i.key}=${i.value}`) }
    if (!host.ok) return [`gpu.profile failed: ${host.message}`]
    if (!(await openSystemPage(sh, 'Graphics'))) return ['no System room with a Graphics page']
    if (host.status === 'none') { await sh.click(navButton, 'Library'); return f }
    const page = await sh.waitFor(() => {
      const panel = document.querySelector('section.exo-system .exo-system-panel[aria-label="Driver and profile"]')
      const rows = [...(panel?.querySelectorAll('.exo-set-item') ?? [])].map((r) => ({ label: r.querySelector('.exo-set-item-label')?.textContent.trim(), detail: r.querySelector('.exo-set-item-detail')?.textContent.trim() ?? '', control: r.querySelector('.exo-set-item-control')?.textContent.trim() ?? '' }))
      const sync = panel?.querySelector('button[aria-label^="Sync, "]')?.getAttribute('aria-label').slice(6) ?? null
      const panels = [...document.querySelectorAll('section.exo-system .exo-system-page:not([hidden]) .exo-system-panel')].map((p) => p.getAttribute('aria-label'))
      return rows.length > 1 ? { rows, sync, panels } : null
    }, { timeoutMs: 10000 })
    if (!page) { await sh.click(navButton, 'Library'); return ['Driver and profile did not show its rows'] }
    Object.assign(c.evidence, { rows: page.rows.slice(0, 12), sync: page.sync, panels: page.panels })
    await c.shot(sh)
    if (page.panels.join('|') !== 'Driver and profile|HDR|Upscalers') f.push(`Graphics shows ${page.panels.join(', ')} (expected Driver and profile, HDR, Upscalers)`)
    const card = page.rows.find((r) => r.label === (host.gpu || 'Graphics card'))
    if (!card) f.push(`no row for the card ${host.gpu}`)
    if (host.vendor === 'nvidia') {
      const want = host.gsync ? 'G-SYNC' : 'No G-SYNC'
      if (page.sync !== want) f.push(`the Sync menu shows "${page.sync}", host variant is ${want}`)
      if (!page.rows.some((r) => r.label === 'Profile Inspector file')) f.push('no Profile Inspector import row')
      const allowed = ['NVIDIA driver', 'Driver type', host.gpu || 'Graphics card', 'Sync', 'Profile Inspector file']
      const extra = page.rows.filter((r) => !allowed.includes(r.label)).map((r) => r.label)
      if (extra.length) f.push(`individual settings are shown: ${extra.join(', ')}`)
    } else {
      for (const item of host.items) {
        const row = page.rows.find((r) => r.label === item.label)
        if (!row) f.push(`no row for ${item.label}`)
        else if (!row.detail) f.push(`${item.label} has no reason`)
      }
    }
    const head = card?.control ?? ''
    const wantHead = { ready: /Use Exo/, applied: /Applied/, changed: /changed/, manual: /^$/ }[host.status]
    if (card && wantHead && !wantHead.test(head)) f.push(`status ${host.status} but the card row shows "${head}"`)
    await sh.click(navButton, 'Library')
    return f
  })
}

/** The System room on one of its pages (Graphics, Display, Shaders): the room's tab, then the page's. */
async function openSystemPage(sh, name) {
  if (!await sh.click(navButton, 'System')) return null
  if (!(await sh.waitFor(() => !!document.querySelector('section.exo-system [role=tablist][aria-label="System"]') && document.querySelector('section.exo-system').getBoundingClientRect().width > 0, { timeoutMs: 8000 }))) return null
  const tab = (n) => [...document.querySelectorAll('section.exo-system [role=tablist][aria-label="System"] [role=tab]')].find((b) => b.textContent.trim() === n)
  if (!await sh.click(tab, name)) return null
  return sh.waitFor((n) => [...document.querySelectorAll('section.exo-system [role=tab]')].find((b) => b.textContent.trim() === n)?.getAttribute('aria-selected') === 'true', { timeoutMs: 3000, args: [name] })
}
const isCurrent = (name) => [...document.querySelectorAll('nav[aria-label="Rooms"] button')].find((b) => b.textContent.trim().startsWith(name))?.getAttribute('aria-current') === 'page'
const STORE_PAGE = () => {
  const p = document.querySelector('[role=dialog] .exo-sgp')
  if (!p) return null
  return {
    title: p.querySelector('#exo-sgp-title')?.textContent.trim() ?? null,
    media: !!p.querySelector('.exo-sgp-media:not(.is-skeleton) .exo-sgp-stage :is(img,video)'),
    skeleton: !!p.querySelector('.exo-sgp-media.is-skeleton'),
    thumbs: p.querySelectorAll('.exo-sgp-media:not(.is-skeleton) .exo-sgp-thumb').length,
    specs: p.querySelectorAll('.exo-sgp-spec-row:not(.is-head)').length,
    offers: [...p.querySelectorAll('.exo-sgp-offer:not(.is-loading)')].map((o) => ({ name: o.querySelector('.exo-sgp-offer-name')?.textContent.trim(), price: o.querySelector('.exo-store-now')?.textContent.trim() ?? null })),
    facts: [...p.querySelectorAll('.exo-sgp-facts dt')].map((d) => d.textContent.trim()),
    tools: [...p.querySelectorAll('.exo-sgp-tools button')].map((b) => b.textContent.trim()),
    error: p.querySelector('.exo-sgp-main .exo-store-error')?.innerText.trim() ?? null,
    retry: !![...p.querySelectorAll('.exo-sgp-main .exo-store-error button')].find((b) => b.textContent.trim() === 'Retry'),
    // Every block placed inside the page with none over another and nothing scrolling; the requirements judged against this PC.
    layout: (() => {
      const page = p.getBoundingClientRect()
      const body = p.querySelector('.exo-sgp-body')
      const blocks = [...p.querySelectorAll('.exo-sgp-body > [data-block]')].filter((el) => getComputedStyle(el).display !== 'none').map((el) => ({ name: el.dataset.block, r: el.getBoundingClientRect() }))
      const inside = blocks.every(({ r }) => r.left >= page.left - 1 && r.right <= page.right + 1 && r.top >= page.top - 1 && r.bottom <= page.bottom + 1)
      const overlaps = blocks.flatMap((a, i) => blocks.slice(i + 1).filter((b) => a.r.left < b.r.right - 1 && b.r.left < a.r.right - 1 && a.r.top < b.r.bottom - 1 && b.r.top < a.r.bottom - 1).map((b) => `${a.name}/${b.name}`))
      const specs = p.querySelector('.exo-sgp-body > .exo-sgp-specs')
      return {
        blocks: blocks.map((b) => b.name), inside, overlaps, scrolls: !!body && body.scrollHeight > body.clientHeight + 1,
        verdict: specs?.querySelector('.exo-sgp-verdict')?.textContent.trim() ?? null,
        mine: [...(specs?.querySelectorAll('.exo-sgp-spec-row:not(.is-head) .exo-sgp-spec-mine') ?? [])].filter((c) => c.textContent.trim()).length,
      }
    })(),
  }
}
const money = (cents, currency) => cents === 0 ? 'Free' : new Intl.NumberFormat(undefined, { style: 'currency', currency: currency || 'USD' }).format(cents / 100)

/**
 * The Store room on the real stores: Discover's banner and named shelves, search
 * (no banner skeleton while it answers), a game page (media, specs, offers with
 * prices, Escape), Retry after a failed page (the failure is simulated by holding
 * the page's store.game request), the wishlist round trip (put back as it was),
 * the Store tab's sale count, In your library, offers going through store.open,
 * and prices in the Windows region's currency. --arg is that currency (ISO code).
 */
async function store(sh, currency) {
  const library = await sh.eval(hostCall('library.get')).catch(() => null)
  const ownedSteam = new Set((library?.games ?? []).filter((g) => g.owned || g.installed).flatMap((g) => [g.id, ...(g.variants ?? []).map((v) => v.id)]).map((id) => /^steam:(\d+)$/.exec(id)?.[1]).filter(Boolean))
  const wishBefore = await sh.eval(hostCall('store.wishlist', { action: 'get' })).catch(() => null)
  await sh.eval('window.__exoStorePage = ' + STORE_PAGE.toString())
  const closePage = async () => {
    await sh.key('Escape')
    return sh.waitFor(() => !document.querySelector('[role=dialog] .exo-sgp'), { timeoutMs: 3000 })
  }
  const openCard = async (finder, ...args) => {
    const label = await sh.click(finder, ...args)
    if (!label) return null
    return sh.waitFor(() => { const p = document.querySelector('[role=dialog] .exo-sgp'); return p ? true : null }, { timeoutMs: 5000 }) && label
  }

  await check('store.home', 'Store Discover: the banner and named shelves load with games', async (c) => {
    if (!await sh.click(navButton, 'Store')) return ['no Store room button']
    const home = await sh.waitFor(() => {
      const m = document.querySelector('main.exo-store')
      if (!m || m.querySelector('.exo-store-hero.is-skeleton')) return null
      // A room switch lands a frame later inside its transition; the Store stays mounted meanwhile.
      if (![...document.querySelectorAll('nav[aria-label="Rooms"] button')].some((b) => b.textContent.trim().startsWith('Store') && b.getAttribute('aria-current') === 'page')) return null
      const rows = [...m.querySelectorAll('section.exo-store-row')].map((s) => ({ title: s.querySelector('h2')?.textContent.trim() ?? '', cards: s.querySelectorAll('.exo-store-card, .exo-store-free-card').length }))
      const hero = m.querySelector('.exo-store-hero')
      if (!hero && !rows.length && !m.querySelector('[role=alert]')) return null
      return { current: [...document.querySelectorAll('nav[aria-label="Rooms"] button')].find((b) => b.textContent.trim().startsWith('Store'))?.getAttribute('aria-current') === 'page', hero: !!hero, heroPicks: m.querySelectorAll('.exo-store-hero-pick').length, heroName: m.querySelector('.exo-store-hero-stage')?.getAttribute('aria-label') ?? null, rows, alert: m.querySelector('[role=alert]')?.textContent.trim() ?? null }
    }, { timeoutMs: 45000, interval: 250 })
    await c.shot(sh)
    if (!home) return ['Discover did not load within 45 s']
    Object.assign(c.evidence, home)
    const f = []
    if (!home.current) f.push('the Store tab is not current')
    if (home.alert) f.push(`the store says "${home.alert}"`)
    if (!home.hero || home.heroPicks < 2) f.push(`banner: ${home.hero ? home.heroPicks + ' picks' : 'missing'}`)
    if (home.rows.length < 3) f.push(`${home.rows.length} shelves`)
    for (const r of home.rows) if (!r.title || r.cards === 0) f.push(`shelf "${r.title}" has ${r.cards} games`)
    return f
  })

  await check('store.search', 'Search answers as you type with results, never the banner skeleton, and keeps them while the query grows', async (c) => {
    await sh.eval(`(() => { const r = window.__e2eSearch = { heroSkeleton: 0, gridSkeleton: 0, frames: 0, on: true }; const tick = () => { if (!r.on) return; r.frames++; if ((document.querySelector('main.exo-store input[type=search]')?.value.trim().length ?? 0) >= 2 && document.querySelector('main.exo-store .exo-store-hero')) r.heroSkeleton++; requestAnimationFrame(tick) }; requestAnimationFrame(tick); return true })()`)
    if (!await sh.click(() => document.querySelector('main.exo-store input[type=search]'))) return ['no search field']
    await sh.type('portal')
    const first = await sh.waitFor(() => { const g = document.querySelector('main.exo-store .exo-store-grid'); const cards = g ? [...g.querySelectorAll('.exo-store-card:not(.is-skeleton)')] : []; return cards.length ? cards.map((x) => x.getAttribute('aria-label')).slice(0, 8) : null }, { timeoutMs: 15000 })
    // A longer query: the first answer stays up while the next one is on its way.
    await sh.type(' 2')
    let emptied = 0
    for (let i = 0; i < 15; i++) { if (!(await sh.eval(`document.querySelectorAll('main.exo-store .exo-store-grid .exo-store-card:not(.is-skeleton)').length`))) emptied++; await sleep(50) }
    const second = await sh.waitFor(() => { const cards = [...document.querySelectorAll('main.exo-store .exo-store-grid .exo-store-card:not(.is-skeleton)')]; return cards.length && /portal 2/i.test(cards.map((x) => x.getAttribute('aria-label')).join('|')) ? cards.map((x) => x.getAttribute('aria-label')).slice(0, 8) : null }, { timeoutMs: 15000 })
    await c.shot(sh)
    const frames = await sh.eval(`(() => { const r = window.__e2eSearch; r.on = false; return r })()`)
    await sh.click(() => document.querySelector('main.exo-store button[aria-label="Clear search"]'))
    await sh.waitFor(() => !document.querySelector('main.exo-store .exo-store-grid'), { timeoutMs: 3000 })
    Object.assign(c.evidence, { first, second, framesWatched: frames.frames, framesWithBanner: frames.heroSkeleton, samplesWithNoResults: emptied })
    const f = []
    if (!first) f.push('"portal" returned no results within 15 s')
    if (!second) f.push('"portal 2" returned no Portal 2 within 15 s')
    if (frames.heroSkeleton) f.push(`the banner (or its skeleton) showed in ${frames.heroSkeleton} frames while searching`)
    if (emptied) f.push(`results disappeared in ${emptied} of 15 samples while the longer query was on its way`)
    return f
  })

  // A not-owned, not-wishlisted game from a shelf: the page, the round trips and the offers use it.
  const pick = await sh.eval(`(() => { const out = []; for (const s of document.querySelectorAll('main.exo-store section.exo-store-row')) for (const b of s.querySelectorAll('.exo-store-card')) { const l = b.getAttribute('aria-label') ?? ''; if (!/in your library/.test(l) && !b.querySelector('.exo-store-badge') && b.querySelector('.exo-store-now') && !/Free/.test(b.querySelector('.exo-store-now').textContent)) out.push(l) } return out.slice(0, 3) })()`)
  const cardNamed = (label) => [...document.querySelectorAll('main.exo-store section.exo-store-row .exo-store-card')].find((b) => b.getAttribute('aria-label') === label)

  await check('store.page', 'A game page opens with media, specs, offers and prices; Escape closes it', async (c) => {
    if (!pick.length) c.skip('no priced, unowned game on the shelves')
    c.evidence.game = pick[0]
    if (!await openCard(cardNamed, pick[0])) return ['the card did not open a page']
    const page = await sh.waitFor(() => { const p = window.__exoStorePage(); return p && (p.media || p.error) && p.offers.length ? p : null }, { timeoutMs: 20000, interval: 250, args: [] }) ?? await sh.call(STORE_PAGE)
    await sleep(800)
    await c.shot(sh)
    Object.assign(c.evidence, page ?? {})
    const f = []
    if (!page) f.push('no page')
    else {
      if (page.error) f.push(`the page says "${page.error}"`)
      if (!page.media) f.push('no trailer or screenshot on the stage')
      if (page.thumbs < 2) f.push(`${page.thumbs} media thumbnails`)
      if (!page.specs) f.push('no system requirements')
      if (!page.offers.length) f.push('no offers')
      if (!page.offers.some((o) => /^Get on Steam|^Wishlist on Steam/.test(o.name ?? ''))) f.push('no Steam offer')
      if (page.offers[0] && !page.offers[0].price && !/^Wishlist/.test(page.offers[0].name)) f.push('the Steam offer shows no price')
      if (!page.facts.length) f.push('no details (reviews, release, developer)')
    }
    if (!await closePage()) f.push('Escape did not close the page')
    return f
  })

  await check('store.page-requirements', 'A game page fits its window with every block in place and nothing scrolling, and its system requirements are judged against this PC', async (c) => {
    if (!pick.length) c.skip('no priced, unowned game on the shelves')
    c.evidence.game = pick[0]
    if (!await openCard(cardNamed, pick[0])) return ['the card did not open a page']
    const page = await sh.waitFor(() => { const p = window.__exoStorePage(); return p && p.specs && p.offers.length ? p : null }, { timeoutMs: 20000, interval: 250 })
    await sleep(400)
    const settled = page ? await sh.call(STORE_PAGE) : null
    await c.shot(sh)
    Object.assign(c.evidence, settled ? { specs: settled.specs, ...settled.layout } : { page: null })
    const f = []
    if (!settled) f.push('the page showed no system requirements within 20 s')
    else {
      const l = settled.layout
      for (const b of ['media', 'head', 'buy', 'specs']) if (!l.blocks.includes(b)) f.push(`no ${b} block on the page`)
      if (!l.inside) f.push('a block reaches outside the page')
      if (l.overlaps.length) f.push(`blocks overlap: ${l.overlaps.join(', ')}`)
      if (l.scrolls) f.push('the page scrolls')
      if (!l.mine) f.push('no requirement shows this PC\'s part')
    }
    if (!await closePage()) f.push('Escape did not close the page')
    return f
  })

  // A page that never loaded says why, with Retry. The page is warmed while its card is on screen, so
  // the failure is made on a search result: store.game is held from before the search, so neither the
  // warm-up nor the page itself gets an answer; released, Retry asks again and the page loads.
  await check('store.retry', 'A game page that fails to load says so with Retry, and Retry loads it', async (c) => {
    const clearSearch = async () => {
      if (await sh.eval("!!document.querySelector('[role=dialog] .exo-sgp')")) await closePage()
      await sh.click(() => document.querySelector('main.exo-store button[aria-label="Clear search"]'))
      await sh.waitFor(() => !document.querySelector('main.exo-store .exo-store-grid'), { timeoutMs: 3000 })
    }
    const shelfNames = new Set(await sh.eval(`[...document.querySelectorAll('main.exo-store .exo-store-card')].map(b => b.getAttribute('aria-label'))`))
    const f = []
    let failed = null
    let name = null
    await sh.eval(holdHost(['store.game']))
    try {
      if (!await sh.click(() => document.querySelector('main.exo-store input[type=search]'))) return ['no search field']
      await sh.type('terraria')
      const results = await sh.waitFor(() => { const cards = [...document.querySelectorAll('main.exo-store .exo-store-grid .exo-store-card:not(.is-skeleton)')]; return cards.length ? cards.map((x) => x.getAttribute('aria-label')) : null }, { timeoutMs: 15000 })
      name = (results ?? []).find((l) => !shelfNames.has(l)) ?? null
      c.evidence.game = name
      if (!name) return ['the search "terraria" found no game that is not on a shelf']
      if (!await openCard((l) => [...document.querySelectorAll('main.exo-store .exo-store-grid .exo-store-card')].find((b) => b.getAttribute('aria-label') === l), name)) return ['the result did not open a page']
      failed = await sh.waitFor(() => { const p = window.__exoStorePage(); return p?.error ? p : null }, { timeoutMs: 8000 })
      c.evidence.held = await sh.eval('window.__exoHold?.calls.length ?? 0')
      await c.shot(sh, 'error')
    } finally {
      await sh.eval(releaseHost)
      if (!failed) await clearSearch()
    }
    if (!failed) f.push('a failed store.game showed no error')
    else if (!failed.retry) f.push(`the error "${failed.error}" has no Retry`)
    c.evidence.error = failed?.error ?? null
    if (failed?.retry) {
      await sh.click(() => [...document.querySelectorAll('[role=dialog] .exo-sgp-main .exo-store-error button')].find((b) => b.textContent.trim() === 'Retry'))
      const loaded = await sh.waitFor(() => { const p = window.__exoStorePage(); return p?.media && !p.error ? p : null }, { timeoutMs: 20000, interval: 250 })
      c.evidence.afterRetry = loaded ? { media: loaded.media, offers: loaded.offers.length, specs: loaded.specs } : null
      await c.shot(sh, 'retried')
      if (!loaded) f.push('Retry did not load the page')
    }
    await clearSearch()
    return f
  })

  await check('store.wishlist', 'Wishlist add and remove round trip through the host (the wishlist ends as it was)', async (c) => {
    const name = pick[2] ?? pick[0]
    if (!name || !wishBefore) c.skip(wishBefore ? 'no game on the shelves' : 'store.wishlist did not answer')
    c.evidence.game = name
    const before = wishBefore.items.map((w) => w.appId)
    if (!await openCard(cardNamed, name)) return ['the card did not open a page']
    const wishButton = () => [...document.querySelectorAll('[role=dialog] .exo-sgp-tools button')].find((b) => b.hasAttribute('aria-pressed'))
    const f = []
    try {
      await sh.waitFor(() => window.__exoStorePage()?.offers.length ? true : null, { timeoutMs: 15000 })
      const pressed0 = await sh.eval(`[...document.querySelectorAll('[role=dialog] .exo-sgp-tools button')].find(b => b.hasAttribute('aria-pressed'))?.getAttribute('aria-pressed') ?? null`)
      if (pressed0 !== 'false') { f.push(`the Wishlist button is ${pressed0}`); return f }
      await sh.click(wishButton)
      const added = await poll(async () => { const w = await sh.eval(hostCall('store.wishlist', { action: 'get' })); return w.items.length === before.length + 1 ? w.items : null }, 5000, 250)
      const pressed1 = await sh.eval(`[...document.querySelectorAll('[role=dialog] .exo-sgp-tools button')].find(b => b.hasAttribute('aria-pressed'))?.getAttribute('aria-pressed') ?? null`)
      await c.shot(sh, 'added')
      c.evidence.alerts = await sh.eval("[...document.querySelectorAll('main.exo-store [role=alert], [role=dialog] [role=alert]')].map(a => a.textContent.trim()).filter(Boolean)")
      c.evidence.added = added ? added.find((w) => !before.includes(w.appId)) && { appId: added.find((w) => !before.includes(w.appId)).appId, name: added.find((w) => !before.includes(w.appId)).name } : null
      if (!added) f.push('the host wishlist did not gain the game')
      if (pressed1 !== 'true') f.push('the button did not say On wishlist')
      await sh.click(wishButton)
      const removed = await poll(async () => { const w = await sh.eval(hostCall('store.wishlist', { action: 'get' })); return w.items.length === before.length ? w.items : null }, 5000, 250)
      c.evidence.after = removed?.map((w) => w.appId) ?? null
      if (!removed) f.push('the host wishlist did not lose the game again')
      else if (removed.map((w) => w.appId).join() !== before.join()) f.push('the wishlist did not end as it started')
    } finally {
      // Whatever happened, the wishlist ends as it was.
      const now = await sh.eval(hostCall('store.wishlist', { action: 'get' })).catch(() => null)
      for (const w of now?.items ?? []) if (!before.includes(w.appId)) await sh.eval(hostCall('store.wishlist', { action: 'remove', appId: w.appId, name: w.name })).catch(() => null)
      await closePage()
    }
    return f
  })

  await check('store.sale-badge', 'The Store tab shows the count of wishlisted games on sale (none when there are none)', async (c) => {
    const w = await sh.eval(hostCall('store.wishlist', { action: 'get' }))
    // The page's titleIdentity: accents and symbols (™ included) dropped, letters and digits kept.
    const key = (t) => t.normalize('NFD').toLowerCase().replace(/\p{Mark}/gu, '').replace(/[^\p{L}\p{N}]+/gu, '')
    const titles = new Set((library?.games ?? []).filter((g) => g.owned || g.installed).map((g) => key(g.title)))
    const expected = w.items.filter((i) => i.discount > 0 && !ownedSteam.has(i.appId) && !titles.has(key(i.name))).length
    const badge = await sh.eval(`(() => { const b = [...document.querySelectorAll('nav[aria-label="Rooms"] button')].find(b => b.textContent.trim().startsWith('Store')); const n = b?.querySelector('.exo-discord-notification-count'); return n ? { text: n.textContent.trim(), label: n.getAttribute('aria-label') } : null })()`)
    const chip = await sh.eval(`document.querySelector('main.exo-store .exo-store-sale')?.textContent.trim() ?? null`)
    Object.assign(c.evidence, { wishlist: w.items.length, onSale: w.items.filter((i) => i.discount > 0).length, expectedCount: expected, badge, wishlistChip: chip })
    const f = []
    if (expected === 0 && badge) f.push(`no wishlisted game is on sale but the tab shows ${badge.text}`)
    if (expected > 0 && (!badge || badge.text !== (expected > 99 ? '99+' : String(expected)))) f.push(`${expected} on sale but the tab shows ${badge?.text ?? 'nothing'}`)
    if (expected > 0 && chip !== `${expected} on sale`) f.push(`the Wishlist chip says ${chip}`)
    return f
  })

  await check('store.offers-open', "Offers open through store.open (held, nothing opens); store.open refuses a non-store page", async (c) => {
    if (!pick.length) c.skip('no game on the shelves')
    if (!await openCard(cardNamed, pick[0])) return ['the card did not open a page']
    const f = []
    await sh.waitFor(() => window.__exoStorePage()?.offers.length ? true : null, { timeoutMs: 15000 })
    await sh.eval(holdHost(['store.open', 'shell.openUrl']))
    try {
      await sh.click(() => document.querySelector('[role=dialog] .exo-sgp-offer.is-primary'))
      await sleep(400)
      c.evidence.calls = await sh.eval('window.__exoHold?.calls ?? []')
    } finally { await sh.eval(releaseHost) }
    const call = c.evidence.calls.find((x) => x.method === 'store.open')
    if (!call) f.push('the Get offer made no store.open call' + (c.evidence.calls.length ? ` (made ${c.evidence.calls.map((x) => x.method).join(', ')})` : ''))
    else if (!/^https:\/\/store\.steampowered\.com\//.test(call.params?.url ?? '')) f.push(`store.open went to ${call.params?.url}`)
    c.evidence.nonStore = await sh.eval(hostCall('store.open', { url: 'https://example.com/' })).catch((e) => ({ error: e.message }))
    if (c.evidence.nonStore?.ok !== false) f.push('store.open did not refuse https://example.com/')
    await closePage()
    return f
  })

  await check('store.in-library', 'In your library on an owned game opens that game in the library', async (c) => {
    const owned = await sh.eval(`[...document.querySelectorAll('main.exo-store section.exo-store-row .exo-store-card')].map(b => b.getAttribute('aria-label')).filter(l => /, in your library(,|$)/.test(l))`)
    if (!owned.length) c.skip('no owned game on the Store shelves')
    const name = owned[0]
    c.evidence.card = name
    if (!await openCard(cardNamed, name)) return ['the card did not open a page']
    const button = await sh.waitFor(() => !![...document.querySelectorAll('[role=dialog] .exo-sgp-tools button')].find((b) => b.textContent.trim() === 'In your library'), { timeoutMs: 15000 })
    if (!button) { await closePage(); return ['no In your library button on an owned game'] }
    await sh.click(() => [...document.querySelectorAll('[role=dialog] .exo-sgp-tools button')].find((b) => b.textContent.trim() === 'In your library'))
    const sheet = await sh.waitFor(() => {
      const lib = [...document.querySelectorAll('nav[aria-label="Rooms"] button')].find((b) => b.textContent.trim().startsWith('Library'))?.getAttribute('aria-current') === 'page'
      const d = document.querySelector('[role=dialog][aria-modal=true]:not(:has(.exo-sgp)):not([hidden] *)')
      return lib && d ? { library: lib, title: d.getAttribute('aria-label') } : null
    }, { timeoutMs: 5000 })
    await c.shot(sh)
    Object.assign(c.evidence, sheet ?? {})
    const f = []
    const want = name.replace(/, in your library(,.*)?$/, '') // the label is name, in your library, review score
    const norm = (t) => (t ?? '').normalize('NFKC').toLowerCase().replace(/[™®©]/g, '').replace(/[^\p{L}\p{N}]+/gu, '')
    if (!sheet) f.push('Library did not open on a game sheet')
    else if (norm(sheet.title) !== norm(want)) f.push(`opened "${sheet.title}" for "${want}"`)
    await sh.key('Escape')
    await sh.waitFor(() => !document.querySelector('[role=dialog][aria-modal=true]:not([hidden] *)'), { timeoutMs: 3000 })
    return f
  })

  await check('store.currency', "Store prices are in the Windows region's currency and formatted for it", async (c) => {
    const home = await sh.eval(hostCall('store.home', {}, 30000))
    const tiles = [...(home.hero ?? []), ...(home.shelves ?? []).flatMap((s) => s.items)].filter((t) => t.final != null && t.final > 0)
    const currencies = [...new Set(tiles.map((t) => t.currency || 'USD'))]
    Object.assign(c.evidence, { expected: currency, currencies, pricedTiles: tiles.length })
    const f = []
    if (!tiles.length) c.skip('no priced games on the shelves')
    if (currency && currencies.some((x) => x !== currency)) f.push(`prices in ${currencies.join(', ')}, the region's currency is ${currency}`)
    // The cards print what the host sent, formatted for that currency.
    if (!await sh.click(navButton, 'Store')) return ['no Store room button']
    const shown = await sh.eval(`[...document.querySelectorAll('main.exo-store section.exo-store-row .exo-store-card')].slice(0, 60).map(b => ({ name: b.getAttribute('aria-label').replace(/, in your library$/, ''), now: b.querySelector('.exo-store-now')?.textContent.trim() ?? null }))`)
    const byName = new Map(tiles.map((t) => [t.name, t]))
    const wrong = []
    for (const s of shown) { const t = byName.get(s.name); if (t && s.now && s.now !== money(t.final, t.currency)) wrong.push(`${s.name}: ${s.now} (expected ${money(t.final, t.currency)})`) }
    c.evidence.sample = shown.slice(0, 6)
    if (wrong.length) f.push(...wrong.slice(0, 5))
    return f
  })
  await sh.click(navButton, 'Library')
}

// ---------- main ----------
const sh = await openShell(port, Number(opt('timeout', '60000')))
try {
  if (suite === 'startup') await startup(sh)
  else if (suite === 'library') await library(sh, opt('cache'))
  else if (suite === 'covers') await covers(sh, opt('arg'), opt('cache'))
  else if (suite === 'settings') await settings(sh, opt('settings'))
  else if (suite === 'room') await room(sh, opt('arg'))
  else if (suite === 'devices') await devices(sh)
  else if (suite === 'home') await home(sh)
  else if (suite === 'minimize') await minimize(sh)
  else if (suite === 'chrome') await chrome(sh)
  else if (suite === 'volume') await volume(sh, opt('arg'))
  else if (suite === 'spotify' || suite === 'apple') await nativeMusic(sh, suite)
  else if (suite === 'miniplayer') { await miniVolume(sh, opt('arg')); await miniplayer(sh, opt('arg')) }
  else if (suite === 'upscalers') await upscalers(sh, opt('settings'))
  else if (suite === 'shaders') { await gpuProfile(sh); await shaders(sh) }
  else if (suite === 'onboarding') await onboarding(sh)
  else if (suite === 'store') await store(sh, opt('arg'))
  else if (suite === 'libraryplus') await libraryPlus(sh, opt('settings'))
  else if (suite === 'upscalercycle') await upscalerCycle(sh)
  else if (suite === 'runstate') await runState(sh)
  else if (suite === 'realgame') await realGame(sh, opt('arg'))
  else if (suite === 'activity') Object.assign(data, await sh.call(() => ({ inactive: document.documentElement.dataset.exoInactive === 'true', hidden: document.hidden })))
  else throw new Error('unknown suite ' + suite)
} finally {
  sh.close()
}
// Exit once the result is written: a page connection left open (a frozen app's) must not keep the driver alive.
process.stdout.write(JSON.stringify({ suite, checks, data }), () => process.exit(0))
