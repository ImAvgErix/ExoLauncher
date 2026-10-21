
import { CompanyLogo } from '../../brand/CompanyLogo'
import { type Game, type GameVariant } from '../../lib/host'
import { formatRelativeLastPlayed, formatSize, storeLabel, formatPlayed } from '../../lib/utils'
import type { Achievements } from './useGameDetails'

/** The title with the store it belongs to beside it. */
export function GameHeader({ selected, children }: { selected: Game; children?: React.ReactNode }) {
  return (
    <div className="exo-game-title-row">
      <h1 className="exo-game-title">{selected.title}</h1>
      {children}
    </div>
  )
}

/**
 * The store the game belongs to, beside its name: the store's mark and name
 * in a pill, or one pill to switch between the stores you own it on.
 */
export function GameStore({
  selected,
  sources,
  locked,
  onSelectSource,
}: {
  selected: Game
  sources: GameVariant[] | null
  locked: boolean
  onSelectSource?: (id: string) => void
}) {
  if (sources && onSelectSource) {
    return (
      <div className="exo-game-store">
        <span className="exo-game-sources" role="group" aria-label="Choose game source">
          {sources.map((variant) => (
            <button
              key={variant.id}
              type="button"
              disabled={locked}
              onClick={() => onSelectSource(variant.id)}
              aria-pressed={variant.id === selected.id}
              aria-label={variant.updateAvailable ? `${storeLabel(variant.store)}, update available` : storeLabel(variant.store)}
              className={`exo-game-source${variant.id === selected.id ? ' is-on' : ''}${variant.updateAvailable ? ' has-update' : ''}`}
            >
              <CompanyLogo company={variant.store.toLowerCase()} size={14} />
              {storeLabel(variant.store)}
            </button>
          ))}
        </span>
      </div>
    )
  }
  if (selected.isAddPortable) return null
  return (
    <div className="exo-game-store">
      <span className="exo-game-store-name">
        <CompanyLogo company={selected.store.toLowerCase()} size={14} />
        {storeLabel(selected.store)}
      </span>
    </div>
  )
}

function Stat({ label, value }: { label: string; value: string }) {
  return (
    <div className="exo-game-stat">
      <span className="exo-game-stat-label">{label}</span>
      <span className="exo-game-stat-value tabular-nums">{value}</span>
    </div>
  )
}

/**
 * One row of facts in four equal columns: time played, last launched, size
 * and achievements. A fact Exo doesn't know is left out; the others keep
 * their width.
 */
export function GameStats({
  selected,
  sources,
  achievements,
}: {
  selected: Game
  sources: GameVariant[] | null
  achievements: Achievements | null
}) {
  // Source chips name one store. Use that store's hours, not the grouped
  // card total — the default chip matches the card id, so reading
  // selected.playtimeMinutes showed Epic+Steam while Epic looked selected.
  const source = sources?.find((variant) => variant.id === selected.id)
  const rows: Array<[string, string]> = [
    ['Time played', formatPlayed(source?.playtimeMinutes ?? selected.playtimeMinutes)],
    ['Last launched', formatRelativeLastPlayed(source?.lastPlayedUtc ?? selected.lastPlayedUtc)],
    ['Size', formatSize(selected.sizeBytes)],
    ['Achievements', achievements ? `${achievements.unlocked} of ${achievements.total}` : '—'],
  ]
  const known = rows.filter(([, value]) => value && value !== '—')
  if (known.length === 0) return null
  return (
    <div className="exo-game-stats">
      {known.map(([label, value]) => (
        <Stat key={label} label={label} value={value} />
      ))}
    </div>
  )
}

