import { useEffect, useMemo, useRef, useState } from 'react'
import { Star } from '../../brand/icons'
import { host, type LucidChange, type LucidHeadphone, type LucidSettings, type LucidState } from '../../lib/host'
import { report } from '../../lib/report'
import { Switch } from '../Switch'
import { Slider } from '../ui/Slider'
import { Skeleton } from '../ui/Skeleton'
import { Row, State, SwitchRow } from './parts'
import { ErrorNote } from '../ui/ErrorNote'

/**
 * Lucid, the separate headphone EQ app, set up from Exo's Devices › audio page (devices/AudioPage.tsx), in panels:
 * the app (Start with Exo, its window and setup, a newer Lucid when its feed offers one, the output its EQ is on with
 * Equalizer APO's device picker, its on/off key), Game Mode, and its sounds and notifications; the headphone its EQ is
 * tuned for is the page's one headphone pick, and its Night Shift is devices/NightShiftRows.tsx. Lucid reads its settings only when it starts, so a change closes it and starts it again out
 * of sight. Lucid Voice is kept off; Exo Voice handles the mic. Without Lucid, one row offers its download page.
 */
/** Lucid's state, read again when Windows' output changes (its id as output), since its EQ is on one output. */
export function useLucid(output?: string) {
  const [state, setState] = useState<LucidState | null>(null)
  const newest = useRef(0)
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<{ row: string; text: string } | null>(null)
  const [fetched, setFetched] = useState(false)
  const [update, setUpdate] = useState<string | null>(null)

  useEffect(() => { host.lucid().then(setState, (cause: unknown) => report(cause, 'Lucid.state')) }, [output])
  const found = state?.found === true
  useEffect(() => {
    if (found) host.lucidUpdate().then(result => setUpdate(result.version), (cause: unknown) => report(cause, 'Lucid.update'))
  }, [found])

  async function act(row: string, action: 'open' | 'devices' | 'get' | 'state') {
    setBusy(row)
    setError(null)
    try {
      const next = await host.lucid(action)
      setState(next)
      if (next.message) setError({ row, text: next.message })
      if (action === 'get' && next.ok) setFetched(true)
    } catch (cause) {
      report(cause, `Lucid.${action}`)
      setError({ row, text: "Lucid didn't answer. Try again." })
    } finally {
      setBusy(null)
    }
  }

  /** Shows the change at once; Lucid's own file has the last word when it lands. */
  function change(row: string, patch: LucidChange, shown: Partial<LucidSettings>) {
    const mine = ++newest.current
    setError(null)
    setState(current => current?.settings ? { ...current, settings: { ...current.settings, ...shown } } : current)
    host.lucid('change', { change: patch }).then(next => {
      // Only the newest change's answer: an older one would snap a quick second change back until its own answer lands.
      if (mine === newest.current) setState(next)
      if (next.message) setError({ row, text: next.message })
    }, (cause: unknown) => {
      report(cause, 'Lucid.change')
      setError({ row, text: "Lucid's settings didn't change. Try again." })
      void host.lucid().then(setState, () => {})
    })
  }
  const errorFor = (row: string) => error?.row === row ? error.text : null
  return { state, setState, busy, fetched, update, act, change, errorFor }
}

export type Lucid = ReturnType<typeof useLucid>

/** Lucid isn't on this PC: its download page, and a check once it's installed. */
export function LucidMissingRow({ lucid: l }: { lucid: Lucid }) {
  return (
    <Row label="Lucid" labelId="set-lucid-get" error={l.errorFor('get')}>
      {l.fetched ? <button type="button" className="exo-set-btn" disabled={l.busy !== null} onClick={() => void l.act('get', 'state')}>Check again</button> : null}
      <button type="button" className="exo-set-btn" disabled={l.busy !== null} onClick={() => void l.act('get', 'get')}>Get Lucid</button>
    </Row>
  )
}

/** The app itself: Start with Exo, its window or setup, a newer Lucid, the output its EQ is on, and its on/off key. */
export function LucidAppRows({ lucid: l }: { lucid: Lucid }) {
  const state = l.state
  if (!state?.found) return null
  const settings = state.settings
  const setUp = settings?.headphone.chosen === true
  return (
    <>
      <SwitchRow id="set-lucid-start" label="Start Lucid with Exo" checked={state.startWithExo} disabled={false}
        onChange={on => host.lucid('set', { on }).then(next => { l.setState(next); return next.message ?? null }, () => "Lucid couldn't be changed. Try again.")} />
      <Row label={setUp ? 'Lucid' : 'Set up Lucid'} labelId="set-lucid-open"
        detail={state.held ? 'Your changes go in when your game ends' : setUp ? (state.version ?? undefined) : undefined} error={l.errorFor('open')}>
        {setUp ? <State on={state.running}>{state.running ? 'Running' : 'Not running'}</State> : null}
        <button type="button" className="exo-set-btn" disabled={l.busy !== null} onClick={() => void l.act('open', 'open')}>{setUp ? 'Open Lucid' : 'Set up'}</button>
      </Row>
      {l.update ? (
        <Row label={`Lucid ${l.update} is available`} labelId="set-lucid-update" error={l.errorFor('update')}>
          <button type="button" className="exo-set-btn" disabled={l.busy !== null} onClick={() => void l.act('update', 'open')}>Update</button>
        </Row>
      ) : null}
      <EqRow lucid={l} />
      {settings ? <HotkeyRow hotkey={settings.hotkey} error={l.errorFor('hotkey')} onChange={hotkey => l.change('hotkey', { hotkey }, { hotkey })} /> : null}
    </>
  )
}

/**
 * Where Lucid's EQ plays and whether that's what you hear: Windows may play on a headset or a controller's speaker that
 * Equalizer APO isn't on. Choose devices opens Equalizer APO's picker.
 */
function EqRow({ lucid: l }: { lucid: Lucid }) {
  const state = l.state
  const eq = state?.eq
  if (!state || !eq && !state.devices) return null
  const detail = !eq?.on ? 'Not on an output yet' : eq.covered ? eq.on : `Windows is playing on ${eq.playing ?? 'another output'}, where it isn't`
  return (
    <Row label="Lucid's EQ" labelId="set-lucid-eq" detail={detail} error={l.errorFor('devices')}>
      {eq?.covered && state.settings ? <State on={state.settings.eqOn}>{state.settings.eqOn ? 'On' : 'Off'}</State> : null}
      {state.devices ? <button type="button" className="exo-set-btn" disabled={l.busy !== null} onClick={() => void l.act('devices', 'devices')}>Choose devices</button> : null}
    </Row>
  )
}

/** A USB or wireless headset Windows plays on that Lucid has a tuning for, offered in one press. */
export function LucidGuessRow({ lucid: l, onPicked }: { lucid: Lucid; onPicked: (headphone: LucidHeadphone) => void }) {
  const guess = l.state?.found ? l.state.guess : null
  if (!guess) return null
  const use = () => {
    l.change('headphone', { headphone: guess.id }, { headphone: { id: guess.id, name: guess.name, chosen: true } })
    void host.lucidHeadphones().then(result => {
      const headphone = result.ok ? result.headphones.find(h => h.id === guess.id) : undefined
      if (headphone) onPicked(headphone)
    }, () => undefined)
  }
  return (
    <Row label={`Looks like ${guess.name}`} labelId="set-lucid-guess">
      <button type="button" className="exo-set-btn" disabled={l.busy !== null} onClick={use}>Use it</button>
    </Row>
  )
}

/** The headphone Lucid's EQ is tuned for: the page's one headphone pick (devices/AudioPage.tsx). */
export function LucidHeadphoneRow({ lucid: l, label, onPicked }: { lucid: Lucid; label: string; onPicked: (headphone: LucidHeadphone) => void }) {
  const settings = l.state?.found ? l.state.settings : null
  if (!settings) return null
  return (
    <HeadphoneRow lucid={settings} label={label} error={l.errorFor('headphone')}
      onPick={h => { l.change('headphone', { headphone: h.id }, { headphone: { id: h.id, name: h.model, chosen: true } }); onPicked(h) }}
      onFavorite={(id, on) => l.change('headphone', { favorites: { [id]: on } },
        { favorites: on ? [...settings.favorites, id] : settings.favorites.filter(f => f.toLowerCase() !== id.toLowerCase()) })} />
  )
}

/** Game Mode: each game its own EQ, and how it behaves around the game. */
export function LucidGameRows({ lucid: l }: { lucid: Lucid }) {
  const settings = l.state?.found ? l.state.settings : null
  if (!settings) return null
  return (
    <>
      <LucidSwitch id="set-lucid-game" label="Game Mode" error={l.errorFor('gameMode')}
        checked={settings.gameMode} onChange={on => l.change('gameMode', { gameMode: on }, { gameMode: on })} />
      {settings.gameMode ? (
        <>
          <LucidSwitch id="set-lucid-alt" label="Keep the game's EQ on Alt+Tab" error={l.errorFor('keepOnAltTab')}
            checked={settings.keepOnAltTab} onChange={on => l.change('keepOnAltTab', { keepOnAltTab: on }, { keepOnAltTab: on })} />
          <LucidSwitch id="set-lucid-only" label="Only while gaming" error={l.errorFor('onlyWhileGaming')}
            checked={settings.onlyWhileGaming} onChange={on => l.change('onlyWhileGaming', { onlyWhileGaming: on }, { onlyWhileGaming: on })} />
        </>
      ) : null}
    </>
  )
}

/** Lucid's cue sounds and notifications, and its line in your Discord status. */
export function LucidAlertRows({ lucid: l }: { lucid: Lucid }) {
  const settings = l.state?.found ? l.state.settings : null
  if (!settings) return null
  return (
    <>
      <LucidSwitch id="set-lucid-notify" label="Lucid's notifications" error={l.errorFor('notifications')}
        checked={settings.notifications} onChange={on => l.change('notifications', { notifications: on }, { notifications: on })} />
      {settings.notifications ? (
        <>
          <SoundRow id="set-lucid-toggle-sound" label="Switch sound" value={settings.toggleSound} error={l.errorFor('toggleSound')}
            onCommit={toggleSound => l.change('toggleSound', { toggleSound }, { toggleSound })} />
          <SoundRow id="set-lucid-detect-sound" label="Game sound" value={settings.detectSound} error={l.errorFor('detectSound')}
            onCommit={detectSound => l.change('detectSound', { detectSound }, { detectSound })} />
        </>
      ) : null}
      <LucidSwitch id="set-lucid-discord" label="Show Lucid in Discord" error={l.errorFor('discord')}
        checked={settings.discord} onChange={on => l.change('discord', { discord: on }, { discord: on })} />
    </>
  )
}

function LucidSwitch({ id, label, checked, error, onChange }: {
  id: string; label: string; checked: boolean; error: string | null; onChange: (on: boolean) => void
}) {
  return (
    <Row label={label} labelId={id} error={error}>
      <Switch checked={checked} labelledBy={id} onChange={onChange} />
    </Row>
  )
}

/** The headphone Lucid tunes for, and its list to pick from: search, favorites first, then each kind. */
function HeadphoneRow({ lucid, label, error, onPick, onFavorite }: {
  lucid: LucidSettings; label: string; error: string | null; onPick: (headphone: LucidHeadphone) => void; onFavorite: (id: string, on: boolean) => void
}) {
  const [open, setOpen] = useState(false)
  const [list, setList] = useState<LucidHeadphone[] | null>(null)
  const [listError, setListError] = useState<string | null>(null)
  const [query, setQuery] = useState('')
  const field = useRef<HTMLInputElement>(null)

  useEffect(() => {
    if (!open || list) return
    host.lucidHeadphones().then(result => {
      if (result.ok) setList(result.headphones)
      else setListError("Lucid's headphone list couldn't be read. Pick yours in Lucid.")
    }, (cause: unknown) => { report(cause, 'LucidSection.headphones'); setListError("Lucid's headphone list couldn't be read.") })
  }, [open, list])
  useEffect(() => { if (open) field.current?.focus() }, [open])

  const favorites = useMemo(() => new Set(lucid.favorites.map(id => id.toLowerCase())), [lucid.favorites])
  const groups = useMemo(() => {
    if (!list) return []
    const words = query.trim().toLowerCase().split(/\s+/).filter(Boolean)
    const hits = list.filter(h => words.every(word => `${h.brand} ${h.model} ${h.keywords}`.toLowerCase().includes(word)))
    const byKind = new Map<string, LucidHeadphone[]>()
    const starred = hits.filter(h => favorites.has(h.id.toLowerCase()))
    if (starred.length && !words.length) byKind.set('Favorites', starred)
    for (const h of hits) byKind.set(h.category, [...byKind.get(h.category) ?? [], h])
    return [...byKind]
  }, [list, query, favorites])

  return (
    <div className="exo-set-item">
      <div className="exo-set-item-main">
        <div className="exo-set-item-copy">
          <span id="set-lucid-headphone" className="exo-set-item-label">{label}</span>
          {lucid.headphone.chosen ? <span className="exo-set-item-detail">{lucid.headphone.name}</span> : null}
        </div>
        <div className="exo-set-item-control">
          <button type="button" className="exo-set-btn" aria-expanded={open} onClick={() => setOpen(!open)}>{open ? 'Done' : 'Change'}</button>
        </div>
      </div>
      {open ? (
        <div className="exo-lucid-picker">
          <input ref={field} type="search" className="exo-field" value={query} placeholder="Search headphones" aria-label="Search headphones"
            onChange={event => setQuery(event.target.value)} />
          {listError ? <ErrorNote className="exo-set-error" message={listError} /> : !list ? <Skeleton label="Reading Lucid's list" rows={5} /> : (
            <div className="exo-lucid-list" role="listbox" aria-labelledby="set-lucid-headphone">
              {groups.length === 0 ? <p className="exo-lucid-empty">No headphone by that name</p> : groups.map(([kind, items]) => (
                <div key={kind} role="group" aria-label={kind}>
                  <h3 className="exo-lucid-kind">{kind}</h3>
                  {items.map(h => {
                    const selected = h.id === lucid.headphone.id
                    const starred = favorites.has(h.id.toLowerCase())
                    return (
                      <div key={`${kind}-${h.id}`} className={`exo-lucid-headphone${selected ? ' is-current' : ''}`}>
                        <button type="button" role="option" aria-selected={selected} className="exo-lucid-pick" onClick={() => { if (!selected) onPick(h) }}>
                          <span>{h.model}</span>
                          <small>{h.brand ? `${h.brand} · ${h.grade}` : h.grade}</small>
                        </button>
                        {h.id === 'lucid_iems' ? null : (
                          <button type="button" className={`exo-lucid-star${starred ? ' is-on' : ''}`} aria-pressed={starred}
                            aria-label={`${starred ? 'Remove' : 'Add'} ${h.model} ${starred ? 'from' : 'to'} favorites`} onClick={() => onFavorite(h.id, !starred)}>
                            <Star size={14} filled={starred} />
                          </button>
                        )}
                      </div>
                    )
                  })}
                </div>
              ))}
            </div>
          )}
        </div>
      ) : null}
      {error ? <ErrorNote className="exo-set-error" message={error} /> : null}
    </div>
  )
}

type Hotkey = LucidSettings['hotkey']

// Windows' modifier bits, as Lucid stores them, in the order a shortcut is written.
const MODIFIERS: Array<[number, string]> = [[2, 'Ctrl'], [1, 'Alt'], [4, 'Shift'], [8, 'Win']]

/** "Ctrl+Alt+L", "F11"; null for no key. */
function hotkeyName({ modifiers, key }: Hotkey): string | null {
  if (key === 0) return null
  const name = key >= 112 && key <= 135 ? `F${key - 111}`
    : key >= 48 && key <= 57 || key >= 65 && key <= 90 ? String.fromCharCode(key)
      : key >= 96 && key <= 105 ? `Num ${key - 96}`
        : key === 32 ? 'Space' : `Key ${key}`
  return [...MODIFIERS.filter(([bit]) => (modifiers & bit) !== 0).map(([, word]) => word), name].join('+')
}

/** The key the browser reports, as Windows' virtual key and modifier bits; null for a modifier pressed alone. */
function hotkeyOf(event: KeyboardEvent): Hotkey | null {
  if (['Control', 'Alt', 'Shift', 'Meta', 'OS'].includes(event.key) || event.keyCode === 0) return null
  const modifiers = (event.ctrlKey ? 2 : 0) | (event.altKey ? 1 : 0) | (event.shiftKey ? 4 : 0) | (event.metaKey ? 8 : 0)
  return { modifiers, key: event.keyCode }
}

/**
 * Lucid's on/off key. It holds the key for the whole PC while Lucid runs, so a key alone (Lucid's F11) never reaches a
 * browser's full screen or a game: Change takes the next keys pressed, Escape keeps the old one, Clear leaves none.
 */
function HotkeyRow({ hotkey, error, onChange }: { hotkey: Hotkey; error: string | null; onChange: (hotkey: Hotkey) => void }) {
  const [listening, setListening] = useState(false)
  useEffect(() => {
    if (!listening) return
    const onKey = (event: KeyboardEvent) => {
      event.preventDefault()
      event.stopPropagation()
      if (event.key === 'Escape') { setListening(false); return }
      const next = hotkeyOf(event)
      if (!next) return
      setListening(false)
      if (next.key !== hotkey.key || next.modifiers !== hotkey.modifiers) onChange(next)
    }
    window.addEventListener('keydown', onKey, true)
    return () => window.removeEventListener('keydown', onKey, true)
  }, [listening, hotkey, onChange])
  const name = hotkeyName(hotkey)
  const detail = listening ? 'Press the keys' : name ?? 'None'
  return (
    <Row label="On/off key" labelId="set-lucid-hotkey" detail={detail} error={error}>
      {name && !listening ? <button type="button" className="exo-set-btn" onClick={() => onChange({ modifiers: 0, key: 0 })}>Clear</button> : null}
      <button type="button" className="exo-set-btn" aria-pressed={listening} onClick={() => setListening(on => !on)}>{listening ? 'Cancel' : 'Change'}</button>
    </Row>
  )
}

/** One of Lucid's cue sounds, 0 (off) to 100, saved when the slider is let go. */
function SoundRow({ id, label, value, error, onCommit }: {
  id: string; label: string; value: number; error: string | null; onCommit: (value: number) => void
}) {
  const [shown, setShown] = useState(value)
  useEffect(() => setShown(value), [value])
  const commit = () => { if (shown !== value) onCommit(shown) }
  return (
    <Row label={label} labelId={id} error={error}>
      <div className="exo-lucid-volume">
        <Slider min={0} max={100} step={5} value={shown} aria-labelledby={id}
          onChange={event => setShown(Number(event.target.value))} onPointerUp={commit} onKeyUp={commit} />
        <span>{shown === 0 ? 'Off' : `${shown}%`}</span>
      </div>
    </Row>
  )
}
