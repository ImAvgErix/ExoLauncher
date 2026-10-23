import { useCallback, useEffect, useRef, useState } from 'react'
import { Eye, EyeOff, Folder, FolderPlus, Trash } from '../../brand/icons'
import type { Game, LibraryFolder } from '../../lib/host'
import { useLens } from '../../lib/glass'
import { useDisarm } from '../game/ManageStrip'
import { Menu } from '../ui/Menu'

/**
 * Select mode's bar: glass floating over the grid's foot, as Discord's call bar does. The count, then what applies to
 * every picked game: Hide (Unhide in Hidden games), Add to folder, Uninstall for installed games that aren't running
 * (a second press confirms, as on a game's page; Remove when they were all added from a folder); then Select all and
 * Clear.
 */
export function LibrarySelectBar({ games, total, hiddenView, folders, onAll, onClear, onHide, onFolder, onUninstall }: {
  /** The picked games. */
  games: Game[]
  /** How many the grid can pick. */
  total: number
  hiddenView: boolean
  folders: LibraryFolder[]
  onAll: () => void
  onClear: () => void
  onHide: () => void
  /** Into this folder; null: a new one. */
  onFolder: (folderId: string | null) => void
  onUninstall: () => void
}) {
  const count = games.length
  const removable = count > 0 && games.every(game => game.installed && !game.canStop && !game.isRunning)
  const verb = games.every(game => game.store === 'local') ? 'Remove' : 'Uninstall'
  const [armed, setArmed] = useState(false)
  const disarm = useCallback(() => setArmed(false), [])
  useDisarm(armed, disarm)
  useEffect(disarm, [count, disarm])
  const glass = useRef<HTMLDivElement>(null)
  useLens(glass)
  return (
    <div ref={glass} className="exo-glass exo-select-bar" role="toolbar" aria-label="Selected games">
      <span className="exo-select-count" role="status">{count ? `${count} selected` : 'Select games'}</span>
      <button type="button" className="exo-set-btn" disabled={!count} onClick={onHide}>
        {hiddenView ? <Eye size={16} /> : <EyeOff size={16} />}
        {hiddenView ? 'Unhide' : 'Hide'}
      </button>
      {hiddenView ? null : (
        <Menu
          label="Add to folder"
          anchor="--exo-select-folder"
          offset={8}
          triggerClassName="exo-set-btn"
          trigger={<><Folder size={16} />Add to folder</>}
          disabled={!count}
          items={[
            ...folders.map(folder => ({ id: `in:${folder.id}`, label: folder.name, icon: <Folder size={16} /> })),
            { id: 'new', label: 'New folder', icon: <FolderPlus size={16} /> },
          ]}
          onAction={id => onFolder(id.startsWith('in:') ? id.slice(3) : null)}
        />
      )}
      {removable ? (
        <button type="button" className="exo-set-btn is-danger" onBlur={disarm}
          aria-label={armed ? `Confirm: ${verb.toLowerCase()} ${count} ${count === 1 ? 'game' : 'games'}` : undefined}
          onClick={() => { if (armed) { setArmed(false); onUninstall() } else setArmed(true) }}>
          <Trash size={16} />
          {armed ? 'Confirm' : verb}
        </button>
      ) : null}
      <span className="exo-select-rule" aria-hidden="true" />
      {count < total ? <button type="button" className="exo-set-btn" onClick={onAll}>Select all</button> : null}
      <button type="button" className="exo-set-btn" disabled={!count} onClick={onClear}>Clear</button>
    </div>
  )
}
