// Capture: Capture settings, the Captures sheet's own, opened from the Capture menu and from the sheet's button, show
// what is saved. Then Snip from the title bar's Capture menu, round after round: the overlay shows at once, the snip
// opens in the editor drawn no larger than it is shown, and closing it leaves no memory behind and the page alive (a
// snip once came before the page ran out of memory). Each snip is of the screen under the cursor; it goes to the
// Recycle Bin as the round ends. Skipped without Exo Capture.
import { sleep } from '../cdp.mjs'

export async function run(t) {
  const { run, k } = t
  const rounds = t.quick ? 3 : 5

  await run.check('capture', 'settings', 'Capture settings opens from the Capture menu and the Captures sheet, Make recordings smaller as saved', async (c) => {
    const status = await t.host('capture.status')
    if (!status?.state?.present || !status?.settings) c.skip('Exo Capture is not installed or did not answer')
    await k.closeAll()
    await k.nav('Library')
    const menu = async (label) => {
      if (!await t.shell.eval(`(() => { const b = document.querySelector('.exo-capture-btn'); b?.click(); return !!b })()`)) return 'no Capture button in the title bar'
      if (!await t.shell.waitFor((l) => [...document.querySelectorAll('.exo-capture-menu [role="menuitem"]')].some(i => i.textContent.includes(l) && i.checkVisibility()), { timeoutMs: 3000, args: [label] }))
        return `the Capture menu has no ${label}`
      await t.shell.click((l) => [...document.querySelectorAll('.exo-capture-menu [role="menuitem"]')].find(i => i.textContent.includes(l)), label)
      return null
    }
    const shrink = () => t.shell.waitFor(() => { const s = document.querySelector('.exo-captures [role="switch"][aria-labelledby="set-capture-shrink"]'); return s?.checkVisibility() ? s.getAttribute('aria-checked') : null }, { timeoutMs: 5000 })
    const title = () => t.shell.eval(`document.getElementById('exo-captures-title')?.textContent.trim() ?? null`)
    const button = (l) => [...document.querySelectorAll('.exo-captures .exo-hub-head-actions button')].find(b => b.textContent.trim() === l)
    const failures = []
    const missing = await menu('Capture settings')
    if (missing) return [missing]
    const fromMenu = await shrink()
    c.evidence.saved = status.settings.autoShrink
    c.evidence.fromMenu = fromMenu
    await c.shot('capture-settings', t.shell)
    if (fromMenu === null) failures.push('the Capture menu did not open Capture settings with Make recordings smaller')
    else if (String(status.settings.autoShrink !== false) !== fromMenu) failures.push(`the switch shows ${fromMenu}, the saved setting is ${status.settings.autoShrink}`)
    // All captures goes back to the list; the sheet's own Capture settings comes again.
    await t.shell.click(button, 'All captures')
    if (!await t.shell.waitFor(() => document.getElementById('exo-captures-title')?.textContent.trim() === 'Captures', { timeoutMs: 3000 })) failures.push(`All captures left ${await title()} up`)
    await sleep(300)
    await t.shell.click(button, 'Capture settings')
    const fromSheet = await shrink()
    c.evidence.fromSheet = fromSheet
    if (fromSheet === null) failures.push("the sheet's Capture settings did not show Make recordings smaller")
    await k.closeAll()
    return failures
  })

  await run.check('capture', 'snips', 'a snip starts at once and leaves nothing behind', async (c) => {
    const status = await t.host('capture.status')
    if (!status?.state?.present) c.skip('Exo Capture is not installed')
    await k.closeAll()
    await k.nav('Library')
    const gc = () => t.shell.s.send('HeapProfiler.collectGarbage').catch(() => {})
    // One round first, so the editor's code and the add-on are warm in what is measured.
    const round = async () => {
      if (!await t.shell.eval(`(() => { const b = document.querySelector('.exo-capture-btn'); b?.click(); return !!b })()`)) return { error: 'no Capture button in the title bar' }
      if (!await t.shell.waitFor(() => [...document.querySelectorAll('.exo-capture-menu [role="menuitem"]')].some(i => i.textContent.includes('Snip') && i.checkVisibility()), { timeoutMs: 3000 }))
        return { error: 'the Capture menu did not open' }
      const waiter = t.win.snip('Return')
      await waiter.ready
      const started = Date.now()
      await t.shell.eval(`[...document.querySelectorAll('.exo-capture-menu [role="menuitem"]')].find(i => i.textContent.includes('Snip')).click()`)
      const shown = await waiter.done
      if (!shown.shown) return { error: 'the snip overlay never showed' }
      const editor = await t.shell.waitFor(() => {
        const canvas = document.querySelector('.exo-snip-editor canvas')
        const frame = canvas?.parentElement
        return canvas && canvas.width > 1 ? { width: canvas.width, height: canvas.height, frame: [frame.clientWidth, frame.clientHeight], dpr: devicePixelRatio } : null
      }, { timeoutMs: 15000 })
      const newest = (await t.host('capture.list', { limit: 1 }))?.items?.[0]
      if (editor) await t.shell.eval(`[...document.querySelectorAll('.exo-snip-editor button')].find(b => b.textContent === 'Cancel')?.click()`)
      await k.closeAll()
      if (newest && Date.parse(newest.created) >= started - 2000) await t.host('capture.item', { id: newest.id, action: 'delete' })
      return editor ? { ms: shown.at - started, editor } : { error: 'the snip did not open in the editor' }
    }
    const warm = await round()
    if (warm.error) return [warm.error]
    await gc(); await sleep(1500)
    const before = await k.metrics(), procBefore = await t.win.proc()
    const times = [], failures = []
    let editor = warm.editor
    for (let r = 0; r < rounds; r++) {
      const result = await round()
      if (result.error) { failures.push(result.error); break }
      times.push(result.ms)
      editor = result.editor
      await sleep(800)
    }
    await gc(); await sleep(1500)
    const after = await k.metrics(), procAfter = await t.win.proc()
    const lines = await t.exo.logSince(t.logMark)
    const crashed = lines.filter(l => l.text.startsWith('webview-process-failed'))
    const sorted = [...times].sort((a, b) => a - b), median = sorted[Math.floor(sorted.length / 2)] ?? 0
    const heap = (after.JSHeapUsedSize - before.JSHeapUsedSize) / 1048576
    const web = (procAfter.byName.msedgewebview2?.workingSetMB ?? 0) - (procBefore.byName.msedgewebview2?.workingSetMB ?? 0)
    c.metric('start.median', median, ' ms', 150)
    c.metric('start.worst', sorted.at(-1) ?? 0, ' ms', 300)
    c.metric('heap.growth', heap, ' MB', 10)
    c.metric('webview.growth', web, ' MB', 100)
    c.evidence.times = times
    c.evidence.editor = editor
    // The editor's canvas is the size it is shown (frame × device pixels), never the snip's full size beyond that.
    if (editor && editor.width > Math.ceil(editor.frame[0] * editor.dpr) + 1 && editor.height > Math.ceil(editor.frame[1] * editor.dpr) + 1)
      failures.push(`the editor drew ${editor.width}×${editor.height} in a ${editor.frame.join('×')} frame`)
    if (median > 1000) failures.push(`a snip took ${median} ms to show`)
    if (heap > 40) failures.push(`the page kept ${Math.round(heap)} MB after ${rounds} snips`)
    if (web > 400) failures.push(`WebView2 kept ${Math.round(web)} MB after ${rounds} snips`)
    if (crashed.length) failures.push(`the page's process failed: ${crashed[0].text}`)
    return failures
  })
}
