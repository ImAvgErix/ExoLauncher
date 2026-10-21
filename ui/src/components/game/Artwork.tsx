import { useEffect, useState, type ReactNode } from 'react'
import { ImageUpload, Loader2, Refresh, Undo } from '../../brand/icons'
import { host, type ArtworkMutationResponse, type Game } from '../../lib/host'
import { useSelectionGuard } from './useSelectionGuard'

type ArtAction = 'replace' | 'reset' | 'refetch'

function errorText(error: unknown, fallback: string): string {
  return error instanceof Error ? error.message : fallback
}

/** The cover's own tools: Replace, and Reset (a custom cover) or Refresh art (the store's). */
export type Artwork = { action: ArtAction | null; custom: boolean; replace: () => void; reset: () => void; refetch: () => void }

/** Replace, reset or fetch the cover again; the new cover is the confirmation, only a failure says anything. */
export function useArtwork(selected: Game, coverSource: string | null | undefined, onArtwork: (game: Game) => void,
  onStatus: (msg: string | null, sticky?: boolean) => void): Artwork {
  const guard = useSelectionGuard(`${selected.store}:${selected.id}`)
  const [action, setAction] = useState<ArtAction | null>(null)
  useEffect(() => setAction(null), [selected.id, selected.store])
  const run = (name: ArtAction, operation: (id: string) => Promise<ArtworkMutationResponse>) => {
    if (action) return
    setAction(name)
    void operation(selected.id)
      .then(guard((result) => {
        if (result.cancelled) { onStatus(null); return }
        if (result.ok && result.game) onArtwork(result.game)
        onStatus(result.ok ? null : result.message ?? 'Artwork could not be updated.', !result.ok)
      }))
      .catch(guard((error: unknown) => onStatus(errorText(error, 'Artwork could not be updated.'), true)))
      .finally(() => guard(() => setAction(null))(undefined))
  }
  return {
    action,
    custom: coverSource === 'custom',
    replace: () => run('replace', host.artReplace),
    reset: () => run('reset', host.artReset),
    refetch: () => run('refetch', host.artRefetch),
  }
}

/**
 * Replace the cover, or put Exo's art back (reset a custom cover, or fetch the
 * store's art again). A busy button keeps its label and swaps its icon for a
 * spinner, so the row never changes width.
 */
export function ArtworkTools({ artwork, disabled, className = 'exo-game-tool' }: { artwork: Artwork; disabled: boolean; className?: string }) {
  const { action } = artwork
  const busy = action !== null
  const icon = (name: ArtAction, idle: ReactNode) =>
    action === name ? <Loader2 size={14} className="animate-spin motion-reduce:animate-none" /> : idle
  return (
    <>
      <button type="button" className={className} disabled={disabled || busy} aria-busy={action === 'replace'} onClick={artwork.replace}>
        {icon('replace', <ImageUpload size={14} />)}
        Replace cover
      </button>
      {artwork.custom ? (
        <button type="button" className={className} disabled={disabled || busy} aria-busy={action === 'reset'} onClick={artwork.reset}>
          {icon('reset', <Undo size={14} />)}
          Reset cover
        </button>
      ) : (
        <button type="button" className={className} disabled={disabled || busy} aria-busy={action === 'refetch'} onClick={artwork.refetch}>
          {icon('refetch', <Refresh size={14} />)}
          Refresh art
        </button>
      )}
    </>
  )
}
