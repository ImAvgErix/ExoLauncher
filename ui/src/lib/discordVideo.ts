import { useCallback, useEffect, useRef, useState } from 'react'
import { host, onHostEvent } from './host'
import { onPageActivityChanged, pageIsVisible } from './pageActivity'
import { report } from './report'

/**
 * Discord video in the page (docs/architecture/discord-video.md): the host hands over whole H.264
 * frames (Annex B, already decrypted) per person, and each feed decodes them with WebCodecs
 * (hardware where the PC has it) and draws the newest picture into whichever canvas shows that
 * person. A feed starts at a keyframe and starts over at the next one after a decoding error.
 * Nothing is decoded while no canvas shows a feed or Exo is off screen, and the host sends call
 * video only while one does (asking the senders for a keyframe when it starts again).
 */
/** A camera in the call, a stream being watched, or your own stream (its tile, the frames viewers get). */
export type VideoSource = 'call' | 'stream' | 'self'
// rotation: quarter turns clockwise that show the sender's picture upright, flip: mirrored first (their video-orientation).
type FrameEvent = { source: VideoSource; userId: string; timestamp: number; keyframe: boolean; data: string; rotation?: number; flip?: boolean }

const key = (source: VideoSource, userId: string) => `${source}:${userId}`

// A frame's base64 as bytes, in a plain loop: Uint8Array.from with a map function walks the string as an iterator,
// several times slower on every frame of a 60 fps stream.
function decode(base64: string): Uint8Array {
  const text = atob(base64)
  const bytes = new Uint8Array(text.length)
  for (let i = 0; i < text.length; i++) bytes[i] = text.charCodeAt(i)
  return bytes
}

class Feed {
  private decoder: VideoDecoder | null = null
  private waiting = true
  // Hardware first; a graphics card that can't take this stream (an older one, or a size or level it lacks) fails
  // before its first picture, and the feed then decodes in software instead of starting over the same way forever.
  private hardware = true
  private drew = false
  private canvases = new Set<HTMLCanvasElement>()
  private listeners = new Set<() => void>()
  private turn = { rotation: 0, flip: false }
  live = false
  /** The host said this feed ended (a stream that closed or never opened). */
  ended = false

  constructor(readonly source: VideoSource) { }

  attach(canvas: HTMLCanvasElement) {
    this.canvases.add(canvas)
    wantFrames()
    return () => { this.canvases.delete(canvas); wantFrames() }
  }
  get shown() { return this.canvases.size > 0 }
  subscribe(listener: () => void) { this.listeners.add(listener); return () => { this.listeners.delete(listener) } }
  private changed() { this.listeners.forEach(listener => listener()) }

  push(event: FrameEvent) {
    // Nowhere to draw it, or Exo off screen: skipped, and the feed starts again at the next keyframe.
    if (!this.shown || !pageIsVisible()) { this.waiting = true; return }
    if (this.waiting && !event.keyframe) return
    const decoder = this.decoder ?? this.open()
    if (!decoder) return
    this.waiting = false
    if (this.ended) { this.ended = false; this.changed() }
    this.turn = { rotation: event.rotation ?? 0, flip: event.flip ?? false }
    const bytes = decode(event.data)
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
      output: frame => {
        const { rotation, flip } = this.turn
        const w = frame.displayWidth, h = frame.displayHeight
        const [width, height] = rotation % 2 ? [h, w] : [w, h]
        for (const canvas of this.canvases) {
          if (canvas.width !== width || canvas.height !== height) {
            canvas.width = width
            canvas.height = height
          }
          const draw = canvas.getContext('2d')
          if (!draw) continue
          // Upright: about the canvas's center, turned clockwise and mirrored as the sender says.
          draw.setTransform(1, 0, 0, 1, width / 2, height / 2)
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
      decoder.configure({ codec: 'avc1.640034', hardwareAcceleration: this.hardware ? 'prefer-hardware' : 'no-preference', optimizeForLatency: true })
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
