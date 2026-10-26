import { ChevronRight, ExternalLink } from '../../brand/icons'
import { SkeletonWord } from '../ui/Skeleton'

export type LineWord = { text: string; good?: boolean; bad?: boolean }

/**
 * One of the game page's lines (Upscalers, Shaders) as a single row, like an iOS Settings row: the
 * name, its state, and a chevron to the PC page that handles it (an arrow out when it opens a
 * web page instead). While its state is read, a word-sized skeleton shines in its place (DESIGN.md › Waits).
 */
export function GameLine({ name, word, state, external, onOpen }: {
  name: string
  /** Its state in a word; null while it is read. */
  word: LineWord | null
  /** What the line reads (Shaders: the engine's state), for the E2E run. */
  state?: string
  external?: boolean
  onOpen: () => void
}) {
  const where = external ? 'open in browser' : 'open in PC'
  return (
    <section className="exo-game-line" aria-label={name} data-state={state}>
      <button type="button" className="exo-game-line-row" aria-label={[name, word?.text, where].filter(Boolean).join(', ')} onClick={onOpen}>
        <span className="exo-game-line-name">{name}</span>
        <span className={`exo-game-line-word${word?.good ? ' is-good' : ''}${word?.bad ? ' is-bad' : ''}`}>
          {word ? word.text : <SkeletonWord label={`Checking ${name.toLowerCase()}`} />}
        </span>
        {external ? <ExternalLink size={16} /> : <ChevronRight size={16} />}
      </button>
    </section>
  )
}
