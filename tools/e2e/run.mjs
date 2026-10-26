#!/usr/bin/env node
// Exo's end-to-end suite: the installed app, driven the way a person drives it, checked the way a person would see
// it (the screen itself, not only the page's own picture).
//
//   node tools/e2e/run.mjs [--suites shell,rooms,...] [--skip zoom] [--quick] [--attach] [--keep] [--port 9339] [--out dir]
//   node tools/e2e/run.mjs --changed [base] [--plan]   # only the suites the changed files can reach (lib/affected.mjs)
//
// --changed picks the suites from the files changed against base (default HEAD, untracked files included); --plan only
// prints that choice and why, and exits without touching Exo, so a change can be sized before a 30-minute run.
// --suites names suites outright and wins over --changed.
//
// Suites (tools/e2e/suites/*.mjs) run in the order listed in SUITES. --attach drives an Exo already running with the
// debug port instead of restarting it; --keep leaves it running with the port afterwards; --quick does fewer rounds
// of the stress and performance loops. Before it starts, the run refuses to go on during a Discord call or while a
// game runs, and saves the files it may change (lib/exo.mjs GUARDED); at the end it closes Exo, puts those files back
// byte for byte, and starts Exo again. The report lands in e2e-reports/<time>/: summary.md, report.json,
// screenshots/, SHA256SUMS.txt.
//
// Exit code: 0 nothing failed, 1 a check failed, 2 the run itself could not go on.
import { join, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'
import { Run } from './lib/check.mjs'
import * as exo from './lib/exo.mjs'
import { win } from './lib/win.mjs'
import { kit } from './lib/kit.mjs'
import { sleep } from './cdp.mjs'
import { suitesFor, changedFiles, describePlan } from './lib/affected.mjs'

export const SUITES = ['shell', 'rooms', 'library', 'store', 'discord', 'music', 'system', 'devices', 'capture', 'settings', 'appearance', 'layout', 'motion', 'perf', 'stress', 'zoom']

const argv = process.argv.slice(2)
const flag = (name) => argv.includes('--' + name)
const opt = (name, fallback) => { const i = argv.indexOf('--' + name); return i >= 0 ? argv[i + 1] : fallback }
const repo = join(dirname(fileURLToPath(import.meta.url)), '..', '..')
const stamp = new Date().toISOString().replace(/[-:]/g, '').replace(/\..+/, '').replace('T', '-')
const out = opt('out', join(repo, 'e2e-reports', stamp))
const port = Number(opt('port', 9339))
const skip = new Set((opt('skip', '') || '').split(',').filter(Boolean))
// --changed [base]: the base is the next word unless that is another flag.
const changedAt = argv.indexOf('--changed')
const changedBase = changedAt >= 0 && argv[changedAt + 1] && !argv[changedAt + 1].startsWith('--') ? argv[changedAt + 1] : 'HEAD'
let picked = opt('suites', '') ? opt('suites', '').split(',') : SUITES
if (changedAt >= 0 && !opt('suites', '')) {
  const plan = suitesFor(changedFiles(repo, changedBase), SUITES)
  console.log(describePlan(plan, SUITES.length).join('\n'))
  picked = plan.suites
}
const chosen = picked.filter(s => SUITES.includes(s) && !skip.has(s))
if (flag('plan')) { console.log(chosen.length ? `would run: ${chosen.join(', ')}` : 'would run nothing'); process.exit(0) }
if (changedAt >= 0 && !chosen.length) { console.log('Nothing to run.'); process.exit(0) }

const run = new Run(out)
await run.init()
run.meta = { suites: chosen, quick: flag('quick'), attach: flag('attach') }

const t = { run, out, port, quick: flag('quick'), win, exo, shell: null, console: [], startedAt: null, logMark: 0 }
t.k = kit(t)
t.host = (method, params) => exo.host(t.shell, method, params)
/** Starts (or restarts) Exo with the debug port and extra environment; the suite's page session follows. */
t.relaunch = async (env = {}) => {
  try { t.shell?.close() } catch { /* already gone */ }
  if ((await exo.pids()).length) await exo.close()
  t.logMark = await exo.logMark()
  const started = await exo.launch({ port, env })
  t.shell = started.shell
  t.startedAt = started.startedAt
  t.console = await t.shell.collectConsole()
  await t.shell.waitFor(() => document.querySelector('.exo-titlebar') && !document.querySelector('.exo-boot'), { timeoutMs: 60000 })
  await t.k.warmedUp()
}

let exitCode = 0
const guard = new exo.Guard(join(out, 'guard'))
try {
  run.meta.version = await exo.version()
  console.log(`Exo ${run.meta.version}; suites: ${chosen.join(', ')}; report: ${out}`)

  // Never during a call (a restart ends it) or a game.
  if ((await exo.pids()).length) {
    const udp = await win.udp().catch(() => ({ count: 0 }))
    if (udp.count > 0) throw Object.assign(new Error('Exo is in a voice call; try again after it'), { code: 2 })
  }
  await guard.save()

  const attached = flag('attach') ? await exo.attach(port) : null
  if (attached) {
    t.shell = attached
    t.console = await t.shell.collectConsole()
    t.logMark = await exo.logMark()
    await t.k.warmedUp()
  } else {
    await t.relaunch()
  }
  // --allow-game: the owner said to run anyway; its real clicks and window moves may take focus from the game.
  if (!flag('allow-game') && await t.shell.eval(`!!document.querySelector('.exo-continue-card.is-playing')`))
    throw Object.assign(new Error('a game is running; try again after it'), { code: 2 })
  await win.front().catch(() => {})

  for (const name of chosen) {
    const suite = await import(`./suites/${name}.mjs`)
    console.log(`\n— ${name}`)
    try { await suite.run(t) } catch (error) {
      await run.check(name, 'suite', `the ${name} suite ran to its end`, () => [`stopped: ${error.message}`])
    }
    await t.k.closeAll().catch(() => {})
  }
} catch (error) {
  console.error('run stopped:', error.message)
  run.meta.stopped = error.message
  exitCode = error.code ?? 2
} finally {
  try { t.shell?.close() } catch { /* gone */ }
  try {
    // Attached and nothing to put back: Exo is left as it is. Otherwise it closes, its files go back, and it starts again.
    const changed = await guard.changed()
    if (!(flag('attach') && flag('keep') && !changed.length)) {
      if ((await exo.pids()).length) await exo.close()
      const restored = await guard.restore()
      run.meta.restored = restored.map(f => f.split(/[\\/]/).pop())
      await exo.launch(flag('keep') ? { port, waitPage: true } : {})
      if (flag('keep')) await sleep(1000)
    }
  } catch (error) { console.error('putting Exo back:', error.message); run.meta.restoreError = error.message }
  const report = await run.write()
  const n = report.counts
  console.log(`\n${n.PASS} passed, ${n.WARN} warnings, ${n.FAIL} failed, ${n.SKIP} skipped — ${join(out, 'summary.md')}`)
  if (!exitCode && n.FAIL) exitCode = 1
  process.exit(exitCode)
}
