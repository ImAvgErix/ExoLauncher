// Layout, everywhere a person goes: every room and the states it opens (sheets, menus, Ctrl+K, each Settings section,
// a Store page, Music's pages and panels, each part of System, Discord's channel with its member list, threads, pins,
// inbox, server pages, a Server Guide resource, a forum and each section of its settings), in every Appearance, then at three window sizes and
// at the largest text size. In each, no two controls may overlap, no control may be cut by what holds it and no words
// may be cut off (lib/page.mjs › layoutFaults); every fault is reported with its path, its box and a crop of it.
// Nothing is sent, joined or changed: Discord is only looked at, in the test place.
import { sleep } from '../cdp.mjs'
import { ROOMS, THEMES } from '../lib/kit.mjs'

const TEST_SERVER = 'Uhh', TEST_CHANNEL = 'mod-chat'
const LOOKED_AT = ROOMS.filter(r => r !== 'Devices') // Exo Mouse is its own program in a frame (mouse/ has its own checks).

/**
 * Every room's layout as it first shows; used by the window-size, text-size and big-screen passes too. `go` changes
 * room (zoom's passes click for real: a page drawn zoomed takes DevTools' clicks off target).
 */
export async function roomsLayout(t, c, tag, go = (room) => t.k.nav(room)) {
  const failures = []
  for (const room of LOOKED_AT) {
    await go(room)
    await sleep(900)
    await t.k.settle(300, 6000)
    failures.push(...await t.k.layout(c, `${tag}-${room.toLowerCase()}`))
  }
  return failures
}

/** The first of a selector's elements that shows (rooms stay mounted, hidden); passed to the page as a finder. */
const visible = (sel) => [...document.querySelectorAll(sel)].find(e => e.checkVisibility())

async function openTestChannel(t) {
  await t.k.closeAll()
  await t.shell.key('k', { ctrl: true })
  if (!await t.shell.waitFor(() => document.activeElement?.getAttribute('role') === 'combobox', { timeoutMs: 3000 })) return false
  await t.shell.type(TEST_CHANNEL)
  const pick = (n, s) => [...document.querySelectorAll('[role="dialog"] [role="option"]')].find(o => { const x = o.querySelectorAll('span span'); return x[0]?.textContent.trim() === n && x[1]?.textContent.trim() === s })
  if (!await t.shell.waitFor(pick, { timeoutMs: 8000, args: [TEST_CHANNEL, TEST_SERVER] })) { await t.shell.key('Escape'); return false }
  await t.shell.click(pick, TEST_CHANNEL, TEST_SERVER)
  return !!await t.shell.waitFor((n) => document.querySelector('main.exo-dc section.exo-dc-chat')?.getAttribute('aria-label') === n, { timeoutMs: 10000, args: [TEST_CHANNEL] })
}

/** Opens something, looks, closes it: each `[label, open]` in `steps`, where open returns false when it can't. */
async function walk(t, c, steps) {
  const failures = []
  for (const [label, open, close] of steps) {
    let shown = false
    try { shown = await open() } catch { shown = false }
    if (!shown) { c.evidence[`${label}.skipped`] = true; continue }
    await sleep(500)
    await t.k.settle(300, 6000)
    failures.push(...await t.k.layout(c, label))
    if (close) await close().catch(() => {})
  }
  return failures
}

export async function run(t) {
  const { run, k } = t
  const before = (await t.host('settings.get')).appearance ?? 'system'
  const click = (finder, ...args) => t.shell.click(finder, ...args)
  const wait = (fn, ms = 5000, args = []) => t.shell.waitFor(fn, { timeoutMs: ms, args })

  for (const theme of THEMES) {
    const ok = await run.check('layout', `${theme}-rooms`, `every room in ${theme}: no controls over each other, none cut, no words cut off`, async (c) => {
      await k.setAppearance(theme)
      return roomsLayout(t, c, theme)
    }, { shell: t.shell })
    if (ok.status === 'FAIL' && ok.reasons.some(r => /did not|no Bar tab/.test(r))) continue

    await run.check('layout', `${theme}-library`, `the Library's sheet, menus and Ctrl+K in ${theme}`, async (c) => {
      await k.nav('Library')
      return walk(t, c, [
        [`${theme}-game-sheet`, async () => { await click(() => [...document.querySelectorAll('.exo-game-grid button[data-game-id]')].find(b => !b.closest('.exo-folder-tile'))); return wait(() => !!document.querySelector('.exo-game-overlay-stage .exo-game-title')) }, () => k.closeAll()],
        [`${theme}-add-menu`, async () => { await click(visible, '.exo-library-add'); return wait(() => [...document.querySelectorAll('[role="menu"]')].some(m => m.getBoundingClientRect().width)) }, () => k.closeAll()],
        [`${theme}-sort-menu`, async () => { await click(visible, '.exo-shelf-sort-trigger'); return wait(() => [...document.querySelectorAll('[role="menu"]')].some(m => m.getBoundingClientRect().width)) }, () => k.closeAll()],
        [`${theme}-ctrl-k`, async () => { await t.shell.key('k', { ctrl: true }); if (!await wait(() => document.activeElement?.getAttribute('role') === 'combobox', 3000)) return false; await t.shell.type('a'); return wait(() => document.querySelectorAll('[role="dialog"] [role="option"]').length > 2, 4000) }, () => k.closeAll()],
      ])
    }, { shell: t.shell })

    await run.check('layout', `${theme}-settings`, `every Settings section in ${theme}`, async (c) => {
      await k.closeAll()
      await click(() => document.querySelector('.exo-settings-btn'))
      if (!await wait(() => !!document.querySelector('.exo-settings')?.checkVisibility())) return ['Settings did not open']
      const sections = await t.shell.eval(`[...document.querySelectorAll('.exo-settings-nav button')].map(b => b.textContent.trim())`)
      const failures = await walk(t, c, sections.map(s => [`${theme}-settings-${s.toLowerCase().replace(/\W+/g, '-')}`, async () => {
        await click((n) => [...document.querySelectorAll('.exo-settings-nav button')].find(b => b.textContent.trim() === n), s)
        await sleep(700)
        return true
      }]))
      await k.closeAll()
      return failures
    }, { shell: t.shell })

    await run.check('layout', `${theme}-store`, `the Store and a game's page in ${theme}`, async (c) => {
      await k.nav('Store')
      await wait(() => document.querySelectorAll('section.exo-store-row').length > 1, 12000)
      return walk(t, c, [[`${theme}-store-page`, async () => {
        await click(() => document.querySelector('.exo-store-hero-stage'))
        if (!await wait(() => !!document.querySelector('.exo-sgp'), 15000)) return false
        await wait(() => !document.querySelector('.exo-sgp-placeholder'), 15000)
        return true
      }, () => k.closeAll()]])
    }, { shell: t.shell })

    await run.check('layout', `${theme}-music`, `Music's search, a playlist and its panels in ${theme}`, async (c) => {
      await k.nav('Music')
      if (await wait(() => document.querySelectorAll('.exo-music-card, .exo-music-row').length > 0 ? 'ok' : document.querySelector('.exo-music-signin-note, .exo-music-error') ? 'off' : null, 25000) !== 'ok') c.skip('no music service signed in')
      const field = '.exo-music-search input[aria-label^="Search"]'
      const panels = await t.shell.eval(`[...document.querySelectorAll('.exo-music-bar .exo-music-toggle[aria-pressed="false"]')].map(b => b.getAttribute('aria-label')).filter(Boolean)`)
      return walk(t, c, [
        [`${theme}-music-search`, async () => { if (!await k.fill(field, 'lofi')) return false; await t.shell.key('Enter'); return wait(() => document.querySelectorAll('.exo-music-row, .exo-music-card').length > 3, 12000) },
          async () => { await k.fill(field, ''); await click(() => [...document.querySelectorAll('.exo-music-side button, nav button')].find(b => b.textContent.trim() === 'Home')); await sleep(800) }],
        [`${theme}-music-playlist`, async () => { await click(() => document.querySelector('.exo-music-card .exo-music-card-open, .exo-music-card button')); return wait(() => document.querySelectorAll('.exo-music-row').length > 2 && !!document.querySelector('.exo-music-header-text'), 12000) }],
        ...panels.map(p => [`${theme}-music-${p.toLowerCase().replace(/\W+/g, '-')}`,
          async () => click((l) => document.querySelector(`.exo-music-bar .exo-music-toggle[aria-label="${l}"]`), p),
          async () => { await click((l) => document.querySelector(`.exo-music-bar .exo-music-toggle[aria-label="${l}"]`), p); await sleep(400) }]),
      ])
    }, { shell: t.shell })

    await run.check('layout', `${theme}-system`, `each part of System in ${theme}`, async (c) => {
      await k.nav('System')
      const parts = await t.shell.eval(`[...document.querySelectorAll('[data-system-part]')].map(p => p.dataset.systemPart)`)
      return walk(t, c, parts.map(p => [`${theme}-system-${p}`, () => t.shell.call((n) => { const at = document.querySelector(`[data-system-part="${n}"]`); at?.scrollIntoView({ block: 'start' }); return !!at }, p)]))
    }, { shell: t.shell })

    await run.check('layout', `${theme}-discord`, `Discord's channel, side panels, server pages, a forum and its settings in ${theme}`, async (c) => {
      await k.nav('Discord')
      if (await wait(() => document.querySelector('main.exo-dc') ? 'client' : document.querySelector('main.exo-dc-signin') ? 'signin' : null, 30000) !== 'client') c.skip('Discord is not signed in')
      if (!await openTestChannel(t)) c.skip(`no ${TEST_SERVER} #${TEST_CHANNEL}`)
      const header = (label) => click(visible, `main.exo-dc button[aria-label="${label}"]`)
      const toggle = (label) => async () => !!await header(label)
      const members = await t.shell.eval(`!!document.querySelector('main.exo-dc button[aria-label="Hide member list"]')`)
      const home = await t.host('discord.home').catch(() => null)
      const guild = home?.guilds?.find(g => g.name === TEST_SERVER)
      const list = guild ? await t.host('discord.channels', { guildId: guild.id }).catch(() => null) : null
      const forum = list?.channels?.find(ch => ch.kind === 'forum')?.name ?? null
      const page = (name) => async () => { if (!await click((p) => [...document.querySelectorAll('.exo-dc-side button, .exo-dc-side a')].find(b => b.textContent.trim() === p), name)) return false; await sleep(1200); return true }
      const failures = await walk(t, c, [
        [`${theme}-discord-channel`, async () => true],
        [`${theme}-discord-members-${members ? 'hidden' : 'shown'}`, toggle(members ? 'Hide member list' : 'Show member list'), async () => { await header(members ? 'Show member list' : 'Hide member list') }],
        [`${theme}-discord-threads`, toggle('Threads'), () => k.closeAll()],
        [`${theme}-discord-thread`, async () => { if (!await header('Threads')) return false; await sleep(800); return !!await click(visible, '[role="dialog"][aria-label="Threads"] .exo-dc-item, [role="dialog"][aria-label="Threads"] li button') }, () => openTestChannel(t)],
        [`${theme}-discord-pins`, toggle('Pinned messages'), () => k.closeAll()],
        [`${theme}-discord-inbox`, toggle('Inbox'), () => k.closeAll()],
        ...['Server Guide', 'Events', 'Browse channels', 'Members'].map(p => [`${theme}-discord-${p.toLowerCase().replace(/\W+/g, '-')}`, page(p)]),
        // A Server Guide resource opens its channel in a side panel beside the guide.
        [`${theme}-discord-guide-resource`, async () => await page('Server Guide')() && !!await click(visible, 'main.exo-dc .exo-dc-resource') && await sleep(1500).then(() => true), () => openTestChannel(t)],
        [`${theme}-discord-forum`, async () => forum ? !!await click((n) => [...document.querySelectorAll('.exo-dc-side .exo-dc-item')].find(b => b.textContent.trim() === n), forum) && await sleep(1500).then(() => true) : false],
        [`${theme}-discord-forum-post`, async () => forum ? !!await click(visible, '.exo-dc-post') && await sleep(1500).then(() => true) : false, () => openTestChannel(t)],
      ])
      // Discord's settings, section by section.
      await k.closeAll()
      if (await click(() => document.querySelector('main.exo-dc button[aria-label="Discord settings"]')) && await wait(() => !!document.querySelector('.exo-dc-settings')?.getClientRects().length, 6000)) {
        const sections = await t.shell.eval(`[...document.querySelectorAll('.exo-dc-settings-nav button')].filter(b => b.checkVisibility()).map(b => b.textContent.trim()).filter(Boolean)`)
        failures.push(...await walk(t, c, sections.filter(s => !/^Log Out$/i.test(s)).map(s => [`${theme}-discord-settings-${s.toLowerCase().replace(/\W+/g, '-')}`,
          async () => { await click((n) => [...document.querySelectorAll('.exo-dc-settings-nav button')].find(b => b.textContent.trim() === n), s); await sleep(600); return true }])))
        await t.shell.key('Escape')
      }
      return failures
    }, { shell: t.shell })
  }
  await k.setAppearance(before).catch(() => {})

  await run.check('layout', 'sizes', 'every room at three window sizes', async (c) => {
    const w0 = await t.win.window()
    const m = w0.monitor.work
    const failures = []
    for (const [fw, fh] of [[0.5, 0.6], [0.7, 0.75], [1, 1]]) {
      await t.win.place(m.x + 20, m.y + 20, Math.round(m.w * fw) - 40, Math.round(m.h * fh) - 40)
      await sleep(700)
      failures.push(...await roomsLayout(t, c, `${Math.round(fw * 100)}x${Math.round(fh * 100)}`))
    }
    await t.win.maximize()
    await sleep(600)
    return failures
  }, { shell: t.shell })

  await run.check('layout', 'text-large', 'every room and Settings at the largest text size', async (c) => {
    const choose = async (label) => {
      await k.closeAll()
      await click(() => document.querySelector('.exo-settings-btn'))
      await wait(() => !!document.querySelector('.exo-settings')?.checkVisibility())
      await click(() => document.getElementById('set-text-size')?.closest('.exo-set-item')?.querySelector('button[aria-haspopup="menu"]') ?? null)
      await sleep(300)
      await click((l) => [...document.querySelectorAll('[role^="menuitem"]')].find(i => i.getBoundingClientRect().width && i.textContent.trim() === l), label)
      await sleep(600)
    }
    await k.closeAll()
    await click(() => document.querySelector('.exo-settings-btn'))
    await wait(() => !!document.querySelector('.exo-settings')?.checkVisibility())
    const was = await t.shell.eval(`document.getElementById('set-text-size')?.closest('.exo-set-item')?.querySelector('button[aria-haspopup="menu"]')?.textContent.trim()`)
    const sizes = await t.shell.call(() => { document.getElementById('set-text-size')?.closest('.exo-set-item')?.querySelector('button[aria-haspopup="menu"]')?.click(); return new Promise(r => setTimeout(() => { const items = [...document.querySelectorAll('[role^="menuitem"]')].filter(i => i.getBoundingClientRect().width).map(i => i.textContent.trim()); r(items) }, 300)) })
    await k.closeAll()
    const largest = sizes[sizes.length - 1]
    if (!largest) return ['no Text size choices']
    c.evidence.textSize = { was, largest }
    const failures = []
    try {
      await choose(largest)
      failures.push(...await k.layout(c, `text-${largest.toLowerCase()}-settings`))
      await k.closeAll()
      failures.push(...await roomsLayout(t, c, `text-${largest.toLowerCase()}`))
    } finally { await choose(was || 'Normal'); await k.closeAll() }
    return failures
  }, { shell: t.shell })
}
