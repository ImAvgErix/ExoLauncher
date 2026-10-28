import { useEffect, useMemo, useState } from 'react'
import { host, type ControllerGameProfile, type PadOverrides } from '../../lib/host'
import { report } from '../../lib/report'
import { Choice } from '../settings/Choice'
import { Row } from '../settings/parts'
import { Switch } from '../Switch'
import { Dialog } from '../ui/Dialog'
import { ErrorNote } from '../ui/ErrorNote'
import { Spinner } from '../ui/Spinner'

type Game = { id: string; title: string }

const SAME = 'same'
const LIGHTS = [
  { value: SAME, label: 'As usual' }, { value: 'games', label: 'The game sets it' }, { value: 'off', label: 'Off' },
  { value: '#FFFFFF', label: 'White' }, { value: '#1E64FF', label: 'Blue' }, { value: '#00C8FF', label: 'Cyan' }, { value: '#8A2BE2', label: 'Purple' },
  { value: '#FF4FA0', label: 'Pink' }, { value: '#FF2020', label: 'Red' }, { value: '#FF8C00', label: 'Orange' }, { value: '#20D060', label: 'Green' },
]
const ONOFF = [{ value: SAME, label: 'As usual' }, { value: 'on', label: 'On' }, { value: 'off', label: 'Off' }]
const STRENGTHS = [{ value: SAME, label: 'As usual' }, { value: 'full', label: 'Full' }, { value: 'medium', label: 'Medium' }, { value: 'low', label: 'Low' }, { value: 'off', label: 'Off' }]
const TRIGGERS = STRENGTHS.map(s => s.value === 'off' ? { ...s, label: 'Lightest' } : s)

/** The installed games a profile can link, by title. */
async function installedGames(): Promise<Game[]> {
  const library = await host.getLibrary()
  return (library.games ?? []).filter(g => g.installed).map(g => ({ id: g.id, title: g.title })).sort((a, b) => a.title.localeCompare(b.title))
}

/**
 * Game profiles (DESIGN.md › Devices), as Exo Mouse has them: named sets of the controller's light, sound and vibration,
 * each linked to games. Exo sends a linked game's profile just before the game starts, over USB, and the usual settings
 * when it ends; anything a profile leaves As usual stays as the controller's page sets it.
 */
export function GameProfileRows() {
  const [profiles, setProfiles] = useState<ControllerGameProfile[] | null>(null)
  const [games, setGames] = useState<Game[]>([])
  const [editing, setEditing] = useState<ControllerGameProfile | null>(null)
  const [error, setError] = useState<string | null>(null)
  useEffect(() => {
    let current = true
    host.controllerGameProfiles().then(r => { if (current) setProfiles(r.profiles ?? []) }, (cause: unknown) => report(cause, 'GameProfiles.read'))
    installedGames().then(list => { if (current) setGames(list) }, (cause: unknown) => report(cause, 'GameProfiles.games', { level: 'warn' }))
    return () => { current = false }
  }, [])
  const titles = useMemo(() => new Map(games.map(g => [g.id, g.title])), [games])

  async function save(next: ControllerGameProfile[]) {
    const result = await host.controllerGameProfilesSave(next)
    if (!result.ok || !result.profiles) return result.message ?? 'Exo couldn’t save them. Try again.'
    setProfiles(result.profiles)
    setEditing(null)
    return null
  }

  if (!profiles) return <Row label="Game profiles" detail={<Spinner label="Reading game profiles" />} />
  return (
    <>
      {profiles.map(profile => (
        <Row key={profile.id} label={profile.name} detail={linked(profile.games, titles)}>
          <button type="button" className="exo-set-btn" onClick={() => setEditing(profile)}>Edit</button>
        </Row>
      ))}
      <Row label={profiles.length === 0 ? 'A game’s own light, sound and vibration' : 'Another game profile'} error={error}>
        <button type="button" className="exo-set-btn" onClick={() => { setError(null); setEditing({ id: crypto.randomUUID(), name: `Profile ${profiles.length + 1}`, settings: {}, games: [] }) }}>New</button>
      </Row>
      {editing ? (
        <ProfileEditor profile={editing} profiles={profiles} games={games} isNew={!profiles.some(p => p.id === editing.id)}
          onClose={() => setEditing(null)}
          onSave={draft => save(profiles.some(p => p.id === draft.id) ? profiles.map(p => p.id === draft.id ? draft : withoutGames(p, draft.games)) : [...profiles.map(p => withoutGames(p, draft.games)), draft])}
          onDelete={() => save(profiles.filter(p => p.id !== editing.id))} />
      ) : null}
    </>
  )
}

/** A game belongs to one profile: linking it here takes it from another. */
const withoutGames = (profile: ControllerGameProfile, games: string[]) => ({ ...profile, games: profile.games.filter(g => !games.includes(g)) })

function linked(ids: string[], titles: Map<string, string>) {
  if (ids.length === 0) return 'No games linked'
  const names = ids.map(id => titles.get(id) ?? 'A removed game')
  return names.length <= 2 ? names.join(' and ') : `${names.slice(0, 2).join(', ')} and ${names.length - 2} more`
}

const pick = (value: string | null | undefined) => value ?? SAME
const onoff = (value: boolean | null | undefined, inverted = false) => value == null ? SAME : (value !== inverted ? 'on' : 'off')
const fromOnoff = (value: string, inverted = false) => value === SAME ? null : (value === 'on') !== inverted

/** One profile: its name, what it changes (each As usual until set), its games, and Delete. */
function ProfileEditor({ profile, profiles, games, isNew, onClose, onSave, onDelete }: {
  profile: ControllerGameProfile
  profiles: ControllerGameProfile[]
  games: Game[]
  isNew: boolean
  onClose: () => void
  onSave: (draft: ControllerGameProfile) => Promise<string | null>
  onDelete: () => Promise<string | null>
}) {
  const [draft, setDraft] = useState(profile)
  const [picking, setPicking] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const set = (patch: Partial<PadOverrides>) => setDraft(d => ({ ...d, settings: { ...d.settings, ...patch } }))
  async function run(work: () => Promise<string | null>) {
    setBusy(true)
    setError(null)
    try { setError(await work()) } catch (cause) { report(cause, 'GameProfiles.save'); setError('Exo couldn’t save them. Try again.') } finally { setBusy(false) }
  }
  const s = draft.settings
  const owner = (id: string) => profiles.find(p => p.id !== draft.id && p.games.includes(id))?.name
  return (
    <Dialog title={isNew ? 'New game profile' : draft.name} form onClose={onClose} actions={
      <>
        <button type="button" className="exo-set-btn" data-autofocus onClick={onClose}>Cancel</button>
        {!isNew ? <button type="button" className="exo-set-btn is-danger" disabled={busy} onClick={() => void run(onDelete)}>Delete</button> : null}
        <button type="button" className="exo-set-btn is-primary" disabled={busy || !draft.name.trim()} onClick={() => void run(() => onSave(draft))}>
          {busy ? <Spinner label="Saving" /> : null}Save
        </button>
      </>
    }>
      <label className="exo-field exo-profile-name">
        <input value={draft.name} maxLength={40} aria-label="Profile name" onChange={event => setDraft(d => ({ ...d, name: event.target.value }))} />
      </label>
      <Row label="Games" detail={`${draft.games.length} linked`}>
        <button type="button" className="exo-set-btn" onClick={() => setPicking(true)}>Choose</button>
      </Row>
      <Row label="Light bar"><Choice label="Light bar" value={pick(s.light)} choices={LIGHTS} onChange={v => set({ light: v === SAME ? null : v })} /></Row>
      <Row label="Player light"><Choice label="Player light" value={onoff(s.playerLight)} choices={ONOFF} onChange={v => set({ playerLight: fromOnoff(v) })} /></Row>
      <Row label="Microphone"><Choice label="Microphone" value={onoff(s.micOff, true)} choices={ONOFF} onChange={v => set({ micOff: fromOnoff(v, true) })} /></Row>
      <Row label="Speaker"><Choice label="Speaker" value={onoff(s.speakerOff, true)} choices={ONOFF} onChange={v => set({ speakerOff: fromOnoff(v, true) })} /></Row>
      <Row label="Vibration"><Choice label="Vibration" value={pick(s.vibration)} choices={STRENGTHS} onChange={v => set({ vibration: v === SAME ? null : v as PadOverrides['vibration'] })} /></Row>
      <Row label="Trigger effects"><Choice label="Trigger effects" value={pick(s.triggerEffects)} choices={TRIGGERS} onChange={v => set({ triggerEffects: v === SAME ? null : v as PadOverrides['triggerEffects'] })} /></Row>
      {error ? <ErrorNote className="exo-dialog-error" message={error} /> : null}
      {picking ? (
        <GamePicker name={draft.name} games={games} chosen={draft.games} owner={owner} onClose={() => setPicking(false)}
          onToggle={id => setDraft(d => ({ ...d, games: d.games.includes(id) ? d.games.filter(g => g !== id) : [...d.games, id] }))} />
      ) : null}
    </Dialog>
  )
}

/** The installed games, searchable by title: this profile's checked, one on another profile named (choosing it moves it). */
function GamePicker({ name, games, chosen, owner, onToggle, onClose }: {
  name: string
  games: Game[]
  chosen: string[]
  owner: (id: string) => string | undefined
  onToggle: (id: string) => void
  onClose: () => void
}) {
  const [query, setQuery] = useState('')
  const needle = query.trim().toLowerCase()
  const shown = games.filter(g => !needle || g.title.toLowerCase().includes(needle))
  return (
    <Dialog title={`Games for ${name || 'this profile'}`} onClose={onClose} actions={<button type="button" className="exo-set-btn is-primary" data-autofocus onClick={onClose}>Done</button>}>
      <label className="exo-field">
        <input value={query} placeholder="Find a game" aria-label="Find a game" onChange={event => setQuery(event.target.value)} />
      </label>
      <div className="exo-game-pick-list" role="group" aria-label={`Games for ${name}`}>
        {shown.map(game => {
          const on = chosen.includes(game.id)
          const elsewhere = on ? undefined : owner(game.id)
          const label = `gp-${game.id.replace(/[^a-zA-Z0-9]/g, '-')}`
          return (
            <Row key={game.id} label={game.title} labelId={label} detail={elsewhere ? `On ${elsewhere}` : undefined}>
              <Switch checked={on} labelledBy={label} onChange={() => onToggle(game.id)} />
            </Row>
          )
        })}
        {shown.length === 0 ? <p className="exo-game-pick-empty">{games.length === 0 ? 'No installed games.' : `No game called “${query.trim()}”.`}</p> : null}
      </div>
    </Dialog>
  )
}
