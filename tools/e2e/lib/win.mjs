// Node side of lib/win.ps1: the screen as shown, the real mouse, Exo's window and its processes.
import { execFile, spawn } from 'node:child_process'
import { fileURLToPath } from 'node:url'

const script = fileURLToPath(new URL('./win.ps1', import.meta.url))

function run(command, args = {}) {
  const argv = ['-NoProfile', '-NonInteractive', '-File', script, '-Command', command]
  for (const [k, v] of Object.entries(args)) argv.push('-' + k, String(v))
  return new Promise((resolve, reject) => {
    execFile('pwsh', argv, { windowsHide: true, timeout: 60000, maxBuffer: 1 << 20 }, (error, stdout, stderr) => {
      if (error) return reject(new Error(`win.ps1 ${command}: ${(stderr || error.message).trim().slice(0, 400)}`))
      try { resolve(JSON.parse(stdout.trim().split(/\r?\n/).pop())) } catch { reject(new Error(`win.ps1 ${command}: no JSON in ${stdout.slice(0, 200)}`)) }
    })
  })
}

export const win = {
  window: () => run('window'),
  monitors: () => run('monitors').then(m => Array.isArray(m) ? m : [m]),
  front: () => run('front'),
  /** The screen under Exo's client area as a PNG at `out`; returns the window's info at that moment. */
  capture: (out) => run('capture', { Out: out }),
  click: (x, y) => run('click', { X: Math.round(x), Y: Math.round(y) }),
  move: (x, y) => run('move', { X: Math.round(x), Y: Math.round(y) }),
  place: (x, y, w, h) => run('place', { X: x, Y: y, W: w, H: h }),
  maximize: () => run('maximize'),
  restore: () => run('restore'),
  proc: () => run('proc'),
  udp: () => run('udp'),
  /** Visible windows on a monitor belonging to processes with this name: { running, shown }. */
  shown: (name) => run('shown', { Name: name }),
  /** Which of Exo's own assemblies shipped without ReadyToRun code: { checked, plain }. */
  precompiled: () => run('precompiled'),
  /**
   * Waits for Exo Capture's snip overlay and gives it Return (snip the screen under the cursor) or Escape (cancel).
   * `ready` settles once the waiter is watching (start the snip after it); `done` with { shown, at } (Unix ms).
   */
  snip(key) {
    const child = spawn('pwsh', ['-NoProfile', '-NonInteractive', '-File', script, '-Command', 'snip', '-Name', key], { windowsHide: true })
    let out = '', err = '', readied
    const ready = new Promise(resolve => { readied = resolve })
    child.stdout.on('data', chunk => { out += chunk; if (out.includes('"ready"')) readied() })
    child.stderr.on('data', chunk => { err += chunk })
    const done = new Promise((resolve, reject) => child.on('close', () => {
      readied()
      try { resolve(JSON.parse(out.trim().split(/\r?\n/).pop())) } catch { reject(new Error(`win.ps1 snip: ${(err || out).trim().slice(0, 400)}`)) }
    }))
    return { ready, done }
  },
}
