// Stress: what an impatient person does (rooms clicked faster than they can open, sheets and menus and Ctrl+K open
// and shut over and over, the window resized again and again, the theme flipped), then Exo must be whole: in the
// last room asked for, nothing stuck half way, no errors, and no memory left behind.
import { sleep } from '../cdp.mjs'
import { layoutScan } from '../lib/page.mjs'
import { ROOMS, THEMES } from '../lib/kit.mjs'
import { screenMatchesPage } from './shell.mjs'

async function whole(t, c, label, room) {
  await t.k.settle(300, 8000)
  const s = await t.k.state()
  const failures = []
  if (room && s.room !== room) failures.push(`${label}: ended in ${s.room}, not ${room}`)
  if (s.vt) failures.push(`${label}: a transition is stuck`)
  if (s.errorScreen) failures.push(`${label}: an error screen shows`)
  return failures
}

export async function run(t) {
  const { run, k } = t
  const n = t.quick ? 0.5 : 1
  await k.nav('Library')
  const gc = () => t.shell.s.send('HeapProfiler.collectGarbage').catch(() => {})
  await gc()
  const before = await k.metrics()
  const mark = t.console.length

  await run.check('stress', 'rooms', 'rooms clicked faster than they open end in the last one asked for', async (c) => {
    const tabs = await t.shell.eval(`Object.fromEntries([...document.querySelectorAll('.exo-titlebar button')].map(b => [(b.getAttribute('aria-label') || b.textContent || '').trim().replace(/\\s*\\d+$/, ''), (() => { const r = b.getBoundingClientRect(); return { x: r.left + r.width / 2, y: r.top + r.height / 2 } })()]))`)
    const order = Array.from({ length: Math.round(120 * n) }, (_, i) => ROOMS[(i * 7 + 3) % ROOMS.length])
    for (const room of order) { await t.shell.clickAt(tabs[room].x, tabs[room].y); await sleep(25) }
    return whole(t, c, 'rooms', order[order.length - 1])
  })

  await run.check('stress', 'sheets', 'a game\'s sheet opened and shut 25 times leaves the Library whole', async (c) => {
    await k.nav('Library')
    for (let i = 0; i < Math.round(25 * n); i++) {
      await t.shell.click((j) => document.querySelectorAll('.exo-game-grid button[data-game-id]')[j % 8], i)
      await sleep(120)
      await t.shell.key('Escape')
      await sleep(80)
    }
    await sleep(800)
    const open = await t.shell.eval(`!!document.querySelector('.exo-game-overlay-stage')?.checkVisibility()`)
    return [...await whole(t, c, 'sheets', 'Library'), open && 'a sheet stayed open'].filter(Boolean)
  })

  await run.check('stress', 'search-and-menus', 'Ctrl+K and menus opened and shut 25 times each', async (c) => {
    for (let i = 0; i < Math.round(25 * n); i++) {
      await t.shell.key('k', { ctrl: true }); await sleep(60)
      await t.shell.type(String.fromCharCode(97 + (i % 26))); await sleep(40)
      await t.shell.key('Escape'); await sleep(60)
    }
    for (let i = 0; i < Math.round(25 * n); i++) {
      await t.shell.click(() => document.querySelector('.exo-library-add')); await sleep(60)
      await t.shell.key('Escape'); await sleep(60)
    }
    await sleep(600)
    const s = await k.state()
    return [...await whole(t, c, 'search', 'Library'), s.dialogs && 'a dialog stayed open', s.menus && 'a menu stayed open'].filter(Boolean)
  })

  await run.check('stress', 'resize', 'the window resized over and over keeps its layout and fills the screen', async (c) => {
    const w0 = await t.win.window()
    const m = w0.monitor.work
    const sizes = [[0.5, 0.6], [0.7, 0.7], [0.45, 0.9], [0.9, 0.5], [0.6, 0.8], [1, 1], [0.55, 0.55], [0.8, 0.95]]
    const failures = []
    for (const [fw, fh] of sizes) {
      await t.win.place(m.x + 20, m.y + 20, Math.round(m.w * fw) - 40, Math.round(m.h * fh) - 40)
      await sleep(500)
      await k.settle()
      const layout = await t.shell.call(layoutScan)
      if (layout.sideScroll) failures.push(`${Math.round(fw * 100)}x${Math.round(fh * 100)}%: the page scrolls sideways`)
      if (layout.offscreen.length) failures.push(`${Math.round(fw * 100)}x${Math.round(fh * 100)}%: cut off: ${layout.offscreen.slice(0, 2).join(' | ')}`)
    }
    failures.push(...await screenMatchesPage(t, c, 'resized'))
    await t.win.maximize()
    await sleep(800)
    failures.push(...await screenMatchesPage(t, c, 'maximized'))
    return failures
  })

  await run.check('stress', 'themes', 'the Appearance flipped nine times ends where it was asked to', async (c) => {
    const was = (await t.host('settings.get')).appearance ?? 'system'
    for (let i = 0; i < 9; i++) await k.setAppearance(THEMES[i % 3])
    const now = await t.shell.eval('document.documentElement.dataset.exoTheme')
    await k.setAppearance(was)
    return now !== THEMES[8 % 3] ? [`ended in ${now}`] : []
  })

  await run.check('stress', 'aftermath', 'after all of it: no errors, no memory left behind', async (c) => {
    await k.nav('Library')
    await gc(); await sleep(800)
    const after = await k.metrics()
    const heap = (after.JSHeapUsedSize - before.JSHeapUsedSize) / 1048576
    c.metric('heap.growth', heap, ' MB', 25)
    c.metric('nodes.growth', after.Nodes - before.Nodes, '', Math.max(3000, before.Nodes * 0.15))
    c.metric('listeners.growth', after.JSEventListeners - before.JSEventListeners, '', 500)
    const errors = k.consoleSince(mark).filter(e => e.level !== 'warning' && !/Failed to load resource/.test(e.text))
    c.evidence.errors = errors.slice(0, 8)
    return errors.length ? [`${errors.length} console errors: ${errors.slice(0, 2).map(e => e.text.slice(0, 140)).join(' | ')}`] : []
  })
}
