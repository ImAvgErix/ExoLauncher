import { useRef } from 'react'
import { Eye, EyeOff, Folder, FolderPlus, Trash } from '../../brand/icons'
import type { Game, LibraryFolder } from '../../lib/host'
import { useLens } from '../../lib/glass'
import { confirm } from '../ui/Dialog'
import { Menu } from '../ui/Menu'

/**
 * Select mode's bar: glass floating over the grid's foot, as Discord's call bar does. The count, then what applies to
 * every picked game: Hide (Unhide in Hidden games), Add to folder, Uninstall for installed games that aren't running
 * (it asks first, as on a game's page; Remove when they were all added from a folder); then Select all and Clear.
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
  const uninstall = async () => {
    const one = count === 1
    const sure = await confirm({
      title: `${verb} ${one ? games[0].title : `${count} games`}?`,
      body: verb === 'Remove'
        ? one ? 'It leaves your Library. Its files stay where they are.' : 'They leave your Library. Their files stay where they are.'
        : one ? 'Its files leave this PC. It stays in your Library, ready to install again.' : 'Their files leave this PC. They stay in your Library, ready to install again.',
      action: verb,
      danger: true,
    })
    if (sure === true) onUninstall()
  }
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
        <button type="button" className="exo-set-btn is-danger" onClick={() => void uninstall()}>
          <Trash size={16} />
          {verb}
        </button>
      ) : null}
      <span className="exo-select-rule" aria-hidden="true" />
      {count < total ? <button type="button" className="exo-set-btn" onClick={onAll}>Select all</button> : null}
      <button type="button" className="exo-set-btn" disabled={!count} onClick={onClear}>Clear</button>
    </div>
  )
}
