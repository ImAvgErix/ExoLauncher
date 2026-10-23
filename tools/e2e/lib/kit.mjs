// What every suite uses: going to a room the way a person does (a real click on the Bar), waiting for the view to
// settle, closing what floats, switching Appearance through Settings, and the full look at a view (layout,
// contrast from the screen, the other theme's colors, broken pictures, unnamed controls, console errors).
import { sleep } from '../cdp.mjs'
import { pixelContrast } from './contrast.mjs'
import { a11yScan, brokenImages, layoutScan, roomState, settle, themeLeakScan } from './page.mjs'

export const ROOMS = ['Library', 'Store', 'Discord', 'Music', 'System', 'Devices']
export const THEMES = ['void', 'dark', 'light']
const THEME_LABEL = { system: 'Match Windows', light: 'Light', dark: 'Dark', void: 'Void' }

/** The Bar's tab for a room (its label without a count badge). */
export const roomTab = (name) => [...document.querySelectorAll('.exo-titlebar button, nav button')]
  .find(b => (b.getAttribute('aria-label') || b.textContent || '').trim().replace(/\s*\d+$/, '') === name)

export function kit(t) {
  const k = {
    settle: (quiet = 250, timeout = 6000) => t.shell.call(settle, quiet, timeout),
    /** After a start: the rooms warming out of sight (Room › is-backstage) have gone back to hidden, and the page is still. */
    warmedUp: async () => {
      await t.shell.waitFor(() => !document.querySelector('.exo-room.is-backstage'), { timeoutMs: 30000 })
      await t.shell.call(settle, 400, 10000)
    },
    state: () => t.shell.call(roomState),

    /** Closes whatever floats (menus, dialogs, a game's sheet, Settings) with Escape, up to five times. */
    async closeAll() {
      for (let i = 0; i < 5; i++) {
        const s = await k.state()
        if (!s.dialogs && !s.menus && !s.settings && !s.sheet) return
        await t.shell.key('Escape')
        await sleep(350)
      }
    },

    /** Goes to a room by its Bar tab and waits until it is current and still; returns the time it took (ms). */
    async nav(room) {
      await k.closeAll()
      const t0 = Date.now()
      const clicked = await t.shell.click(roomTab, room)
      if (!clicked) throw new Error(`no Bar tab for ${room}`)
      const ok = await t.shell.waitFor((name) => {
        const cur = document.querySelector('.exo-titlebar [aria-current="page"], nav [aria-current="page"]')
        return cur && (cur.getAttribute('aria-label') || cur.textContent || '').trim().replace(/\s*\d+$/, '') === name
      }, { timeoutMs: 8000, args: [room] })
      if (!ok) throw new Error(`${room} did not become the current room`)
      const ms = Date.now() - t0
      await k.settle()
      return ms
    },

    /** Settings › General › Appearance, chosen in its menu as a person would. */
    async setAppearance(theme) {
      await k.closeAll()
      // The gear toggles: pressed only while Settings isn't already showing.
      if (!await t.shell.eval(`!!document.querySelector('.exo-settings')?.checkVisibility()`)) await t.shell.click(() => document.querySelector('.exo-settings-btn'))
      const open = await t.shell.waitFor(() => !!document.querySelector('.exo-settings')?.checkVisibility(), { timeoutMs: 5000 })
      if (!open) throw new Error('Settings did not open')
      await k.settle()
      const row = () => document.getElementById('set-appearance')?.closest('.exo-set-item')?.querySelector('button[aria-haspopup="menu"]') ?? null
      await t.shell.waitFor(() => { const b = document.getElementById('set-appearance')?.closest('.exo-set-item')?.querySelector('button[aria-haspopup="menu"]'); return !!b && b.getBoundingClientRect().width > 0 }, { timeoutMs: 3000 })
      const trigger = await t.shell.click(row)
      if (!trigger) throw new Error('no Appearance menu')
      await sleep(300)
      const picked = await t.shell.click((label) => [...document.querySelectorAll('[role^="menuitem"]')].find(i => i.textContent.trim() === label), THEME_LABEL[theme])
      if (!picked) throw new Error(`no ${THEME_LABEL[theme]} in the Appearance menu`)
      const applied = await t.shell.waitFor((want) => want === 'system' || document.documentElement.dataset.exoTheme === want, { timeoutMs: 5000, args: [theme] })
      await k.closeAll()
      await k.settle()
      if (!applied) throw new Error(`Appearance ${theme} did not apply`)
    },

    /** Sets a field's text as typing does (React hears it); '' clears it. */
    fill: (selector, text) => t.shell.call((sel, value) => {
      const field = document.querySelector(sel)
      if (!field) return false
      field.focus()
      const setter = Object.getOwnPropertyDescriptor(field instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype, 'value').set
      setter.call(field, value)
      field.dispatchEvent(new Event('input', { bubbles: true }))
      return true
    }, selector, text),

    /** The visible menu's items, or null. */
    menuItems: () => t.shell.call(() => { const m = [...document.querySelectorAll('[role="menu"]')].find(x => x.getBoundingClientRect().width); return m ? [...m.querySelectorAll('[role^="menuitem"]')].map(i => i.textContent.trim()) : null }),

    /** Console errors and exceptions since `mark` (an index into t.console). */
    consoleSince: (mark) => t.console.slice(mark).filter(e => !/favicon|ERR_BLOCKED_BY_CLIENT|ResizeObserver loop/i.test(e.text)),

    /**
     * The full look at the current view, as failures and warnings on check `c`: layout faults, words below 3:1
     * (fail) or 4.5:1 (warn), big blocks in the other theme's color, broken pictures, unnamed controls.
     */
    async look(c, label) {
      const failures = []
      // Chromium draws a page transition from snapshots: wait until it is over, or the words can't be measured.
      await k.settle(250, 8000)
      const s = await k.state()
      if (s.errorScreen) failures.push(`${label}: an error screen shows`)
      if (s.vt) failures.push(`${label}: a page transition is stuck`)
      const layout = await t.shell.call(layoutScan)
      if (layout.sideScroll) failures.push(`${label}: the page scrolls sideways`)
      if (layout.offscreen.length) failures.push(`${label}: controls cut off by the window: ${layout.offscreen.slice(0, 4).join(' | ')}`)
      if (layout.covered.length) c.warn(`${label}: controls under something: ${layout.covered.slice(0, 4).join(' | ')}`)
      if (layout.cutText.length) c.warn(`${label}: words cut off: ${layout.cutText.slice(0, 4).join(' | ')}`)
      const contrast = await pixelContrast(t.shell)
      c.metric(`${label}.labels`, contrast.checked)
      if (contrast.unreadable.length) failures.push(`${label}: unreadable words: ${contrast.unreadable.slice(0, 4).map(b => `"${b.text}" ${b.ratio}:1`).join(' | ')}`)
      const low = contrast.bad.filter(b => b.ratio >= 3)
      if (low.length) c.warn(`${label}: words under ${low[0].need}:1: ${low.slice(0, 4).map(b => `"${b.text}" ${b.ratio}:1`).join(' | ')}`)
      const leak = await t.shell.call(themeLeakScan)
      if (leak.length) failures.push(`${label}: the other theme's color: ${leak.slice(0, 3).join(' | ')}`)
      const broken = await t.shell.call(brokenImages)
      if (broken.length) c.warn(`${label}: pictures that failed: ${broken.slice(0, 3).join(' | ')}`)
      const a11y = await t.shell.call(a11yScan)
      if (a11y.unnamed.length) c.warn(`${label}: controls without a name: ${a11y.unnamed.slice(0, 5).join(', ')}`)
      return failures
    },

    /** The page's own counters (Chromium): JS heap, DOM nodes, listeners, layouts, style recalcs. */
    async metrics() {
      await t.shell.s.send('Performance.enable').catch(() => {})
      const { metrics } = await t.shell.s.send('Performance.getMetrics')
      return Object.fromEntries(metrics.map(m => [m.name, m.value]))
    },
  }
  return k
}
