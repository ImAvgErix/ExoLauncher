import { host, onHostEvent, type HidReply } from './host'
import { thumbUrl } from './coverThumb'
import { publishMouseNotice, publishMouseProfile, publishMouseState } from './mouseState'
import { report } from './report'
import { onTextScale, textScale } from './exoTheme'

/** The HID calls the mouse module may make. */
const HID: Record<string, (p: Record<string, unknown>) => Promise<HidReply>> = {
  'hid.list': host.hidList,
  'hid.open': host.hidOpen,
  'hid.close': host.hidClose,
  'hid.sendReport': host.hidSendReport,
  'hid.sendFeatureReport': host.hidSendFeatureReport,
  'hid.receiveFeatureReport': host.hidReceiveFeatureReport,
  'hid.receiveInputReport': host.hidReceiveInputReport,
}

/**
 * Each exact source id to its library card id. Profiles link to card ids, while
 * game.runState names the exact source that started, so a card owned on two
 * stores still switches its profile.
 */
let cardOf = new Map<string, string>()

async function readLibrary() {
  const library = await host.getLibrary()
  const next = new Map<string, string>()
  for (const g of library.games ?? []) {
    next.set(g.id, g.id)
    for (const v of g.variants ?? []) if (!next.has(v.id)) next.set(v.id, g.id)
  }
  cardOf = next
  return library
}

/** The card a source id belongs to, reading the library again for a source not seen yet. */
async function cardId(id: string): Promise<string> {
  if (!cardOf.has(id)) await readLibrary().catch(() => undefined)
  return cardOf.get(id) ?? id
}

/**
 * The installed games, by id, title and Exo's local cover thumbnail, so a mouse profile can be linked
 * to a game. Exo then tells the module when games start and stop (game.runState).
 */
async function installedGames() {
  const library = await readLibrary()
  const games = (library.games ?? [])
    .filter(g => g.installed)
    // The cover is Exo's own cached thumbnail (a local URL), never a store's.
    .map(g => ({ id: g.id, title: g.title, cover: g.coverUrl?.startsWith('https://covers.exo-launcher.local/') ? thumbUrl(g.coverUrl, 240) : null }))
    .sort((a, b) => a.title.localeCompare(b.title))
  return { ok: true, games }
}

/** The module's own reads and writes besides HID: the games list and its saved profiles. */
const EXO: Record<string, (p: Record<string, unknown>) => Promise<unknown>> = {
  'exo.games': installedGames,
  'exo.profiles.read': () => host.mouseProfilesGet(),
  'exo.profiles.write': p => host.mouseProfilesSet({ data: p.data }),
  // Exo's text size, so the module's type follows Settings > General > Text size.
  'exo.textScale': () => Promise.resolve({ ok: true, data: String(textScale()) }),
  // Exo's motion ('reduce' or 'full'), so the module stops the moves Exo stops.
  'exo.motion': () => Promise.resolve({ ok: true, data: motion() }),
  // Exo's Appearance (light, dark or void), so the module is drawn in it.
  'exo.theme': () => Promise.resolve({ ok: true, data: theme() }),
}

/** Exo's motion setting as the shell writes it on the root (useShellLayout). */
const motion = () => document.documentElement.dataset.exoMotion === 'reduce' ? 'reduce' : 'full'
const theme = () => document.documentElement.dataset.exoTheme ?? 'void'

type Request = {
  exo?: string; id?: number; method?: string; params?: Record<string, unknown>
  notice?: unknown; state?: { name?: unknown; battery?: unknown; charging?: unknown; low?: unknown } | null; profile?: unknown
}

/** A notice is one short line; anything longer or not text is dropped. */
const noticeText = (v: unknown) => typeof v === 'string' && v.length > 0 && v.length <= 160 ? v : null

/** Exo Mouse runs on its own host, in a frame in the Devices room (DevicesRoom). */
export const MOUSE_ORIGIN = 'https://mouse.exo-launcher.local'

const frameWindow = () => document.querySelector<HTMLIFrameElement>('.exo-devices-frame')?.contentWindow ?? null

/** A message to Exo Mouse's frame, when it is there. */
export function postToMouse(message: object) {
  frameWindow()?.postMessage({ exo: 'mouse', ...message }, MOUSE_ORIGIN)
}

/** A notice or the mouse's state, posted by the module without asking for a reply. */
function fromModule(m: Request) {
  const text = noticeText(m.notice)
  if (text) publishMouseNotice(text)
  if (m.profile === null || (typeof m.profile === 'string' && m.profile.length > 0)) publishMouseProfile(m.profile === null ? null : m.profile.slice(0, 40))
  if (m.state === null) { publishMouseState(null); return }
  if (!m.state || typeof m.state.name !== 'string') return
  const battery = typeof m.state.battery === 'number' && m.state.battery >= 0 && m.state.battery <= 100 ? Math.round(m.state.battery) : null
  publishMouseState({ name: m.state.name.slice(0, 60), battery, charging: m.state.charging === true, low: m.state.low === true })
}

let relaying = false

/**
 * Exo Mouse's line to Exo. Started with Exo and never removed: the module runs
 * from startup with the Devices room hidden, and a hidden room's effects do not
 * run, so the relay cannot live in the room.
 */
export function startMouseRelay() {
  if (relaying) return
  relaying = true
  window.addEventListener('message', event => {
    if (event.source === null || event.source !== frameWindow() || event.origin !== MOUSE_ORIGIN) return
    const m = event.data as Request | null
    if (m?.exo === 'mouse' && m.id === undefined) { fromModule(m); return }
    const call = m && m.exo === 'mouse' && typeof m.id === 'number' && m.method
      ? Object.hasOwn(EXO, m.method) ? EXO[m.method] : Object.hasOwn(HID, m.method) ? HID[m.method] : undefined
      : undefined
    if (!m || !call) return
    const { id, params } = m
    call(params ?? {})
      .then(result => postToMouse({ id, result }))
      .catch((error: unknown) => {
        report(error, 'DevicesRoom.hid', { level: 'warn' })
        postToMouse({ id, error: error instanceof Error ? error.message : 'Exo could not reach the device.' })
      })
  })
  onTextScale(scale => postToMouse({ event: 'exo.textScale', data: { scale } }))
  new MutationObserver(() => postToMouse({ event: 'exo.motion', data: { motion: motion() } }))
    .observe(document.documentElement, { attributes: true, attributeFilter: ['data-exo-motion'] })
  new MutationObserver(() => postToMouse({ event: 'exo.theme', data: { theme: theme() } }))
    .observe(document.documentElement, { attributes: true, attributeFilter: ['data-exo-theme'] })
  onHostEvent('hid.input', data => postToMouse({ event: 'hid.input', data }))
  onHostEvent('hid.changed', data => postToMouse({ event: 'hid.changed', data }))
  // In order: a stop never overtakes the start it follows while a library read is pending.
  let runStates = Promise.resolve()
  onHostEvent('game.runState', data => {
    runStates = runStates
      .then(() => cardId(data.id))
      .then(id => postToMouse({ event: 'game.runState', data: { id, isRunning: data.isRunning } }))
  })
}
