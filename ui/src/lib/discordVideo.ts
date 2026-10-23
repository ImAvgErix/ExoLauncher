import { useCallback, useEffect, useRef, useState } from 'react'
import { host, onHostEvent } from './host'
import { onPageActivityChanged, pageIsVisible } from './pageActivity'
import { report } from './report'

/**
 * Discord video in the page (docs/architecture/discord-video.md): the host hands over whole H.264
 * or H.265 frames (Annex B, already decrypted) per person, and each feed decodes them with WebCodecs
 * (hardware where the PC has it) and draws the newest picture into whichever canvas shows that
 * person. A feed starts at a keyframe and starts over at the next one after a decoding error.
 * Nothing is decoded while no canvas shows a feed or Exo is off screen, and the host sends call
 * video only while one does (asking the senders for a keyframe when it starts again).
 */
/** A camera in the call, a stream being watched, or your own stream (its tile, the frames viewers get). */
export type VideoSource = 'call' | 'stream' | 'self'
// rotation: quarter turns clockwise that show the sender's picture upright, flip: mirrored first (their video-orientation).
type FrameEvent = { source: VideoSource; userId: string; timestamp: number; keyframe: boolean; bytes?: Uint8Array; data?: string; rotation?: number; flip?: boolean; codec?: 'h264' | 'h265' }

// H.265 Main up to level 5.1 (4K 60), Annex B as the host sends it. Discord's apps offer to receive H.265 only where it
// decodes in hardware, and so does Exo: the host hears once whether this PC can.
const HEVC = 'hvc1.1.6.L153.B0'
if (typeof VideoDecoder !== 'undefined')
  void VideoDecoder.isConfigSupported({ codec: HEVC, hardwareAcceleration: 'prefer-hardware' })
    .then(result => host.discordVideoDecode(result.supported === true))
    .catch(() => { })

const key = (source: VideoSource, userId: string) => `${source}:${userId}`

/**
 * An H.265 keyframe's picture size, from its sequence parameter set (NAL type 33) less the conformance window. Edge's
 * hardware H.265 decoder calls every picture 1280×720 (WebCodecs' size when the config names none), so a 1440p or
 * 1080p stream was drawn cropped to its top-left corner; the SPS says what it really is.
 */
function hevcSize(bytes: Uint8Array): { width: number; height: number } | null {
  for (let i = 0; i + 5 < bytes.length; i++)
    if (bytes[i] === 0 && bytes[i + 1] === 0 && bytes[i + 2] === 1 && ((bytes[i + 3] >> 1) & 0x3f) === 33) return spsSize(spsPayload(bytes, i + 5))
  return null
}

// An SPS's payload after its two-byte header, emulation prevention (00 00 03) taken out, up to the next start code.
function spsPayload(bytes: Uint8Array, from: number): number[] {
  const sps: number[] = []
  for (let j = from, zeros = 0; j < bytes.length && sps.length < 128; j++) {
    const b = bytes[j]
    if (zeros >= 2 && b === 3) { zeros = 0; continue }
    if (zeros >= 2 && b <= 1) break
    sps.push(b)
    zeros = b === 0 ? zeros + 1 : 0
  }
  return sps
}

// H.265's seq_parameter_set_rbsp up to the conformance window (ITU-T H.265 7.3.2.2).
function spsSize(sps: number[]): { width: number; height: number } | null {
  let at = 0
  const bit = () => ((sps[at >> 3] ?? 0) >> (7 - (at++ & 7))) & 1
  const bits = (n: number) => { let v = 0; for (let k = 0; k < n; k++) v = v * 2 + bit(); return v }
  const ue = () => { let z = 0; while (z < 32 && bit() === 0) z++; return 2 ** z - 1 + bits(z) }
  at += 4 // sps_video_parameter_set_id
  const subLayers = bits(3)
  at += 1 + 96 // temporal id nesting; profile_tier_level's general profile (88 bits) and level (8)
  const present = Array.from({ length: subLayers }, () => [bit(), bit()])
  if (subLayers > 0) at += 2 * (8 - subLayers)
  for (const [profile, level] of present) at += profile * 88 + level * 8
  ue() // sps_seq_parameter_set_id
  const chroma = ue()
  if (chroma === 3) at += 1
  let width = ue(), height = ue()
  if (bit()) {
    const [left, right, top, bottom] = [ue(), ue(), ue(), ue()]
    width -= (chroma === 1 || chroma === 2 ? 2 : 1) * (left + right)
    height -= (chroma === 1 ? 2 : 1) * (top + bottom)
  }
  return width > 0 && height > 0 && width <= 8192 && height <= 8192 ? { width, height } : null
}

// The decoded picture at the size its SPS says, where the decoder said otherwise (a view of the same picture, no copy).
function atSize(frame: VideoFrame, size: { width: number; height: number } | null): VideoFrame {
  if (!size || (frame.displayWidth === size.width && frame.displayHeight === size.height)
    || size.width > frame.codedWidth || size.height > frame.codedHeight) return frame
  try {
    const sized = new VideoFrame(frame, { visibleRect: { x: 0, y: 0, ...size }, displayWidth: size.width, displayHeight: size.height })
    frame.close()
    return sized
  } catch {
    return frame
  }
}

// A frame's base64 as bytes, in a plain loop: Uint8Array.from with a map function walks the string as an iterator,
// several times slower on every frame of a 60 fps stream.
function decode(base64: string): Uint8Array {
  const text = atob(base64)
  const bytes = new Uint8Array(text.length)
  for (let i = 0; i < text.length; i++) bytes[i] = text.charCodeAt(i)
  return bytes
}

// Each canvas's size on screen in device pixels, and whether it covers its box or fits in it (object-fit), kept current:
// a frame is drawn at that size, never above its own.
const onScreen = new WeakMap<HTMLCanvasElement, { width: number; height: number; cover: boolean }>()
const sizes = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(entries => {
  for (const entry of entries) {
    const canvas = entry.target as HTMLCanvasElement
    const box = entry.devicePixelContentBoxSize?.[0]
    onScreen.set(canvas, {
      width: box ? box.inlineSize : entry.contentRect.width * devicePixelRatio,
      height: box ? box.blockSize : entry.contentRect.height * devicePixelRatio,
      cover: getComputedStyle(canvas).objectFit === 'cover',
    })
  }
})

class Feed {
  private decoder: VideoDecoder | null = null
  private codec: 'h264' | 'h265' = 'h264'
  private waiting = true
  // Hardware first; a graphics card that can't take this stream (an older one, or a size or level it lacks) fails
  // before its first picture, and the feed then decodes in software instead of starting over the same way forever.
  private hardware = true
  private drew = false
  private canvases = new Set<HTMLCanvasElement>()
  private listeners = new Set<() => void>()
  private turn = { rotation: 0, flip: false }
  // An H.265 stream's picture size, from its latest keyframe's SPS (hevcSize).
  private size: { width: number; height: number } | null = null
  live = false
  /** The host said this feed ended (a stream that closed or never opened). */
  ended = false

  constructor(readonly source: VideoSource) { }

  attach(canvas: HTMLCanvasElement) {
    this.canvases.add(canvas)
    try { sizes?.observe(canvas, { box: 'device-pixel-content-box' }) } catch { sizes?.observe(canvas) }
    wantFrames()
    return () => { this.canvases.delete(canvas); sizes?.unobserve(canvas); wantFrames() }
  }
  get shown() { return this.canvases.size > 0 }
  subscribe(listener: () => void) { this.listeners.add(listener); return () => { this.listeners.delete(listener) } }
  private changed() { this.listeners.forEach(listener => listener()) }

  push(event: FrameEvent) {
    // Nowhere to draw it, or Exo off screen: skipped, and the feed starts again at the next keyframe.
    if (!this.shown || !pageIsVisible()) { this.waiting = true; return }
    // The server moved this sender to the other codec: a decoder for it, from its next keyframe.
    const codec = event.codec ?? 'h264'
    if (codec !== this.codec) { this.reset(); this.codec = codec }
    if (this.waiting && !event.keyframe) return
    const decoder = this.decoder ?? this.open()
    if (!decoder) return
    this.waiting = false
    if (this.ended) { this.ended = false; this.changed() }
    this.turn = { rotation: event.rotation ?? 0, flip: event.flip ?? false }
    const bytes = event.bytes ?? decode(event.data ?? '')
    if (this.codec === 'h265' && event.keyframe) this.size = hevcSize(bytes) ?? this.size
    try {
      // RTP's 90 kHz clock in microseconds, as WebCodecs counts.
      decoder.decode(new EncodedVideoChunk({ type: event.keyframe ? 'key' : 'delta', timestamp: Math.round(event.timestamp / 0.09), data: bytes }))
    } catch (error) {
      report(error, 'DiscordVideo.decode', { level: 'warn' })
      this.reset()
    }
  }

  private open(): VideoDecoder | null {
    if (typeof VideoDecoder === 'undefined') return null
    const decoder = new VideoDecoder({
      output: decoded => {
        const frame = atSize(decoded, this.codec === 'h265' ? this.size : null)
        const { rotation, flip } = this.turn
        const w = frame.displayWidth, h = frame.displayHeight
        const [width, height] = rotation % 2 ? [h, w] : [w, h]
        for (const canvas of this.canvases) {
          // At the size it shows, never above the picture's own: a 1440p stream drawn full size into a canvas shown
          // 1500 wide cost the page and the GPU process almost three times the pixels, on every frame.
          const box = onScreen.get(canvas)
          const fit = box && box.width > 0 && box.height > 0 ? (box.cover ? Math.max : Math.min)(box.width / width, box.height / height) : 1
          const scale = Math.min(1, fit)
          const cw = Math.max(1, Math.round(width * scale)), ch = Math.max(1, Math.round(height * scale))
          if (canvas.width !== cw || canvas.height !== ch) {
            canvas.width = cw
            canvas.height = ch
          }
          const draw = canvas.getContext('2d')
          if (!draw) continue
          // Upright: about the canvas's center, scaled to it, turned clockwise and mirrored as the sender says.
          draw.setTransform(scale, 0, 0, scale, cw / 2, ch / 2)
          draw.rotate(rotation * Math.PI / 2)
          if (flip) draw.scale(-1, 1)
          draw.drawImage(frame, -w / 2, -h / 2)
        }
        frame.close()
        this.drew = true
        if (!this.live) { this.live = true; this.changed() }
      },
      error: error => {
        report(error, this.hardware ? 'DiscordVideo.decoder (hardware)' : 'DiscordVideo.decoder (software)', { level: 'warn' })
        if (!this.drew) this.hardware = false
        this.reset()
      },
    })
    this.drew = false
    try {
      // Annex B with its parameter sets in the stream, so no description is needed.
      decoder.configure(this.codec === 'h265'
        ? { codec: HEVC, hardwareAcceleration: 'prefer-hardware', optimizeForLatency: true }
        : { codec: 'avc1.640034', hardwareAcceleration: this.hardware ? 'prefer-hardware' : 'no-preference', optimizeForLatency: true })
    } catch (error) {
      report(error, 'DiscordVideo.configure', { level: 'warn' })
      this.hardware = false
      try { decoder.close() } catch { /* never opened */ }
      return null
    }
    this.decoder = decoder
    return decoder
  }

  reset() {
    try { if (this.decoder && this.decoder.state !== 'closed') this.decoder.close() } catch { /* already closed */ }
    this.decoder = null
    this.waiting = true
    this.size = null
  }

  /** The video stopped; ended when the host says it closed rather than that it is still connecting. */
  stop(ended: boolean) {
    this.reset()
    if (this.live || this.ended !== ended) { this.live = false; this.ended = ended; this.changed() }
  }
}

const feeds = new Map<string, Feed>()
const feedOf = (source: VideoSource, userId: string) => {
  const id = key(source, userId)
  let feed = feeds.get(id)
  if (!feed) { feed = new Feed(source); feeds.set(id, feed) }
  return feed
}

// Whether the host should send call video: a camera or watched stream has a canvas, and Exo is on screen.
let framesOn = false
function wantFrames() {
  const on = pageIsVisible() && [...feeds.values()].some(feed => feed.source !== 'self' && feed.shown)
  if (on === framesOn) return
  framesOn = on
  void host.discordFrames(on).catch(() => { framesOn = !on })
}

let listening = false
function listen() {
  if (listening) return
  listening = true
  onHostEvent('discord.video.frame', event => feedOf(event.source, event.userId).push(event))
  onHostEvent('discord.video', event => { if (!event.active) feeds.get(key(event.source, event.userId))?.stop(event.state !== undefined && event.state !== 'connecting') })
  onPageActivityChanged(wantFrames)
  // The host may still send from before this page loaded: say where things stand now.
  framesOn = true
  wantFrames()
}

/** A person's video: a ref for the canvas that shows it, whether pictures are arriving, and whether the host said it ended. */
export function useVideoFeed(source: VideoSource, userId: string | null) {
  const [state, setState] = useState({ live: false, ended: false })
  useEffect(() => {
    if (!userId) return
    listen()
    const feed = feedOf(source, userId)
    // A stream watched again starts fresh, not from how the last watch ended.
    if (source === 'stream' && !feed.live) feed.ended = false
    setState({ live: feed.live, ended: feed.ended })
    return feed.subscribe(() => setState({ live: feed.live, ended: feed.ended }))
  }, [source, userId])
  const detach = useRef<(() => void) | null>(null)
  const ref = useCallback((canvas: HTMLCanvasElement | null) => {
    detach.current?.()
    detach.current = null
    if (!canvas || !userId) return
    listen()
    detach.current = feedOf(source, userId).attach(canvas)
  }, [source, userId])
  return { ref, ...state }
}
