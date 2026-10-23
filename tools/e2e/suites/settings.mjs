// Exo's Settings, used: it opens from the gear, every section shows, every switch in General and Downloads turns and
// turns back, Text size scales the page and goes back, Escape closes it where you were.
import { sleep } from '../cdp.mjs'

async function open(t) {
  await t.k.closeAll()
  await t.shell.click(() => document.querySelector('.exo-settings-btn'))
  return !!await t.shell.waitFor(() => !!document.querySelector('.exo-settings')?.checkVisibility(), { timeoutMs: 5000 })
}

export async function run(t) {
  const { run, k } = t
  await k.nav('Library')

  await run.check('settings', 'sections', 'Settings opens from the gear and every section shows', async (c) => {
    const t0 = Date.now()
    if (!await open(t)) return ['Settings did not open']
    c.metric('open', Date.now() - t0, ' ms', 500)
    const sections = await t.shell.eval(`[...document.querySelectorAll('.exo-settings-nav button')].map(b => b.textContent.trim())`)
    c.evidence.sections = sections
    const failures = []
    for (const s of sections) {
      await t.shell.click((n) => [...document.querySelectorAll('.exo-settings-nav button')].find(b => b.textContent.trim() === n), s)
      // Smooth scrolling: wait until the heading has arrived inside the sheet's own scroll area.
      const shown = await t.shell.waitFor((n) => { const h = [...document.querySelectorAll('.exo-settings h2, .exo-settings h3')].find(e => e.textContent.trim() === n); if (!h) return false; let sc = h.parentElement; while (sc && !(sc.scrollHeight > sc.clientHeight + 1 && /(auto|scroll)/.test(getComputedStyle(sc).overflowY))) sc = sc.parentElement; const r = h.getBoundingClientRect(), box = (sc ?? document.documentElement).getBoundingClientRect(); return r.top >= box.top - 1 && r.bottom <= box.bottom + 1 }, { timeoutMs: 2500, args: [s] })
      if (!shown) failures.push(`${s} did not come into view`)
    }
    await c.shot('settings', t.shell)
    failures.push(...await k.look(c, 'settings'))
    return failures
  })

  await run.check('settings', 'switches', 'every switch in General and Downloads turns and turns back', async (c) => {
    if (!await t.shell.eval(`!!document.querySelector('.exo-settings')?.checkVisibility()`) && !await open(t)) return ['Settings did not open']
    const ids = await t.shell.eval(`[...document.querySelectorAll('.exo-settings button[role="switch"]')].filter(b => { const sec = b.closest('section'); return /^(General|Downloads)/.test(sec?.querySelector('h2, h3')?.textContent.trim() ?? '') }).map((b, i) => b.id || ('#' + i))`)
    c.metric('switches', ids.length)
    const failures = []
    for (let i = 0; i < ids.length; i++) {
      const pick = (n) => [...document.querySelectorAll('.exo-settings button[role="switch"]')].filter(b => /^(General|Downloads)/.test(b.closest('section')?.querySelector('h2, h3')?.textContent.trim() ?? ''))[n]
      const before = await t.shell.call((n) => { const all = [...document.querySelectorAll('.exo-settings button[role="switch"]')].filter(b => /^(General|Downloads)/.test(b.closest('section')?.querySelector('h2, h3')?.textContent.trim() ?? '')); return all[n]?.getAttribute('aria-checked') }, i)
      await t.shell.click(pick, i)
      const flipped = await t.shell.waitFor((n, was) => { const all = [...document.querySelectorAll('.exo-settings button[role="switch"]')].filter(b => /^(General|Downloads)/.test(b.closest('section')?.querySelector('h2, h3')?.textContent.trim() ?? '')); return all[n]?.getAttribute('aria-checked') !== was }, { timeoutMs: 3000, args: [i, before] })
      await sleep(300)
      await t.shell.click(pick, i)
      const back = await t.shell.waitFor((n, was) => { const all = [...document.querySelectorAll('.exo-settings button[role="switch"]')].filter(b => /^(General|Downloads)/.test(b.closest('section')?.querySelector('h2, h3')?.textContent.trim() ?? '')); return all[n]?.getAttribute('aria-checked') === was }, { timeoutMs: 3000, args: [i, before] })
      const label = await t.shell.call((n) => { const all = [...document.querySelectorAll('.exo-settings button[role="switch"]')].filter(b => /^(General|Downloads)/.test(b.closest('section')?.querySelector('h2, h3')?.textContent.trim() ?? '')); return all[n]?.closest('.exo-set-item')?.querySelector('.exo-set-item-label')?.textContent.trim() ?? `switch ${n}` }, i)
      if (!flipped) failures.push(`${label} did not turn`)
      else if (!back) failures.push(`${label} did not turn back`)
    }
    return failures
  })

  await run.check('settings', 'text-size', 'Text size scales the page and goes back', async (c) => {
    if (!await t.shell.eval(`!!document.querySelector('.exo-settings')?.checkVisibility()`) && !await open(t)) return ['Settings did not open']
    const choose = async (label) => {
      await t.shell.click(() => document.getElementById('set-text-size')?.closest('.exo-set-item')?.querySelector('button[aria-haspopup="menu"]') ?? null)
      await sleep(300)
      await t.shell.click((l) => [...document.querySelectorAll('[role^="menuitem"]')].find(i => i.getBoundingClientRect().width && i.textContent.trim() === l), label)
      await sleep(600)
      return t.shell.eval(`getComputedStyle(document.documentElement).getPropertyValue('--exo-scale').trim()`)
    }
    const was = await t.shell.eval(`document.getElementById('set-text-size')?.closest('.exo-set-item')?.querySelector('button[aria-haspopup="menu"]')?.textContent.trim()`)
    const large = await choose('Large')
    const small = await choose('Small')
    await choose(was || 'Normal')
    c.evidence.scales = { large, small, was }
    const failures = []
    if (+large <= 1) failures.push(`Large set the scale to ${large}`)
    if (+small >= 1) failures.push(`Small set the scale to ${small}`)
    return failures
  })

  await run.check('settings', 'updates', 'About › Check for updates asks and answers (nothing is installed)', async (c) => {
    if (!await t.shell.eval(`!!document.querySelector('.exo-settings')?.checkVisibility()`) && !await open(t)) return ['Settings did not open']
    const button = () => [...document.querySelectorAll('.exo-settings button')].find(b => /^(Check for updates|Checking…|Install update)$/.test(b.textContent.trim()))
    const was = await t.shell.eval(`(${button.toString()})()?.textContent.trim() ?? null`)
    c.evidence.was = was
    if (!was) return ['no Check for updates in About']
    if (was === 'Install update') c.skip('an update is already waiting (not installed by the suite)')
    const t0 = Date.now()
    await t.shell.click(button)
    const answer = await t.shell.waitFor(() => {
      const b = [...document.querySelectorAll('.exo-settings button')].find(b => /^(Check for updates|Install update)$/.test(b.textContent.trim()))
      const row = b?.closest('.exo-set-item')
      const said = row?.querySelector('[role="alert"]')?.textContent.trim() || row?.querySelector('.exo-set-item-detail')?.textContent.trim()
      return b && !b.disabled && said && !/^Version /.test(said) ? said : null
    }, { timeoutMs: 20000 })
    c.metric('answer', Date.now() - t0, ' ms', 5000)
    c.evidence.answer = answer
    return answer ? [] : ['no answer within 20 s']
  })

  await run.check('settings', 'close', 'Escape closes Settings back to the room you were in', async () => {
    if (!await t.shell.eval(`!!document.querySelector('.exo-settings')?.checkVisibility()`) && !await open(t)) return ['Settings did not open']
    await t.shell.key('Escape')
    const closed = await t.shell.waitFor(() => !document.querySelector('.exo-settings')?.checkVisibility(), { timeoutMs: 3000 })
    const s = await k.state()
    return [!closed && 'Escape did not close it', closed && s.room !== 'Library' && `it came back to ${s.room}`].filter(Boolean)
  })
}
