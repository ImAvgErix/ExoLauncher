import { useEffect, useRef, useState, useSyncExternalStore } from 'react'
import { createPortal } from 'react-dom'
import { AppWindow, ChevronLeft, Close, Gamepad, Monitor } from '../../brand/icons'
import { useGlide } from '../../lib/glide'
import { goLivePreview, goLiveSources, startGoLive, stopGoLive, useGoLive, type GoLiveStats } from '../../lib/goLive'
import { host, type GoLiveSource } from '../../lib/host'
import { useVideoFeed } from '../../lib/discordVideo'
import { useDialogFocus } from '../../lib/focusTrap'
import { usePageActive, usePageVisible } from '../../lib/pageActivity'
import { Switch } from '../Switch'
import { Icons } from './icons'
import { ModalBack } from './SettingsLayer'
import { useDismiss } from '../../lib/dismiss'

type Quality = { height: number; fps: number; sound: boolean }
type Option = { key: number; label: string; note: string }

const RESOLUTIONS: Option[] = [
  { key: 720, label: '720p', note: 'Lightest on your connection; fine for most sharing.' },
  { key: 1080, label: '1080p', note: 'Full HD: text and games stay sharp.' },
  { key: 1440, label: '1440p', note: 'Extra sharp on big screens; needs a fast connection.' },
  { key: 0, label: 'Source', note: 'Your window or screen at its own size, up to 4K.' },
]
// A stream is never larger than what it shows: a size above the screen or window says what it really sends.
function fitTo(source: GoLiveSource): Option[] {
  const tall = source.height ?? 0
  if (tall <= 0) return RESOLUTIONS
  const what = source.kind === 'screen' ? 'screen' : 'window'
  return RESOLUTIONS.map(r => r.key > tall ? { ...r, note: `Your ${what} is ${tall}p, so this streams at ${tall}p.` } : r)
}

const RATES: Option[] = [
  { key: 15, label: '15 fps', note: 'Slides and documents; the least data.' },
  { key: 30, label: '30 fps', note: 'Everyday sharing: detail before motion.' },
  { key: 60, label: '60 fps', note: 'Games and video: smooth motion.' },
  { key: 120, label: '120 fps', note: 'Fast games on a 120 Hz or faster screen, up to 1440p; never faster than the screen.' },
]

// The last quality chosen, for next time (this PC only).
const SAVED = 'exo.discord.goLive'
function savedQuality(): Quality {
  try {
    const saved = JSON.parse(localStorage.getItem(SAVED) ?? 'null') as Partial<Quality> | null
    return {
      height: RESOLUTIONS.some(r => r.key === saved?.height) ? saved!.height! : 1080,
      fps: RATES.some(r => r.key === saved?.fps) ? saved!.fps! : 30,
      sound: saved?.sound !== false,
    }
  } catch {
    return { height: 1080, fps: 30, sound: true }
  }
}
function saveQuality(quality: Quality) {
  try { localStorage.setItem(SAVED, JSON.stringify(quality)) } catch { /* private storage */ }
}

/** Polls a picture (a source's, or the live stream's with no source) while mounted and Exo is in use; null until the first. */
function usePicture(source: string | null, width: number, every: number, first: string | null = null) {
  const [picture, setPicture] = useState<string | null>(first)
  const active = usePageVisible()
  useEffect(() => {
    if (!active) return
    let alive = true
    let busy = false
    const tick = () => {
      if (busy) return
      busy = true
      void goLivePreview(source, width).then(next => {
        busy = false
        if (alive && next) setPicture(next)
      })
    }
    tick()
    const timer = window.setInterval(tick, every)
    return () => { alive = false; window.clearInterval(timer) }
  }, [source, width, every, active])
  return picture
}

function useEscape(onEscape: () => void) {
  useEffect(() => {
    const key = (event: KeyboardEvent) => { if (event.key === 'Escape') { event.stopPropagation(); onEscape() } }
    window.addEventListener('keydown', key)
    return () => window.removeEventListener('keydown', key)
  }, [onEscape])
}

// The rate shown is the stream's ceiling: a still picture sends almost nothing, which is no measure of its quality.
const formatStats = (stats: GoLiveStats) =>
  `${stats.width}×${stats.height} · ${stats.fps} fps · ${stats.codec ? `${stats.codec} · ` : ''}${stats.max ? `up to ${(stats.max / 1_000_000).toFixed(1)}` : (stats.bitrate / 1_000_000).toFixed(1)} Mbps`

// Whether your own stream shows as a tile in the call (Discord's Hide Preview), kept on this PC.
const PREVIEW = 'exo.discord.goLivePreview'
const previewListeners = new Set<() => void>()
function previewShown() {
  try { return localStorage.getItem(PREVIEW) !== 'hidden' } catch { return true }
}
function setPreviewShown(shown: boolean) {
  try { localStorage.setItem(PREVIEW, shown ? 'shown' : 'hidden') } catch { /* this session only */ }
  previewListeners.forEach(listener => listener())
}
/** Your stream's tile is in the call: live, and its preview not hidden. */
export function useStreamTile() {
  const live = useGoLive()
  const shown = useSyncExternalStore(listener => { previewListeners.add(listener); return () => { previewListeners.delete(listener) } }, previewShown)
  return live.state !== 'off' && shown
}

/**
 * Share Your Screen, as Discord's Go Live: Exo's own picker (Applications and Screens, each a picture
 * with its title and app icon, the running game first), then the stream's quality with a live
 * preview, then Go Live. While live your stream is a tile among the people in the call (never over
 * them), and this button's menu shows or hides that preview or stops the stream.
 */
export function ShareScreen() {
  const live = useGoLive()
  // The dialog opens in the Discord room itself: the call's bar is moved into place, which would hold it.
  const [picking, setPicking] = useState<HTMLElement | null>(null)
  const [menu, setMenu] = useState(false)
  const shown = useStreamTile()
  const box = useRef<HTMLSpanElement>(null)
  const on = live.state !== 'off'
  useDismiss(box, () => setMenu(false), { on: menu })
  useEscape(() => setMenu(false))
  return (
    <span ref={box} className="exo-dc-share">
      <button type="button" className={`exo-dc-voice-btn${on ? ' is-live' : ''}`} aria-label={on ? 'Your stream' : 'Share Your Screen'}
        aria-expanded={on ? menu : picking !== null} aria-haspopup={on ? 'menu' : 'dialog'}
        onClick={event => on ? setMenu(open => !open) : setPicking(event.currentTarget.closest<HTMLElement>('.exo-dc') ?? document.body)}>
        <Icons.Streaming size={20} />
      </button>
      {on && menu ? (
        <div className="exo-dc-menu exo-dc-golive-menu" role="menu">
          <span className="exo-dc-golive-menu-head">{live.title ?? 'Your stream'}<small>{live.stats ? formatStats(live.stats) : 'Connecting…'}</small></span>
          <button type="button" role="menuitem" onClick={() => { setMenu(false); setPreviewShown(!shown) }}>{shown ? 'Hide Preview' : 'Show Preview'}</button>
          <button type="button" role="menuitem" className="is-danger" onClick={() => { setMenu(false); void stopGoLive() }}>Stop Streaming</button>
        </div>
      ) : null}
      {picking ? createPortal(<GoLiveDialog onClose={() => setPicking(null)} onLive={() => { setPicking(null); setPreviewShown(true) }} />, picking) : null}
    </span>
  )
}

/**
 * Your stream as a tile in the call, as Discord shows it: the very frames viewers get, at the stream's
 * own rate (a still picture stands in until the first one decodes), LIVE, its real numbers; Hide
 * Preview and Stop Streaming on it.
 */
export function MyStreamTile({ meId }: { meId: string | null }) {
  const live = useGoLive()
  const feed = useVideoFeed('self', meId)
  const picture = usePicture(null, 960, feed.live ? 60_000 : 500)
  // Your stream's own frames come while Exo is on screen to see them, focused or not (Exo beside the game on another
  // screen shows it live); minimized or hidden, none.
  const active = usePageVisible()
  useEffect(() => {
    if (!active) return
    void host.discordGoLive('self', { on: true }).catch(() => { })
    return () => { void host.discordGoLive('self', { on: false }).catch(() => { }) }
  }, [active])
  return (
    <article className="exo-dc-voice-tile has-video exo-dc-my-stream">
      <canvas ref={feed.ref} className="exo-dc-voice-video" hidden={!feed.live} aria-label="What your stream shows" />
      {!feed.live && picture ? <img className="exo-dc-voice-video" src={picture} alt="What your stream shows" draggable={false} /> : null}
      <span className="exo-dc-live">LIVE</span>
      <span className="exo-dc-my-stream-actions">
        <button type="button" className="exo-dc-tab" onClick={() => setPreviewShown(false)}>Hide Preview</button>
        <button type="button" className="exo-dc-tab exo-dc-golive-stop" onClick={() => void stopGoLive()}>Stop Streaming</button>
      </span>
      <span className="exo-dc-voice-tile-name">{live.title ?? 'Your stream'}{live.stats ? <small> · {formatStats(live.stats)}</small> : null}</span>
    </article>
  )
}

function GoLiveDialog({ onClose, onLive }: { onClose: () => void; onLive: () => void }) {
  const [chosen, setChosen] = useState<GoLiveSource | null>(null)
  const dialog = useRef<HTMLDivElement>(null)
  useDialogFocus(dialog)
  useEscape(onClose)
  return (
    <ModalBack onClose={onClose}>
      <div ref={dialog} className={`exo-dc-modal exo-dc-golive${chosen ? '' : ' is-picking'}`} role="dialog" aria-modal="true" aria-label="Share your screen" tabIndex={-1}
        onClick={event => event.stopPropagation()}>
        {chosen
          ? <GoLiveSettings source={chosen} onBack={() => setChosen(null)} onClose={onClose} onLive={onLive} />
          : <SourcePicker onPick={setChosen} onClose={onClose} />}
      </div>
    </ModalBack>
  )
}

/** Applications and Screens, refreshed every four seconds so pictures stay current and new windows appear. */
function SourcePicker({ onPick, onClose }: { onPick: (source: GoLiveSource) => void; onClose: () => void }) {
  const [sources, setSources] = useState<GoLiveSource[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [tab, setTab] = useState<'window' | 'screen'>('window')
  const tabs = useGlide<HTMLSpanElement>()
  const active = usePageActive()
  useEffect(() => {
    if (!active) return
    let alive = true
    const load = () => void goLiveSources().then(result => {
      if (!alive) return
      setSources(result.sources)
      setError(result.message)
    })
    load()
    const timer = window.setInterval(load, 4000)
    return () => { alive = false; window.clearInterval(timer) }
  }, [active])
  const game = sources?.find(source => source.game)
  const shown = (sources ?? []).filter(source => source.kind === tab && source !== game)
  return (
    <>
      <header className="exo-dc-golive-head">
        <span ref={tabs} className="exo-dc-tabs exo-glide" role="tablist" aria-label="What to share">
          <button type="button" role="tab" className="exo-dc-tab" aria-selected={tab === 'window'} onClick={() => setTab('window')}>Applications</button>
          <button type="button" role="tab" className="exo-dc-tab" aria-selected={tab === 'screen'} onClick={() => setTab('screen')}>Screens</button>
        </span>
        <button type="button" className="exo-dc-icon-btn" aria-label="Close" onClick={onClose}><Close size={18} /></button>
      </header>
      {game && tab === 'window' ? (
        <button type="button" className="exo-dc-golive-game" onClick={() => onPick(game)}>
          <span className="exo-dc-golive-thumb">{game.thumb ? <img src={game.thumb} alt="" draggable={false} /> : <SourceMark source={game} size={28} />}</span>
          <span className="exo-dc-golive-game-text">
            <small><Gamepad size={14} /> Game</small>
            <strong>{game.game}</strong>
          </span>
          <span className="exo-dc-tab is-add">Stream</span>
        </button>
      ) : null}
      {sources === null ? <p className="exo-dc-note">Finding your windows…</p>
        : shown.length === 0 ? <p className="exo-dc-note">{error ?? (tab === 'window' ? (game ? 'No other windows to share.' : 'No windows to share.') : 'No screens found.')}</p>
          : (
            <div className="exo-dc-golive-grid exo-motion-switch" key={tab}>
              {shown.map(source => (
                <button key={source.id} type="button" className="exo-dc-golive-tile" aria-label={`Share ${source.title}`} onClick={() => onPick(source)}>
                  <span className="exo-dc-golive-thumb">{source.thumb ? <img src={source.thumb} alt="" draggable={false} /> : <SourceMark source={source} size={28} />}</span>
                  <span className="exo-dc-golive-name"><SourceMark source={source} size={16} /><span>{source.title}</span></span>
                </button>
              ))}
            </div>
          )}
    </>
  )
}

function SourceMark({ source, size }: { source: GoLiveSource; size: number }) {
  if (source.icon) return <img className="exo-dc-golive-icon" src={source.icon} width={size} height={size} alt="" draggable={false} />
  return source.kind === 'screen' ? <Monitor size={size} /> : <AppWindow size={size} />
}

/** The chosen source's live preview, resolution, frame rate and sound, then Go Live. */
function GoLiveSettings({ source, onBack, onClose, onLive }: { source: GoLiveSource; onBack: () => void; onClose: () => void; onLive: () => void }) {
  const [quality, setQuality] = useState(savedQuality)
  const [starting, setStarting] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const preview = usePicture(source.id, 960, 250, source.thumb ?? null)
  const go = async () => {
    setStarting(true)
    setError(null)
    saveQuality(quality)
    const why = await startGoLive({ source: source.id, ...quality })
    setStarting(false)
    if (why) setError(why)
    else onLive()
  }
  const sound = source.kind === 'window'
    ? `Sends ${source.app || 'this app'}'s sound only.`
    : 'Sends everything your PC plays, except Exo and the call.'
  return (
    <>
      <header className="exo-dc-golive-head">
        <button type="button" className="exo-dc-icon-btn" aria-label="Pick something else" onClick={onBack}><ChevronLeft size={18} /></button>
        <strong className="exo-dc-golive-title"><SourceMark source={source} size={18} /><span>{source.game ?? source.title}</span></strong>
        <button type="button" className="exo-dc-icon-btn" aria-label="Close" onClick={onClose}><Close size={18} /></button>
      </header>
      <div className="exo-dc-golive-body">
        <div className="exo-dc-golive-preview">
          {preview ? <img src={preview} alt={`Preview of ${source.title}`} draggable={false} /> : <SourceMark source={source} size={40} />}
        </div>
        <div className="exo-dc-golive-options">
          <Choice label="Resolution" options={fitTo(source)} value={quality.height} onChange={height => setQuality(q => ({ ...q, height }))} />
          <Choice label="Frame rate" options={RATES} value={quality.fps} onChange={fps => setQuality(q => ({ ...q, fps }))} />
          <div className="exo-dc-golive-choice is-row">
            <span>
              <strong id="exo-dc-golive-sound">Stream sound</strong>
              <small>{sound}</small>
            </span>
            <Switch checked={quality.sound} labelledBy="exo-dc-golive-sound" onChange={on => setQuality(q => ({ ...q, sound: on }))} />
          </div>
          {error ? <p className="exo-dc-golive-error" role="alert">{error}</p> : null}
          <button type="button" className="exo-dc-tab is-add exo-dc-golive-go" disabled={starting} onClick={() => void go()}>{starting ? 'Starting…' : 'Go Live'}</button>
        </div>
      </div>
    </>
  )
}

function Choice({ label, options, value, onChange }: { label: string; options: Option[]; value: number; onChange: (key: number) => void }) {
  const group = useGlide<HTMLSpanElement>()
  const current = options.find(option => option.key === value) ?? options[0]
  return (
    <div className="exo-dc-golive-choice">
      <strong>{label}</strong>
      <span ref={group} className="exo-dc-tabs exo-glide exo-dc-golive-seg" role="radiogroup" aria-label={label}>
        {options.map(option => (
          <button key={option.key} type="button" role="radio" className="exo-dc-tab" aria-checked={option.key === value} onClick={() => onChange(option.key)}>{option.label}</button>
        ))}
      </span>
      <small>{current.note}</small>
    </div>
  )
}
