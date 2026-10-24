import { Button } from '../ui/Button'
import { Menu } from '../ui/Menu'
import { CHECK_PATH, ChevronDown, FolderPlus, Gamepad, Key, Plus, Refresh, Settings } from '../../brand/icons'
import type { SortMode } from '../../lib/host'
import { SORT_LABELS, SORT_OPTIONS } from '../../lib/library'

const SORT_ITEMS = SORT_OPTIONS.map((mode) => ({ id: mode, label: SORT_LABELS[mode], section: 'Sort by' }))
const ADD_ITEMS = [
  { id: 'new-folder', label: 'New folder', icon: <FolderPlus size={16} /> },
  { id: 'game', label: 'Add a game', icon: <Gamepad size={16} /> },
  { id: 'key', label: 'Activate key', icon: <Key size={16} /> },
]

/**
 * Related library tools stay together; adding never leaves the current shelf. The sort menu's Show section is what
 * the grid lists: All games, or Hidden games (only the games you hid). Select picks games to act on together; Done
 * (or Escape) ends it.
 */
export function LibraryTools({ selecting, onSelecting, sort, onSort, hidden, onHidden, onActivateKey, onNewFolder, onAddGame, refresh, onRefresh }: {
  selecting: boolean
  onSelecting: (on: boolean) => void
  sort: SortMode
  onSort: (mode: SortMode) => void
  hidden: boolean
  onHidden: (hidden: boolean) => void
  onActivateKey: () => void
  /** A Library folder: its name, color and games (FolderSheet). */
  onNewFolder: () => void
  /** A game's own .exe. */
  onAddGame: () => void
  refresh: RefreshState
  onRefresh: () => void
}) {
  return (
    <div className="exo-library-tools">
      <button type="button" className={`exo-set-btn${selecting ? ' is-primary' : ''}`} onClick={() => onSelecting(!selecting)}>
        {selecting ? 'Done' : 'Select'}
      </button>
      <Menu
        label="Sort library"
        anchor="--exo-sort-menu"
        offset={4}
        triggerClassName="exo-shelf-sort-trigger"
        trigger={<>{SORT_LABELS[sort]}<ChevronDown size={12} /></>}
        items={[
          ...SORT_ITEMS.map(item => ({ ...item, on: item.id === sort })),
          { id: 'all', label: 'All games', section: 'Show', on: !hidden },
          { id: 'hidden', label: 'Hidden games', section: 'Show', on: hidden },
        ]}
        onAction={(id) => id === 'all' || id === 'hidden' ? onHidden(id === 'hidden') : onSort(id as SortMode)}
      />
      <RefreshLibrary state={refresh} onRun={onRefresh} />
      <Menu
        label="Add game"
        anchor="--exo-add-menu"
        offset={8}
        triggerClassName="exo-add-game-btn exo-library-add"
        trigger={<Plus size={16} />}
        items={ADD_ITEMS}
        onAction={(id) => id === 'key' ? onActivateKey() : id === 'new-folder' ? onNewFolder() : onAddGame()}
      />
    </div>
  )
}


export type RefreshState = 'idle' | 'working' | 'done'

/** The Refresh button; LibraryPane owns its state so a run survives the menu leaving with its route. */
function RefreshLibrary({ state, onRun }: { state: RefreshState; onRun: () => void }) {
  return (
    <button
      type="button"
      className="exo-add-game-btn exo-refresh-covers"
      data-state={state}
      aria-label={state === 'working' ? 'Refreshing the library' : 'Refresh the library and covers'}
      aria-busy={state === 'working'}
      disabled={state === 'working'}
      onClick={onRun}
    >
      {state === 'done'
        ? <svg className="exo-refresh-covers-check" viewBox="0 0 24 24" aria-hidden="true"><path d={CHECK_PATH} pathLength="1" /></svg>
        : <Refresh size={16} />}
    </button>
  )
}

/** The shelf with nothing on it: a load failure, or no games from any store yet. */
export function LibraryEmpty({ error, onRetry, onAddGame, onActivateKey, onStoreSettings }: {
  error: string | null
  onRetry: () => void
  onAddGame: () => void
  onActivateKey: () => void
  onStoreSettings: () => void
}) {
  if (error) {
    return (
      <div className="exo-empty" role="alert">
        <h2>Library unavailable</h2>
        <p>{error}</p>
        <Button type="button" size="sm" className="exo-set-btn is-primary" onPress={onRetry}>
          Retry
        </Button>
      </div>
    )
  }
  return (
    <div className="exo-empty">
      <h2>No games yet</h2>
      <p>Sign in to a store in Settings, or add a game.</p>
      <div className="exo-empty-actions">
        <button type="button" className="exo-set-btn" onClick={onStoreSettings}>
          <Settings size={14} />
          Store settings
        </button>
        <button type="button" className="exo-set-btn" onClick={onAddGame}>
          <Gamepad size={14} />
          Add a game
        </button>
        <button type="button" className="exo-set-btn" onClick={onActivateKey}>
          <Key size={14} />
          Activate key
        </button>
      </div>
    </div>
  )
}
