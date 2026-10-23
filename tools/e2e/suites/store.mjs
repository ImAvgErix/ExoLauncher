// The Store, used: its shelves, the banner's picks, a shelf's arrows, search, and a game's page (with its
// requirements against this PC). Nothing is bought or wishlisted.
import { sleep } from '../cdp.mjs'

export async function run(t) {
  const { run, k } = t
  await k.nav('Store')
  await t.shell.waitFor(() => document.querySelector('.exo-store-hero') && document.querySelectorAll('.exo-store-row').length > 1, { timeoutMs: 20000 })

  await run.check('store', 'shelves', 'the Store shows its banner and shelves, each with games', async (c) => {
    const rows = await t.shell.eval(`[...document.querySelectorAll('section.exo-store-row')].map(r => ({ title: r.querySelector('h2, h3')?.textContent.trim(), items: r.querySelectorAll('button, a').length }))`)
    c.evidence.rows = rows
    const failures = []
    if (!await t.shell.eval(`!!document.querySelector('.exo-store-hero')`)) failures.push('no banner')
    if (rows.length < 2) failures.push(`only ${rows.length} shelves`)
    for (const r of rows) if (r.items < 2) failures.push(`${r.title} is empty`)
    return failures
  })

  await run.check('store', 'banner-pick', 'picking another game beside the banner shows it in the banner', async (c) => {
    const before = await t.shell.eval(`document.querySelector('.exo-store-hero-stage')?.getAttribute('aria-label')`)
    const picked = await t.shell.click(() => document.querySelectorAll('.exo-store-hero-pick:not(.is-on)')[1] ?? null)
    if (!picked) c.skip('one game in the banner')
    const after = await t.shell.waitFor((b) => { const now = document.querySelector('.exo-store-hero-stage')?.getAttribute('aria-label'); return now && now !== b ? now : null }, { timeoutMs: 3000, args: [before] })
    c.evidence.before = before
    c.evidence.after = after
    return after ? [] : ['the banner did not change']
  })

  await run.check('store', 'shelf-arrows', 'a shelf\'s next arrow scrolls it', async (c) => {
    const moved = await t.shell.eval(`(async () => {
      const row = [...document.querySelectorAll('section.exo-store-row')].find(r => { const s = r.querySelector('[class*="track"], [class*="scroll"]'); return s && s.scrollWidth > s.clientWidth + 10 })
      if (!row) return null
      const track = row.querySelector('[class*="track"], [class*="scroll"]')
      const next = [...row.querySelectorAll('button')].find(b => /^More /.test(b.getAttribute('aria-label') || '') && !b.disabled)
      if (!next) return 'no next arrow'
      const x0 = track.scrollLeft
      next.click()
      await new Promise(r => setTimeout(r, 700))
      const x1 = track.scrollLeft
      track.scrollLeft = 0
      return x1 > x0 ? true : 'did not scroll'
    })()`)
    if (moved === null) c.skip('no shelf wider than the room')
    return moved === true ? [] : [moved]
  })

  await run.check('store', 'search', 'searching the store lists matching games', async (c) => {
    await k.fill('.exo-store-search input', 'portal')
    // Real cards, not the loading grid's twelve placeholders (which this once counted as results).
    const results = await t.shell.waitFor(() => { const n = document.querySelectorAll('.exo-store-grid .exo-store-card:not(.is-skeleton)').length; return n || null }, { timeoutMs: 15000 })
    c.metric('results', results ?? 0)
    await c.shot('results', t.shell)
    await k.fill('.exo-store-search input', '')
    await sleep(800)
    return results ? [] : ['no results for "portal"']
  })

  await run.check('store', 'page', 'a game\'s page opens with its price, About and requirements against this PC', async (c) => {
    await t.shell.waitFor(() => document.querySelectorAll('section.exo-store-row').length > 1, { timeoutMs: 10000 })
    const t0 = Date.now()
    // The banner's game opens in the Store (Free on Epic's cards open Epic's own site: never clicked here).
    const opened = await t.shell.click(() => document.querySelector('.exo-store-hero-stage'))
    if (!opened) return ['no game to open']
    const page = await t.shell.waitFor(() => document.querySelector('.exo-sgp') && document.querySelector('.exo-sgp h1, .exo-sgp h2, .exo-sgp-side h2')?.textContent.trim(), { timeoutMs: 15000 })
    c.metric('open', Date.now() - t0, ' ms', 1500)
    if (!page) return ['the page did not open']
    await t.shell.waitFor(() => !document.querySelector('.exo-sgp-placeholder'), { timeoutMs: 15000 })
    await sleep(600)
    const parts = await t.shell.eval(`({ about: /About/.test(document.querySelector('.exo-sgp')?.innerText ?? ''), reqs: /System requirements/.test(document.querySelector('.exo-sgp')?.innerText ?? ''), thisPc: /This PC/.test(document.querySelector('.exo-sgp')?.innerText ?? ''), buy: !!document.querySelector('.exo-sgp-buy') })`)
    c.evidence.title = page
    c.evidence.parts = parts
    await c.shot('page', t.shell)
    const failures = await k.look(c, 'page')
    if (!parts.buy) failures.push('no buying section')
    if (!parts.about) c.warn('no About')
    if (parts.reqs && !parts.thisPc) failures.push('requirements without this PC')
    await t.shell.key('Escape')
    if (!await t.shell.waitFor(() => !document.querySelector('.exo-sgp'), { timeoutMs: 3000 })) failures.push('Escape did not close the page')
    return failures
  })
}
