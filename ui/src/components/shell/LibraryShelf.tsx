import { Button } from '../ui/Button'
import { Menu } from '../ui/Menu'
import { CHECK_PATH, ChevronDown, FolderPlus, Key, Plus, Refresh, Settings } from '../../brand/icons'
import type { SortMode } from '../../lib/host'
import { SORT_LABELS, SORT_OPTIONS } from '../../lib/library'

const SORT_ITEMS = SORT_OPTIONS.map((mode) => ({ id: mode, label: SORT_LABELS[mode] }))
const ADD_ITEMS = [
  { id: 'folder', label: 'Add a folder', icon: <FolderPlus size={16} /> },
  { id: 'key', label: 'Activate key', icon: <Key size={16} /> },
]

/** Related library tools stay together; adding never leaves the current shelf. */
export function LibraryTools({ sort, onSort, onActivateKey, onAddFolder, refresh, onRefresh }: {
  sort: SortMode
  onSort: (mode: SortMode) => void
  onActivateKey: () => void
  onAddFolder: () => void
  refresh: RefreshState
  onRefresh: () => void
}) {
  return (
    <div className="exo-library-tools">
      <Menu
        label="Sort library"
        anchor="--exo-sort-menu"
        offset={4}
        triggerClassName="exo-shelf-sort-trigger"
        trigger={<>{SORT_LABELS[sort]}<ChevronDown size={12} /></>}
        items={SORT_ITEMS}
        checked={sort}
        onAction={(id) => onSort(id as SortMode)}
      />
      <RefreshLibrary state={refresh} onRun={onRefresh} />
      <Menu
        label="Add game"
        anchor="--exo-add-menu"
        offset={8}
        triggerClassName="exo-add-game-btn exo-library-add"
        trigger={<Plus size={16} />}
        items={ADD_ITEMS}
        onAction={(id) => id === 'key' ? onActivateKey() : onAddFolder()}
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
export function LibraryEmpty({ error, onRetry, onAddFolder, onActivateKey, onStoreSettings }: {
  error: string | null
  onRetry: () => void
  onAddFolder: () => void
  onActivateKey: () => void
  onStoreSettings: () => void
}) {
  if (error) {
    return (
      <div className="exo-empty" role="alert">
        <h2>Library unavailable</h2>
        <p>{error}</p>
        <Button type="button" size="sm" className="exo-ghost-btn is-primary" onPress={onRetry}>
          Retry
        </Button>
      </div>
    )
  }
  return (
    <div className="exo-empty">
      <h2>No games yet</h2>
      <p>Sign in to a store in Settings, or add a folder of games.</p>
      <div className="exo-empty-actions">
        <button type="button" className="exo-ghost-btn" onClick={onStoreSettings}>
          <Settings size={14} />
          Store settings
        </button>
        <button type="button" className="exo-ghost-btn" onClick={onAddFolder}>
          <FolderPlus size={14} />
          Add a folder
        </button>
        <button type="button" className="exo-ghost-btn" onClick={onActivateKey}>
          <Key size={14} />
          Activate key
        </button>
      </div>
    </div>
  )
}
