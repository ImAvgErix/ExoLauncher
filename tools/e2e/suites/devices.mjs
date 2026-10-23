// Devices: Exo Mouse's page loads in its frame, finds the mouse (or says there is none), and wears Exo's Appearance.
import { openFrame } from '../cdp.mjs'

export async function run(t) {
  const { run, k } = t
  await k.nav('Devices')

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
