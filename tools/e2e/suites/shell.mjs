// The window and the Bar: how fast Exo starts, a clean start (log and console), the screen showing the whole page
// (what 3.13.3 got wrong on 4K), the window filling its monitor, the Bar's parts, the window's own buttons.
import { sleep } from '../cdp.mjs'
import { compare, cropFraction, readPng } from '../lib/image.mjs'
import { ROOMS } from '../lib/kit.mjs'

/** The screen under Exo's window against the page's own picture of the same moment. */
export async function screenMatchesPage(t, c, label) {
  const screenPath = c.file(`${label}-screen`), pagePath = c.file(`${label}-page`)
  await t.win.front()
  await sleep(300)
  await t.shell.screenshot(pagePath)
  const info = await t.win.capture(screenPath)
  let screen = await readPng(screenPath), page = await readPng(pagePath)
  // Only the part of the window that is on its monitor can be seen (a window can hang off the screen's edge).
  const m = info.monitor, cl = info.client
  const vx0 = Math.max(cl.x, m.x), vy0 = Math.max(cl.y, m.y), vx1 = Math.min(cl.x + cl.w, m.x + m.w), vy1 = Math.min(cl.y + cl.h, m.y + m.h)
  if (vx1 - vx0 < cl.w - 2 || vy1 - vy0 < cl.h - 2) {
    const f = [(vx0 - cl.x) / cl.w, (vy0 - cl.y) / cl.h, (vx1 - vx0) / cl.w, (vy1 - vy0) / cl.h]
    screen = cropFraction(screen, ...f); page = cropFraction(page, ...f)
    c.evidence[`${label}-offscreen`] = `only ${Math.round(f[2] * 100)}% x ${Math.round(f[3] * 100)}% of the window is on its monitor`
  }
  const d = compare(screen, page)
  c.evidence[label] = { ...d, client: info.client }
  const failures = []
  if (d.right > 30 || d.bottom > 30) failures.push(`${label}: the screen doesn't show the page's ${d.right > d.bottom ? 'right' : 'bottom'} side (edge difference ${Math.max(d.right, d.bottom)})`)
  else if (d.mean > 18) failures.push(`${label}: the screen differs from the page (mean ${d.mean})`)
  return failures
}

export async function run(t) {
  const { run, k } = t

  await run.check('shell', 'startup', 'Exo starts quickly and its page loads', async (c) => {
    const lines = await t.exo.logSince(t.logMark)
    const milestones = Object.fromEntries(lines.filter(l => l.text.includes('startup milestone=')).map(l => {
      const m = /milestone=(\S+) elapsedMs=(\d+)/.exec(l.text); return m ? [m[1], +m[2]] : null
    }).filter(Boolean))
    c.evidence.milestones = milestones
    if (!milestones['webview-visible']) c.skip('this run attached to a running Exo (no start to time)')
    c.metric('window-constructed', milestones['window-constructed'] ?? 0, ' ms', 800)
    c.metric('webview-visible', milestones['webview-visible'], ' ms', 3500)
  })

  await run.check('shell', 'log-clean', 'app.log has no errors since Exo started', async (c) => {
    const lines = await t.exo.logSince(t.logMark)
    const errors = lines.filter(l => /^(ERROR|FATAL|CRASH)$/.test(l.level))
    const warns = lines.filter(l => l.level === 'WARN')
    c.evidence.errors = errors.slice(0, 10).map(l => l.text.slice(0, 200))
    c.evidence.warnings = warns.slice(0, 10).map(l => l.text.slice(0, 200))
    if (warns.length) c.warn(`${warns.length} warnings in app.log: ${warns.slice(0, 2).map(l => l.text.slice(0, 120)).join(' | ')}`)
    return errors.length ? [`${errors.length} errors: ${errors.slice(0, 2).map(l => l.text.slice(0, 160)).join(' | ')}`] : []
  })

  await run.check('shell', 'precompiled', 'the installed build is ReadyToRun (a stale publish once shipped plain IL)', async (c) => {
    const r = await t.win.precompiled()
    c.evidence.checked = r.checked
    return r.plain.map(n => `${n} is not precompiled`)
  })

  await run.check('shell', 'console-clean', 'the page logged no errors since it loaded', async (c) => {
    const all = k.consoleSince(0).filter(e => e.level !== 'warning')
    // Wide art tries Exo's own hero_<id>.jpg first and moves on when it isn't cached (CoverArt.tsx, once per game).
    const probe = (e) => /covers\.exo-launcher\.local\/hero_/.test(e.text)
    const missing = all.filter(e => /Failed to load resource/.test(e.text) && !probe(e))
    const errors = all.filter(e => !missing.includes(e) && !probe(e))
    c.evidence.errors = errors.slice(0, 10)
    c.evidence.missing = [...new Set(missing.map(e => e.text.replace(/^.*?(https?:\/\/\S+).*$/, '$1')))].slice(0, 10)
    if (missing.length) c.warn(`${missing.length} requests for files that aren't there: ${c.evidence.missing.slice(0, 3).join(', ')}`)
    return errors.length ? [`${errors.length}: ${errors.slice(0, 2).map(e => e.text.slice(0, 160)).join(' | ')}`] : []
  })

  await run.check('shell', 'screen', 'the screen shows the whole page, as the page draws it', async (c) => {
    await k.nav('Library')
    return screenMatchesPage(t, c, 'library')
  })

  await run.check('shell', 'window', 'the maximized window fills its monitor', async (c) => {
    const w = await t.win.window()
    c.evidence.window = w
    if (!w.maximized) c.skip('the window is not maximized')
    const work = w.monitor.work
    const off = Math.abs(w.client.x - work.x) + Math.abs(w.client.y - work.y) + Math.abs(w.client.w - work.w) + Math.abs(w.client.h - work.h)
    return off > 4 ? [`client ${w.client.w}x${w.client.h} at ${w.client.x},${w.client.y}; monitor work area ${work.w}x${work.h} at ${work.x},${work.y}`] : []
  })

  await run.check('shell', 'bar', 'the Bar shows every room, search and the window buttons, all inside the window', async (c) => {
    const parts = await t.shell.call((rooms) => {
      const box = (el) => { if (!el) return null; const r = el.getBoundingClientRect(); return r.width && r.right <= innerWidth + 1 && r.bottom <= innerHeight + 1 && r.left >= -1 ? true : 'outside' }
      const tabs = [...document.querySelectorAll('.exo-titlebar button')]
      const out = {}
      for (const room of rooms) out[room] = box(tabs.find(b => (b.getAttribute('aria-label') || b.textContent || '').trim().replace(/\s*\d+$/, '') === room))
      out.search = box(document.querySelector('.exo-bar-search'))
      out.settings = box(document.querySelector('.exo-settings-btn'))
      for (const name of ['Minimize', 'Maximize', 'Restore', 'Close']) out[name] = box(document.querySelector(`.exo-titlebar button[aria-label="${name}"]`))
      return out
    }, ROOMS)
    c.evidence.parts = parts
    const missing = Object.entries(parts).filter(([name, v]) => v !== true && !(name === 'Restore' || name === 'Maximize')).map(([n, v]) => `${n} ${v ?? 'missing'}`)
    if (parts.Restore !== true && parts.Maximize !== true) missing.push('no maximize/restore button')
    return missing.length ? [missing.join(', ')] : []
  })

  await run.check('shell', 'window-buttons', 'the Bar\'s restore, maximize and minimize work for real', async (c) => {
    const before = await t.win.window()
    const toggle = () => t.shell.click(() => document.querySelector('.exo-titlebar button[aria-label="Restore"], .exo-titlebar button[aria-label="Maximize"]'))
    if (!await toggle()) c.skip('no maximize/restore button')
    await sleep(900)
    const mid = await t.win.window()
    await toggle()
    await sleep(900)
    const after = await t.win.window()
    c.evidence.maximized = [before.maximized, mid.maximized, after.maximized]
    const failures = []
    if (mid.maximized === before.maximized) failures.push('restore/maximize did nothing')
    if (after.maximized !== before.maximized) failures.push('the second press did not put the window back')
    await t.shell.click(() => document.querySelector('.exo-titlebar button[aria-label="Minimize"]'))
    await sleep(900)
    const min = await t.win.window()
    if (!min.minimized) failures.push('minimize did nothing')
    await t.win.front()
    await sleep(900)
    const back = await t.win.window()
    if (back.minimized) failures.push('the window did not come back')
    failures.push(...await screenMatchesPage(t, c, 'after-minimize'))
    return failures
  })
}
