import { useCallback, useEffect, useState } from 'react'
import { Loader2, Sliders, Trash } from '../../brand/icons'
import { host, type DlssStatus, type Game } from '../../lib/host'
import { ArtworkTools, useArtwork } from './Artwork'
import { PropertiesSheet } from './PropertiesSheet'
import { useSelectionGuard } from './useSelectionGuard'

/** How long an armed Uninstall waits for its second press before it stands down. */
const DISARM_MS = 4000

/** An armed Uninstall stands down on Escape, after DISARM_MS, or when focus leaves it (onBlur at the call site). */
export function useDisarm(armed: boolean, disarm: () => void) {
  useEffect(() => {
    if (!armed) return
    function onKeyDown(event: KeyboardEvent) {
      if (event.key !== 'Escape') return
      event.stopPropagation()
      disarm()
    }
    const timer = window.setTimeout(disarm, DISARM_MS)
    window.addEventListener('keydown', onKeyDown, true)
    return () => {
      window.clearTimeout(timer)
      window.removeEventListener('keydown', onKeyDown, true)
    }
  }, [armed, disarm])
}

function errorText(error: unknown, fallback: string): string {
  return error instanceof Error ? error.message : fallback
}

export type GameActionsProps = {
  selected: Game
  /** The cover currently on screen; `coverSource === 'custom'` unlocks Reset. */
  coverSource: string | null | undefined
  /** Host action in flight elsewhere (launch, install, ...): Uninstall waits. */
  locked: boolean
  upscalers: DlssStatus | null
  onUpscalersChanged: () => void
  onStatus: (msg: string | null, sticky?: boolean) => void
  onArtwork: (game: Game) => void
  onUninstalled: () => void
  /** True while Uninstall has a host call in flight. */
  onBusyChange: (busy: boolean) => void
  /** Properties over the page: its button here, or the card menu's Properties. */
  propertiesOpen: boolean
  onPropertiesOpen: (open: boolean) => void
}

/**
 * Beside Play on an installed game: Uninstall (a second press confirms; Remove for a game added from a folder,
 * whose files Exo leaves where they are) and Properties,
 * which holds everything else (folder, files, updates, DLC, artwork).
 */
export function GameActions({
  selected,
  coverSource,
  locked,
  upscalers,
  onUpscalersChanged,
  onStatus,
  onArtwork,
  onUninstalled,
  onBusyChange,
  propertiesOpen,
  onPropertiesOpen,
}: GameActionsProps) {
  const guard = useSelectionGuard(`${selected.store}:${selected.id}`)
  const [uninstalling, setUninstalling] = useState(false)
  const [armed, setArmed] = useState(false)
  const artwork = useArtwork(selected, coverSource, onArtwork, onStatus)

  useEffect(() => {
    setUninstalling(false)
    setArmed(false)
  }, [selected.id, selected.store])

  const busy = uninstalling || artwork.action !== null
  useEffect(() => onBusyChange(busy), [busy, onBusyChange])
  const disarm = useCallback(() => setArmed(false), [])
  useDisarm(armed, disarm)

  // A game added from a folder leaves the Library, its files where they are: that is a Remove.
  const addedHere = selected.store === 'local'
  const verb = addedHere ? 'remove' : 'uninstall'
  function remove() {
    if (!armed) {
      setArmed(true)
      return
    }
    setArmed(false)
    setUninstalling(true)
    onStatus(`${addedHere ? 'Removing' : 'Uninstalling'} ${selected.title}…`)
    void host.uninstall(selected.id)
      .then(guard((r) => {
        // A handoff opened the store's own app: neutral info, never a sticky error.
        const handoff = !!r.handoffOnly
        onStatus(r.message ?? (handoff ? 'Finish uninstalling it in the store app.' : r.ok ? (addedHere ? 'Removed.' : 'Uninstalled.') : `Could not ${verb} the game.`), !r.ok && !handoff)
        if (r.ok && !r.handoffOnly && !r.queued) onUninstalled()
      }))
      .catch(guard((error: unknown) => onStatus(errorText(error, `Could not ${verb} the game.`), true)))
      .finally(() => guard(() => setUninstalling(false))(undefined))
  }

  return (
    <>
      <button
        type="button"
        disabled={locked || uninstalling || !!selected.canStop}
        className={`exo-game-tool exo-game-side${armed ? ' is-armed' : ''}`}
        onClick={remove}
        onBlur={disarm}
      >
        {uninstalling ? <Loader2 size={16} className="animate-spin motion-reduce:animate-none" /> : <Trash size={16} />}
        {uninstalling ? (addedHere ? 'Removing…' : 'Uninstalling…') : armed ? 'Confirm' : addedHere ? 'Remove' : 'Uninstall'}
      </button>
      <button type="button" className="exo-game-tool exo-game-side is-icon" aria-label="Properties" aria-haspopup="dialog" onClick={() => onPropertiesOpen(true)}>
        <Sliders size={16} />
      </button>
      {propertiesOpen && (
        <PropertiesSheet
          key={`${selected.store}:${selected.id}`}
          game={selected}
          artwork={artwork}
          upscalers={upscalers}
          onUpscalersChanged={onUpscalersChanged}
          onClose={() => onPropertiesOpen(false)}
          onStatus={onStatus}
        />
      )}
    </>
  )
}

/** A game that is not installed keeps its cover tools on the page: it has no Properties. */
export function ArtworkStrip({ selected, coverSource, locked, onArtwork, onStatus }: {
  selected: Game
  coverSource: string | null | undefined
  locked: boolean
  onArtwork: (game: Game) => void
  onStatus: (msg: string | null, sticky?: boolean) => void
}) {
  const artwork = useArtwork(selected, coverSource, onArtwork, onStatus)
  return (
    <div className="exo-game-tools exo-utility-row" role="group" aria-label="Artwork">
      <ArtworkTools artwork={artwork} disabled={locked} />
    </div>
  )
}
