import { useLayoutEffect, useRef } from 'react'

const SELECTED = '[aria-pressed="true"], [aria-checked="true"], [aria-selected="true"], [aria-current]:not([aria-current="false"])'

/**
 * The selected pill glides: one fill under the group slides to the chosen
 * item (the 200 ms switch motion, DESIGN.md › Motion) instead of jumping
 * between items. Put the returned ref and `exo-glide` on the group; the
 * selected item is whichever carries aria-pressed, aria-checked,
 * aria-selected or aria-current, so groups need no extra state. The first
 * placement does not animate; Reduce motion makes every move instant.
 */
export function useGlide<T extends HTMLElement>() {
  const ref = useRef<T>(null)
  useLayoutEffect(() => {
    const box = ref.current
    if (!box) return
    // A group mounted hidden (Settings before it opens) arms only after its
    // first visible placement, so the fill never slides in from nothing.
    let ready = 0
    const place = () => {
      const on = box.querySelector<HTMLElement>(SELECTED)
      const style = box.style
      if (!on || on.offsetWidth === 0) {
        style.setProperty('--glide-o', '0')
        return
      }
      // Measured on screen, so an item inside another positioned box or a scrolled list still lines up.
      const at = on.getBoundingClientRect()
      const origin = box.getBoundingClientRect()
      style.setProperty('--glide-x', `${at.left - origin.left - box.clientLeft + box.scrollLeft}px`)
      style.setProperty('--glide-y', `${at.top - origin.top - box.clientTop + box.scrollTop}px`)
      style.setProperty('--glide-w', `${on.offsetWidth}px`)
      style.setProperty('--glide-h', `${on.offsetHeight}px`)
      style.setProperty('--glide-o', '1')
      if (!ready) ready = requestAnimationFrame(() => box.classList.add('is-glide-ready'))
    }
    place()
    const attributes = new MutationObserver(place)
    attributes.observe(box, { subtree: true, childList: true, attributes: true, attributeFilter: ['aria-pressed', 'aria-checked', 'aria-selected', 'aria-current'] })
    const sizes = new ResizeObserver(place)
    sizes.observe(box)
    for (const child of box.querySelectorAll('button')) sizes.observe(child)
    return () => {
      cancelAnimationFrame(ready)
      attributes.disconnect()
      sizes.disconnect()
    }
  }, [])
  return ref
}
