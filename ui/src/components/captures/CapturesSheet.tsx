import { useCallback, useEffect, useMemo, useRef, useState, type KeyboardEvent as ReactKeyboardEvent, type PointerEvent as ReactPointerEvent, type ReactNode } from 'react'
import { GameOverlay } from '../../motion'
import { Capture, Check, ChevronLeft, Close, FolderOpen, Gallery, History, Record, Scissors, Settings, Star as StarGlyph, Trash } from '../../brand/icons'
import { host, onHostEvent, type Game } from '../../lib/host'
import { clock, closeCaptures, keysLabel, useCapture, useCapturesSheet, type CaptureExport, type CaptureItem } from '../../lib/capture'
import { shouldReduceMotion } from '../../lib/motionPreference'
import { notify } from '../../lib/notify'
import { report } from '../../lib/report'
import { CoverArt } from '../CoverArt'
import { CaptureSettingsView } from './CaptureSettings'
import { SnipEditor } from './SnipEditor'
import { pane, pressedArt, transition, visible } from '../../lib/viewTransition'

/** A capture plays at half volume, as the player's slider shows, until you move it; kept while Exo runs. */
let playbackVolume = 0.5

function Star({ on }: { on: boolean }) {
  return <StarGlyph size={14} filled={on} />
}

function when(iso: string): string {
  const date = new Date(iso)
  const today = new Date()
  const time = date.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' })
  if (date.toDateString() === today.toDateString()) return `Today ${time}`
  const yesterday = new Date(today.getTime() - 86_400_000)
  if (date.toDateString() === yesterday.toDateString()) return `Yesterday ${time}`
  return `${date.toLocaleDateString(undefined, { day: 'numeric', month: 'short' })} ${time}`
}

/** The captures folder a game's title gets, as the host names it (MainWindow.Capture.cs SafeCaptureName). */
function captureFolder(title: string): string {
  // eslint-disable-next-line no-control-regex -- the host blanks control characters too
  let name = title.replace(/[\u0000-\u001f<>:"/\\|?*]/g, ' ').trim().replace(/\.+$/, '').trim()
  if (name.length > 80) name = name.slice(0, 80).replace(/[ .]+$/, '')
  return name || 'Capture'
}

function size(bytes: number): string {
  return bytes >= 1024 ** 3 ? `${(bytes / 1024 ** 3).toFixed(1)} GB` : `${Math.max(0.1, bytes / 1024 ** 2).toFixed(1)} MB`
}

/** The Captures sheet over any room: Exo Capture's clips, recordings and snips. */
export function CapturesOverlay({ games, onDiscord }: { games: readonly Game[]; onDiscord: () => void }) {
  const sheet = useCapturesSheet()
  return (
    <GameOverlay
      open={sheet.open}
      label="Captures"
      scrim={<button type="button" className="exo-game-overlay-scrim" aria-label="Close" tabIndex={-1} onClick={closeCaptures} />}
    >
      <CapturesSheet key={sheet.seq} gameId={sheet.gameId ?? null} itemId={sheet.itemId ?? null} edit={!!sheet.edit} settings={!!sheet.settings} games={games} onDiscord={onDiscord} />
    </GameOverlay>
  )
}

/**
 * One card: the thumbnail; a video previews itself while hovered. Favorite and
 * Delete sit on its corner; while selecting, a click picks the card instead.
 */
function CaptureCard({ item, selected, onOpen, onFavourite, onDelete }: {
  item: CaptureItem
  /** Null: not selecting. */
  selected: boolean | null
  onOpen: () => void
  onFavourite: () => void
  onDelete: () => void
}) {
  const [hover, setHover] = useState(false)
  const name = `${item.kind === 'image' ? 'Snip' : 'Clip'} ${when(item.created)}`
  return (
    <div className={`exo-capture-card${selected ? ' is-selected' : ''}`} onPointerEnter={() => setHover(true)} onPointerLeave={() => setHover(false)}>
      <button type="button" className="exo-capture-thumb" data-capture-id={item.id} onClick={onOpen} aria-label={name} aria-pressed={selected ?? undefined}>
        {item.thumb ? <img src={item.thumb} alt="" loading="lazy" decoding="async" /> : <span className="exo-capture-thumb-empty" />}
        {hover && selected === null && item.kind === 'video' && !shouldReduceMotion() ? <video src={item.url} muted autoPlay loop playsInline preload="metadata" /> : null}
        {item.durationMs ? <span className="exo-capture-duration">{clock(item.durationMs)}</span> : null}
        {selected !== null ? (
          <span className="exo-capture-check" aria-hidden>
            {selected ? <Check size={14} /> : null}
          </span>
        ) : null}
      </button>
      {selected === null ? (
        <div className="exo-capture-corner">
          <button type="button" className="exo-capture-corner-btn exo-capture-delete" aria-label={`Delete ${name}`} onClick={onDelete}>
            <Trash size={14} />
          </button>
          <button
            type="button"
            className={`exo-capture-corner-btn exo-capture-fav${item.favourite ? ' is-on' : ''}`}
            aria-label={item.favourite ? 'Remove from favorites' : 'Favorite'}
            aria-pressed={item.favourite}
            onClick={onFavourite}
          >
            <Star on={item.favourite} />
          </button>
        </div>
      ) : null}
      <div className="exo-capture-meta">
        <span>{when(item.created)}</span>
        <span>{item.kind === 'image' ? 'Snip' : size(item.size)}</span>
      </div>
    </div>
  )
}

/** A trim point's keys: arrows move it a second (Shift: five), Home and End to as far as it goes. */
function trimKey(event: ReactKeyboardEvent, value: number, min: number, max: number, set: (ms: number) => void) {
  const step = event.shiftKey ? 5000 : 1000
  const next = event.key === 'ArrowLeft' || event.key === 'ArrowDown' ? value - step
    : event.key === 'ArrowRight' || event.key === 'ArrowUp' ? value + step
      : event.key === 'Home' ? min : event.key === 'End' ? max : null
  if (next === null) return
  event.preventDefault()
  set(Math.min(max, Math.max(min, next)))
}

/** In and out points on the clip's timeline; drag either, or click to seek. Each point is a slider for the keyboard too. */
function TrimBar({ duration, start, end, time, onChange, onSeek }: {
  duration: number
  start: number
  end: number
  time: number
  onChange: (start: number, end: number) => void
  onSeek: (ms: number) => void
}) {
  const bar = useRef<HTMLDivElement>(null)
  const drag = useRef<'start' | 'end' | null>(null)
  const at = (event: ReactPointerEvent) => {
    const box = bar.current!.getBoundingClientRect()
    return Math.min(duration, Math.max(0, ((event.clientX - box.left) / box.width) * duration))
  }
  const pct = (ms: number) => `${duration ? (ms / duration) * 100 : 0}%`
  return (
    <div
      ref={bar}
      className="exo-trim"
      role="group"
      aria-label="Trim"
      onPointerDown={event => {
        const ms = at(event)
        const near = Math.abs(ms - start) < Math.abs(ms - end) ? 'start' : 'end'
        const box = bar.current!.getBoundingClientRect()
        const handle = Math.abs(((near === 'start' ? start : end) / duration) * box.width - (event.clientX - box.left)) < 12
        if (handle) { drag.current = near; event.currentTarget.setPointerCapture(event.pointerId) }
        else onSeek(ms)
      }}
      onPointerMove={event => {
        if (!drag.current) return
        const ms = at(event)
        if (drag.current === 'start') onChange(Math.min(ms, end - 500), end)
        else onChange(start, Math.max(ms, start + 500))
      }}
      onPointerUp={() => { drag.current = null }}
      onPointerCancel={() => { drag.current = null }}
      onLostPointerCapture={() => { drag.current = null }}
    >
      <div className="exo-trim-kept" style={{ left: pct(start), width: `calc(${pct(end)} - ${pct(start)})` }} />
      <div className="exo-trim-time" style={{ left: pct(time) }} />
      <span className="exo-trim-handle" style={{ left: pct(start) }} role="slider" tabIndex={0} aria-label="Start"
        aria-valuemin={0} aria-valuemax={Math.round(end - 500)} aria-valuenow={Math.round(start)} aria-valuetext={clock(start)}
        onKeyDown={event => trimKey(event, start, 0, end - 500, ms => onChange(ms, end))} />
      <span className="exo-trim-handle" style={{ left: pct(end) }} role="slider" tabIndex={0} aria-label="End"
        aria-valuemin={Math.round(start + 500)} aria-valuemax={Math.round(duration)} aria-valuenow={Math.round(end)} aria-valuetext={clock(end)}
        onKeyDown={event => trimKey(event, end, start + 500, duration, ms => onChange(start, ms))} />
    </div>
  )
}

/** Make smaller: Exo Capture re-saves the recording at a third of the size (it checks the result first). The press starts it; Exo says how it went. */
function MakeSmaller({ item, disabled, onChanged, setNote }: {
  item: CaptureItem
  disabled: boolean
  onChanged: (item: CaptureItem) => void
  setNote: (note: { text: string; bad?: boolean }) => void
}) {
  const [shrinking, setShrinking] = useState(false)
  useEffect(() => onHostEvent('capture.shrunk', ({ id, item: next, before, after, message }) => {
    if (id !== item.id) return
    setShrinking(false)
    if (next) {
      onChanged(next)
      setNote({ text: `Smaller now: ${size(before)} to ${size(after)}.` })
    } else setNote({ text: message ?? 'Left as it was.' })
  }), [item.id, onChanged, setNote])
  const start = async () => {
    setNote({ text: '' })
    try {
      const result = await host.captureItem(item.id, 'shrink')
      if (!result.ok) { setNote({ text: result.message ?? 'That did not work.', bad: true }); return }
      setShrinking(true)
      setNote({ text: result.message ?? 'Making it smaller.' })
    } catch (error) { setNote({ text: error instanceof Error ? error.message : 'That did not work.', bad: true }) }
  }
  return <button type="button" className="exo-set-btn" disabled={disabled || shrinking} onClick={() => void start()}>{shrinking ? 'Making smaller' : 'Make smaller'}</button>
}

function Detail({ item, edit, onBack, onFavourite, onChanged, onDeleted, onAdded, onDiscord }: {
  item: CaptureItem
  edit: boolean
  onBack: () => void
  onFavourite: () => void
  onChanged: (item: CaptureItem) => void
  onDeleted: () => void
  onAdded: (item: CaptureItem) => void
  onDiscord: () => void
}) {
  const video = useRef<HTMLVideoElement>(null)
  const [duration, setDuration] = useState(item.durationMs ?? 0)
  const [range, setRange] = useState<[number, number]>([0, item.durationMs ?? 0])
  const [time, setTime] = useState(0)
  const [busy, setBusy] = useState<string | null>(null)
  const [note, setNote] = useState<{ text: string; bad?: boolean } | null>(null)
  const [editing, setEditing] = useState(edit && item.kind === 'image')
  const trimmed = duration > 0 && (range[0] > 250 || range[1] < duration - 250)


  async function act(action: 'delete' | 'reveal' | 'copy' | 'discord') {
    setNote(null)
    try {
      const result = await host.captureItem(item.id, action)
      if (!result.ok) { setNote({ text: result.message ?? 'That did not work.', bad: true }); return }
      if (result.item) onChanged(result.item)
      if (action === 'delete') onDeleted()
      if (action === 'copy') notify({ text: item.kind === 'image' ? 'Copied.' : 'Copied. Paste it into a chat or folder.', tone: 'good' })
      if (action === 'discord') onDiscord()
    } catch (error) { setNote({ text: error instanceof Error ? error.message : 'That did not work.', bad: true }) }
  }

  async function save(preset: CaptureExport) {
    if (busy) return
    setBusy(preset)
    setNote(null)
    try {
      const result = await host.captureEdit(item.id, preset, range[0], trimmed ? range[1] : 0)
      if (!result.ok) setNote({ text: result.message ?? 'That did not work.', bad: true })
      else {
        setNote({ text: preset === 'trim' ? 'Saved the trim as a new clip.' : preset === 'discord' ? 'Saved a copy for Discord, under 10 MB.' : preset === 'vertical' ? 'Saved a vertical copy.' : 'Saved a full-quality copy.' })
        if (result.item) onAdded(result.item)
      }
    } catch (error) { setNote({ text: error instanceof Error ? error.message : 'That did not work.', bad: true }) }
    finally { setBusy(null) }
  }

  if (editing) {
    return <SnipEditor item={item} onDone={saved => { setEditing(false); if (saved) { onChanged(saved); setNote({ text: 'Saved and copied.' }) } }} />
  }
  return (
    <div className="exo-capture-detail exo-motion-switch">
      <div className="exo-capture-stage">
        {item.kind === 'video' ? (
          <video
            ref={video}
            src={item.url}
            controls
            playsInline
            onLoadedMetadata={event => {
              event.currentTarget.volume = playbackVolume
              const ms = event.currentTarget.duration * 1000
              if (Number.isFinite(ms) && ms > 0) { setDuration(ms); setRange(r => [r[0], r[1] > 0 && r[1] <= ms ? r[1] : ms]) }
            }}
            onTimeUpdate={event => {
              const ms = event.currentTarget.currentTime * 1000
              setTime(ms)
              // Plays only what stays after the trim.
              if (trimmed && ms > range[1]) { event.currentTarget.pause(); event.currentTarget.currentTime = range[0] / 1000 }
            }}
            onVolumeChange={event => { playbackVolume = event.currentTarget.volume }}
          />
        ) : <img src={item.url} alt="" />}
      </div>
      {item.kind === 'video' && duration > 0 ? (
        <div className="exo-capture-trim-row">
          <TrimBar duration={duration} start={range[0]} end={range[1]} time={time}
            onChange={(start, end) => { setRange([start, end]); if (video.current) video.current.currentTime = start / 1000 }}
            onSeek={ms => { if (video.current) video.current.currentTime = ms / 1000 }} />
          <span className="exo-capture-trim-len">{clock(range[1] - range[0])}</span>
        </div>
      ) : null}
      <div className="exo-capture-actions">
        <button type="button" className="exo-set-btn" onClick={onBack}><ChevronLeft size={14} />All captures</button>
        {item.kind === 'video' ? (
          <>
            <button type="button" className="exo-set-btn is-primary" disabled={!trimmed || !!busy} onClick={() => void save('trim')}>{busy === 'trim' ? 'Saving' : 'Save trim'}</button>
            <button type="button" className="exo-set-btn" disabled={!!busy} onClick={() => void save('discord')}>{busy === 'discord' ? 'Exporting' : 'For Discord'}</button>
            <button type="button" className="exo-set-btn" disabled={!!busy} onClick={() => void save('youtube')}>{busy === 'youtube' ? 'Exporting' : 'Full quality'}</button>
            <button type="button" className="exo-set-btn" disabled={!!busy} onClick={() => void save('vertical')}>{busy === 'vertical' ? 'Exporting' : 'Vertical'}</button>
            <MakeSmaller item={item} disabled={!!busy} onChanged={onChanged} setNote={setNote} />
          </>
        ) : (
          <button type="button" className="exo-set-btn is-primary" onClick={() => setEditing(true)}>Edit</button>
        )}
        <span className="exo-capture-actions-gap" />
        <button type="button" className={`exo-set-btn exo-capture-fav-btn${item.favourite ? ' is-on' : ''}`} aria-pressed={item.favourite} onClick={onFavourite}>
          <Star on={item.favourite} />Favorite
        </button>
        <button type="button" className="exo-set-btn" onClick={() => void act('copy')}>Copy</button>
        <button type="button" className="exo-set-btn" onClick={() => void act('discord')}>Send to Discord</button>
        <button type="button" className="exo-set-btn" onClick={() => void act('reveal')}>Show in folder</button>
        <button type="button" className="exo-set-btn is-danger" onClick={() => void act('delete')}>Delete</button>
      </div>
      {note ? <p className={`exo-capture-note${note.bad ? ' is-bad' : ''}`} role="status">{note.text}</p> : null}
    </div>
  )
}

/** Nothing saved yet: the three ways to capture, each one press away, with its keys. */
function CapturesEmpty({ onSettings }: { onSettings: () => void }) {
  const { state, settings } = useCapture()
  const [note, setNote] = useState<string | null>(null)
  const ways = [
    { action: 'saveReplay' as const, label: 'Save replay', keys: settings?.saveReplayKeys, Icon: History },
    { action: 'record' as const, label: 'Record', keys: settings?.recordKeys, Icon: Record },
    { action: 'snip' as const, label: 'Snip', keys: settings?.snipKeys, Icon: Scissors },
  ]
  const run = (action: 'saveReplay' | 'record' | 'snip') => {
    setNote(null)
    // Nothing is held to save while instant replay is off.
    if (action === 'saveReplay' && state && !state.buffering) {
      setNote(settings?.replayMode === 'off' ? 'Instant replay is off. Turn it on in Capture settings.'
        : settings?.replayMode === 'games' ? 'Instant replay runs only while a game is open.' : "Instant replay isn't running.")
      return
    }
    void host.captureAct(action).then(result => { if (!result.ok) setNote(result.message ?? 'That did not work.') })
      .catch((error: unknown) => {
        report(error, 'capture.act', { level: 'warn' })
        setNote(error instanceof Error ? error.message : 'That did not work.')
      })
  }
  // Without the add-on nothing can be captured: say so instead of buttons that do nothing.
  if (state && !state.present) {
    return (
      <div className="exo-captures-empty">
        <span className="exo-captures-empty-icon" aria-hidden="true"><Capture size={28} /></span>
        <strong>Exo Capture isn't installed</strong>
        <span>Replays, recordings and snips need the Exo Capture add-on on this PC.</span>
      </div>
    )
  }
  return (
    <div className="exo-captures-empty">
      <span className="exo-captures-empty-icon" aria-hidden="true"><Gallery size={28} /></span>
      <strong>No captures yet</strong>
      <span>Your replays, recordings and snips land here, sorted by game.</span>
      {note ? <p className="exo-capture-note is-bad" role="alert">{note}</p> : null}
      {note && settings?.replayMode === 'off' ? <button type="button" className="exo-set-btn" onClick={onSettings}><Settings size={14} />Capture settings</button> : null}
      <div className="exo-captures-empty-ways">
        {ways.map(({ action, label, keys, Icon }) => (
          <button key={action} type="button" className="exo-captures-way" onClick={() => run(action)}>
            <Icon size={16} />
            {label}
            {keys ? <kbd className="exo-capture-keys">{keysLabel(keys)}</kbd> : null}
          </button>
        ))}
      </div>
    </div>
  )
}

function subtitle(items: readonly CaptureItem[] | null, selecting: boolean, failed: boolean): string {
  if (items === null) return failed ? 'Exo could not read the captures folder' : 'Reading the captures folder'
  if (selecting) return 'Pick captures to move to the Recycle Bin'
  if (!items.length) return 'Nothing saved yet'
  const total = items.reduce((sum, item) => sum + item.size, 0)
  return `${items.length} ${items.length === 1 ? 'capture' : 'captures'}, ${size(total)}`
}

const nothingShown = (favorites: boolean) => favorites ? 'No favorites yet. Star a capture to keep it here.' : 'Nothing saved for this game yet.'

type Group = { key: string; title: string; gameId?: string | null }

/** All, Favorites, then one chip per game. */
function Chips({ groups, filter, favorites, items, matches, onPick }: {
  groups: readonly Group[]
  filter: string | null
  favorites: boolean
  items: readonly CaptureItem[] | null
  matches: (group: Group) => boolean
  onPick: (key: string | null, favorites?: boolean) => void
}) {
  if (groups.length < 2 && !filter && !favorites && !items?.some(item => item.favourite)) return null
  return (
    <div className="exo-shelf-chips exo-captures-chips" role="toolbar" aria-label="Games">
      <button type="button" className="exo-shelf-chip" aria-pressed={!filter && !favorites} onClick={() => onPick(null)}>All</button>
      <button type="button" className="exo-shelf-chip" aria-pressed={favorites} onClick={() => onPick(null, true)}><Star on={favorites} />Favorites</button>
      {groups.map(group => (
        <button key={group.key} type="button" className="exo-shelf-chip"
          aria-pressed={!!filter && matches(group)} onClick={() => onPick(group.key)}>{group.title}</button>
      ))}
    </div>
  )
}

/** The folder could not be read: Try again, not an empty sheet. */
function ReadFailed({ failed, onRetry }: { failed: boolean; onRetry: () => void }) {
  return failed ? <button type="button" className="exo-set-btn" onClick={onRetry}>Try again</button> : null
}

/** Select, then Delete N or Cancel. */
function SelectActions({ selection, deleting, onSelect, onDelete }: {
  selection: ReadonlySet<string> | null
  deleting: boolean
  onSelect: (next: ReadonlySet<string> | null) => void
  onDelete: () => void
}) {
  if (!selection) return <button type="button" className="exo-set-btn" onClick={() => onSelect(new Set<string>())}>Select</button>
  return (
    <>
      <button type="button" className="exo-set-btn is-danger" disabled={!selection.size || deleting} onClick={onDelete}>
        {deleting ? 'Deleting' : selection.size ? `Delete ${selection.size}` : 'Delete'}
      </button>
      <button type="button" className="exo-set-btn" disabled={deleting} onClick={() => onSelect(null)}>Cancel</button>
    </>
  )
}

/**
 * The sheet's title line: Captures with what it holds, or Capture settings; then its actions (Select and the rest,
 * given as children), Open folder, Capture settings (All captures from them) and Close.
 */
function Head({ setting, list, sub, onSettings, onFolder, children }: {
  setting: boolean
  /** The list is shown and nothing is being picked: Capture settings is offered. */
  list: boolean
  sub: string
  onSettings: (open: boolean) => void
  onFolder: () => void
  children: ReactNode
}) {
  const { state } = useCapture()
  return (
    <header className="exo-hub-head">
      <div className="min-w-0">
        <h2 id="exo-captures-title" className="exo-hub-title">{setting ? 'Capture settings' : 'Captures'}</h2>
        {setting ? null : <p className="exo-hub-sub">{sub}</p>}
      </div>
      <div className="exo-hub-head-actions">
        {setting ? <button type="button" className="exo-set-btn" onClick={() => onSettings(false)}><ChevronLeft size={14} />All captures</button> : children}
        <button type="button" className="exo-set-btn" onClick={onFolder}><FolderOpen size={14} />Open folder</button>
        {/* Without the add-on there is nothing to set. */}
        {setting || !list || state?.present === false ? null : (
          <button type="button" className="exo-set-btn" onClick={() => onSettings(true)}><Settings size={14} />Capture settings</button>
        )}
        <button type="button" className="exo-game-close" aria-label="Close Captures" onClick={closeCaptures}><Close size={16} /></button>
      </div>
    </header>
  )
}

function CapturesSheet({ gameId, itemId, edit, settings, games, onDiscord }: {
  gameId: string | null
  itemId: string | null
  edit: boolean
  /** Open on Capture settings (the Capture menu's). */
  settings: boolean
  games: readonly Game[]
  onDiscord: () => void
}) {
  const [items, setItems] = useState<CaptureItem[] | null>(null)
  /** Capture settings in place of the captures, pushed in over the same body. */
  const [setting, setSetting] = useState(settings)
  const body = useRef<HTMLDivElement>(null)
  const sheetRef = useRef<HTMLElement>(null)
  // The button pressed goes with the view it was in: focus stays in the sheet.
  const showSettings = (next: boolean) => void transition(() => { body.current?.scrollTo(0, 0); setSetting(next) }, { kind: 'push', back: !next, morph: pane(body.current) })
    .then(() => { if (!sheetRef.current?.contains(document.activeElement)) sheetRef.current?.focus({ preventScroll: true }) })
  const [filter, setFilter] = useState<string | null>(gameId)
  /** Favorites: every game's starred captures, one chip beside All. */
  const [favorites, setFavorites] = useState(false)
  const [openId, setOpenId] = useState<string | null>(itemId)

  // A failed read is not an empty folder: it says so, with Try again.
  const [listFailed, setListFailed] = useState(false)
  const load = useCallback(() => {
    setListFailed(false)
    void host.captureList().then(result => {
      if (result.ok && result.items) setItems(result.items)
      else setListFailed(true)
    }).catch((error: unknown) => { report(error, 'capture.list', { level: 'warn' }); setListFailed(true) })
  }, [])
  useEffect(() => { load() }, [load])
  useEffect(() => onHostEvent('capture.saved', ({ item }) => {
    setItems(list => list ? [item, ...list.filter(other => other.id !== item.id)] : list)
  }), [])

  const byGame = useMemo(() => {
    const groups = new Map<string, { key: string; title: string; gameId?: string | null; items: CaptureItem[] }>()
    for (const item of items ?? []) {
      const key = item.gameId ?? item.game
      const group = groups.get(key) ?? { key, title: item.game, gameId: item.gameId, items: [] }
      group.items.push(item)
      groups.set(key, group)
    }
    return [...groups.values()]
  }, [items])
  // A game page's filter matches like the host's list: the game id in any case, or the folder named for its title.
  const folder = useMemo(() => {
    const id = filter?.toLowerCase()
    const game = id ? games.find(g => g.id.toLowerCase() === id || g.variants?.some(v => v.id.toLowerCase() === id)) : undefined
    return game ? captureFolder(game.title).toLowerCase() : null
  }, [filter, games])
  const matches = (group: { key: string; title: string; gameId?: string | null }) => !filter || group.key === filter
    || group.gameId?.toLowerCase() === filter.toLowerCase() || (folder !== null && group.title.toLowerCase() === folder)
  const shown = byGame.filter(matches).map(group => favorites ? { ...group, items: group.items.filter(item => item.favourite) } : group)
    .filter(group => group.items.length > 0)
  const open = openId ? items?.find(item => item.id === openId) ?? null : null
  const update = (next: CaptureItem) => setItems(list => list?.map(item => item.id === next.id ? next : item) ?? list)
  // The star turns at once; the host's answer settles it (and puts it back if the change did not save).
  const favourite = (item: CaptureItem) => {
    update({ ...item, favourite: !item.favourite })
    void host.captureItem(item.id, item.favourite ? 'unfavourite' : 'favourite')
      .then(result => {
        if (result.item) update(result.item)
        else if (!result.ok) { update(item); setNote(result.message ?? 'That did not work.') }
      }).catch((error: unknown) => { update(item); setNote(error instanceof Error ? error.message : 'That did not work.') })
  }
  const openFolder = () => void host.captureAct('openFolder')
    .then(result => { if (!result.ok) setNote(result.message ?? 'Exo could not open the captures folder.') })
    .catch((error: unknown) => setNote(error instanceof Error ? error.message : 'Exo could not open the captures folder.'))
  // Null while not selecting; a filter change starts the pick again (no hidden captures in a delete).
  const [selection, setSelection] = useState<ReadonlySet<string> | null>(null)
  const [deleting, setDeleting] = useState(false)
  const [note, setNote] = useState<string | null>(null)
  const pickFilter = (next: string | null, starred = false) => { setFilter(next); setFavorites(starred); setSelection(s => s && new Set<string>()) }
  const toggle = (id: string) => setSelection(s => {
    if (!s) return s
    const next = new Set(s)
    if (!next.delete(id)) next.add(id)
    return next
  })

  /** To the Recycle Bin, one at a time like Delete on a capture; what Windows refused stays. */
  async function remove(ids: readonly string[]) {
    setNote(null)
    setDeleting(true)
    const gone = new Set<string>()
    let failed: string | null = null
    for (const id of ids) {
      try {
        const result = await host.captureItem(id, 'delete')
        if (result.ok) gone.add(id)
        else failed = result.message ?? 'Windows could not move it to the Recycle Bin.'
      } catch (error) { failed = error instanceof Error ? error.message : 'That did not work.' }
    }
    setItems(list => list?.filter(item => !gone.has(item.id)) ?? list)
    setDeleting(false)
    if (failed) setNote(ids.length > 1 ? `${ids.length - gone.size} not deleted: ${failed}` : failed)
    else setSelection(null)
  }

  return (
    <section ref={sheetRef} className="exo-hub exo-captures" aria-labelledby="exo-captures-title" tabIndex={-1} data-autofocus>
      <Head setting={setting} list={!open && !selection} sub={subtitle(items, !!selection && !open, listFailed)} onSettings={showSettings} onFolder={openFolder}>
        {open || !items?.length ? null : (
          <SelectActions selection={selection} deleting={deleting}
            onSelect={next => { setSelection(next); setNote(null) }} onDelete={() => void remove([...selection ?? []])} />
        )}
      </Head>
      {setting ? (
        <div ref={body} className="exo-captures-body">
          <CaptureSettingsView />
        </div>
      ) : open ? (
        <Detail
          key={open.id}
          item={open}
          edit={edit && open.id === itemId}
          onBack={() => {
            // The picture flies back down into its thumbnail.
            const id = open.id
            void transition(() => setOpenId(null), {
              kind: 'morph',
              back: true,
              morph: { name: 'exo-capture', from: visible('.exo-capture-stage > :is(img, video)'), to: () => visible(`.exo-capture-thumb[data-capture-id="${CSS.escape(id)}"]`) },
            })
          }}
          onFavourite={() => favourite(open)}
          onChanged={update}
          onDeleted={() => { setItems(list => list?.filter(item => item.id !== open.id) ?? list); setOpenId(null) }}
          onAdded={item => setItems(list => list ? [item, ...list.filter(other => other.id !== item.id)] : [item])}
          onDiscord={onDiscord}
        />
      ) : (
        <div ref={body} className="exo-captures-body">
          {/* All is always there while filtered, so a game with nothing saved never leaves an empty sheet with no way out. */}
          <Chips groups={byGame} filter={filter} favorites={favorites} items={items} matches={matches} onPick={pickFilter} />
          {note ? <p className="exo-capture-note is-bad exo-captures-note" role="status">{note}</p> : null}
          <ReadFailed failed={listFailed && items === null} onRetry={load} />
          {items !== null && items.length === 0 ? (
            <CapturesEmpty onSettings={() => showSettings(true)} />
          ) : items?.length && shown.length === 0 ? <p className="exo-hub-sub">{nothingShown(favorites)}</p> : null}
          {shown.map(group => {
            const game = games.find(g => g.id === group.gameId)
            return (
              <section key={group.key} className="exo-captures-group" aria-label={group.title}>
                <header className="exo-captures-group-head">
                  {game ? <span className="exo-captures-cover"><CoverArt game={game} /></span> : null}
                  <h3>{game?.title ?? group.title}</h3>
                  <span>{group.items.length}</span>
                </header>
                <div className="exo-captures-grid">
                  {group.items.map(item => (
                    <CaptureCard
                      key={item.id}
                      item={item}
                      selected={selection ? selection.has(item.id) : null}
                      onOpen={() => {
                        if (selection) toggle(item.id)
                        // The thumbnail flies up into the viewer.
                        else void transition(() => setOpenId(item.id), { kind: 'morph', morph: { name: 'exo-capture', from: pressedArt('.exo-capture-thumb'), to: () => visible('.exo-capture-stage > :is(img, video)') } })
                      }}
                      onFavourite={() => favourite(item)}
                      onDelete={() => void remove([item.id])}
                    />
                  ))}
                </div>
              </section>
            )
          })}
        </div>
      )}
    </section>
  )
}
