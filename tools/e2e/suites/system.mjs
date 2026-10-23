// System (one page), read: the graphics card and its driver, each display's menus (opened, never changed), Shaders' games.
import { sleep } from '../cdp.mjs'

// System is one page: each part is scrolled into view before it's read.
const part = (name) => { const at = document.querySelector(`[data-system-part="${name}"]`); at?.scrollIntoView({ block: 'start' }); return !!at }

export async function run(t) {
  const { run, k } = t
  await k.nav('System')

  await run.check('system', 'graphics', 'Graphics shows the card, its driver and its newest driver', async (c) => {
    await t.shell.call(part, 'graphics')
    const info = await t.shell.waitFor(() => { const text = document.querySelector('[data-system-part="graphics"]')?.innerText ?? ''; const v = /\b\d{3}\.\d{2}\b/.exec(text); return v && /driver/i.test(text) ? { version: v[0], newest: /newest|update|Reinstall|Update/i.test(text) } : null }, { timeoutMs: 15000 })
    c.evidence.info = info
    await c.shot('graphics', t.shell)
    const failures = info ? [] : ['no driver version shown']
    failures.push(...await k.look(c, 'graphics'))
    return failures
  })

  await run.check('system', 'display', 'Display lists the screen and its menus open with choices', async (c) => {
    if (!await t.shell.call(part, 'display')) return ['no Display part']
    await sleep(1200)
    const menus = await t.shell.eval(`[...document.querySelectorAll('[data-system-part="display"] button[aria-haspopup="menu"]')].filter(b => b.checkVisibility()).map(b => b.textContent.trim()).slice(0, 12)`)
    c.evidence.menus = menus
    if (!menus.length) return ['no settings with choices']
    const failures = []
    for (let i = 0; i < Math.min(3, menus.length); i++) {
      await t.shell.click((n) => [...document.querySelectorAll('[data-system-part="display"] button[aria-haspopup="menu"]')].filter(b => b.checkVisibility())[n], i)
      const items = await k.menuItems()
      if (!items?.length) failures.push(`"${menus[i]}" opened no choices`)
      await t.shell.key('Escape')
      await sleep(300)
    }
    await c.shot('display', t.shell)
    failures.push(...await k.look(c, 'display'))
    return failures
  })

  await run.check('system', 'shaders', 'Shaders lists the games with their state', async (c) => {
    if (!await t.shell.call(part, 'shaders')) return ['no Shaders part']
    const games = await t.shell.waitFor(() => { const n = [...document.querySelectorAll('[data-system-part="shaders"] img')].filter(i => i.checkVisibility()).length; return n > 2 ? n : null }, { timeoutMs: 15000 })
    c.metric('games', games ?? 0)
    await c.shot('shaders', t.shell)
    const failures = games ? [] : ['no games listed']
    failures.push(...await k.look(c, 'shaders'))
    await t.shell.call(part, 'graphics')
    return failures
  })
}
