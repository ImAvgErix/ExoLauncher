// Performance: how long a room change takes, how smoothly the Library scrolls, how much the page grows as you use
// it, how hard Exo works while you do nothing, and how quickly the host answers the page.
import { sleep } from '../cdp.mjs'
import { frameStart, frameStop } from '../lib/page.mjs'
import { ROOMS } from '../lib/kit.mjs'

const pct = (a, p) => { const s = [...a].sort((x, y) => x - y); return s[Math.min(s.length - 1, Math.floor(s.length * p))] ?? 0 }

export async function run(t) {
  const { run, k } = t
  const rounds = t.quick ? 2 : 4

  await run.check('perf', 'room-switch', 'every room comes up quickly, every time', async (c) => {
    const times = {}
    for (let r = 0; r < rounds; r++) for (const room of [...ROOMS.slice(1), 'Library']) (times[room] ??= []).push(await k.nav(room))
    for (const [room, list] of Object.entries(times)) c.metric(`${room}.p95`, pct(list, 0.95), ' ms', 350)
    c.evidence.times = times
    const all = Object.values(times).flat()
    c.metric('all.p50', pct(all, 0.5), ' ms')
    return pct(all, 0.95) > 1500 ? [`a room took ${Math.round(pct(all, 0.95))} ms (p95)`] : []
  })

  await run.check('perf', 'discord-switch', 'switching into Discord draws without a long frame', async (c) => {
    const worst = []
    for (let r = 0; r < 3; r++) {
      await k.nav('Library')
      await t.shell.call(frameStart)
      await k.nav('Discord')
      await sleep(400)
      const gaps = await t.shell.call(frameStop)
      worst.push(Math.round(Math.max(0, ...gaps)))
    }
    c.evidence.worst = worst
    c.metric('worst', Math.min(...worst), ' ms', 50)
    await k.nav('Library')
    return []
  })

  await run.check('perf', 'library-scroll', 'the Library scrolls smoothly with the wheel', async (c) => {
    await k.nav('Library')
    await t.win.front()
    const at = await t.shell.eval(`(() => { const r = document.querySelector('.exo-game-grid')?.getBoundingClientRect(); return r ? { x: r.left + r.width / 2, y: Math.min(innerHeight - 100, r.top + 200) } : null })()`)
    if (!at) c.skip('no grid')
    await t.shell.call(frameStart)
    for (let i = 0; i < 40; i++) { await t.shell.wheel(at.x, at.y, 120); await sleep(16) }
    await sleep(600)
    for (let i = 0; i < 40; i++) { await t.shell.wheel(at.x, at.y, -120); await sleep(16) }
    await sleep(600)
    const gaps = await t.shell.call(frameStop)
    const refresh = pct(gaps, 0.5) || 16.7
    const late = gaps.filter(g => g > refresh * 1.6).length
    c.metric('frames', gaps.length)
    c.metric('late', late / Math.max(1, gaps.length) * 100, '%', 5)
    c.metric('worst', Math.max(0, ...gaps), ' ms', 50)
    await t.shell.eval(`(document.querySelector('main.exo-library-pane') ?? document.scrollingElement).scrollTop = 0`)
    return Math.max(0, ...gaps) > 250 ? [`a ${Math.round(Math.max(...gaps))} ms freeze while scrolling`] : []
  })

  await run.check('perf', 'memory', 'using every room again doesn\'t keep growing the page', async (c) => {
    const gc = () => t.shell.s.send('HeapProfiler.collectGarbage').catch(() => {})
    for (const room of [...ROOMS.slice(1), 'Library']) await k.nav(room)
    await gc(); await sleep(500)
    const a = await k.metrics()
    for (let r = 0; r < rounds; r++) for (const room of [...ROOMS.slice(1), 'Library']) await k.nav(room)
    await gc(); await sleep(500)
    const b = await k.metrics()
    const heap = (b.JSHeapUsedSize - a.JSHeapUsedSize) / 1048576
    const nodes = b.Nodes - a.Nodes
    c.metric('heap', a.JSHeapUsedSize / 1048576, ' MB')
    c.metric('heap.growth', heap, ' MB', 15)
    c.metric('nodes', b.Nodes)
    c.metric('nodes.growth', nodes, '', Math.max(2000, a.Nodes * 0.1))
    c.metric('listeners', b.JSEventListeners)
    return heap > 60 ? [`the page grew ${Math.round(heap)} MB over ${rounds} rounds of rooms`] : []
  })

  await run.check('perf', 'idle-cpu', 'Exo rests when nothing happens (Exo and its WebView2)', async (c) => {
    await k.nav('Library')
    await t.win.front()
    await sleep(3000)
    const seconds = t.quick ? 10 : 20
    const a = await t.win.proc()
    await sleep(seconds * 1000)
    const b = await t.win.proc()
    const used = (name) => (b.byName[name]?.cpuSeconds ?? 0) - (a.byName[name]?.cpuSeconds ?? 0)
    const exo = used('ExoLauncher'), web = used('msedgewebview2')
    c.metric('exo', exo / seconds * 100, '% of a core', 2)
    c.metric('webview2', web / seconds * 100, '% of a core', 6)
    c.metric('memory', (b.byName.ExoLauncher?.workingSetMB ?? 0) + (b.byName.msedgewebview2?.workingSetMB ?? 0), ' MB')
    c.evidence.byName = b.byName
    return (exo + web) / seconds * 100 > 25 ? [`${Math.round((exo + web) / seconds * 100)}% of a core while idle`] : []
  })

  await run.check('perf', 'host-calls', 'the host answers the page quickly', async (c) => {
    const lines = await t.exo.logSince(t.logMark)
    const byMethod = {}
    for (const l of lines) { const m = /PERF rpc method=(\S+) elapsedMs=(\d+)/.exec(l.text); if (m) (byMethod[m[1]] ??= []).push(+m[2]) }
    // A measurement takes as long as it was asked to measure (the controller's three seconds); it isn't slow.
    const timed = new Set(['controller.measure', 'controller.test'])
    const slow = Object.entries(byMethod).filter(([m]) => !timed.has(m)).map(([m, list]) => [m, Math.max(...list), list.length]).filter(([, max]) => max > 1000).sort((x, y) => y[1] - x[1])
    c.evidence.slowest = Object.entries(byMethod).map(([m, list]) => [m, Math.max(...list), list.length]).sort((x, y) => y[1] - x[1]).slice(0, 8)
    if (!Object.keys(byMethod).length) c.skip('app.log has no timed host calls in this run')
    if (slow.length) c.warn(`slow host calls: ${slow.slice(0, 4).map(([m, max, n]) => `${m} ${max} ms (x${n})`).join(', ')}`)
    return []
  })
}
