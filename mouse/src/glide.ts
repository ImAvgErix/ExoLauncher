import { useLayoutEffect, useRef } from 'react'

/**
 * The selected pill glides: one fill under the group slides to the chosen
 * item on the 200 ms switch timing instead of jumping, as in Exo. Put the
 * returned ref and `m-glide` on the group; the selected item is whichever
 * carries aria-checked or aria-pressed. The first placement does not animate;
 * reduce motion makes every move instant (mouse.css).
 */
export function useGlide<T extends HTMLElement>() {
  const ref = useRef<T>(null)
  useLayoutEffect(() => {
    const box = ref.current
    if (!box) return
    let ready = 0
    const place = () => {
      const on = box.querySelector<HTMLElement>('[aria-checked="true"], [aria-pressed="true"]')
      const style = box.style
      if (!on || on.offsetWidth === 0) {
        style.setProperty('--glide-o', '0')
        return
      }
      style.setProperty('--glide-x', `${on.offsetLeft}px`)
      style.setProperty('--glide-y', `${on.offsetTop}px`)
      style.setProperty('--glide-w', `${on.offsetWidth}px`)
      style.setProperty('--glide-h', `${on.offsetHeight}px`)
      style.setProperty('--glide-o', '1')
      if (!ready) ready = requestAnimationFrame(() => box.classList.add('is-glide-ready'))
    }
    place()
    const attributes = new MutationObserver(place)
    attributes.observe(box, { subtree: true, childList: true, attributes: true, attributeFilter: ['aria-checked', 'aria-pressed'] })
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
