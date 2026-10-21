import { type DlssStatus } from '../lib/host'
import { trimStop } from '../lib/utils'
import { upscalerFamilies, upscalerView, type UpscalerView } from '../lib/upscalers'
import { GameLine } from './game/GameLine'

export type UpscalerFilesProps = {
  status?: DlssStatus | null
  scanState?: 'loading' | 'ready' | 'failed'
  /** System > Graphics > Upscalers, where every game's upscalers are updated and checked again. */
  onOpen: () => void
}

/**
 * The game's upscalers as one row that opens System > Graphics > Upscalers: up to date, how many
 * updates wait, the anti-cheat, or why the files could not be read. Updating happens on that page;
 * the originals and the anti-cheat consent are in Properties.
 */
export function UpscalerFiles({ status, scanState = 'ready', onOpen }: UpscalerFilesProps) {
  const view = upscalerView(status)
  const scanError = scanFailure(scanState, status)
  if (view.rows.length === 0 && !scanError) return null
  return <GameLine name="Upscalers" word={lineWord(view, scanError, scanState)} onOpen={onOpen} />
}

/** The files could not be read (the install folder is gone, the read failed): why, for the line. */
function scanFailure(scanState: UpscalerFilesProps['scanState'], status: DlssStatus | null | undefined): string | null {
  if (scanState !== 'failed') return null
  return trimStop(status?.message ?? '') || 'Could not check the files'
}

/** The line's one word: why the files could not be read, the anti-cheat, else how many upscalers have an update. */
function lineWord(view: UpscalerView, scanError: string | null, scanState: UpscalerFilesProps['scanState']): { text: string; good?: boolean; bad?: boolean } {
  if (scanError) return { text: scanError, bad: true }
  if (scanState === 'loading') return { text: 'Checking…' }
  if (view.consent === 'never') return { text: 'Never changed' }
  if (view.locked) return { text: `Uses ${view.antiCheatName ?? 'anti-cheat'}` }
  const outdated = upscalerFamilies(view.rows).filter(row => row.state === 'outdated').length || (view.updatable > 0 ? 1 : 0)
  if (outdated > 0) return { text: outdated === 1 ? '1 update' : `${outdated} updates` }
  return { text: 'Up to date' }
}
