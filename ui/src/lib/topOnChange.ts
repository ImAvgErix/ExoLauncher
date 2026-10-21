import { useLayoutEffect, useRef, type RefObject } from 'react'

/**
 * Scrolls back to the top when `key` changes (another page, tab or search), and only then: a hidden room's
 * effects run again when it comes back, and that must not lose the place it was left at.
 */
export function useTopOnChange(scroller: RefObject<HTMLElement | null>, key: string) {
  const last = useRef(key)
  useLayoutEffect(() => {
    if (last.current === key) return
    last.current = key
    scroller.current?.scrollTo({ top: 0 })
  }, [scroller, key])
}
