import { useEffect, useRef } from 'react'
import { createPortal } from 'react-dom'
import type { WhatsNewRelease } from '../../lib/whatsNew'
import { useDismiss } from '../../lib/dismiss'

/**
 * What's new in Exo <version>: a sheet of large glass over the dimmed window, its items one under another (an icon in
 * a soft circle, a title, one line on what it does for you), then Continue. Escape or a press outside closes it too.
 * Opened once after an update (LauncherApp) and from Settings › About.
 */
export function WhatsNewSheet({ release, onClose }: { release: WhatsNewRelease | null; onClose: () => void }) {
  return release ? <Sheet release={release} onClose={onClose} /> : null
}

function Sheet({ release, onClose }: { release: WhatsNewRelease; onClose: () => void }) {
  const continueButton = useRef<HTMLButtonElement>(null)
  const sheet = useRef<HTMLDivElement>(null)
  useDismiss(sheet, onClose, { modal: true })
  const close = useRef(onClose)
  useEffect(() => { close.current = onClose }, [onClose])

  useEffect(() => {
    const before = document.activeElement instanceof HTMLElement ? document.activeElement : null
    continueButton.current?.focus({ preventScroll: true })
    // Ahead of the window's own Escape (Settings, a game's page): this sheet is on top. Tab stays on Continue.
    const onKey = (event: KeyboardEvent) => {
      if (event.key !== 'Escape' && event.key !== 'Tab') return
      event.preventDefault()
      event.stopPropagation()
      if (event.key === 'Escape') close.current()
      else continueButton.current?.focus()
    }
    window.addEventListener('keydown', onKey, true)
    return () => {
      window.removeEventListener('keydown', onKey, true)
      before?.focus({ preventScroll: true })
    }
  }, [])

  return createPortal(
    <div className="exo-whats-new-back">
      <div ref={sheet} className="exo-whats-new" role="dialog" aria-modal="true" aria-labelledby="exo-whats-new-title">
        <span className="exo-glass is-panel exo-sheet-glass" aria-hidden="true" />
        <h2 id="exo-whats-new-title">What's new in Exo {release.version}</h2>
        <ul className="exo-whats-new-list">
          {release.items.map(({ icon: Icon, title, detail }, index) => (
            <li key={title} style={{ animationDelay: `${Math.min(index, 8) * 30}ms` }}>
              <span className="exo-whats-new-icon"><Icon size={20} /></span>
              <span className="exo-whats-new-words">
                <strong>{title}</strong>
                <span>{detail}</span>
              </span>
            </li>
          ))}
        </ul>
        <button ref={continueButton} type="button" className="exo-ghost-btn is-primary exo-whats-new-continue" onClick={onClose}>Continue</button>
      </div>
    </div>,
    document.body,
  )
}
