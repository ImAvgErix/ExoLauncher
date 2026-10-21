import { ChevronRight, ExternalLink } from '../../brand/icons'

export type LineWord = { text: string; good?: boolean; bad?: boolean }

/**
 * One of the game page's lines (Upscalers, Shaders) as a single row, like an iOS Settings row: the
 * name, its state, and a chevron to the System page that handles it (an arrow out when it opens a
 * web page instead).
 */
export function GameLine({ name, word, state, external, onOpen }: {
  name: string
  word: LineWord | null
  /** What the line reads (Shaders: the engine's state), for the E2E run. */
  state?: string
  external?: boolean
  onOpen: () => void
}) {
  const where = external ? 'open in browser' : 'open in System'
  return (
    <section className="exo-game-line" aria-label={name} data-state={state}>
      <button type="button" className="exo-game-line-row" aria-label={[name, word?.text, where].filter(Boolean).join(', ')} onClick={onOpen}>
        <span className="exo-game-line-name">{name}</span>
        <span className={`exo-game-line-word${word?.good ? ' is-good' : ''}${word?.bad ? ' is-bad' : ''}`}>{word?.text}</span>
        {external ? <ExternalLink size={16} /> : <ChevronRight size={16} />}
      </button>
    </section>
  )
}
