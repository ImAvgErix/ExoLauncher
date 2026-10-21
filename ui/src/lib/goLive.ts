import { useEffect, useState } from 'react'
import { host, onHostEvent, type GoLiveSource } from './host'
import { report } from './report'
import { discordPrefs } from './discordPrefs'

/**
 * This client's own video to Discord (docs/architecture/discord-video.md).
 *
 * Go Live is the host's: Exo Capture captures the chosen window or screen, the graphics card encodes
 * it, and the frames go to Discord without passing through this page. The page picks the source and
 * the quality, shows pictures and a preview, and follows the state and the stream's real numbers.
 *
 * The camera is the page's: getUserMedia, encoded to H.264 by the graphics card (WebCodecs, Annex B;
 * the processor when the card won't take the size) and handed to the host frame by frame, which
 * encrypts them (DAVE) and sends them. Keyframes come when Discord asks (and every five seconds).
 */
export type SendState = 'off' | 'picking' | 'connecting' | 'live'
/** What the stream sends now; max is its ceiling, which moving pictures use (a still one needs far less). */
export type GoLiveStats = { width: number; height: number; fps: number; bitrate: number; max?: number }
export type GoLiveChoice = { source: string; height: number; fps: number; sound: boolean }

function base64(bytes: Uint8Array): string {
  let text = ''
  for (let i = 0; i < bytes.length; i += 0x8000) text += String.fromCharCode(...bytes.subarray(i, i + 0x8000))
  return btoa(text)
}

class Camera {
  state: SendState = 'off'
  preview: MediaStream | null = null
  private listeners = new Set<() => void>()
  private track: MediaStreamTrack | null = null
  private encoder: VideoEncoder | null = null
  private reading = false
  private wantKeyframe = true
  private inFlight = 0
  private skipping = false

  subscribe(listener: () => void) { this.listeners.add(listener); return () => { this.listeners.delete(listener) } }
  private set(next: SendState) { this.state = next; this.listeners.forEach(listener => listener()) }

  /** Discord wants a keyframe (someone started watching, or lost one): the next frame is one. */
  keyframe() { this.wantKeyframe = true }

  /** The host's word on the camera: live (send now) or off (Discord ended it). */
  onHost(next: 'connecting' | 'live' | 'off') {
    if (next === 'live') { this.wantKeyframe = true; this.set('live') }
    else if (next === 'off' && this.state !== 'off' && this.state !== 'picking') void this.stop()
  }

  // Each start counts up: a start whose camera was turned off (or started again) while it waited stops there.
  private attempt = 0

  async start(): Promise<string | null> {
    if (this.state !== 'off') return null
    if (typeof VideoEncoder === 'undefined') return 'This PC can\'t send video from Exo.'
    const attempt = ++this.attempt
    const current = () => attempt === this.attempt && this.state !== 'off'
    this.set('picking')
    const track = await this.open(current)
    if (typeof track === 'string' || track === null) return track
    const settings = track.getSettings()
    // Even sizes no taller than 720p, as encoders want.
    const scale = Math.min(1, 720 / (settings.height ?? 720))
    const width = Math.max(2, Math.round(((settings.width ?? 1280) * scale) / 2) * 2)
    const height = Math.max(2, Math.round(((settings.height ?? 720) * scale) / 2) * 2)
    track.addEventListener('ended', () => void this.stop())
    const answer = await host.discordCamera('start', { width, height, fps: 30 }).catch(() => null)
    // Turned off while Discord was asked: stop() already let the track go; the host's camera goes too.
    if (!current()) { if (answer?.ok) void host.discordCamera('stop').catch(() => { }); return null }
    if (!answer?.ok) { await this.stop(); return answer?.message ?? 'Discord didn\'t start the video.' }
    this.set('live')
    const why = await this.encode(width, height, current)
    if (!why && current()) void this.pump(track)
    return why
  }

  // The camera chosen in Discord settings > Voice & Video, else Windows' default: its track, why it didn't open, or
  // null when the camera was turned off meanwhile.
  private async open(current: () => boolean): Promise<MediaStreamTrack | string | null> {
    const cameraId = discordPrefs().cameraId
    let media: MediaStream
    try { media = await navigator.mediaDevices.getUserMedia({ video: { height: { ideal: 720 }, frameRate: { ideal: 30 }, ...(cameraId ? { deviceId: { ideal: cameraId } } : {}) }, audio: false }) } catch (error) {
      if (!current()) return null
      this.set('off')
      return cameraError(error)
    }
    const track = media.getVideoTracks()[0]
    if (current() && track) {
      this.track = track
      this.preview = media
      return track
    }
    media.getTracks().forEach(t => t.stop())
    if (!current()) return null
    this.set('off')
    return 'No camera found.'
  }

  // The graphics card's H.264 encoder (else the processor's), its frames to the host; why it couldn't, or null.
  private async encode(width: number, height: number, current: () => boolean): Promise<string | null> {
    try {
      const encoder = new VideoEncoder({
        output: chunk => {
          if (this.state !== 'live') return
          // The host is behind: this frame is dropped, and so is every one after it until a keyframe (asked for now),
          // since each builds on the one before and viewers would see it smear.
          if (chunk.type !== 'key' && (this.skipping || this.inFlight > 4)) {
            if (!this.skipping) this.wantKeyframe = true
            this.skipping = true
            return
          }
          this.skipping = false
          const data = new Uint8Array(chunk.byteLength)
          chunk.copyTo(data)
          this.inFlight++
          void host.discordCamera('frame', { timestamp: chunk.timestamp / 1000, data: base64(data) })
            .catch(() => { }).finally(() => { this.inFlight-- })
        },
        error: error => { report(error, 'VideoSend.encoder'); void this.stop() },
      })
      const config: VideoEncoderConfig = {
        codec: 'avc1.640034', width, height, bitrate: 3_500_000, framerate: 30,
        latencyMode: 'realtime', hardwareAcceleration: 'prefer-hardware', avc: { format: 'annexb' },
      }
      if (!(await VideoEncoder.isConfigSupported(config)).supported) config.hardwareAcceleration = 'no-preference'
      if (!current()) { encoder.close(); return null }
      encoder.configure(config)
      this.encoder = encoder
      return null
    } catch (error) {
      report(error, 'VideoSend.configure')
      await this.stop()
      return 'This PC can\'t encode the video.'
    }
  }

  /** Reads the captured frames and encodes them, dropping frames while the encoder is behind. */
  private async pump(source: MediaStreamTrack) {
    const Processor = (window as unknown as { MediaStreamTrackProcessor?: new (init: { track: MediaStreamTrack }) => { readable: ReadableStream<VideoFrame> } }).MediaStreamTrackProcessor
    if (!Processor) { report(new Error('MediaStreamTrackProcessor missing'), 'VideoSend.pump'); void this.stop(); return }
    const reader = new Processor({ track: source }).readable.getReader()
    this.reading = true
    let count = 0
    try {
      while (this.reading) {
        const { value: frame, done } = await reader.read()
        if (done || !frame) break
        const encoder = this.encoder
        if (!encoder || encoder.state !== 'configured' || encoder.encodeQueueSize > 2 || this.state !== 'live') { frame.close(); continue }
        // Discord asks for a keyframe when someone starts watching or loses one, so a regular one every 5 s is enough.
        const key = this.wantKeyframe || count % 150 === 0
        this.wantKeyframe = false
        encoder.encode(frame, { keyFrame: key })
        frame.close()
        count++
      }
    } catch (error) {
      report(error, 'VideoSend.pump', { level: 'warn' })
    } finally {
      reader.releaseLock()
    }
  }

  async stop() {
    const was = this.state
    this.attempt++
    this.reading = false
    this.track?.stop()
    this.track = null
    this.preview = null
    try { if (this.encoder && this.encoder.state !== 'closed') this.encoder.close() } catch { /* closed */ }
    this.encoder = null
    this.set('off')
    if (was !== 'off' && was !== 'picking') await host.discordCamera('stop').catch(() => { })
  }
}

// Why the camera didn't open, by the browser's own error name.
export function cameraError(error: unknown): string {
  const name = error instanceof DOMException ? error.name : ''
  if (name === 'NotAllowedError' || name === 'SecurityError') return 'Windows isn\'t letting Exo use the camera. Turn on camera access in Windows Settings > Privacy & security > Camera.'
  if (name === 'NotFoundError' || name === 'OverconstrainedError') return 'No camera found.'
  if (name === 'NotReadableError' || name === 'AbortError') return 'The camera is in use by another app, or it stopped answering.'
  report(error, 'VideoSend.camera', { level: 'warn' })
  return 'Exo couldn\'t open the camera.'
}

const camera = new Camera()

type GoLiveState = { state: SendState; title: string | null; stats: GoLiveStats | null; message: string | null }
let goLive: GoLiveState = { state: 'off', title: null, stats: null, message: null }
const goLiveListeners = new Set<() => void>()
function setGoLive(next: Partial<GoLiveState>) {
  goLive = { ...goLive, ...next }
  goLiveListeners.forEach(listener => listener())
}

let hostListening = false
function listenHost() {
  if (hostListening) return
  hostListening = true
  onHostEvent('discord.golive', event => setGoLive(event.state === 'off'
    ? { state: 'off', stats: null, message: event.message ?? null }
    : { state: event.state, title: event.title ?? goLive.title, message: null }))
  onHostEvent('discord.golive.stats', stats => { if (goLive.state !== 'off') setGoLive({ stats }) })
  onHostEvent('discord.camera', event => camera.onHost(event.state))
  onHostEvent('discord.video.keyframe', () => camera.keyframe())
}

/** What can be shared now: screens, then windows front to back, with their pictures. */
export async function goLiveSources(): Promise<{ sources: GoLiveSource[]; message: string | null }> {
  const answer = await host.discordGoLive('sources').catch(() => null)
  return { sources: answer?.sources ?? [], message: answer?.ok ? null : answer?.message ?? 'Exo couldn\'t list your windows.' }
}

/** A fresh picture of a source, or of the stream while live (no source), as a data URL. */
export async function goLivePreview(source: string | null, width: number): Promise<string | null> {
  const answer = await host.discordGoLive('preview', source ? { source, width } : { width }).catch(() => null)
  return answer?.thumb ?? null
}

/** Goes live with a source; resolves with why it couldn't, or null. */
export async function startGoLive(choice: GoLiveChoice): Promise<string | null> {
  listenHost()
  setGoLive({ state: 'connecting', stats: null, message: null })
  const answer = await host.discordGoLive('start', choice).catch(() => null)
  if (answer?.ok) return null
  setGoLive({ state: 'off' })
  return answer?.message ?? 'Discord didn\'t start the stream.'
}

/** Why the stream ended, read and put away. */
export const clearGoLiveMessage = () => { if (goLive.message) setGoLive({ message: null }) }

export const stopGoLive = () => {
  setGoLive({ state: 'off', stats: null, message: null })
  return host.discordGoLive('stop').catch(() => null)
}

/** Where Go Live stands: its state, what is shared, the stream's real numbers, and why it ended. */
export function useGoLive() {
  const [state, setState] = useState(goLive)
  useEffect(() => {
    listenHost()
    const update = () => setState(goLive)
    goLiveListeners.add(update)
    return () => { goLiveListeners.delete(update) }
  }, [])
  return state
}

/** The camera at 720p 30 fps, Discord's camera quality. Windows asks which camera may be used the first time. */
export function startCamera() {
  listenHost()
  if (!navigator.mediaDevices?.getUserMedia) return Promise.resolve('This PC has no camera Exo can use.')
  return camera.start()
}

export const stopCamera = () => camera.stop()

/** Whether the camera is on, and its picture. */
export function useCamera() {
  const [state, setState] = useState(camera.state)
  useEffect(() => {
    listenHost()
    return camera.subscribe(() => setState(camera.state))
  }, [])
  return { state, preview: state === 'live' || state === 'connecting' ? camera.preview : null }
}
