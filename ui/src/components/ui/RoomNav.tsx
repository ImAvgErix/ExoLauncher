import type { ReactNode } from 'react'

export type RoomPage = { id: string; label: string; sub?: string | null; icon: ReactNode }

/**
 * A room's page list (DESIGN.md › Rooms): a floating pane of glass at the room's left, one row per page with its glyph,
 * its name and a short line under it; the current page is lit. The same in every room with more than one page
 * (Devices, PC), so moving between related tools is one press and never a long scroll.
 */
export function RoomNav({ label, title, pages, current, onPick, children }: {
  label: string
  title?: string
  pages: RoomPage[]
  current: string
  onPick: (id: string) => void
  /** What sits at the pane's foot (a rescan, a note). */
  children?: ReactNode
}) {
  return (
    <nav className="exo-room-nav" aria-label={label}>
      {title ? <h2 className="exo-room-nav-title">{title}</h2> : null}
      <ul>
        {pages.map(page => (
          <li key={page.id}>
            <button type="button" className="exo-room-nav-item" aria-current={current === page.id ? 'page' : undefined} onClick={() => onPick(page.id)}>
              <span className="exo-room-nav-icon" aria-hidden="true">{page.icon}</span>
              <span className="exo-room-nav-text">
                <span className="exo-room-nav-label">{page.label}</span>
                {page.sub ? <span className="exo-room-nav-sub">{page.sub}</span> : null}
              </span>
            </button>
          </li>
        ))}
      </ul>
      {children ? <div className="exo-room-nav-foot">{children}</div> : null}
    </nav>
  )
}
