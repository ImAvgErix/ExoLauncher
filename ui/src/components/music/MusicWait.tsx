import { useKeepShine } from '../../lib/shine'
import { cn } from '../../lib/utils'

const bone = (className: string) => <span className={cn('exo-skeleton exo-shine', className)} />

/** Home's shapes: a greeting, the recent grid and two shelves, at their sizes. */
function HomeBones() {
  // The room's opening frame gives way to the player's own: one sweep goes on across both.
  useKeepShine()
  return <>
    {bone('is-greeting')}
    <span className="exo-music-cover-recent">{Array.from({ length: 8 }, (_, i) => <span key={i}>{bone('is-art')}{bone('is-line')}</span>)}</span>
    {[0, 1].map(shelf => (
      <span key={shelf} className="exo-music-cover-shelf">
        {bone('is-title')}
        <span>{Array.from({ length: 8 }, (_, i) => <span key={i}>{bone('is-cover')}{bone('is-line')}{bone('is-sub')}</span>)}</span>
      </span>
    ))}
  </>
}

/** The first page still on its way after the room has shown (a slow service): Home's shapes where Home will be. */
export function PageBones({ label }: { label: string }) {
  return <div className="exo-music-cover" role="status" aria-label={label}><HomeBones /></div>
}

/**
 * The room before its first showing (DESIGN.md › Waits): the room's own frame, your library down the left, Home in the
 * middle (a greeting, the recent grid, two shelves) and the player along the bottom, each a clear skeleton in the place
 * and size of what comes, shining. It stands from the moment the room opens, through the service starting, until your
 * library and the first page are both in; then it fades once and the room is there whole, never squares, then a frame,
 * then content. `leaving` starts the fade; `onLeft` follows it.
 */
export function MusicWait({ name, leaving = false, onLeft }: { name: string; leaving?: boolean; onLeft?: () => void }) {
  return (
    <div className={cn('exo-music exo-music-cover', onLeft && 'is-over', leaving && 'is-leaving')} role="status" aria-label={`Opening ${name}`}
      onAnimationEnd={event => { if (event.target === event.currentTarget && leaving) onLeft?.() }}>
      <div className="exo-music-side" aria-hidden="true">
        {[0, 1].map(i => <span key={i} className="exo-music-cover-nav">{bone('is-glyph')}{bone('is-word')}</span>)}
        {bone('is-search')}
        <span className="exo-music-cover-head">{bone('is-word')}{bone('is-pill')}</span>
        {Array.from({ length: 12 }, (_, i) => <span key={i} className="exo-music-cover-row">{bone('is-art')}<span>{bone('is-line')}{bone('is-sub')}</span></span>)}
      </div>
      <div className="exo-music-main exo-music-cover-page" aria-hidden="true"><HomeBones /></div>
      <div className="exo-glass exo-music-bar exo-music-cover-bar" aria-hidden="true">
        <span>{bone('is-art')}<span>{bone('is-line')}{bone('is-sub')}</span></span>
        <span>{bone('is-play')}{bone('is-track')}</span>
        <span>{bone('is-word')}</span>
      </div>
    </div>
  )
}
