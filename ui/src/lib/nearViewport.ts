let shared: { observer: IntersectionObserver; callbacks: Map<Element, () => void> } | null = null

/** One native observer for every tile near the viewport; released when no tiles remain. */
export function observeNearViewport(element: Element, visible: () => void): () => void {
  if (!shared) {
    const callbacks = new Map<Element, () => void>()
    const observer = new IntersectionObserver(entries => {
      for (const entry of entries) {
        if (!entry.isIntersecting) continue
        const callback = callbacks.get(entry.target)
        if (!callback) continue
        callbacks.delete(entry.target)
        observer.unobserve(entry.target)
        callback()
      }
      if (!callbacks.size) {
        observer.disconnect()
        if (shared?.observer === observer) shared = null
      }
    }, { rootMargin: '240px 480px' })
    shared = { observer, callbacks }
  }
  const current = shared
  current.callbacks.set(element, visible)
  current.observer.observe(element)
  return () => {
    if (current.callbacks.get(element) !== visible) return
    current.callbacks.delete(element)
    current.observer.unobserve(element)
    if (!current.callbacks.size) {
      current.observer.disconnect()
      if (shared === current) shared = null
    }
  }
}
