// Devices: the grid of what you plug in (each card with its picture, keyboards and controllers marked Coming soon),
// a card growing into its page and folding back, your audio's page on one screen; and Exo Mouse's page loads in its
// frame, finds the mouse (or says there is none), and wears Exo's Appearance.
import { openFrame, sleep } from '../cdp.mjs'

/** Every value the view transition's kind took while `act` ran in the page. */
const transitionsDuring = (t, act) => t.shell.eval(`(async () => {
  const seen = []
  const watch = new MutationObserver(() => seen.push(document.documentElement.dataset.exoVt || '-'))
  watch.observe(document.documentElement, { attributes: true, attributeFilter: ['data-exo-vt'] })
  ${act}
  await new Promise(resolve => setTimeout(resolve, 1500))
  watch.disconnect()
  return seen
})()`)

export async function run(t) {
  const { run, k } = t
  await k.nav('Devices')

  const grid = await t.shell.waitFor(() => document.querySelectorAll('[data-device-card]').length || null, { timeoutMs: 15000 })
  await run.check('devices', 'grid', 'each device is a card with its picture; ones Exo does not set up yet say Coming soon and open nothing', async (c) => {
    if (!grid) c.skip('no devices found on this PC')
    await t.shell.waitFor(() => document.querySelectorAll('[data-device-card] .exo-device-card-art > *').length === document.querySelectorAll('[data-device-card]').length || null, { timeoutMs: 15000 })
    const cards = await t.shell.eval(`[...document.querySelectorAll('[data-device-card]')].map(card => ({ id: card.dataset.deviceCard, soon: card.classList.contains('is-soon'), button: card.tagName === 'BUTTON', label: card.querySelector('.exo-device-soon')?.textContent ?? null, picture: !!card.querySelector('.exo-device-card-art > *'), name: card.querySelector('strong')?.textContent ?? '' }))`)
    const head = await t.shell.eval(`document.querySelector('.exo-devices-head')?.innerText ?? ''`)
    c.evidence.cards = cards.map(({ id, soon, picture }) => ({ id, soon, picture }))
    await c.shot('grid', t.shell)
    const failures = []
    for (const card of cards) {
      if (!card.picture) failures.push(`${card.name} has no picture`)
      if (card.soon && (card.button || card.label !== 'Coming soon')) failures.push(`${card.name} is coming soon but opens or doesn't say so`)
    }
    if (/found/i.test(head)) failures.push('a line under the heading counts the devices')
    return failures
  })

  await run.check('devices', 'morph', 'a card grows into its page and Devices folds it back, every card the same way', async (c) => {
    const open = await t.shell.eval(`[...document.querySelectorAll('button[data-device-card]')].map(card => card.dataset.deviceCard)`)
    if (!open.length) c.skip('no device with a page')
    const failures = []
    for (const id of open) {
      const opening = await transitionsDuring(t, `document.querySelector('[data-device-card="${id}"]').click()`)
      const folding = await transitionsDuring(t, `document.querySelector('.exo-devices-back button').click()`)
      c.evidence[id] = { opening, folding }
      if (opening[0] !== 'morph' && !await t.shell.eval(`document.documentElement.dataset.exoMotion === 'reduce'`)) failures.push(`${id} opened without growing from its card (${opening.join(',') || 'nothing'})`)
      if (folding[0] !== 'morph' && !await t.shell.eval(`document.documentElement.dataset.exoMotion === 'reduce'`)) failures.push(`${id} closed without folding back (${folding.join(',') || 'nothing'})`)
      if (!await t.shell.eval(`!!document.querySelector('[data-device-card="${id}"]')`)) failures.push(`${id}'s card did not come back`)
    }
    return failures
  })

  await run.check('devices', 'audio', 'your audio page fits one screen beside its hero, credits Lucid, and its headphone search lists under its row', async (c) => {
    if (!await t.shell.eval(`!!document.querySelector('button[data-device-card="audio"]')`)) c.skip('no audio device')
    await t.shell.click(() => document.querySelector('button[data-device-card="audio"]'))
    await t.shell.waitFor(() => document.querySelector('.exo-audio-hero') || null, { timeoutMs: 8000 })
    await sleep(1200)
    const read = () => t.shell.eval(`(() => { const page = document.querySelector('.exo-devices-page'), panels = document.querySelector('.exo-audio-panels'); return { page: page.scrollHeight - page.clientHeight, panels: panels ? panels.scrollHeight - panels.clientHeight : null, credit: document.querySelector('.exo-audio-credit')?.textContent.trim() ?? '' } })()`)
    const shown = await read()
    c.evidence.shown = shown
    await c.shot('audio', t.shell)
    const failures = []
    if (shown.page > 0 || shown.panels > 0) failures.push(`the page scrolls (${shown.page}px, its panels ${shown.panels}px)`)
    if (!/Lucid/.test(shown.credit)) failures.push('no credit to Lucid at the hero\'s foot')
    if (await t.shell.click(() => [...document.querySelectorAll('.exo-audio-panel button')].find(b => /^(Change|Pick)$/.test(b.textContent.trim())))) {
      await sleep(1200)
      const picking = await read()
      c.evidence.picking = picking
      if (picking.page > 0 || picking.panels > 0) failures.push(`the open headphone search pushes the page into a scroll (${picking.panels}px)`)
      await t.shell.key('Escape')
    }
    await t.shell.click(() => document.querySelector('.exo-devices-back button'))
    await sleep(800)
    return failures
  })

  await run.check('devices', 'mouse', 'Exo Mouse loads and shows the mouse or how to connect one', async (c) => {
    const frame = await openFrame(t.shell, 'mouse.exo-launcher.local', 20000).catch(() => null)
    if (!frame) return ['the mouse page did not load']
    const state = await frame.waitFor(() => { const text = document.body.innerText; return text.length > 40 ? { text: text.slice(0, 80), theme: document.documentElement.dataset.exoTheme ?? null } : null }, { timeoutMs: 15000 })
    const theme = await t.shell.eval('document.documentElement.dataset.exoTheme')
    c.evidence.state = state
    frame.close()
    await c.shot('devices', t.shell)
    const failures = []
    if (!state) failures.push('the mouse page stayed empty')
    else if (state.theme !== theme) failures.push(`the mouse page shows ${state.theme}, Exo ${theme}`)
    return failures
  })
}
