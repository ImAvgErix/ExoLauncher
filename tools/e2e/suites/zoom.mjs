// Big screens and other monitors, the way 3.13.3 broke: Exo restarted as if on a 1440p or 4K screen (EXO_PAGE_SCREEN),
// each time checked on the screen itself (the whole page shows), clicked for real (the tabs are where they're drawn),
// resized, and moved to each monitor. Restarts Exo; runs last.
import { sleep } from '../cdp.mjs'
import { screenMatchesPage } from './shell.mjs'
import { roomsLayout } from './layout.mjs'

/** A real mouse click on a page element: its place in the page, times the zoom, from the window's client corner. */
async function realClick(t, finder, ...args) {
  const r = await t.shell.call((f, a) => null, null) // keeps the page session warm
  const box = await t.shell.eval(`(() => { const el = (${finder.toString()})(...${JSON.stringify(args)}); if (!el) return null; const r = el.getBoundingClientRect(); return { x: r.left + r.width / 2, y: r.top + r.height / 2, vw: innerWidth } })()`)
  if (!box) return false
  const w = await t.win.window()
  const zoom = w.client.w / box.vw
  await t.win.click(w.client.x + box.x * zoom, w.client.y + box.y * zoom)
  return true
}

const tab = (name) => [...document.querySelectorAll('.exo-titlebar button')].find(b => (b.getAttribute('aria-label') || b.textContent || '').trim().replace(/\s*\d+$/, '') === name)

export async function run(t) {
  const { run, k } = t
  const screens = t.quick ? [2160] : [1440, 2160]
  try {
    for (const screen of screens) {
      await run.check('zoom', `screen-${screen}`, `as on a ${screen}-pixel-tall screen: the whole page shows and clicks land`, async (c) => {
        await t.relaunch({ EXO_PAGE_SCREEN: String(screen) })
        await t.win.maximize().catch(() => {})
        await sleep(800)
        const lines = await t.exo.logSince(t.logMark)
        const scale = lines.map(l => /Page scale: ([\d.]+)/.exec(l.text)?.[1]).filter(Boolean).pop()
        c.evidence.scale = scale
        const w = await t.win.window()
        const failures = []
        const expected = screen / 1080 * (w.monitor.h / w.monitor.scale / 1080 > 0 ? 1 : 1)
        if (!scale) failures.push('Exo did not zoom the page')
        failures.push(...await screenMatchesPage(t, c, `zoom-${screen}`))
        // Real clicks where the tabs are drawn.
        await realClick(t, tab, 'Store')
        if (!await t.shell.waitFor(() => document.querySelector('.exo-titlebar [aria-current="page"]')?.textContent.trim().startsWith('Store'), { timeoutMs: 4000 })) failures.push('a real click on Store missed')
        await realClick(t, tab, 'Library')
        if (!await t.shell.waitFor(() => document.querySelector('.exo-titlebar [aria-current="page"]')?.textContent.trim().startsWith('Library'), { timeoutMs: 4000 })) failures.push('a real click on Library missed')
        // Every room laid out at this screen's zoom: nothing over, cut or clipped.
        failures.push(...await roomsLayout(t, c, `zoom-${screen}`, async (room) => {
          await t.k.closeAll()
          await realClick(t, tab, room)
          if (!await t.shell.waitFor((n) => document.querySelector('.exo-titlebar [aria-current="page"]')?.textContent.trim().startsWith(n), { timeoutMs: 6000, args: [room] })) throw new Error(`a real click on ${room} missed`)
        }))
        // Restored to a smaller window and back.
        const m = w.monitor.work
        await t.win.place(m.x + 60, m.y + 60, Math.round(m.w * 0.66), Math.round(m.h * 0.7))
        await sleep(900)
        failures.push(...await screenMatchesPage(t, c, `zoom-${screen}-window`))
        await t.win.maximize()
        await sleep(800)
        c.evidence.expectedZoom = +expected.toFixed(3)
        return failures
      })
    }
  } finally {
    await t.relaunch({})
    await t.win.maximize().catch(() => {})
  }

  await run.check('zoom', 'monitors', 'on every monitor the window fills it and the whole page shows', async (c) => {
    const monitors = await t.win.monitors()
    c.evidence.monitors = monitors.map(m => `${m.w}x${m.h} @${m.scale}${m.primary ? ' primary' : ''}`)
    if (monitors.length < 2) c.skip('one monitor')
    const home = (await t.win.window()).monitor
    const failures = []
    for (const m of monitors) {
      await t.win.place(m.work.x + 80, m.work.y + 80, Math.round(m.work.w * 0.6), Math.round(m.work.h * 0.6))
      await sleep(600)
      await t.win.maximize()
      await sleep(900)
      const w = await t.win.window()
      if (w.monitor.device !== m.device) failures.push(`the window did not move to ${m.device}`)
      failures.push(...await screenMatchesPage(t, c, m.device.replace(/\W+/g, '')))
    }
    // Back where it was.
    await t.win.place(home.work.x + 80, home.work.y + 80, Math.round(home.work.w * 0.6), Math.round(home.work.h * 0.6))
    await sleep(500)
    await t.win.maximize()
    return failures
  })
}
