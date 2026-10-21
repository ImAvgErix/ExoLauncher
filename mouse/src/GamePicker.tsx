import { useEffect, useMemo, useState } from 'react'
import { createPortal } from 'react-dom'
import { Segmented } from './ui'

export type Game = { id: string; title: string; cover?: string | null }
type Owner = { id: string; name: string; games?: string[] }

/** A game's cover, or its title on black when Exo has no art for it. */
export function Cover({ game, className }: { game: Game; className: string }) {
  const [failed, setFailed] = useState(false)
  return game.cover && !failed
    ? <img className={className} src={game.cover} alt="" loading="lazy" decoding="async" draggable={false} onError={() => setFailed(true)} />
    : <span className={`${className} is-blank`}>{game.title}</span>
}

/** Up to five linked games as small covers beside a profile's name, then a count. */
export function LinkedCovers({ ids, games }: { ids: string[]; games: Map<string, Game> }) {
  if (ids.length === 0) return null
  const shown = ids.slice(0, 5)
  const names = ids.map(id => games.get(id)?.title ?? 'A removed game')
  return (
    <span className="m-linked" aria-label={`Linked to ${names.join(', ')}`}>
      {shown.map(id => {
        const game = games.get(id)
        return game ? <Cover key={id} game={game} className="m-linked-cover" /> : <span key={id} className="m-linked-cover is-blank" />
      })}
      {ids.length > shown.length ? <span className="m-linked-more">+{ids.length - shown.length}</span> : null}
      {ids.length === 1 ? <span className="m-linked-name">{names[0]}</span> : null}
    </span>
  )
}

/**
 * The games a profile is for, picked from the installed library by cover: this
 * profile's games first and ringed, a game another profile has dimmed with that
 * profile's name (choosing it moves it here). Search by title; Linked shows
 * only this profile's. Escape or Done closes.
 */
export function GamePicker({ profile, profiles, games, onToggle, onClose }: {
  profile: Owner
  profiles: Owner[]
  games: Game[]
  onToggle: (game: string) => void
  onClose: () => void
}) {
  const [query, setQuery] = useState('')
  const [only, setOnly] = useState<'all' | 'linked'>('all')
  const mine = useMemo(() => new Set(profile.games ?? []), [profile.games])
  const owner = useMemo(() => {
    const map = new Map<string, string>()
    for (const p of profiles) if (p.id !== profile.id) for (const g of p.games ?? []) map.set(g, p.name)
    return map
  }, [profiles, profile.id])
  // Linked first, in the order they were at open, so a tile does not jump while you click.
  const [order] = useState(() => [...games].sort((a, b) => Number(mine.has(b.id)) - Number(mine.has(a.id)) || a.title.localeCompare(b.title)))
  const needle = query.trim().toLowerCase()
  const shown = order.filter(g => (only === 'all' || mine.has(g.id)) && (!needle || g.title.toLowerCase().includes(needle)))

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose() }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose])

  // On the page itself: a panel's arrive animation leaves a transform that would hold a fixed overlay inside the panel.
  return createPortal(
    <div className="m-picker-scrim" onMouseDown={e => { if (e.target === e.currentTarget) onClose() }}>
      <section className="m-picker" role="dialog" aria-modal="true" aria-labelledby="m-picker-title">
        <header className="m-picker-head">
          <h2 id="m-picker-title" className="m-picker-title">Games for {profile.name}</h2>
          <button type="button" className="m-button is-primary" onClick={onClose}>Done</button>
        </header>
        <div className="m-picker-tools">
          <input className="m-text" aria-label="Find a game" placeholder="Find a game" autoFocus value={query} onChange={e => setQuery(e.target.value)} />
          <Segmented label="Show" options={[{ value: 'all', label: 'All' }, { value: 'linked', label: `Linked ${mine.size}` }]} value={only} onChange={setOnly} />
        </div>
        <div className="m-picker-grid" role="group" aria-label={`Games for ${profile.name}`}>
          {shown.map(g => {
            const on = mine.has(g.id)
            const elsewhere = !on ? owner.get(g.id) : undefined
            return (
              <button key={g.id} type="button" role="checkbox" aria-checked={on}
                aria-label={`${g.title}${elsewhere ? `, on ${elsewhere}` : ''}`}
                className={`m-tile${on ? ' is-on' : ''}${elsewhere ? ' is-elsewhere' : ''}`}
                onClick={() => onToggle(g.id)}>
                <span className="m-tile-art">
                  <Cover game={g} className="m-tile-cover" />
                  {on ? <span className="m-tile-check" aria-hidden="true" /> : null}
                  {elsewhere ? <span className="m-tile-owner">On {elsewhere}</span> : null}
                </span>
                <span className="m-tile-title">{g.title}</span>
              </button>
            )
          })}
          {shown.length === 0 ? (
            <p className="m-picker-empty">{games.length === 0 ? 'No installed games.' : only === 'linked' ? 'No games linked yet.' : `No game called "${query.trim()}".`}</p>
          ) : null}
        </div>
      </section>
    </div>,
    document.body,
  )
}
