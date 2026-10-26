/**
 * WebHID over Exo's host: `navigator.hid` and `HIDDevice` as a browser gives
 * them, backed by messages to the frame's host (see README). The protocol
 * library's drivers call this exactly as they would call Chrome.
 */

type Collection = {
  usagePage: number
  usage: number
  inputIds: number[]
  outputIds: number[]
  featureIds: number[]
  inputLength: number
  outputLength: number
  featureLength: number
}

type Summary = { key: string; vendorId: number; productId: number; productName: string; collections: Collection[] }

type Reply = { ok: boolean; message?: string; data?: string; devices?: Summary[] }

// ------------------------------------------------------------------ host channel

const pending = new Map<number, { resolve: (r: Reply) => void; reject: (e: Error) => void; timer: number }>()
const eventHandlers = new Map<string, (data: unknown) => void>()
let nextId = 1

/** The page that frames this one (Exo runs it on its own origin); standalone, this page itself. */
const hostOrigin = window.location.ancestorOrigins?.[0] ?? window.location.origin

window.addEventListener('message', event => {
  if (event.source !== window.parent) return
  const m = event.data as { exo?: string; id?: number; result?: Reply; error?: string; event?: string; data?: unknown } | null
  if (!m || m.exo !== 'mouse') return
  if (typeof m.id === 'number') {
    const call = pending.get(m.id)
    if (!call) return
    pending.delete(m.id)
    window.clearTimeout(call.timer)
    if (m.error) call.reject(new Error(m.error))
    else call.resolve(m.result ?? { ok: false })
  } else if (m.event) {
    eventHandlers.get(m.event)?.(m.data)
  }
})

/** One message to the host; a reply that is not ok throws with its message. */
export function call(method: string, params: Record<string, unknown> = {}, timeoutMs = 10_000): Promise<Reply> {
  const id = nextId++
  return new Promise<Reply>((resolve, reject) => {
    const timer = window.setTimeout(() => {
      pending.delete(id)
      reject(new Error('Exo did not answer in time.'))
    }, timeoutMs)
    pending.set(id, {
      resolve: reply => reply.ok ? resolve(reply) : reject(new Error(reply.message ?? 'The device did not answer.')),
      reject,
      timer,
    })
    window.parent.postMessage({ exo: 'mouse', id, method, params }, hostOrigin)
  })
}

export function onHostEvent(name: string, handler: (data: unknown) => void): void {
  eventHandlers.set(name, handler)
}

/** Tells Exo something without a reply: { notice: 'one line' }, { state: {...} | null } or { profile: 'name' | null }. */
export function tell(message: { notice?: string; state?: { name: string; battery: number | null; charging: boolean; low: boolean; image: string | null; wireless: boolean | null } | null; profile?: string | null }): void {
  window.parent.postMessage({ exo: 'mouse', ...message }, hostOrigin)
}

// ------------------------------------------------------------------ bytes

function toBase64(data: BufferSource): string {
  const bytes = data instanceof ArrayBuffer ? new Uint8Array(data) : new Uint8Array(data.buffer, data.byteOffset, data.byteLength)
  let text = ''
  for (const b of bytes) text += String.fromCharCode(b)
  return btoa(text)
}

function fromBase64(text: string | undefined): DataView {
  const raw = atob(text ?? '')
  const bytes = new Uint8Array(raw.length)
  for (let i = 0; i < raw.length; i++) bytes[i] = raw.charCodeAt(i)
  return new DataView(bytes.buffer)
}

/** Windows pads every report of a collection to one length; that length, less the ID byte, is the report. */
function reports(ids: number[], length: number): HIDReportInfo[] {
  return length > 0 ? ids.map(reportId => ({ reportId, items: [{ reportSize: 8, reportCount: length - 1 }] })) : []
}

function collectionsOf(summary: Summary): HIDCollectionInfo[] {
  return summary.collections.map(c => ({
    usagePage: c.usagePage,
    usage: c.usage,
    inputReports: reports(c.inputIds, c.inputLength),
    outputReports: reports(c.outputIds, c.outputLength),
    featureReports: reports(c.featureIds, c.featureLength),
    children: [],
  }))
}

// ------------------------------------------------------------------ device

class ExoInputReportEvent extends Event implements HIDInputReportEvent {
  readonly device: HIDDevice
  readonly reportId: number
  readonly data: DataView
  constructor(device: HIDDevice, reportId: number, data: DataView) {
    super('inputreport')
    this.device = device
    this.reportId = reportId
    this.data = data
  }
}

class ExoConnectionEvent extends Event implements HIDConnectionEvent {
  readonly device: HIDDevice
  constructor(type: 'connect' | 'disconnect', device: HIDDevice) {
    super(type)
    this.device = device
  }
}

export class ExoHidDevice implements HIDDevice {
  readonly key: string
  readonly vendorId: number
  readonly productId: number
  readonly productName: string
  collections: readonly HIDCollectionInfo[]
  #opened = false
  #listeners = new Set<(event: HIDInputReportEvent) => void>()

  constructor(summary: Summary) {
    this.key = summary.key
    this.vendorId = summary.vendorId
    this.productId = summary.productId
    this.productName = summary.productName
    this.collections = collectionsOf(summary)
  }

  get opened(): boolean {
    return this.#opened
  }

  async open(): Promise<void> {
    if (this.#opened) return
    await call('hid.open', { device: this.key }, 15_000)
    this.#opened = true
  }

  async close(): Promise<void> {
    if (!this.#opened) return
    this.#opened = false
    await call('hid.close', { device: this.key })
  }

  async sendReport(reportId: number, data: BufferSource): Promise<void> {
    await call('hid.sendReport', { device: this.key, reportId, data: toBase64(data) })
  }

  async sendFeatureReport(reportId: number, data: BufferSource): Promise<void> {
    await call('hid.sendFeatureReport', { device: this.key, reportId, data: toBase64(data) })
  }

  async receiveFeatureReport(reportId: number): Promise<DataView> {
    return fromBase64((await call('hid.receiveFeatureReport', { device: this.key, reportId })).data)
  }

  /** Not in WebHID; a few drivers poll an input report this way when the host offers it. */
  async receiveInputReport(reportId: number): Promise<DataView> {
    return fromBase64((await call('hid.receiveInputReport', { device: this.key, reportId })).data)
  }

  addEventListener(type: 'inputreport', listener: (event: HIDInputReportEvent) => void): void {
    if (type === 'inputreport') this.#listeners.add(listener)
  }

  removeEventListener(type: 'inputreport', listener: (event: HIDInputReportEvent) => void): void {
    if (type === 'inputreport') this.#listeners.delete(listener)
  }

  deliver(reportId: number, data: DataView): void {
    const event = new ExoInputReportEvent(this, reportId, data)
    for (const listener of [...this.#listeners]) listener(event)
  }

  /** Refreshed from a new listing: the same physical device keeps its object, which drivers compare by identity. */
  update(summary: Summary): void {
    this.collections = collectionsOf(summary)
  }

  /** It left: nothing more will open it. */
  gone(): void {
    this.#opened = false
  }
}

// ------------------------------------------------------------------ navigator.hid

function matches(device: HIDDevice, filter: HIDDeviceFilter): boolean {
  if (filter.vendorId !== undefined && device.vendorId !== filter.vendorId) return false
  if (filter.productId !== undefined && device.productId !== filter.productId) return false
  if (filter.usagePage === undefined && filter.usage === undefined) return true
  return device.collections.some(c =>
    (filter.usagePage === undefined || c.usagePage === filter.usagePage) && (filter.usage === undefined || c.usage === filter.usage))
}

export class ExoHid {
  readonly #devices = new Map<string, ExoHidDevice>()
  readonly #handlers = { connect: new Set<(e: HIDConnectionEvent) => void>(), disconnect: new Set<(e: HIDConnectionEvent) => void>() }
  readonly #vendorIds: number[]
  /** The first listing is what was already there, not arrivals. */
  #listedOnce = false
  /** Listings run side by side (every change asks for one): one older than the last applied is stale. */
  #asked = 0
  #applied = 0

  constructor(vendorIds: number[]) {
    this.#vendorIds = vendorIds
    onHostEvent('hid.input', data => {
      const d = data as { device?: string; reportId?: number; data?: string }
      this.#devices.get(d.device ?? '')?.deliver(d.reportId ?? 0, fromBase64(d.data))
    })
    onHostEvent('hid.changed', () => { void this.getDevices().catch(() => undefined) })
  }

  /** Every present device from a vendor the drivers know; arrivals and departures fire connect and disconnect. */
  async getDevices(): Promise<HIDDevice[]> {
    const asked = ++this.#asked
    const reply = await call('hid.list', { vendorIds: this.#vendorIds })
    // Answered after a newer listing: it predates what that one saw, so a device it lacks has not left.
    if (asked < this.#applied) return [...this.#devices.values()]
    this.#applied = asked
    const seen = new Set<string>()
    const arrived: ExoHidDevice[] = []
    for (const summary of reply.devices ?? []) {
      seen.add(summary.key)
      const known = this.#devices.get(summary.key)
      if (known) known.update(summary)
      else {
        const device = new ExoHidDevice(summary)
        this.#devices.set(summary.key, device)
        arrived.push(device)
      }
    }
    for (const [key, device] of [...this.#devices]) {
      if (seen.has(key)) continue
      this.#devices.delete(key)
      device.gone()
      this.#emit('disconnect', device)
    }
    if (this.#listedOnce) for (const device of arrived) this.#emit('connect', device)
    this.#listedOnce = true
    return [...this.#devices.values()]
  }

  /** No chooser: Exo already shows only the mice its drivers know, so every match is granted. */
  async requestDevice(options: { filters: HIDDeviceFilter[] }): Promise<HIDDevice[]> {
    const devices = await this.getDevices()
    return devices.filter(d => options.filters.length === 0 || options.filters.some(f => matches(d, f)))
  }

  addEventListener(type: 'connect' | 'disconnect', listener: (event: HIDConnectionEvent) => void): void {
    this.#handlers[type]?.add(listener)
  }

  removeEventListener(type: 'connect' | 'disconnect', listener: (event: HIDConnectionEvent) => void): void {
    this.#handlers[type]?.delete(listener)
  }

  #emit(type: 'connect' | 'disconnect', device: HIDDevice): void {
    const event = new ExoConnectionEvent(type, device)
    for (const handler of [...this.#handlers[type]]) handler(event)
  }
}

let installed: ExoHid | null = null

/** Puts Exo's HID where the drivers look for it. */
export function installExoHid(vendorIds: number[]): ExoHid {
  installed = new ExoHid(vendorIds)
  Object.defineProperty(navigator, 'hid', { configurable: true, value: installed })
  return installed
}

/** The HID installed at start. */
export function exoHid(): ExoHid {
  if (!installed) throw new Error('Exo HID is not installed.')
  return installed
}
