import { useLayoutEffect } from 'react'

/** When the last shining wait left the screen (performance.now()). */
let lastGone = Number.NEGATIVE_INFINITY

/**
 * Every shining wait keeps one rhythm (DESIGN.md › Waits). Each sweep is set to the page's clock, so a wait that takes
 * over from another (the Music room's opening frame giving way to the player's own, a list's skeleton remounting) goes
 * on with the same sweep, and one that replaces another in the same moment doesn't fade in a second time: starting over
 * read as the page reloading.
 */
export function useKeepShine() {
  useLayoutEffect(() => {
    const continuing = performance.now() - lastGone < 400
    for (const animation of document.getAnimations()) {
      if (!(animation instanceof CSSAnimation)) continue
      if (animation.animationName === 'exo-shine' && animation.startTime !== 0) animation.startTime = 0
      else if (continuing && animation.animationName === 'exo-wait-in' && animation.playState !== 'finished') animation.finish()
    }
    return () => { lastGone = performance.now() }
  }, [])
}
