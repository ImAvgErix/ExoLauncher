/** Imperative animations use the same app preference as the shell and Motion. */
export function shouldReduceMotion(): boolean {
  if (typeof document !== 'undefined') {
    const preference = document.documentElement.dataset.exoMotion
    if (preference === 'reduce') return true
    if (preference === 'full') return false
  }
  return typeof window !== 'undefined' && window.matchMedia('(prefers-reduced-motion: reduce)').matches
}

/**
 * What last drove Exo, the keyboard or a pointer (DESIGN.md › Motion › Keys), on the root as
 * data-exo-input. What the keyboard does lands at once: a room or page it changes doesn't slide and a
 * menu it opens doesn't grow; a pointer gets the motion. A key press counts from its keydown and a
 * pointer from its press, so the click that follows a press is already a pointer's.
 */
export function installInputModality() {
  const root = document.documentElement
  const set = (input: 'key' | 'pointer') => { if (root.dataset.exoInput !== input) root.dataset.exoInput = input }
  window.addEventListener('keydown', () => set('key'), { capture: true, passive: true })
  window.addEventListener('pointerdown', () => set('pointer'), { capture: true, passive: true })
}

/** Whether the keyboard, not a pointer, did the last thing (installInputModality). */
export const fromKeyboard = () => typeof document !== 'undefined' && document.documentElement.dataset.exoInput === 'key'
