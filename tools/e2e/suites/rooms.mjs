// Every room in every Appearance: chosen through Settings as a person would, then each room looked at whole (layout,
// words readable as they show on screen, no block in the other theme's color, its page the Appearance's page color,
// pictures, named controls, no console errors), with the screen checked against the page once per Appearance.
import { sleep } from '../cdp.mjs'
import { ROOMS, THEMES } from '../lib/kit.mjs'
import { screenMatchesPage } from './shell.mjs'

/** What a room shows once it has loaded (its own content, not a spinner). */
const READY = {
  Library: () => document.querySelectorAll('.exo-game-grid button[data-game-id]').length > 0,
  Store: () => !!document.querySelector('.exo-store-hero, .exo-store-row, .exo-store-empty'),
  Discord: () => !!document.querySelector('main.exo-dc, main.exo-dc-signin'),
  Music: () => !!document.querySelector('.exo-music-side, .exo-music-panel, [class*="exo-music"] h1, [class*="exo-music"] h2'),
  PC: () => !!document.querySelector('.exo-pc-page:not([hidden]) .exo-system-panel'),
  Devices: () => !!document.querySelector('iframe[src*="mouse.exo-launcher.local"], .exo-devices'),
}

/**
 * What paints over the room's page edge to edge (DESIGN.md › principle 4: every room's background is the Appearance's
 * page color): a fill other than the page's, or glass. System's room once drew one big glass pane under its panels.
 */
const COVERS = () => {
  const room = [...document.querySelectorAll('.exo-room')].find(r => !r.classList.contains('is-backstage') && !r.classList.contains('exo-set-room') && r.checkVisibility())
  if (!room) return []
  const box = room.getBoundingClientRect(), page = getComputedStyle(document.body).backgroundColor
  const found = []
  for (const el of [room, ...room.querySelectorAll('*')]) {
    const r = el.getBoundingClientRect()
    if (r.left > box.left + 1 || r.top > box.top + 1 || r.right < box.right - 1 || r.bottom < box.bottom - 1) continue
    for (const pseudo of [null, '::before', '::after']) {
      const s = getComputedStyle(el, pseudo)
      if ((pseudo && s.content === 'none') || s.display === 'none' || s.visibility === 'hidden' || s.opacity === '0') continue
      if ((s.backgroundColor !== 'rgba(0, 0, 0, 0)' && s.backgroundColor !== page) || s.backdropFilter !== 'none')
        found.push(`${el.tagName.toLowerCase()}.${[...el.classList].slice(0, 2).join('.')}${pseudo ?? ''} (${s.backgroundColor}${s.backdropFilter === 'none' ? '' : ', glass'})`)
    }
  }
  return found.slice(0, 3)
}

export async function run(t) {
  const { run, k } = t
  const before = (await t.host('settings.get')).appearance ?? 'system'
  for (const theme of THEMES) {
    const applied = await run.check('rooms', `appearance-${theme}`, `Settings › Appearance › ${theme} applies`, async (c) => {
      await k.setAppearance(theme)
      const now = await t.shell.eval(`({ theme: document.documentElement.dataset.exoTheme, bg: getComputedStyle(document.body).backgroundColor, saved: localStorage.getItem('exo.appearance') })`)
      c.evidence.now = now
      const want = { void: 'rgb(0, 0, 0)', dark: 'rgb(18, 18, 18)', light: 'rgb(245, 245, 245)' }[theme]
      const failures = []
      if (now.theme !== theme) failures.push(`the page shows ${now.theme}`)
      if (now.bg !== want) failures.push(`the page's background is ${now.bg}, not ${want}`)
      if (now.saved !== theme) failures.push(`the first frame's copy says ${now.saved}`)
      const saved = (await t.host('settings.get')).appearance
      if (saved !== theme) failures.push(`the host saved ${saved}`)
      return failures
    })
    if (applied.status === 'FAIL') continue
    for (const room of ROOMS) {
      await run.check('rooms', `${theme}-${room.toLowerCase()}`, `${room} in ${theme} looks right`, async (c) => {
        const mark = t.console.length
        const ms = await k.nav(room)
        c.metric('switch', ms, ' ms', 600)
        const ready = await t.shell.waitFor(READY[room], { timeoutMs: 15000 })
        if (!ready) return [`${room} showed nothing of its own in 15 s`]
        await sleep(800)
        await k.settle()
        const failures = await k.look(c, room)
        const covers = await t.shell.call(COVERS)
        if (covers.length) failures.push(`the page is not the ${theme} page: ${covers.join(', ')}`)
        await c.shot(room.toLowerCase(), t.shell)
        if (room === 'Library') failures.push(...await screenMatchesPage(t, c, `${theme}-library`))
        const errors = k.consoleSince(mark).filter(e => e.level !== 'warning' && !/Failed to load resource/.test(e.text))
        if (errors.length) failures.push(`console: ${errors.slice(0, 2).map(e => e.text.slice(0, 140)).join(' | ')}`)
        return failures
      }, { shell: t.shell })
    }
  }
  await k.setAppearance(before).catch(() => {})
}
