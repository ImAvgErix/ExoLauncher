// PC, read: its page list (each row opens its page, Overview's rows too, and the room comes back to the page it was
// left on), Overview's About card, the graphics card and its driver, each display's menus (opened, never changed),
// Shaders' games.
import { sleep } from '../cdp.mjs'

/** A page of the PC room by its name in the page list. */
const navItem = (name) => [...document.querySelectorAll('.exo-pc .exo-room-nav-item')].find(b => b.querySelector('.exo-room-nav-label')?.textContent === name)
const currentPage = () => document.querySelector('.exo-pc .exo-room-nav-item[aria-current="page"] .exo-room-nav-label')?.textContent ?? null
/** Graphics' Shaders, scrolled to the top of its page. */
const shadersPart = () => { const at = document.querySelector('[data-pc-page="graphics"] [data-pc-part="shaders"]'); at?.scrollIntoView({ block: 'start' }); return !!at }

export async function run(t) {
  const { run, k } = t
  await k.nav('PC')
  /** Opens a page from the page list, as a person does, and waits until it is the current one and still. */
  const open = async (name) => {
    if (!await t.shell.click(navItem, name)) return false
    const ok = await t.shell.waitFor((n) => document.querySelector('.exo-pc .exo-room-nav-item[aria-current="page"] .exo-room-nav-label')?.textContent === n
      && !!document.querySelector('.exo-pc-page:not([hidden])'), { timeoutMs: 5000, args: [name] })
    await k.settle()
    return !!ok
  }

  await run.check('system', 'overview', "Overview shows this PC's card, and each of its rows opens its page", async (c) => {
    if (!await open('Overview')) return ['no Overview in the page list']
    const hero = await t.shell.waitFor(() => {
      const name = document.querySelector('.exo-pc-hero-name')?.textContent?.trim()
      const facts = [...document.querySelectorAll('.exo-pc-hero-facts dt')].map(d => d.textContent.trim())
      return name && facts.length >= 2 ? { name, facts } : null
    }, { timeoutMs: 15000 })
    c.evidence.hero = hero
    await c.shot('overview', t.shell)
    const failures = hero ? [] : ["the About card shows no card name or what it runs"]
    // What the PC is made of comes from Windows' own records on every PC: the processor, the memory, a drive and Windows.
    const made = await t.shell.waitFor(() => {
      const facts = [...document.querySelectorAll('.exo-pc-hero-facts dt')].map(d => d.textContent.trim())
      return ['Processor', 'Memory', 'Windows'].every(f => facts.includes(f)) && facts.some(f => f.startsWith('Drive ')) ? facts : null
    }, { timeoutMs: 15000 })
    if (!made) failures.push('the About card lacks the processor, memory, a drive or Windows')
    failures.push(...await k.look(c, 'overview'))
    const rows = await t.shell.eval(`[...document.querySelectorAll('.exo-pc-link .exo-pc-link-label')].map(l => l.textContent)`)
    c.evidence.rows = rows
    for (const row of rows) {
      await t.shell.click((n) => [...document.querySelectorAll('.exo-pc-link')].find(b => b.querySelector('.exo-pc-link-label')?.textContent === n), row)
      const want = row === 'Shaders' ? 'Graphics' : row
      const landed = await t.shell.waitFor((n) => document.querySelector('.exo-pc .exo-room-nav-item[aria-current="page"] .exo-room-nav-label')?.textContent === n ? n : null, { timeoutMs: 5000, args: [want] })
      if (!landed) failures.push(`Overview's ${row} row did not open ${want}`)
      await open('Overview')
    }
    return failures
  })

  await run.check('system', 'remembers', 'PC comes back to the page it was left on', async () => {
    if (!await open('Apps')) return ['no Apps in the page list']
    await k.nav('Library')
    await k.nav('PC')
    const now = await t.shell.call(currentPage)
    await open('Overview')
    return now === 'Apps' ? [] : [`PC came back on ${now ?? 'no page'}, not Apps`]
  })

  await run.check('system', 'graphics', 'Graphics shows the card, its driver and its newest driver', async (c) => {
    if (!await open('Graphics')) return ['no Graphics in the page list']
    const info = await t.shell.waitFor(() => { const text = document.querySelector('[data-pc-page="graphics"]')?.innerText ?? ''; const v = /\b\d{3}\.\d{2}\b/.exec(text); return v && /driver/i.test(text) ? { version: v[0], newest: /newest|update|Reinstall|Update/i.test(text) } : null }, { timeoutMs: 15000 })
    c.evidence.info = info
    await c.shot('graphics', t.shell)
    const failures = info ? [] : ['no driver version shown']
    failures.push(...await k.look(c, 'graphics'))
    return failures
  })

  await run.check('system', 'display', 'Display lists the screen and its menus open with choices', async (c) => {
    if (!await open('Display')) return ['no Display in the page list']
    await sleep(1200)
    const menus = await t.shell.eval(`[...document.querySelectorAll('[data-pc-page="display"] button[aria-haspopup="menu"]')].filter(b => b.checkVisibility()).map(b => b.textContent.trim()).slice(0, 12)`)
    c.evidence.menus = menus
    if (!menus.length) return ['no settings with choices']
    const failures = []
    for (let i = 0; i < Math.min(3, menus.length); i++) {
      await t.shell.click((n) => [...document.querySelectorAll('[data-pc-page="display"] button[aria-haspopup="menu"]')].filter(b => b.checkVisibility())[n], i)
      // A menu fills as it opens (a screen's modes are read then): its choices, once there.
      await t.shell.waitFor(() => [...document.querySelectorAll('[role="menu"]')].some(m => m.getBoundingClientRect().width && m.querySelector('[role^="menuitem"]')) || null, { timeoutMs: 2000 })
      const items = await k.menuItems()
      if (!items?.length) failures.push(`"${menus[i]}" opened no choices`)
      await t.shell.key('Escape')
      await sleep(300)
    }
    await c.shot('display', t.shell)
    failures.push(...await k.look(c, 'display'))
    return failures
  })

  await run.check('system', 'shaders', 'Shaders lists the games with their state, without starting its engine', async (c) => {
    // The board shows the state the engine left; only Compile and Refresh start it.
    const before = (await t.host('shaders.addon'))?.state
    if (!await open('Graphics') || !await t.shell.call(shadersPart)) return ['no Shaders on Graphics']
    const games = await t.shell.waitFor(() => { const n = [...document.querySelectorAll('[data-pc-part="shaders"] img')].filter(i => i.checkVisibility()).length; return n > 2 ? n : null }, { timeoutMs: 15000 })
    c.metric('games', games ?? 0)
    await c.shot('shaders', t.shell)
    const after = (await t.host('shaders.addon'))?.state
    c.evidence.engine = { before, after }
    const failures = games ? [] : ['no games listed']
    if (before !== 'running' && (after === 'running' || after === 'starting')) failures.push('showing the board started the engine')
    failures.push(...await k.look(c, 'shaders'))
    await open('Overview')
    return failures
  })

  await run.check('system', 'apps', 'Apps holds the apps and the game runtimes side by side, each list scrolling in its own panel and fading at its foot', async (c) => {
    if (!await open('Apps')) return ['Apps did not open']
    const listed = await t.shell.waitFor(() => document.querySelector('.exo-apps-list .exo-set-item') && document.querySelector('.exo-runtimes-list .exo-set-item') ? true : null, { timeoutMs: 20000 })
    if (!listed) return ['the apps or the runtimes never listed']
    const state = await t.shell.eval(`(() => {
      const page = document.querySelector('[data-pc-page="apps"]')
      const lists = ['.exo-apps-list', '.exo-runtimes-list'].map(sel => { const list = document.querySelector(sel); const box = list.getBoundingClientRect(); return { sel, scrolls: list.scrollHeight > list.clientHeight + 1, fade: getComputedStyle(list).getPropertyValue('--exo-pool-bottom'), bottom: Math.round(box.bottom) } })
      return { page: page.scrollHeight - page.clientHeight, lists, status: document.querySelector('.exo-runtimes-status')?.textContent ?? '', aboutRuntimes: !!document.querySelector('#set-about, [data-section="about"]')?.textContent.includes('Game runtimes') }
    })()`)
    c.evidence.state = state
    await c.shot('apps', t.shell)
    const failures = []
    if (state.page > 0) failures.push(`the page itself scrolls ${state.page}px`)
    for (const list of state.lists) if (list.scrolls && list.fade !== '40px') failures.push(`${list.sel} runs on without fading at its foot`)
    if (!state.status) failures.push('the runtimes say nothing about where they stand')
    return failures
  })

  await run.check('system', 'shaders-engine', "Shaders' engine leaves soon after its work", async (c) => {
    // Only Compile and Refresh start the engine (the board shown above must not have); one that runs exits by itself
    // 15 s after its last scan or compile ends, whether or not the board is on screen. shaders.addon never starts it.
    const status = await t.host('shaders.addon')
    if (!status?.present) { c.skip('Exo Shaders is not installed'); return [] }
    c.evidence.withBoard = status.state
    const t0 = Date.now()
    let state = status.state
    while (state === 'running' && Date.now() - t0 < 60_000) { await sleep(5000); state = (await t.host('shaders.addon'))?.state }
    c.metric('exitSeconds', Math.round((Date.now() - t0) / 1000))
    c.evidence.after = state
    if (state !== 'running') return []
    const snap = (await t.host('shaders.call', { method: 'snapshot', params: {} }))?.result
    // Work that waits for the PC to be idle doesn't keep it: only a compile, the started queue or a scan does.
    const working = !!(snap?.queue?.current || snap?.queue?.running || snap?.app?.scanning)
    c.evidence.working = working
    return working ? [] : ['the engine still runs a minute after the board left the screen, with nothing to do']
  })
}
