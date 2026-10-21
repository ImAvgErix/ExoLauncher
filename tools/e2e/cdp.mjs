// Minimal Chrome DevTools Protocol client for Exo's shell WebView.
// No dependencies: Node's global WebSocket and fetch.

import { inflateSync } from 'node:zlib'

export const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms))

/** RGBA pixels of a PNG as Chromium captures it (8-bit RGB or RGBA, not interlaced). */
function pngPixels(buffer) {
  let at = 8, width = 0, height = 0, channels = 4
  const chunks = []
  while (at < buffer.length) {
    const length = buffer.readUInt32BE(at), type = buffer.toString('ascii', at + 4, at + 8)
    const body = buffer.subarray(at + 8, at + 8 + length)
    if (type === 'IHDR') { width = body.readUInt32BE(0); height = body.readUInt32BE(4); channels = body[9] === 6 ? 4 : 3 }
    if (type === 'IDAT') chunks.push(body)
    at += 12 + length
  }
  const raw = inflateSync(Buffer.concat(chunks)), stride = width * channels, data = Buffer.alloc(width * height * 4)
  let previous = Buffer.alloc(stride)
  for (let y = 0; y < height; y++) {
    const filter = raw[y * (stride + 1)], line = Buffer.from(raw.subarray(y * (stride + 1) + 1, (y + 1) * (stride + 1)))
    for (let x = 0; x < stride; x++) {
      const a = x >= channels ? line[x - channels] : 0, b = previous[x], c = x >= channels ? previous[x - channels] : 0
      const pa = Math.abs(b - c), pb = Math.abs(a - c), pc = Math.abs(a + b - 2 * c)
      line[x] = (line[x] + [0, a, b, (a + b) >> 1, pa <= pb && pa <= pc ? a : pb <= pc ? b : c][filter]) & 255
    }
    for (let x = 0; x < width; x++) for (let k = 0; k < 3; k++) data[(y * width + x) * 4 + k] = line[x * channels + k]
    previous = line
  }
  return { width, height, data }
}

const SHELL_HOST = 'app.exo-launcher.local'

async function connect(wsUrl) {
  const ws = new WebSocket(wsUrl)
  await new Promise((resolve, reject) => { ws.onopen = resolve; ws.onerror = () => reject(new Error('CDP socket failed: ' + wsUrl)) })
  let id = 0
  const pending = new Map()
  const listeners = new Set()
  ws.onmessage = (message) => {
    const msg = JSON.parse(message.data)
    if (msg.id && pending.has(msg.id)) {
      const { resolve, reject } = pending.get(msg.id)
      pending.delete(msg.id)
      if (msg.error) reject(new Error(msg.error.message || JSON.stringify(msg.error)))
      else resolve(msg.result)
    } else if (msg.method) for (const listener of listeners) listener(msg)
  }
  ws.onclose = () => { for (const { reject } of pending.values()) reject(new Error('CDP socket closed')); pending.clear() }
  return {
    send(method, params = {}, sessionId = undefined) {
      return new Promise((resolve, reject) => {
        const i = ++id
        pending.set(i, { resolve, reject })
        ws.send(JSON.stringify(sessionId ? { id: i, method, params, sessionId } : { id: i, method, params }))
      })
    },
    on(fn) { listeners.add(fn); return () => listeners.delete(fn) },
    close() { try { ws.close() } catch { /* already closed */ } },
  }
}

/** Waits for the first page target on 127.0.0.1:<port> whose URL contains `urlPart`. */
export async function openPage(port, urlPart, timeoutMs = 60000) {
  const deadline = Date.now() + timeoutMs
  let lastError = null
  while (Date.now() < deadline) {
    try {
      const list = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json()
      const target = list.find((t) => t.type === 'page' && t.url.includes(urlPart))
      if (target) return new Shell(await connect(target.webSocketDebuggerUrl), port)
      lastError = new Error(`no ${urlPart} page listed yet: ` + list.map((t) => t.type + ' ' + t.url).join(', '))
    } catch (error) { lastError = error }
    await sleep(250)
  }
  throw new Error(`${urlPart} page not reachable on 127.0.0.1:${port} within ${timeoutMs} ms: ${lastError?.message}`)
}

/** Waits for the shell page target on 127.0.0.1:<port> and returns a page session. */
export const openShell = (port, timeoutMs = 60000) => openPage(port, SHELL_HOST, timeoutMs)

export class Shell {
  constructor(session, port) { this.s = session; this.port = port }
  close() { this.s.close() }

  async eval(expression) {
    const r = await this.s.send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true })
    if (r.exceptionDetails) throw new Error('page exception: ' + (r.exceptionDetails.exception?.description || r.exceptionDetails.text).slice(0, 600))
    return r.result.value
  }

  /** Evaluates a function body with JSON args: fn(...args) runs in the page. */
  call(fn, ...args) {
    return this.eval(`(${fn.toString()})(...${JSON.stringify(args)})`)
  }

  /** Polls a page predicate (function) until it returns a truthy value. */
  async waitFor(fn, { timeoutMs = 10000, interval = 100, args = [] } = {}) {
    const deadline = Date.now() + timeoutMs
    let last
    while (Date.now() < deadline) {
      last = await this.call(fn, ...args).catch((error) => { last = error; return null })
      if (last) return last
      await sleep(interval)
    }
    return null
  }

  async screenshot(path) {
    const { writeFile } = await import('node:fs/promises')
    const r = await this.s.send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false })
    await writeFile(path, Buffer.from(r.data, 'base64'))
    return path
  }

  /** Page pixels of one region at the page's own device scale (no re-render at another zoom). */
  async screenshotClip(path, { x, y, width, height }) {
    const { writeFile } = await import('node:fs/promises')
    const r = await this.s.send('Page.captureScreenshot', { format: 'png', clip: { x, y, width, height, scale: 1 } })
    await writeFile(path, Buffer.from(r.data, 'base64'))
    return path
  }

  /** RGBA pixels of one region of the page, at the page's own device scale. */
  async pixelsClip({ x, y, width, height }) {
    const r = await this.s.send('Page.captureScreenshot', { format: 'png', clip: { x, y, width, height, scale: 1 } })
    return pngPixels(Buffer.from(r.data, 'base64'))
  }

  /** One real mouse-wheel notch series at (x, y); positive deltaY scrolls down. */
  async wheel(x, y, deltaY) {
    await this.s.send('Input.dispatchMouseEvent', { type: 'mouseWheel', x, y, deltaX: 0, deltaY })
  }

  async clickAt(x, y) {
    await this.s.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x, y })
    await this.s.send('Input.dispatchMouseEvent', { type: 'mousePressed', x, y, button: 'left', clickCount: 1 })
    await this.s.send('Input.dispatchMouseEvent', { type: 'mouseReleased', x, y, button: 'left', clickCount: 1 })
  }

  /**
   * Real mouse click on the first visible element matched by a page-side finder.
   * `finder` is a function returning an Element or null. Returns a short label or null.
   */
  async click(finder, ...args) {
    const pt = await this.eval(`(() => {
      const el = (${finder.toString()})(...${JSON.stringify(args)})
      if (!el) return null
      el.scrollIntoView({ block: 'nearest', inline: 'nearest' })
      const r = el.getBoundingClientRect()
      if (r.width === 0 || r.height === 0) return null
      return { x: r.left + r.width / 2, y: r.top + r.height / 2, label: (el.getAttribute('aria-label') || el.textContent || el.tagName).trim().slice(0, 60) }
    })()`)
    if (!pt) return null
    await this.clickAt(pt.x, pt.y)
    return pt.label
  }

  /** A key press; `{ ctrl: true }` holds Ctrl (Ctrl+1, Ctrl+,). */
  async key(key, { ctrl = false } = {}) {
    const codes = { Escape: 27, Enter: 13, Tab: 9, ',': 188 }
    const vk = codes[key] ?? (/^[0-9A-Za-z]$/.test(key) ? key.toUpperCase().charCodeAt(0) : 0)
    const code = /^[0-9]$/.test(key) ? 'Digit' + key : key === ',' ? 'Comma' : /^[A-Za-z]$/.test(key) ? 'Key' + key.toUpperCase() : key
    const base = { key, code, windowsVirtualKeyCode: vk, nativeVirtualKeyCode: vk, modifiers: ctrl ? 2 : 0 }
    if (ctrl) await this.s.send('Input.dispatchKeyEvent', { type: 'rawKeyDown', key: 'Control', code: 'ControlLeft', windowsVirtualKeyCode: 17, nativeVirtualKeyCode: 17, modifiers: 2 })
    await this.s.send('Input.dispatchKeyEvent', { type: 'rawKeyDown', ...base })
    await this.s.send('Input.dispatchKeyEvent', { type: 'keyUp', ...base })
    if (ctrl) await this.s.send('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Control', code: 'ControlLeft', windowsVirtualKeyCode: 17, nativeVirtualKeyCode: 17, modifiers: 0 })
  }

  /** Text typed into the focused field, as one input. */
  async type(text) {
    await this.s.send('Input.insertText', { text })
  }

  /** A real right-click at the element's centre (the page gets its contextmenu); returns the point or null. */
  async rightClick(finder, ...args) {
    const pt = await this.eval(`(() => {
      const el = (${finder.toString()})(...${JSON.stringify(args)})
      if (!el) return null
      el.scrollIntoView({ block: 'nearest', inline: 'nearest' })
      const r = el.getBoundingClientRect()
      return r.width ? { x: Math.round(r.left + r.width / 2), y: Math.round(r.top + r.height / 2) } : null
    })()`)
    if (!pt) return null
    await this.s.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: pt.x, y: pt.y })
    await this.s.send('Input.dispatchMouseEvent', { type: 'mousePressed', x: pt.x, y: pt.y, button: 'right', clickCount: 1 })
    await this.s.send('Input.dispatchMouseEvent', { type: 'mouseReleased', x: pt.x, y: pt.y, button: 'right', clickCount: 1 })
    return pt
  }

  /** A real double-click (two presses, the second with clickCount 2) at the element's centre. */
  async doubleClick(finder, ...args) {
    const pt = await this.eval(`(() => {
      const el = (${finder.toString()})(...${JSON.stringify(args)})
      if (!el) return null
      el.scrollIntoView({ block: 'nearest', inline: 'nearest' })
      const r = el.getBoundingClientRect()
      return r.width ? { x: r.left + r.width / 2, y: r.top + r.height / 2 } : null
    })()`)
    if (!pt) return false
    await this.s.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: pt.x, y: pt.y })
    for (const clickCount of [1, 2]) {
      await this.s.send('Input.dispatchMouseEvent', { type: 'mousePressed', x: pt.x, y: pt.y, button: 'left', clickCount })
      await this.s.send('Input.dispatchMouseEvent', { type: 'mouseReleased', x: pt.x, y: pt.y, button: 'left', clickCount })
    }
    return true
  }
}

/**
 * A frame inside the shell page whose URL contains `urlPart`, with the same
 * eval/call/waitFor as a page. A frame in the shell's process gets an isolated
 * world; a frame in its own process (another site) is attached as a target.
 */
export async function openFrame(shell, urlPart, timeoutMs = 15000) {
  const deadline = Date.now() + timeoutMs
  const find = (node) => node.frame.url.includes(urlPart) ? node.frame : (node.childFrames ?? []).map(find).find(Boolean)
  while (Date.now() < deadline) {
    const { frameTree } = await shell.s.send('Page.getFrameTree')
    const frame = find(frameTree)
    if (frame) {
      try {
        const { executionContextId } = await shell.s.send('Page.createIsolatedWorld', { frameId: frame.id, worldName: 'exo-e2e', grantUniveralAccess: true })
        return new FrameShell(shell.s, { contextId: executionContextId })
      } catch { /* out of process: attach below */ }
      const { targetInfos } = await shell.s.send('Target.getTargets')
      const target = targetInfos.find((t) => t.url.includes(urlPart))
      if (target) {
        const { sessionId } = await shell.s.send('Target.attachToTarget', { targetId: target.targetId, flatten: true })
        return new FrameShell(shell.s, { sessionId })
      }
    }
    await sleep(250)
  }
  throw new Error(`no frame with ${urlPart} within ${timeoutMs} ms`)
}

class FrameShell {
  constructor(session, where) { this.s = session; this.where = where }
  close() { if (this.where.sessionId) this.s.send('Target.detachFromTarget', { sessionId: this.where.sessionId }).catch(() => {}) }
  async eval(expression) {
    const params = { expression, awaitPromise: true, returnByValue: true, ...(this.where.contextId ? { contextId: this.where.contextId } : {}) }
    const r = await this.s.send('Runtime.evaluate', params, this.where.sessionId)
    if (r.exceptionDetails) throw new Error('frame exception: ' + (r.exceptionDetails.exception?.description || r.exceptionDetails.text).slice(0, 600))
    return r.result.value
  }
  call(fn, ...args) { return this.eval(`(${fn.toString()})(...${JSON.stringify(args)})`) }
  async waitFor(fn, { timeoutMs = 10000, interval = 100, args = [] } = {}) {
    const deadline = Date.now() + timeoutMs
    while (Date.now() < deadline) {
      const last = await this.call(fn, ...args).catch(() => null)
      if (last) return last
      await sleep(interval)
    }
    return null
  }
}
