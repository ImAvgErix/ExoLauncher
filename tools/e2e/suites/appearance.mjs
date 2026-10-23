// Appearance beyond the rooms: what floats (a game's sheet, Ctrl+K, a menu, Settings, Discord's settings) in every
// theme, Match Windows following Windows' light or dark live, and the first frame after a start already in the
// chosen theme (no black flash under Light).
import { sleep } from '../cdp.mjs'
import { readPng } from '../lib/image.mjs'
import { THEMES } from '../lib/kit.mjs'

const OVERLAYS = {
  'game sheet': async (t) => { await t.k.nav('Library'); await t.shell.click(() => [...document.querySelectorAll('.exo-game-grid button[data-game-id]')].find(b => !b.closest('.exo-folder-tile')) ?? document.querySelector('.exo-continue-open')); return t.shell.waitFor(() => !!document.querySelector('.exo-game-overlay-stage .exo-game-title'), { timeoutMs: 5000 }) },
  'search': async (t) => {
    await t.k.nav('Library')
    for (let i = 0; i < 2; i++) {
      await t.shell.key('k', { ctrl: true })
      if (await t.shell.waitFor(() => document.activeElement?.getAttribute('role') === 'combobox', { timeoutMs: 2500 })) break
      await t.k.closeAll()
    }
    await t.shell.type('a')
    return t.shell.waitFor(() => document.querySelectorAll('[role="dialog"] [role="option"]').length > 2, { timeoutMs: 4000 })
  },
  'menu': async (t) => { await t.k.nav('Library'); await t.shell.click(() => document.querySelector('.exo-library-add')); return t.shell.waitFor(() => [...document.querySelectorAll('[role="menu"]')].some(m => m.getBoundingClientRect().width), { timeoutMs: 3000 }) },
  'settings': async (t) => { await t.k.closeAll(); await t.shell.click(() => document.querySelector('.exo-settings-btn')); return t.shell.waitFor(() => !!document.querySelector('.exo-settings')?.checkVisibility(), { timeoutMs: 4000 }) },
  'discord settings': async (t) => { await t.k.nav('Discord'); if (!await t.shell.waitFor(() => !!document.querySelector('main.exo-dc'), { timeoutMs: 15000 })) return 'skip'; await t.shell.click(() => document.querySelector('main.exo-dc button[aria-label="Discord settings"]')); return t.shell.waitFor(() => !!document.querySelector('.exo-dc-settings'), { timeoutMs: 5000 }) },
}

export async function run(t) {
  const { run, k } = t
  const before = (await t.host('settings.get')).appearance ?? 'system'

  for (const theme of THEMES) {
    await run.check('appearance', `overlays-${theme}`, `what floats reads right in ${theme}`, async (c) => {
      await k.setAppearance(theme)
      const failures = []
      for (const [name, open] of Object.entries(OVERLAYS)) {
        const shown = await open(t)
        if (shown === 'skip') continue
        if (!shown) {
          c.evidence[`${name}-state`] = await k.state()
          c.evidence[`${name}-active`] = await t.shell.eval(`document.activeElement?.tagName + '.' + (document.activeElement?.className?.toString().slice(0, 40) ?? '')`)
          await c.shot(`${name.replace(/ /g, '-')}-failed`, t.shell)
          failures.push(`${name} did not open`); await k.closeAll(); continue
        }
        await sleep(600)
        failures.push(...await k.look(c, name))
        await c.shot(name.replace(/ /g, '-'), t.shell)
        await k.closeAll()
        await sleep(300)
      }
      return failures
    }, { shell: t.shell })
  }

  await run.check('appearance', 'match-windows', 'Match Windows follows Windows\' light and dark as they change', async (c) => {
    await k.setAppearance('system')
    const seen = {}
    for (const mode of ['light', 'dark']) {
      await t.shell.s.send('Emulation.setEmulatedMedia', { features: [{ name: 'prefers-color-scheme', value: mode }] })
      seen[mode] = await t.shell.waitFor((m) => document.documentElement.dataset.exoTheme === m ? m : null, { timeoutMs: 3000, args: [mode] })
    }
    await t.shell.s.send('Emulation.setEmulatedMedia', { features: [] })
    c.evidence.seen = seen
    return ['light', 'dark'].filter(m => seen[m] !== m).map(m => `Windows in ${m} left Exo in ${seen[m] ?? 'the other theme'}`)
  })

  await run.check('appearance', 'first-frame', 'Exo starts in Light without a black frame', async (c) => {
    await k.setAppearance('light')
    await t.shell.close()
    await t.exo.close()
    const launched = t.exo.launch({ port: t.port, waitPage: false })
    await launched
    // The first frames the screen shows: captured as soon as the window exists.
    const frames = []
    const deadline = Date.now() + 8000
    while (Date.now() < deadline && frames.length < 6) {
      const w = await t.win.window().catch(() => null)
      if (w && w.client.w > 200) {
        const file = c.file(`frame-${frames.length}`)
        await t.win.capture(file).catch(() => null)
        try { const img = await readPng(file); let sum = 0, n = 0; for (let i = 0; i < img.data.length; i += 4 * 97) { sum += 0.2126 * img.data[i] + 0.7152 * img.data[i + 1] + 0.0722 * img.data[i + 2]; n++ } frames.push(Math.round(sum / n)) } catch { /* not drawn yet */ }
      }
      await sleep(250)
    }
    c.evidence.meanLuminance = frames
    // Back to a page session for the rest of the run.
    t.shell = await (await import('../cdp.mjs')).openShell(t.port, 60000)
    t.console = await t.shell.collectConsole()
    await t.shell.waitFor(() => !!document.querySelector('.exo-titlebar'), { timeoutMs: 60000 })
    await k.warmedUp()
    if (!frames.length) return ['no frame could be captured']
    const dark = frames.filter(l => l < 90)
    return dark.length ? [`${dark.length} of ${frames.length} early frames were dark (mean ${dark.join(', ')})`] : []
  })

  for (const theme of THEMES) {
    await run.check('appearance', `glass-${theme}`, `glass is glass in ${theme}: its filters are in the page and the Bar shows what is behind it`, async (c) => {
      await k.setAppearance(theme)
      await k.nav('Library')
      const scan = await t.shell.eval(`(() => {
        if (document.documentElement.dataset.exoGlass === 'solid') return { solid: true }
        const glass = [...document.querySelectorAll('.exo-glass')].filter(e => e.checkVisibility() && !e.parentElement.closest('.exo-glass'))
        const flat = [], missing = new Set()
        for (const e of glass) {
          const bf = getComputedStyle(e).backdropFilter
          for (const m of bf.matchAll(/url\\("?#([\\w-]+)"?\\)/g)) if (!document.getElementById(m[1])) missing.add(m[1])
          if (bf === 'none') flat.push(e.className.toString().slice(0, 60))
        }
        const r = document.querySelector('.exo-top-rooms')?.getBoundingClientRect()
        return { count: glass.length, flat, missing: [...missing], bar: r && { x: r.left + 8, y: r.top + 4, width: r.width - 16, height: r.height - 8 } }
      })()`)
      c.evidence.scan = scan
      if (scan.solid) c.skip('glass is solid (Settings or Windows contrast)')
      const failures = []
      if (scan.missing.length) failures.push(`filters not in the page: ${scan.missing.join(', ')}`)
      if (scan.flat.length) failures.push(`glass with no backdrop: ${scan.flat.slice(0, 3).join(' | ')}`)
      if (!scan.bar) return [...failures, 'no Rooms capsule']
      // A sheet of the other brightness slid under the Bar: real glass changes over it (Light's frost sits
      // over a near-white page, so it gets a black one).
      const mean = (img) => { let s = 0, n = 0; for (let i = 0; i < img.data.length; i += 16) { s += 0.2126 * img.data[i] + 0.7152 * img.data[i + 1] + 0.0722 * img.data[i + 2]; n++ } return s / n }
      await t.shell.eval(`for (const e of document.querySelectorAll('.exo-top-rooms *')) e.style.setProperty('color', 'transparent', 'important')`)
      await sleep(150)
      const over = mean(await t.shell.pixelsClip(scan.bar))
      const sheet = over > 128 ? '#000' : '#fff'
      await t.shell.eval(`(() => { const d = document.createElement('div'); d.id = 'exo-e2e-sheet'; d.style.cssText = 'position:fixed;inset:0 0 auto 0;height:120px;background:${sheet};z-index:39;pointer-events:none'; document.body.append(d) })()`)
      await sleep(250)
      const under = mean(await t.shell.pixelsClip(scan.bar))
      await t.shell.eval(`document.getElementById('exo-e2e-sheet')?.remove(); for (const e of document.querySelectorAll('.exo-top-rooms *')) e.style.removeProperty('color')`)
      c.evidence.bar = { over: Math.round(over), sheet, under: Math.round(under) }
      if (Math.abs(under - over) < 10) failures.push(`the Bar stays ${Math.round(over)} over a ${sheet === '#000' ? 'black' : 'white'} sheet (${Math.round(under)}): it doesn't show what is behind it`)
      return failures
    })
  }

  await k.setAppearance(before).catch(() => {})
}
