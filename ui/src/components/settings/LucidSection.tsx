import { memo, useEffect, useMemo, useRef, useState } from 'react'
import { Star } from '../../brand/icons'
import { host, type LucidChange, type LucidHeadphone, type LucidSettings, type LucidState } from '../../lib/host'
import { report } from '../../lib/report'
import { Switch } from '../Switch'
import { Slider } from '../ui/Slider'
import { Choice } from './Choice'
import { Row, Section, State, SwitchRow } from './parts'

/** Lucid's Night Shift times: every half hour, as its own picker offers them. */
const TIMES = Array.from({ length: 48 }, (_, i) => {
  const hour = Math.floor(i / 2), minute = i % 2 ? 30 : 0
  const value = `${String(hour).padStart(2, '0')}:${minute ? '30' : '00'}`
  return { value, label: `${hour % 12 || 12}:${minute ? '30' : '00'} ${hour < 12 ? 'AM' : 'PM'}` }
})

/**
 * Lucid, the separate headphone EQ app, set up from Exo: Start with Exo, its window and setup, Equalizer APO's device
 * picker, the headphone it tunes for, Game Mode, Night Shift, and its notifications, and a newer Lucid when its feed
 * offers one (Lucid's own window installs it). Lucid reads its settings only when it starts, so a change closes it and
 * starts it again out of sight. Lucid Voice is kept off by Exo
 * own; Exo Voice handles the mic. Without Lucid the section offers its download page.
 */
export const LucidSection = memo(function LucidSection() {
  const [state, setState] = useState<LucidState | null>(null)
  const newest = useRef(0)
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<{ row: string; text: string } | null>(null)
  const [fetched, setFetched] = useState(false)
  const [update, setUpdate] = useState<string | null>(null)

  useEffect(() => { host.lucid().then(setState, (cause: unknown) => report(cause, 'LucidSection.state')) }, [])
  const found = state?.found === true
  useEffect(() => {
    if (found) host.lucidUpdate().then(result => setUpdate(result.version), (cause: unknown) => report(cause, 'LucidSection.update'))
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
      report(cause, `LucidSection.${action}`)
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
      report(cause, 'LucidSection.change')
      setError({ row, text: "Lucid's settings didn't change. Try again." })
      void host.lucid().then(setState, () => {})
    })
  }
  const errorFor = (row: string) => error?.row === row ? error.text : null

  if (!state) return null
  if (!state.found) {
    return (
      <Section id="lucid" title="Lucid">
        <Row label="Lucid" labelId="set-lucid-get" detail="Headphone EQ that switches with your device and your game" error={errorFor('get')}>
          {fetched ? <button type="button" className="exo-set-btn" disabled={busy !== null} onClick={() => void act('get', 'state')}>Check again</button> : null}
          <button type="button" className="exo-set-btn" disabled={busy !== null} onClick={() => void act('get', 'get')}>Get Lucid</button>
        </Row>
      </Section>
    )
  }

  const lucid = state.settings
  const setUp = lucid?.headphone.chosen === true
  return (
    <Section id="lucid" title="Lucid">
      <SwitchRow id="set-lucid-start" label="Start Lucid with Exo"
        detail="Out of sight"
        checked={state.startWithExo} disabled={false}
        onChange={on => host.lucid('set', { on }).then(next => { setState(next); return next.message ?? null }, () => "Lucid couldn't be changed. Try again.")} />
      <Row label={setUp ? 'Lucid' : 'Set up Lucid'} labelId="set-lucid-open"
        detail={setUp ? (state.version ?? undefined) : 'Lucid sets up its audio engine and asks for your headphones in its own window'} error={errorFor('open')}>
        {setUp ? <State on={state.running}>{state.running ? 'Running' : 'Not running'}</State> : null}
        <button type="button" className="exo-set-btn" disabled={busy !== null} onClick={() => void act('open', 'open')}>{setUp ? 'Open Lucid' : 'Set up'}</button>
      </Row>
      {update ? (
        <Row label={`Lucid ${update} is available`} labelId="set-lucid-update" error={errorFor('update')}>
          <button type="button" className="exo-set-btn" disabled={busy !== null} onClick={() => void act('update', 'open')}>Update</button>
        </Row>
      ) : null}
      {state.devices ? (
        <Row label="Audio devices" labelId="set-lucid-devices" detail="The outputs Lucid's EQ plays through, in Equalizer APO" error={errorFor('devices')}>
          <button type="button" className="exo-set-btn" disabled={busy !== null} onClick={() => void act('devices', 'devices')}>Choose devices</button>
        </Row>
      ) : null}
      {lucid ? (
        <>
          <HeadphoneRow lucid={lucid} error={errorFor('headphone')}
            onPick={h => change('headphone', { headphone: h.id }, { headphone: { id: h.id, name: h.model, chosen: true } })}
            onFavorite={(id, on) => change('headphone', { favorites: { [id]: on } },
              { favorites: on ? [...lucid.favorites, id] : lucid.favorites.filter(f => f.toLowerCase() !== id.toLowerCase()) })} />
          <LucidSwitch id="set-lucid-game" label="Game Mode" detail="Each game gets its own EQ while you play it" error={errorFor('gameMode')}
            checked={lucid.gameMode} onChange={on => change('gameMode', { gameMode: on }, { gameMode: on })} />
          {lucid.gameMode ? (
            <>
              <LucidSwitch id="set-lucid-alt" label="Keep the game's EQ on Alt+Tab" error={errorFor('keepOnAltTab')}
                checked={lucid.keepOnAltTab} onChange={on => change('keepOnAltTab', { keepOnAltTab: on }, { keepOnAltTab: on })} />
              <LucidSwitch id="set-lucid-only" label="Only while gaming" detail="Lucid turns on when a game starts and off when it ends" error={errorFor('onlyWhileGaming')}
                checked={lucid.onlyWhileGaming} onChange={on => change('onlyWhileGaming', { onlyWhileGaming: on }, { onlyWhileGaming: on })} />
            </>
          ) : null}
          <LucidSwitch id="set-lucid-night" label="Night Shift" detail="A warmer profile late at night" error={errorFor('nightShift')}
            checked={lucid.nightShift} onChange={on => change('nightShift', { nightShift: on }, { nightShift: on })} />
          {lucid.nightShift ? (
            <>
              <LucidSwitch id="set-lucid-sync" label="Follow Windows Night light" error={errorFor('nightSync')}
                checked={lucid.nightSync} onChange={on => change('nightSync', { nightSync: on }, { nightSync: on })} />
              {lucid.nightSync ? null : (
                <Row label="Hours" labelId="set-lucid-hours" error={errorFor('hours')}>
                  <Choice label="Night Shift starts" value={lucid.nightStart} choices={TIMES}
                    onChange={value => change('hours', { nightStart: value }, { nightStart: value })} />
                  <Choice label="Night Shift ends" value={lucid.nightEnd} choices={TIMES}
                    onChange={value => change('hours', { nightEnd: value }, { nightEnd: value })} />
                </Row>
              )}
            </>
          ) : null}
          <LucidSwitch id="set-lucid-notify" label="Lucid's notifications" detail="When its profile switches" error={errorFor('notifications')}
            checked={lucid.notifications} onChange={on => change('notifications', { notifications: on }, { notifications: on })} />
          {lucid.notifications ? (
            <>
              <SoundRow id="set-lucid-toggle-sound" label="Switch sound" detail="When Lucid turns on or off" value={lucid.toggleSound} error={errorFor('toggleSound')}
                onCommit={toggleSound => change('toggleSound', { toggleSound }, { toggleSound })} />
              <SoundRow id="set-lucid-detect-sound" label="Game sound" detail="When Lucid finds a game you play" value={lucid.detectSound} error={errorFor('detectSound')}
                onCommit={detectSound => change('detectSound', { detectSound }, { detectSound })} />
            </>
          ) : null}
          <LucidSwitch id="set-lucid-discord" label="Show Lucid in Discord" detail="Your Discord status says Lucid is on" error={errorFor('discord')}
            checked={lucid.discord} onChange={on => change('discord', { discord: on }, { discord: on })} />
        </>
      ) : null}
    </Section>
  )
})

function LucidSwitch({ id, label, detail, checked, error, onChange }: {
  id: string; label: string; detail?: string; checked: boolean; error: string | null; onChange: (on: boolean) => void
}) {
  return (
    <Row label={label} labelId={id} detail={detail} error={error}>
      <Switch checked={checked} labelledBy={id} onChange={onChange} />
    </Row>
  )
}

/** The headphone Lucid tunes for, and its list to pick from: search, favorites first, then each kind. */
function HeadphoneRow({ lucid, error, onPick, onFavorite }: {
  lucid: LucidSettings; error: string | null; onPick: (headphone: LucidHeadphone) => void; onFavorite: (id: string, on: boolean) => void
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

  const current = lucid.headphone.chosen ? lucid.headphone.name : 'Not chosen yet'
  return (
    <div className="exo-set-item">
      <div className="exo-set-item-main">
        <div className="exo-set-item-copy">
          <span id="set-lucid-headphone" className="exo-set-item-label">Headphones</span>
          <span className="exo-set-item-detail">{current}</span>
        </div>
        <div className="exo-set-item-control">
          <button type="button" className="exo-set-btn" aria-expanded={open} onClick={() => setOpen(!open)}>{open ? 'Done' : 'Change'}</button>
        </div>
      </div>
      {open ? (
        <div className="exo-lucid-picker">
          <input ref={field} type="search" className="exo-field" value={query} placeholder="Search headphones" aria-label="Search headphones"
            onChange={event => setQuery(event.target.value)} />
          {listError ? <p className="exo-set-error" role="alert">{listError}</p> : !list ? <p className="exo-lucid-empty" role="status">Reading Lucid's list…</p> : (
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
      {error ? <p className="exo-set-error" role="alert">{error}</p> : null}
    </div>
  )
}

/** One of Lucid's cue sounds, 0 (off) to 100, saved when the slider is let go. */
function SoundRow({ id, label, detail, value, error, onCommit }: {
  id: string; label: string; detail: string; value: number; error: string | null; onCommit: (value: number) => void
}) {
  const [shown, setShown] = useState(value)
  useEffect(() => setShown(value), [value])
  const commit = () => { if (shown !== value) onCommit(shown) }
  return (
    <Row label={label} labelId={id} detail={detail} error={error}>
      <div className="exo-lucid-volume">
        <Slider min={0} max={100} step={5} value={shown} aria-labelledby={id}
          onChange={event => setShown(Number(event.target.value))} onPointerUp={commit} onKeyUp={commit} />
        <span>{shown === 0 ? 'Off' : `${shown}%`}</span>
      </div>
    </Row>
  )
}
