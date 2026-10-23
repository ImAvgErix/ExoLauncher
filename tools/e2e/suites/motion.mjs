// Motion: frames timed while Exo moves (a room change, a sheet, a menu, Ctrl+K), against the screen's own rate;
// every animation short; Reduce motion stops them.
import { sleep } from '../cdp.mjs'
import { animations, frameStart, frameStop } from '../lib/page.mjs'
import { ROOMS } from '../lib/kit.mjs'

const median = (a) => { const s = [...a].sort((x, y) => x - y); return s[Math.floor(s.length / 2)] ?? 0 }

/** Records frames while `action` runs and a little after; returns the gaps (ms). */
async function framesDuring(t, action, tailMs = 600) {
  await t.shell.call(frameStart)
  await action()
  await sleep(tailMs)
  return t.shell.call(frameStop)
}

function judge(c, gaps, refresh, label) {
  const late = gaps.filter(g => g > refresh * 1.6).length
  const worst = Math.max(0, ...gaps)
  c.metric(`${label}.frames`, gaps.length)
  c.metric(`${label}.late`, gaps.length ? late / gaps.length * 100 : 0, '%', 10)
  c.metric(`${label}.worst`, worst, ' ms', Math.max(50, refresh * 4))
  return worst > 250 ? [`${label}: a ${Math.round(worst)} ms freeze`] : []
}

export async function run(t) {
  const { run, k } = t
  await k.nav('Library')
  let refresh = 16.7

  await run.check('motion', 'rate', 'the page draws at the screen\'s own rate when idle', async (c) => {
    await t.win.front()
    const gaps = await framesDuring(t, () => sleep(1200), 0)
    refresh = median(gaps) || 16.7
    c.metric('hz', 1000 / refresh, ' Hz')
    c.evidence.sample = gaps.slice(0, 10)
    return gaps.length < 30 ? [`only ${gaps.length} frames in 1.2 s`] : []
  })

  await run.check('motion', 'rooms', 'changing rooms keeps the frames coming', async (c) => {
    const failures = []
    const gaps = []
    const worst = {}
    for (const room of [...ROOMS.slice(1), 'Library']) {
      const g = await framesDuring(t, () => t.shell.click((n) => [...document.querySelectorAll('.exo-titlebar button')].find(b => (b.getAttribute('aria-label') || b.textContent || '').trim().replace(/\s*\d+$/, '') === n), room), 700)
      worst[room] = Math.round(Math.max(0, ...g))
      gaps.push(...g)
      await k.settle()
    }
    c.evidence.worstByRoom = worst
    failures.push(...judge(c, gaps, refresh, 'switch'))
    return failures
  })

  await run.check('motion', 'sheet', 'a game\'s sheet opens and closes smoothly', async (c) => {
    const open = await framesDuring(t, () => t.shell.click(() => document.querySelector('.exo-game-grid button[data-game-id]:not(.exo-folder-tile *)')), 700)
    const close = await framesDuring(t, () => t.shell.key('Escape'), 600)
    return [...judge(c, open, refresh, 'open'), ...judge(c, close, refresh, 'close')]
  })

  await run.check('motion', 'menus', 'menus and Ctrl+K open and close smoothly', async (c) => {
    const menu = await framesDuring(t, () => t.shell.click(() => document.querySelector('.exo-library-add')), 400)
    await t.shell.key('Escape')
    await sleep(300)
    const palette = await framesDuring(t, () => t.shell.key('k', { ctrl: true }), 400)
    await t.shell.key('Escape')
    return [...judge(c, menu, refresh, 'menu'), ...judge(c, palette, refresh, 'search')]
  })

  await run.check('motion', 'durations', 'no animation runs longer than 0.7 s (loops aside)', async (c) => {
    const seen = new Map()
    for (const room of ['Store', 'Library']) {
      await t.shell.click((n) => [...document.querySelectorAll('.exo-titlebar button')].find(b => (b.getAttribute('aria-label') || b.textContent || '').trim().replace(/\s*\d+$/, '') === n), room)
      for (let i = 0; i < 6; i++) { for (const a of await t.shell.call(animations)) if (!a.loops) seen.set(`${a.name}@${a.target}`, Math.max(seen.get(`${a.name}@${a.target}`) ?? 0, a.duration)); await sleep(80) }
      await k.settle()
    }
    const long = [...seen].filter(([, d]) => d > 700).map(([n, d]) => `${n} ${d} ms`)
    c.evidence.longest = [...seen].sort((a, b) => b[1] - a[1]).slice(0, 5)
    return long.length ? [`too long: ${long.slice(0, 4).join(', ')}`] : []
  })

  await run.check('motion', 'reduce', 'Reduce motion stops the moves and puts them back after', async (c) => {
    const flip = async () => {
      await k.closeAll()
      await t.shell.click(() => document.querySelector('.exo-settings-btn'))
      await t.shell.waitFor(() => !!document.querySelector('.exo-settings')?.checkVisibility(), { timeoutMs: 4000 })
      await t.shell.click(() => [...document.querySelectorAll('.exo-settings .exo-set-item')].find(i => i.querySelector('.exo-set-item-label')?.textContent.trim() === 'Reduce motion')?.querySelector('button[role="switch"]') ?? null)
      await sleep(500)
      await k.closeAll()
    }
    const was = await t.shell.eval(`document.documentElement.dataset.exoMotion`)
    if (was === 'reduce') c.skip('Reduce motion is already on here (Windows or Exo)')
    await flip()
    const on = await t.shell.eval(`document.documentElement.dataset.exoMotion`)
    let moving = []
    if (on === 'reduce') {
      await t.shell.click((n) => [...document.querySelectorAll('.exo-titlebar button')].find(b => (b.getAttribute('aria-label') || b.textContent || '').trim().replace(/\s*\d+$/, '') === n), 'Store')
      await sleep(60)
      // Crossfades are how Reduce motion changes a view (DESIGN.md › Motion); anything else that runs is movement.
      moving = (await t.shell.call(animations)).filter(a => !a.loops && a.duration > 20 && a.state === 'running' && !/fade|view-transition-group-anim-root/.test(a.name))
      await k.nav('Library')
    }
    await flip()
    const back = await t.shell.eval(`document.documentElement.dataset.exoMotion`)
    c.evidence.moving = moving.slice(0, 5)
    return [on !== 'reduce' && 'the switch did not turn Reduce motion on', moving.length && `still moving: ${moving.slice(0, 3).map(a => `${a.name} ${a.duration} ms`).join(', ')}`, back !== was && 'Reduce motion did not turn back off'].filter(Boolean)
  })
}
