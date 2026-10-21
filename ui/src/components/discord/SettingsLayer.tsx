import type { ReactNode } from 'react'
import { createPortal } from 'react-dom'

/**
 * A Discord settings panel (yours, a server's, a channel's) floating over the whole Discord room on a dimmed backdrop,
 * whichever part of the room opened it; a press on the backdrop closes it.
 */
export function SettingsLayer({ onClose, children }: { onClose: () => void; children: ReactNode }) {
  const layer = (
    <div className="exo-dc-settings-backdrop" onPointerDown={event => { if (event.target === event.currentTarget) onClose() }}>
      {children}
    </div>
  )
  const room = document.querySelector('main.exo-dc')
  return room ? createPortal(layer, room) : layer
}
