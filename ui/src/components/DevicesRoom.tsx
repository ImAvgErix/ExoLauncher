import { useCallback, useEffect, useMemo, useRef, useState, type CSSProperties } from 'react'
import { ChevronLeft, Refresh } from '../brand/icons'
import { host, onHostEvent, type Peripheral } from '../lib/host'
import { MOUSE_ORIGIN, postToMouse } from '../lib/mouseRelay'
import { onMouseState, type MouseState } from '../lib/mouseState'
import { onPageActivityChanged, pageIsActive } from '../lib/pageActivity'
import { report } from '../lib/report'
import { cn } from '../lib/utils'
import { transition, visible } from '../lib/viewTransition'
import { DeviceArt, type ArtKind } from './devices/art'
import { AudioPage, type Wearing } from './devices/AudioPage'

/** How long Exo Mouse has to say anything after its frame starts loading before the room says it didn't load. */
const LOAD_MS = 15_000
/** What you wear, which Windows can't tell (headphones and IEMs share one plug): the model you picked, and its kind. */
const WEARING_KEY = 'exo.devices.headphones'

/** What devices.image is asked for a device's official picture. */
type PictureAsk = { kind: string; model?: string | null; name?: string | null; maker?: string | null; color?: string | null; layout?: string | null }

/** One card in the grid and one page: the mouse, a keyboard, your audio, a controller. */
type Card = {
  id: string; kind: 'mouse' | 'keyboard' | 'audio' | 'controller'; name: string; status: string
  /** Its official picture, asked for by what it is; Exo's drawing stands in until it comes, or when there is none. */
  picture: PictureAsk; art: ArtKind; device?: Peripheral; mic?: Peripheral
}

/**
 * Devices (DESIGN.md › Devices): what you plug in, each as itself. The room is its devices: a grid of large cards, one
 * per device Exo found (your mouse, keyboard, what you listen on, your controller), each its official picture, name and
 * how it's linked, filling the room; they rise in as the room first opens. A press grows a card into its page, and
 * Devices at its top folds the page back into its card. The mouse's page is Exo Mouse, a separate program (AGPL, in
 * /mouse) in a frame, kept running behind the grid so its battery stays current; Exo relays its HID calls
 * (lib/mouseRelay.ts). Your audio's page holds your pick and Lucid on one screen. Keyboards and controllers show what
 * they are; Exo doesn't set them up yet.
 */
export function DevicesRoom({ active }: { active: boolean }) {
  const [foreground, setForeground] = useState(pageIsActive)
  useEffect(() => onPageActivityChanged(() => setForeground(pageIsActive())), [])
  const [page, setPage] = useState('overview')
  const { devices, scanning, rescan } = usePeripherals(active)
  const mouse = useMouse()
  const [wearing, setWearing] = useWearing()
  const cards = useMemo(() => cardsFor(devices, mouse, wearing), [devices, mouse, wearing])
  const pictures = usePictures(cards)
  // A device that went (unplugged) takes its page with it; one that's coming soon has none.
  useEffect(() => { if (page !== 'overview' && !cards.some(card => card.id === page && !SOON.has(card.kind))) setPage('overview') }, [cards, page])
  const current = cards.find(card => card.id === page) ?? null
  // Back from a page the cards are simply there, the one you left folding back into place: they rose in once.
  const [returned, setReturned] = useState(false)
  const open = (id: string) => {
    void transition(() => { setReturned(true); setPage(id) }, { kind: 'morph', morph: { name: MORPH, from: cardOf(id), to: pageShown } })
  }
  const back = () => {
    const from = page
    void transition(() => setPage('overview'), { kind: 'morph', back: true, morph: { name: MORPH, from: pageShown(), to: () => cardOf(from) } })
  }

  return (
    <main className={cn('exo-devices', page !== 'overview' && 'is-device', page === 'mouse' && 'is-mouse')} aria-label="Devices">
      {page !== 'overview' ? (
        <header className="exo-devices-back">
          <button type="button" className="exo-set-btn" onClick={back}><ChevronLeft size={16} />Devices</button>
        </header>
      ) : null}
      <div className="exo-devices-page">
        {page === 'overview' ? <Overview cards={cards} pictures={pictures} scanning={scanning} rise={!returned} onRescan={rescan} onOpen={open} /> : null}
        {current?.kind === 'audio' ? (
          <AudioPage name={current.name} status={current.status} art={<Picture card={current} url={pictures[current.id]} wash />}
            output={current.device} mic={current.mic} wearing={wearing} onWearing={setWearing} />
        ) : null}
        <MouseFrame shown={page === 'mouse'} visible={active && foreground && page === 'mouse'} />
      </div>
    </main>
  )
}

/** What Exo doesn't set up yet: its card says so and opens nothing. */
const SOON: ReadonlySet<Card['kind']> = new Set(['keyboard', 'controller'])

/** A card and the page it opens are one thing in two sizes (a view transition's shared element): every card grows into
    the whole page and folds back out of it alike, Exo Mouse's and your audio's. */
const MORPH = 'exo-device'
const cardOf = (id: string) => visible(`[data-device-card="${CSS.escape(id)}"]`)
const pageShown = () => visible('.exo-devices-page')

const LINK: Record<string, string> = { usb: 'Wired', wireless: 'Wireless', bluetooth: 'Bluetooth', jack: '3.5 mm jack', hdmi: 'HDMI' }

/** How a device is linked, with its battery when Windows knows it ("Bluetooth · 60%"). */
function linked(device: Peripheral) {
  return [device.connection ? LINK[device.connection] : null, device.battery != null ? `${Math.round(device.battery * 100)}%` : null].filter(Boolean).join(' · ') || 'Connected'
}

function cardsFor(devices: Peripheral[], mouse: MouseState | null, wearing: Wearing): Card[] {
  const audio = audioCard(devices, wearing)
  return [
    ...mouseCard(devices, mouse),
    ...devices.filter(device => device.kind === 'keyboard').map((keyboard): Card => ({
      id: keyboard.id, kind: 'keyboard', name: keyboard.name, status: linked(keyboard), device: keyboard, art: 'keyboard', picture: keyboard,
    })),
    ...(audio ? [audio] : []),
    ...devices.filter(device => device.kind === 'controller').map((pad): Card => ({
      // "DualSense Edge Wireless Controller" is a DualSense Edge.
      id: pad.id, kind: 'controller', name: pad.name.replace(/\s+(wireless\s+)?controller$/i, '') || pad.name, status: linked(pad), device: pad,
      art: 'controller', picture: pad,
    })),
  ]
}

/** The mouse as Exo Mouse knows it (its own name, battery and link), else as Windows lists it. */
function mouseCard(devices: Peripheral[], mouse: MouseState | null): Card[] {
  const pointer = devices.find(device => device.kind === 'mouse')
  if (!mouse && !pointer) return []
  const link = mouse?.wireless === true ? 'Wireless' : mouse?.wireless === false ? 'Wired' : pointer?.connection ? LINK[pointer.connection] : null
  const status = [mouse?.battery != null ? `${mouse.battery}%` : null, link].filter(Boolean).join(' · ') || 'Connected'
  const name = mouse?.name ?? pointer?.name ?? 'Mouse'
  return [{ id: 'mouse', kind: 'mouse', name, status, device: pointer, art: 'mouse', picture: { kind: 'mouse', name, maker: pointer?.maker } }]
}

/** What you listen on and speak into: the pair you picked names what's on your head; speakers and a monitor stay themselves. */
function audioCard(devices: Peripheral[], wearing: Wearing): Card | null {
  const output = devices.find(device => device.kind === 'audio' && device.role === 'output')
  const mic = devices.find(device => device.kind === 'audio' && device.role === 'input')
  if (!output && !mic) return null
  const speakers = output?.model === 'speakers' || output?.model === 'monitor'
  const worn = output && !speakers ? wearing : null
  const name = worn?.name ?? output?.name ?? mic?.name ?? 'Audio'
  return {
    id: 'audio', kind: 'audio', name, status: output ? linked(output) : 'Microphone', device: output, mic, art: audioArt(output, worn, speakers),
    picture: worn ? { kind: 'audio', model: worn.look, name: worn.name } : { kind: 'audio', model: output?.model ?? 'microphone', name },
  }
}

/** What an audio card shows when it has no picture: speakers, a mic alone, or what you wear. */
function audioArt(output: Peripheral | undefined, worn: Wearing, speakers: boolean): ArtKind {
  if (speakers) return 'speakers'
  if (!output) return 'microphone'
  return worn?.look ?? (output.model === 'headset' ? 'headset' : 'headphones')
}

/** A device's picture: its official one once it has come, else Exo's drawing of its kind. A page's picture stands in
    its own light, as Exo Mouse's does: itself, blurred far back. */
function Picture({ card, url, wash = false }: { card: Card; url: string | null | undefined; wash?: boolean }) {
  if (url) {
    return <>
      {wash ? <img className="exo-device-wash" src={url} alt="" draggable={false} aria-hidden="true" /> : null}
      <img className="exo-device-render" src={url} alt="" draggable={false} />
    </>
  }
  return <DeviceArt kind={card.art} model={card.device?.model} layout={card.device?.layout} color={card.device?.color} />
}

/** A card's place in the grid, for its turn rising in (DESIGN.md › Motion › Arrive). */
const turn = (i: number) => ({ '--i': i } as CSSProperties)

function Overview({ cards, pictures, scanning, rise, onRescan, onOpen }: {
  cards: Card[]; pictures: Record<string, string | null>; scanning: boolean; rise: boolean; onRescan: () => void; onOpen: (id: string) => void
}) {
  return (
    <section className="exo-devices-overview" aria-label="Devices">
      <header className="exo-devices-head">
        <h1>{cards.length === 0 ? 'No devices found' : 'Devices'}</h1>
        <button type="button" className="exo-set-btn" disabled={scanning} onClick={onRescan}>
          <Refresh size={16} className={scanning ? 'exo-spin' : undefined} />Rescan
        </button>
      </header>
      <div className="exo-devices-grid" data-count={Math.min(cards.length, 4)}>
        {cards.map((card, i) => {
          const inside = <>
            <span className="exo-device-card-text">
              <strong>{card.name}</strong>
              <span>{card.status}</span>
            </span>
            <span className="exo-device-card-art"><Picture card={card} url={pictures[card.id]} /></span>
          </>
          // Keyboards and controllers show what they are until Exo sets them up: no page behind them yet.
          return SOON.has(card.kind) ? (
            <div key={card.id} data-device-card={card.id} className={cn('exo-device-card exo-glass is-panel is-soon', rise && 'exo-motion-arrive')} style={turn(i)}
              role="group" aria-label={`${card.name}, ${card.status}. Coming soon.`}>
              {inside}
              <span className="exo-device-soon exo-glass" aria-hidden="true">Coming soon</span>
            </div>
          ) : (
            <button key={card.id} type="button" data-device-card={card.id} className={cn('exo-device-card exo-glass is-panel', rise && 'exo-motion-arrive')} style={turn(i)}
              onClick={() => onOpen(card.id)} aria-label={`${card.name}, ${card.status}. Open.`}>
              {inside}
            </button>
          )
        })}
      </div>
    </section>
  )
}

/** The devices Windows has (devices.list), read when the room first shows and again when one comes or goes. */
function usePeripherals(active: boolean) {
  const [devices, setDevices] = useState<Peripheral[]>([])
  const [scanning, setScanning] = useState(false)
  const read = useRef(false)
  const rescan = useCallback(() => {
    setScanning(true)
    host.devicesList()
      .then(result => { if (result.ok) setDevices(result.devices) })
      .catch((error: unknown) => report(error, 'DevicesRoom.list'))
      .finally(() => setScanning(false))
  }, [])
  useEffect(() => {
    if (!active || read.current) return
    read.current = true
    rescan()
  }, [active, rescan])
  useEffect(() => onHostEvent('devices.changed', () => { if (read.current) rescan() }), [rescan])
  return { devices, scanning, rescan }
}

/** Each card's official picture (devices.image: fetched once, cut out and framed alike, kept on this PC), by what it is. */
function usePictures(cards: Card[]) {
  const [pictures, setPictures] = useState<Record<string, string | null>>({})
  const asked = useRef(new Map<string, string>())
  useEffect(() => {
    for (const card of cards) {
      const key = JSON.stringify(card.picture)
      if (asked.current.get(card.id) === key) continue
      asked.current.set(card.id, key)
      host.devicesImage(card.picture)
        .then(result => { if (asked.current.get(card.id) === key) setPictures(current => ({ ...current, [card.id]: result.ok ? result.url : null })) })
        .catch((error: unknown) => report(error, 'DevicesRoom.picture', { level: 'warn' }))
    }
  }, [cards])
  return pictures
}

function useMouse() {
  const [mouse, setMouse] = useState<MouseState | null>(null)
  useEffect(() => onMouseState(setMouse), [])
  return mouse
}

/** The headphones you picked, kept on this PC; null until you pick. */
function useWearing(): [Wearing, (wearing: Wearing) => void] {
  const [wearing, setWearing] = useState<Wearing>(() => {
    try {
      const saved = JSON.parse(localStorage.getItem(WEARING_KEY) ?? 'null') as Wearing
      return saved && typeof saved.name === 'string' && (saved.look === 'iem' || saved.look === 'headset' || saved.look === 'headphones') ? saved : null
    } catch { return null }
  })
  const choose = useCallback((next: Wearing) => {
    setWearing(next)
    try { localStorage.setItem(WEARING_KEY, JSON.stringify(next)) } catch { /* the choice lasts this run */ }
  }, [])
  return [wearing, choose]
}

/**
 * Exo Mouse in its frame: the mouse's page. It runs from Exo's start whichever page shows (its battery feeds the
 * grid and the Bar), reading the mouse fast only while its page is on screen in front. Until it has said anything it
 * isn't running, and after a while the page says so with Reload.
 */
function MouseFrame({ shown, visible }: { shown: boolean; visible: boolean }) {
  useEffect(() => {
    postToMouse({ event: 'room.visible', data: visible })
    return () => postToMouse({ event: 'room.visible', data: false })
  }, [visible])

  const frame = useRef<HTMLIFrameElement>(null)
  const [load, setLoad] = useState(0)
  const [failed, setFailed] = useState(false)
  // The load whose frame has said anything: it is running from then on, however quiet it goes after (behind a game it
  // reads the mouse slowly or not at all). Judged per load, not per showing.
  const heard = useRef(-1)
  const loadNow = useRef(load)
  loadNow.current = load
  useEffect(() => {
    const listen = (event: MessageEvent) => {
      if (event.origin !== MOUSE_ORIGIN || event.source === null || event.source !== frame.current?.contentWindow) return
      heard.current = loadNow.current
      setFailed(false)
    }
    window.addEventListener('message', listen)
    return () => window.removeEventListener('message', listen)
  }, [])
  useEffect(() => {
    if (!visible || heard.current === load) return
    const timer = window.setTimeout(() => { if (heard.current !== load) setFailed(true) }, LOAD_MS)
    return () => window.clearTimeout(timer)
  }, [load, visible])

  return (
    <div className={cn('exo-devices-mouse', !shown && 'is-away')} aria-hidden={!shown} inert={!shown}>
      {failed && shown ? (
        <div className="exo-hosted-room-state" role="alert">
          <p>Exo Mouse didn't load.</p>
          <button type="button" className="exo-set-btn" onClick={() => { setFailed(false); setLoad(value => value + 1) }}>Reload</button>
        </div>
      ) : null}
      <iframe
        key={load}
        ref={frame}
        className="exo-devices-frame"
        hidden={failed && shown}
        src={`${MOUSE_ORIGIN}/index.html`}
        title="Mouse"
        onLoad={() => postToMouse({ event: 'room.visible', data: visible })}
      />
    </div>
  )
}
