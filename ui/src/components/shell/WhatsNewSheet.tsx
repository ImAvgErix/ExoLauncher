import { useEffect, useRef } from 'react'
import { createPortal } from 'react-dom'
import type { WhatsNewRelease } from '../../lib/whatsNew'
import { useDismiss } from '../../lib/dismiss'
import { Spinner } from '../ui/Spinner'
import type { AppUpdate } from './useAppUpdate'

/**
 * What's new in Exo <version>: a sheet of large glass over the dimmed window, its items one under another (an icon in
 * a soft circle, a title, one line on what it does for you), then Continue. Escape or a press outside closes it too.
 * Releases after the first follow under their own small heading. While an update with notes is offered, "Coming in
 * Exo <version>" leads, with Install update beside Continue. Opened after an update (LauncherApp), from Settings ›
 * About and from the update prompt.
 */
export function WhatsNewSheet({ releases, update, onClose }: { releases: WhatsNewRelease[] | null; update: AppUpdate; onClose: () => void }) {
  const upcoming = update.upcoming && (update.available || update.busy) ? update.upcoming : null
  return releases && (releases.length || upcoming) ? <Sheet releases={releases} upcoming={upcoming} update={update} onClose={onClose} /> : null
}

function Sheet({ releases, upcoming, update, onClose }: { releases: WhatsNewRelease[]; upcoming: WhatsNewRelease | null; update: AppUpdate; onClose: () => void }) {
  const continueButton = useRef<HTMLButtonElement>(null)
  const sheet = useRef<HTMLDivElement>(null)
  useDismiss(sheet, onClose, { modal: true })
  const close = useRef(onClose)
  useEffect(() => { close.current = onClose }, [onClose])

  useEffect(() => {
    const before = document.activeElement instanceof HTMLElement ? document.activeElement : null
    continueButton.current?.focus({ preventScroll: true })
    // Ahead of the window's own Escape (Settings, a game's page): this sheet is on top. Tab stays on its buttons.
    const onKey = (event: KeyboardEvent) => {
      if (event.key !== 'Escape' && event.key !== 'Tab') return
      event.preventDefault()
      event.stopPropagation()
      if (event.key === 'Escape') { close.current(); return }
      const buttons = [...(sheet.current?.querySelectorAll<HTMLButtonElement>('.exo-whats-new-foot button:not(:disabled)') ?? [])]
      const at = buttons.indexOf(document.activeElement as HTMLButtonElement)
      buttons[(at + (event.shiftKey ? buttons.length - 1 : 1)) % buttons.length]?.focus()
    }
    window.addEventListener('keydown', onKey, true)
    return () => {
      window.removeEventListener('keydown', onKey, true)
      before?.focus({ preventScroll: true })
    }
  }, [])

  const sections = upcoming ? [upcoming, ...releases] : releases
  const starts = sections.map((_, index) => sections.slice(0, index).reduce((count, release) => count + release.items.length, 0))
  return createPortal(
    <div className="exo-whats-new-back">
      <div ref={sheet} className="exo-whats-new" role="dialog" aria-modal="true" aria-labelledby="exo-whats-new-title">
        <span className="exo-glass is-panel exo-sheet-glass" aria-hidden="true" />
        <h2 id="exo-whats-new-title">{upcoming ? `Coming in Exo ${upcoming.version}` : `What's new in Exo ${sections[0].version}`}</h2>
        <div className="exo-whats-new-list">
          {sections.map((release, index) => (
            <section key={release.version}>
              {index > 0 && <h3>In {release.version}</h3>}
              <ul>
                {release.items.map(({ icon: Icon, title, detail }, item) => (
                  <li key={title} style={{ animationDelay: `${Math.min(starts[index] + item, 8) * 30}ms` }}>
                    <span className="exo-whats-new-icon"><Icon size={20} /></span>
                    <span className="exo-whats-new-words">
                      <strong>{title}</strong>
                      <span>{detail}</span>
                    </span>
                  </li>
                ))}
              </ul>
            </section>
          ))}
        </div>
        {upcoming && update.error && <p role="alert" className="exo-whats-new-error">{update.error}</p>}
        <div className="exo-whats-new-foot">
          <button ref={continueButton} type="button" className={`exo-set-btn${upcoming ? '' : ' is-primary'}`} onClick={onClose}>Continue</button>
          {upcoming && (
            <button type="button" className="exo-set-btn is-primary" disabled={update.busy} onClick={() => void update.install()}>
              {/* How far it is, the update's notice says with its bar; the button only that it is under way. */}
              {update.busy ? <Spinner label={update.status} /> : null}
              {update.busy ? update.status : 'Install update'}
            </button>
          )}
        </div>
      </div>
    </div>,
    document.body,
  )
}
