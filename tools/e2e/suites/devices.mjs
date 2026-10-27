// Devices: the grid of what you plug in (each card with its picture, keyboards and other controllers marked Coming
// soon), the line about the processor's own USB and Find them, a card growing into its page and folding back, your
// audio's page and a DualSense's Controller Lab on one screen; and Exo Mouse's page loads in its frame, finds the mouse
// (or says there is none), and wears Exo's Appearance.
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
    await t.shell.waitFor(() => document.querySelector('.exo-device-page-hero') || null, { timeoutMs: 8000 })
    await sleep(1200)
    const read = () => t.shell.eval(`(() => { const page = document.querySelector('.exo-devices-page'), panels = document.querySelector('.exo-device-page-panels'); return { page: page.scrollHeight - page.clientHeight, panels: panels ? panels.scrollHeight - panels.clientHeight : null, credit: document.querySelector('.exo-device-page-credit')?.textContent.trim() ?? '' } })()`)
    const shown = await read()
    c.evidence.shown = shown
    await c.shot('audio', t.shell)
    const failures = []
    if (shown.page > 0 || shown.panels > 0) failures.push(`the page scrolls (${shown.page}px, its panels ${shown.panels}px)`)
    if (!/Lucid/.test(shown.credit)) failures.push('no credit to Lucid at the hero\'s foot')
    if (await t.shell.click(() => [...document.querySelectorAll('.exo-device-page-panel button')].find(b => /^(Change|Pick)$/.test(b.textContent.trim())))) {
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

  await run.check('devices', 'ports', "a device off the processor's own USB is named above the cards, and Find them shows where each card is plugged in", async (c) => {
    const list = await t.host('devices.list')
    const ports = { cpu: list.cpuPorts ?? null, devices: list.devices.map(d => ({ kind: d.kind, name: d.name, port: d.port ?? null })) }
    c.evidence.ports = ports
    const line = await t.shell.eval(`document.querySelector('.exo-devices-ports')?.innerText ?? null`)
    c.evidence.line = line
    const wanting = ports.devices.filter(d => d.port?.better === 'free' && ['mouse', 'keyboard', 'controller'].includes(d.kind))
    const failures = []
    if (!ports.devices.some(d => d.port)) c.skip('no device on USB')
    if (wanting.length && ports.cpu?.free > 0 && !line) failures.push(`${wanting.map(d => d.name).join(', ')} would gain from the processor's own USB, and nothing says so`)
    if (!line) return failures
    await t.shell.click(() => [...document.querySelectorAll('.exo-devices-ports button')].find(b => b.textContent.trim() === 'Find them'))
    await sleep(400)
    const finding = await t.shell.eval(`[...document.querySelectorAll('[data-device-card] .exo-device-card-text span')].map(s => s.textContent)`)
    c.evidence.finding = finding
    await c.shot('finding', t.shell)
    if (!finding.some(text => /processor|chipset|USB chip|dock|Bluetooth/i.test(text))) failures.push('Find them left every card saying how it is linked, not where it is plugged in')
    await t.shell.click(() => [...document.querySelectorAll('.exo-devices-ports button')].find(b => b.textContent.trim() === 'Done'))
    return failures
  })

  await run.check('devices', 'controller', 'a DualSense opens its Controller Lab on one screen: firmware in the hero, its sticks and triggers live, a measured rate and its USB port', async (c) => {
    const pad = (await t.host('devices.list')).devices.find(d => d.kind === 'controller' && /^dualsense/.test(d.model ?? ''))
    if (!pad) c.skip('no DualSense on this PC')
    const id = pad.id
    await t.shell.click((card) => document.querySelector(`button[data-device-card="${CSS.escape(card)}"]`), id)
    await t.shell.waitFor(() => document.querySelector('.exo-device-page-hero') || null, { timeoutMs: 8000 })
    const failures = []
    const live = await t.shell.waitFor(() => document.querySelector('.exo-trigger[aria-valuenow]') && document.querySelector('.exo-stick-dot') ? true : null, { timeoutMs: 8000 })
    const stats = await t.shell.waitFor(() => { const dts = [...document.querySelectorAll('.exo-device-page-stats dt')].map(d => d.textContent); return dts.includes('Firmware') ? dts : null }, { timeoutMs: 15000 })
    c.evidence.stats = stats
    if (!live) failures.push('the sticks and triggers never drew')
    if (!stats) failures.push('no firmware in the hero (a DualSense on a cable reports it)')
    if (await t.shell.click(() => [...document.querySelectorAll('.exo-device-page-panel button')].find(b => b.textContent.trim() === 'Measure'))) {
      const rate = await t.shell.waitFor(() => { const text = document.querySelector('.exo-device-page-panel[aria-label="Polling"] .exo-set-state')?.textContent ?? ''; return /\d+ Hz/.test(text) ? text : null }, { timeoutMs: 12000 })
      c.evidence.rate = rate
      if (!rate) failures.push('Measure gave no rate')
    }
    const fit = await t.shell.eval(`(() => { const page = document.querySelector('.exo-devices-page'), panels = document.querySelector('.exo-device-page-panels'); return { page: page.scrollHeight - page.clientHeight, panels: panels.scrollHeight - panels.clientHeight, port: !!document.querySelector('.exo-device-page-panel[aria-label="USB port"]') } })()`)
    c.evidence.fit = fit
    await c.shot('controller', t.shell)
    if (fit.page > 0 || fit.panels > 0) failures.push(`the page scrolls (${fit.page}px, its panels ${fit.panels}px)`)
    if (pad.connection === 'usb' && !fit.port) failures.push('no USB port panel for a controller on a cable')
    await t.shell.click(() => document.querySelector('.exo-devices-back button'))
    await sleep(800)
    return failures
  })

  await run.check('devices', 'controller-setup', "a DualSense's calibration and an Edge's profiles open, fit and close without writing anything; games' Steam Input is one switch", async (c) => {
    const pad = (await t.host('devices.list')).devices.find(d => d.kind === 'controller' && /^dualsense/.test(d.model ?? ''))
    if (!pad) c.skip('no DualSense on this PC')
    const edge = pad.model === 'dualsense-edge'
    const failures = []
    const before = edge && pad.connection === 'usb' ? await t.host('controller.profiles', { id: pad.id }) : null
    await t.shell.click((card) => document.querySelector(`button[data-device-card="${CSS.escape(card)}"]`), pad.id)
    await t.shell.waitFor(() => document.querySelector('.exo-device-page-hero') || null, { timeoutMs: 8000 })
    const rows = await t.shell.waitFor(() => {
      const labels = [...document.querySelectorAll('.exo-device-page-panel .exo-set-item-label')].map(l => l.textContent)
      return labels.includes('Calibration') ? labels : null
    }, { timeoutMs: 8000 })
    c.evidence.rows = rows
    if (!rows) failures.push('no Calibration row in Sticks')
    if (edge && pad.connection === 'usb') {
      const slots = await t.shell.waitFor(() => { const n = document.querySelectorAll('.exo-device-page-panel[aria-label="Profiles"] .exo-set-item').length; return n === 4 ? n : null }, { timeoutMs: 15000 })
      if (!slots) failures.push("the Edge's four profile slots aren't listed")
      if (await t.shell.click(() => [...document.querySelectorAll('.exo-device-page-panel[aria-label="Profiles"] button')].find(b => /^(Create|Edit)$/.test(b.textContent.trim())))) {
        const dialog = await t.shell.waitFor(() => { const d = document.querySelector('.exo-profile-dialog'); return d ? { scroll: d.scrollHeight - d.clientHeight, sticks: d.querySelectorAll('.exo-profile-curve').length } : null }, { timeoutMs: 5000 })
        c.evidence.editor = dialog
        await c.shot('profile-editor', t.shell)
        if (!dialog) failures.push("the profile editor didn't open")
        else if (dialog.scroll > 0 || dialog.sticks !== 2) failures.push(`the profile editor scrolls (${dialog.scroll}px) or lacks a stick's curve`)
        await t.shell.click(() => [...document.querySelectorAll('.exo-profile-dialog button')].find(b => b.textContent.trim() === 'Cancel'))
        await sleep(500)
      }
    }
    if (await t.shell.click(() => [...document.querySelectorAll('.exo-device-page-panel[aria-label="Sticks"] button')].find(b => /^(Calibrate|Again)$/.test(b.textContent.trim())))) {
      const intro = await t.shell.waitFor(() => document.querySelector('.exo-calibrate h2')?.textContent ?? null, { timeoutMs: 5000 })
      if (intro !== 'Calibrate the sticks') failures.push(`calibration opened on "${intro}", not its first step`)
      await t.shell.click(() => [...document.querySelectorAll('.exo-calibrate button')].find(b => b.textContent.trim() === 'Cancel'))
      await sleep(500)
    }
    if (await t.shell.eval(`document.querySelectorAll('.exo-dialog').length`)) failures.push('a dialog stayed open after Cancel')
    if (before?.ok) {
      const after = await t.host('controller.profiles', { id: pad.id })
      const same = JSON.stringify(after.profiles?.map(p => [p.slot, p.assigned, p.name, p.updated])) === JSON.stringify(before.profiles?.map(p => [p.slot, p.assigned, p.name, p.updated]))
      if (!same) failures.push('looking at the profiles changed them')
    }
    const steam = await t.host('steamInput.state')
    c.evidence.steamInput = steam
    if (steam.steam && !await t.shell.eval(`!!document.getElementById('ctl-native')`)) failures.push('no "Games read it themselves" switch with Steam installed')
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
