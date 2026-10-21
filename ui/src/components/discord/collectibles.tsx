import { useEffect, useRef, useState, useSyncExternalStore, type CSSProperties, type RefObject } from 'react'
import { host } from '../../lib/host'
import { cdnSize, onPerformanceMode, performanceMode, type DiscordCollectibleFields, type DiscordEffectItem, type DiscordLook } from '../../lib/discord'
import { shouldReduceMotion } from '../../lib/motionPreference'

/**
 * Discord's collectibles as its client draws them: avatar decorations, nameplates, display name styles and
 * profile effects. Nothing moves while Exo isn't in front or with Reduce motion on. In a list or a message one
 * moves while the pointer is on its row, as in Discord; on a profile or a call tile it keeps moving, or with
 * Performance mode on (still until pointed at), while the card is pointed at.
 */

const CDN = 'https://cdn.discordapp.com'

/** A collectible's picture by its SKU, as Discord's client asks for it. */
export const skuAsset = (skuId: string, format: 'static' | 'animated' | 'video') => `${CDN}/media/v1/collectibles-shop/${skuId}/${format}`

/** Someone's collectibles from Discord's own fields: a server member's own first, then their user's. */
export function lookOf(user?: DiscordCollectibleFields | null, member?: DiscordCollectibleFields | null): DiscordLook | null {
  const decoration = member?.avatar_decoration_data ?? user?.avatar_decoration_data
  const plate = member?.collectibles?.nameplate ?? user?.collectibles?.nameplate
  const style = [member?.display_name_styles, user?.display_name_styles].find(s => s?.colors?.length)
  if (!decoration && !plate && !style) return null
  return {
    decoration: decoration && { asset: decoration.asset, skuId: decoration.sku_id },
    nameplate: plate && { skuId: plate.sku_id, asset: plate.asset, palette: plate.palette },
    nameStyle: style && { font: style.font_id, effect: style.effect_id, colors: style.colors },
  }
}

// Exo in front, and nothing asks for stillness: what every collectible checks before it moves.
const playListeners = new Set<() => void>()
const notify = () => playListeners.forEach(listener => listener())
if (typeof window !== 'undefined') {
  window.addEventListener('focus', notify)
  window.addEventListener('blur', notify)
  onPerformanceMode(notify)
  new MutationObserver(notify).observe(document.documentElement, { attributes: true, attributeFilter: ['data-exo-motion'] })
}
// off: nothing moves; hover: only what is pointed at (Performance mode); on.
type Motion = 'off' | 'hover' | 'on'
const motion = (): Motion => !document.hasFocus() || shouldReduceMotion() ? 'off' : performanceMode() ? 'hover' : 'on'
function subscribe(listener: () => void) {
  playListeners.add(listener)
  return () => { playListeners.delete(listener) }
}
const useMotion = () => useSyncExternalStore(subscribe, motion)

// The card around a collectible that has no row: pointing at it plays it under Performance mode.
const CARD = '.exo-dc-pf, .exo-dc-voice-tile, .exo-dc-shop-dialog'

/** Whether a collectible moves now: pointed at in its row (scope), or on its card whenever it may. */
function useLive(ref: RefObject<HTMLElement | null>, scope: string | undefined) {
  const hovered = useScopeHover(ref, scope ?? CARD)
  const now = useMotion()
  return now !== 'off' && (hovered || (!scope && now === 'on'))
}

/** Whether the pointer is on the closest `scope` around the element (a row, a message); false without one. */
function useScopeHover(ref: RefObject<HTMLElement | null>, scope: string | undefined) {
  const [hovered, setHovered] = useState(false)
  useEffect(() => {
    const el = scope ? ref.current?.closest(scope) : null
    if (!el) return
    const on = () => setHovered(true)
    const off = () => setHovered(false)
    el.addEventListener('pointerenter', on)
    el.addEventListener('pointerleave', off)
    return () => { el.removeEventListener('pointerenter', on); el.removeEventListener('pointerleave', off) }
  }, [ref, scope])
  return hovered
}

/**
 * An avatar decoration, 1.2 times the picture and centered on it. With a scope it moves while the pointer is on
 * that (a member row, a message); without one it moves whenever it may (a profile, a call).
 */
export function Decoration({ look, size, scope }: { look: DiscordLook | null | undefined; size: number; scope?: string }) {
  const ref = useRef<HTMLImageElement>(null)
  const live = useLive(ref, scope)
  const decoration = look?.decoration
  if (!decoration) return null
  const px = Math.round(size * 1.2)
  const src = decoration.skuId ? skuAsset(decoration.skuId, live ? 'animated' : 'static')
    : `${CDN}/avatar-decoration-presets/${decoration.asset}.png?size=${cdnSize(px)}&passthrough=${live}`
  return <img ref={ref} className="exo-dc-decoration" src={src} alt="" aria-hidden="true" draggable={false} width={px} height={px} />
}

// Discord's nameplate palettes (their dark-theme color).
const PALETTES: Record<string, string> = {
  crimson: '#900007', berry: '#893A99', sky: '#0080B7', teal: '#086460', forest: '#2D5401', bubble_gum: '#DC3E97',
  violet: '#730BC8', cobalt: '#0131C2', clover: '#047B20', lemon: '#F6CD12', white: '#FFFFFF', black: '#000000',
}

/**
 * A nameplate behind a member or DM row: its palette fading in from the left, then its art at the right end,
 * moving while the row is pointed at or open (looping only while pointed at), as Discord's.
 */
export function Nameplate({ look, scope, selected = false }: { look: DiscordLook | null | undefined; scope: string; selected?: boolean }) {
  const ref = useRef<HTMLSpanElement>(null)
  const hovered = useScopeHover(ref, scope)
  const now = useMotion()
  const live = now !== 'off' && (hovered || (selected && now === 'on'))
  const plate = look?.nameplate
  if (!plate) return null
  const color = PALETTES[plate.palette ?? '']
  const end = selected ? '80' : hovered ? '4D' : '33'
  const background = color ? `linear-gradient(90deg, transparent 0%, ${color}14 20%, ${color}14 50%, ${color}${end} 100%)` : undefined
  const still = skuAsset(plate.skuId, 'static')
  return (
    <span ref={ref} className="exo-dc-nameplate" style={{ background }} aria-hidden="true">
      {live
        ? <video src={skuAsset(plate.skuId, 'video')} poster={still} autoPlay muted playsInline loop={hovered} />
        : <img src={still} alt="" draggable={false} />}
    </span>
  )
}

// Discord's display name fonts (DisplayNameFont ids) and effects (DisplayNameEffect ids).
const FONTS: Record<number, [string, string, number]> = {
  3: ['sakura', 'Sakura', 400], 4: ['jellybean', 'Jellybean', 400], 6: ['modern', 'Modern', 500], 7: ['medieval', 'Medieval', 400],
  8: ['bit', '8Bit', 400], 10: ['vampyre', 'Vampyre', 400], 12: ['tempo', 'Tempo', 600], 13: ['monkey', 'Monkey Bars', 700],
  14: ['mainframe', 'Mainframe', 700], 15: ['headbang', 'Headbang', 400], 16: ['journal', 'Journal', 700],
}

const fonts = new Map<string, Promise<void>>()
// A display name font, registered once from Discord's own file (which Discord serves only to its own site).
function loadFont(family: string, weight: number) {
  if (!fonts.has(family)) {
    fonts.set(family, host.discordFont(family).then(async result => {
      if (!result.ok || !result.data) return
      const bytes = Uint8Array.from(atob(result.data), c => c.charCodeAt(0))
      const face = new FontFace(`Exo DN ${family}`, bytes, { weight: String(weight) })
      document.fonts.add(await face.load())
    }).catch(() => { }))
  }
}
const EFFECTS: Record<number, string> = { 1: 'solid', 2: 'gradient', 3: 'neon', 4: 'toon', 5: 'pop', 6: 'gradient', 7: 'prism', 8: 'gummy' }
const MIN_CONTRAST: Record<number, number> = { 2: 2.5, 6: 2.5, 7: 2.5 }

const hex = (rgb: number[]) => '#' + rgb.map(v => Math.round(Math.min(255, Math.max(0, v))).toString(16).padStart(2, '0')).join('')
const rgbOf = (color: number) => [(color >> 16) & 255, (color >> 8) & 255, color & 255]
const luminance = (rgb: number[]) => {
  const [r, g, b] = rgb.map(v => { const c = v / 255; return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4 })
  return 0.2126 * r + 0.7152 * g + 0.0722 * b
}
// A color made light enough to read on a dark background (Exo's black, or a toon name's #333), as Discord adjusts it.
function readable(color: number, background: number[], ratio: number) {
  let rgb = rgbOf(color)
  const bg = luminance(background)
  for (let i = 0; i < 20 && (luminance(rgb) + 0.05) / (bg + 0.05) < ratio; i++) rgb = rgb.map(v => v + (255 - v) * 0.1)
  return hex(rgb)
}

function hsl(color: string): [number, number, number] {
  const [r, g, b] = rgbOf(parseInt(color.slice(1), 16)).map(v => v / 255)
  const max = Math.max(r, g, b), min = Math.min(r, g, b), l = (max + min) / 2, d = max - min
  if (d === 0) return [0, 0, l]
  const s = d / (1 - Math.abs(2 * l - 1))
  const h = max === r ? ((g - b) / d) % 6 : max === g ? (b - r) / d + 2 : (r - g) / d + 4
  return [(h * 60 + 360) % 360, s, l]
}
function fromHsl([h, s, l]: [number, number, number]) {
  const c = (1 - Math.abs(2 * l - 1)) * s, x = c * (1 - Math.abs(((h / 60) % 2) - 1)), m = l - c / 2
  const [r, g, b] = h < 60 ? [c, x, 0] : h < 120 ? [x, c, 0] : h < 180 ? [0, c, x] : h < 240 ? [0, x, c] : h < 300 ? [x, 0, c] : [c, 0, x]
  return hex([r, g, b].map(v => (v + m) * 255))
}

// The custom properties Discord's name effects read: the main color and its lights and darks, or the gradient's stops.
function nameVars(effect: number, colors: string[], length: number): Record<string, string> {
  if (effect === 2 || effect === 6) {
    const stops = colors.length === 1 ? `${colors[0]}, ${colors[0]}` : colors.map((c, i) => `${c} ${10 + 80 * i / (colors.length - 1)}%`).join(', ')
    return { '--dn-gradient': stops, '--dn-main': colors[0] }
  }
  if (effect === 7) {
    const stops = Array.from({ length: colors.length + 1 }, (_, i) => `${colors[i % colors.length]} ${(100 * i / colors.length).toFixed(3)}%`).join(', ')
    return { '--dn-prism': stops, '--dn-prism-cycle': `max(${length}ch, 38px)`, '--dn-main': colors[0] }
  }
  const [h, s, l] = hsl(colors[0])
  return {
    '--dn-main': colors[0],
    '--dn-light-1': fromHsl([h, s, Math.min(1, 1.2 * l)]),
    '--dn-light-2': fromHsl([h, s, Math.min(1, 1.6 * l)]),
    '--dn-dark-2': fromHsl([h, s, Math.max(0, 0.2 * l)]),
    '--dn-toon-stroke': fromHsl([h, s, Math.max(0.12, 0.4 * l)]),
    '--dn-neon-stroke': fromHsl([h, Math.min(1, 1.2 * s), Math.min(0.6, l + 0.1)]),
  }
}

/**
 * A display name in its Display Name Style: Discord's font and effect in their colors. In a list or message it
 * plays once while pointed at; on a profile (loop) it keeps playing.
 */
export function StyledName({ name, look, scope, loop = false }: { name: string; look: DiscordLook | null | undefined; scope?: string; loop?: boolean }) {
  const ref = useRef<HTMLSpanElement>(null)
  const live = useLive(ref, loop ? undefined : scope)
  const style = look?.nameStyle
  if (!style?.colors.length) return <>{name}</>
  const effect = EFFECTS[style.effect] ? style.effect : 1
  const background = effect === 4 ? [0x33, 0x33, 0x33] : [0, 0, 0]
  const colors = style.colors.map(c => readable(c, background, MIN_CONTRAST[effect] ?? 3))
  const animate = live
  const font = FONTS[style.font]
  if (font) loadFont(font[1], font[2])
  const classes = ['exo-dc-dn', font && `is-${font[0]}`, animate && 'is-animated', loop && 'is-loop'].filter(Boolean).join(' ')
  return (
    <span ref={ref} className={classes} style={nameVars(effect, colors, [...name].length) as CSSProperties}>
      <span className={`exo-dc-dn-text is-${EFFECTS[effect]}`} data-name={name}>
        {effect === 8 ? <Gummy name={name} colors={colors} animate={animate} /> : name}
      </span>
    </span>
  )
}

// Gummy: each letter in the next color, squishing one after another.
function Gummy({ name, colors, animate }: { name: string; colors: string[]; animate: boolean }) {
  let index = 0
  return (
    <>
      {name.split(/(\s+)/).map((word, w) => /^\s+$/.test(word) ? <span key={w}>{word}</span> : (
        <span key={w} className="exo-dc-dn-word">
          {[...word].map((letter, i) => {
            const n = index++
            return <span key={i} className={animate ? 'exo-dc-dn-letter' : undefined} style={{ color: colors[n % colors.length], '--dn-letter': n } as CSSProperties}>{letter}</span>
          })}
        </span>
      ))}
    </>
  )
}

/** The SKU of the profile effect a profile wears (the server profile's first). */
export function effectSkuOf(collectibles: ({ sku_id: string; type: number }[] | null | undefined)[]) {
  for (const list of collectibles) {
    const effect = list?.find(item => item.type === 1)
    if (effect) return effect.sku_id
  }
  return null
}

const effects = new Map<string, Promise<DiscordEffectItem | null>>()
const decoded = new Map<string, Promise<void>>()
// An effect's layer pictures, fetched and decoded once.
function layersOf(item: DiscordEffectItem) {
  let done = decoded.get(item.sku_id)
  if (!done) {
    const sources = item.effects.flatMap(e => [e.src, ...(e.randomizedSources ?? []).map(r => r.src)])
    done = Promise.all(sources.map(src => { const img = new Image(); img.src = src; return img.decode().catch(() => { }) })).then(() => { })
    decoded.set(item.sku_id, done)
  }
  return done
}

/** An effect whose layers are already at hand (the Shop's catalog carries them): no need to ask for its product. */
export function primeEffect(item: DiscordEffectItem) {
  if (!effects.has(item.sku_id)) effects.set(item.sku_id, Promise.resolve(item))
}
/** A profile effect's layers, from its product in Discord's Shop; asked once per effect. */
export function effectItem(skuId: string) {
  let item = effects.get(skuId)
  if (!item) {
    item = host.discordShop('product', { skuId }).then(result => result.product?.items?.find(i => i.type === 1) as DiscordEffectItem | undefined ?? null).catch(() => null)
    effects.set(skuId, item)
  }
  return item
}

/**
 * A profile effect over a profile card, as Discord plays it: its layers on a 24 fps clock after half a second,
 * each from its start for its duration (looping ones again after their delay), scaled to the card's width from
 * Discord's 450 px canvas. Still, it shows its reduced-motion frame.
 */
export function ProfileEffect({ skuId, scope }: { skuId: string | null | undefined; scope?: string }) {
  const [item, setItem] = useState<{ skuId: string; item: DiscordEffectItem | null } | null>(null)
  const box = useRef<HTMLDivElement>(null)
  // In the Shop (a scope), its still preview shows until the card is pointed at.
  const play = useLive(box, scope)
  const [time, setTime] = useState(-500)
  useEffect(() => {
    if (!skuId) return
    let live = true
    void effectItem(skuId).then(found => { if (live) setItem({ skuId, item: found }) })
    return () => { live = false }
  }, [skuId])
  const config = item && item.skuId === skuId ? item.item : null
  const [sources] = useState(() => Math.random())
  // Every layer decoded first, so none pops in late or starts mid-way; its still frame shows meanwhile.
  const [ready, setReady] = useState<string | null>(null)
  useEffect(() => {
    if (!config || !play) return
    let live = true
    void layersOf(config).then(() => { if (live) setReady(config.sku_id) })
    return () => { live = false }
  }, [config, play])
  const playing = play && !!config && ready === config.sku_id
  useEffect(() => {
    if (!config || !playing) return
    let at = -500
    let last = performance.now()
    const end = Math.max(...config.effects.map(e => e.start + e.duration))
    const loops = config.effects.some(e => e.loop)
    setTime(at)
    const id = window.setInterval(() => {
      const now = performance.now()
      at += now - last
      last = now
      setTime(at)
      if (!loops && at > end) window.clearInterval(id)
    }, 1000 / 24)
    return () => window.clearInterval(id)
  }, [config, playing])
  if (!config) return skuId ? <div ref={box} className="exo-dc-effect" /> : null
  if (!playing) {
    const still = config.staticFrameSrc ?? config.reducedMotionSrc
    return (
      <div ref={box} className="exo-dc-effect" role="img" aria-label={config.accessibilityLabel ?? undefined}>
        {still ? <img src={still} alt="" draggable={false} /> : null}
      </div>
    )
  }
  const layers = [...config.effects].sort((a, b) => (a.zIndex ?? 0) - (b.zIndex ?? 0))
  return (
    <div ref={box} className="exo-dc-effect" role="img" aria-label={config.accessibilityLabel ?? undefined}>
      {layers.map((layer, i) => {
        if (time < layer.start || (!layer.loop && time > layer.start + layer.duration)) return null
        let cycle = 0
        if (layer.loop && layer.loopDelay > 0) {
          const span = layer.duration + layer.loopDelay
          cycle = Math.floor((time - layer.start) / span)
          if (time - layer.start - cycle * span > layer.duration) return null
        }
        const pool = layer.randomizedSources
        const src = pool?.length ? pool[Math.floor(sources * pool.length)].src : layer.src
        const position = { left: `calc(${layer.position?.x ?? 0} / 450 * 100cqw)`, top: `calc(${layer.position?.y ?? 0} / 450 * 100cqw)` }
        // A new cycle mounts the picture again, so the animation starts over.
        return <img key={`${i}:${cycle}`} src={src} alt="" draggable={false} style={position} />
      })}
    </div>
  )
}

/** A profile's theme colors (Nitro's profile theme) as Discord paints the card: its two colors under a dark veil. */
export function themeStyle(colors: number[] | null | undefined): CSSProperties | undefined {
  if (!colors || colors.length < 2) return undefined
  const [primary, accent] = colors.map(c => hex(rgbOf(c)))
  return {
    background: `linear-gradient(rgb(0 0 0 / 0.6), rgb(0 0 0 / 0.6)), linear-gradient(${primary}, ${accent})`,
    '--pf-ring': `color-mix(in srgb, ${primary} 40%, #000)`,
  } as CSSProperties
}
