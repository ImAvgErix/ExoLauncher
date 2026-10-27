import type { ReactNode } from 'react'

/**
 * A device's page, laid out as Exo Mouse lays out a mouse (DESIGN.md › Devices): one screen, no scroll. The hero on the
 * left: the device's picture large in its own light, its name and how it's linked, a readout of figures on hairlines,
 * and thanks to whoever made what it's built on at its foot. Its panels stand in columns beside it (each child is one
 * column). The audio page and the controller page are built on it.
 */
export function DevicePage({ name, status, art, stats, credit, children }: {
  name: string
  status: string
  art: ReactNode
  /** The readout: label and figure pairs; a pair whose figure is null isn't drawn. */
  stats: { label: string; value: ReactNode | null }[]
  credit?: ReactNode
  children: ReactNode
}) {
  const shown = stats.filter(stat => stat.value !== null && stat.value !== undefined)
  return (
    <section className="exo-device-page" aria-label={name}>
      <aside className="exo-device-page-hero">
        <div className="exo-device-hero-art">{art}</div>
        <div className="exo-device-page-id">
          <h1>{name}</h1>
          <p>{status}</p>
        </div>
        {shown.length ? (
          <dl className="exo-device-page-stats">
            {shown.map(stat => <div key={stat.label}><dt>{stat.label}</dt><dd>{stat.value}</dd></div>)}
          </dl>
        ) : null}
        {credit ? (
          <p className="exo-device-page-credit">
            {credit}
            <svg className="exo-device-page-heart" viewBox="0 0 24 24" aria-hidden="true"><path d="M12 20.5 4.2 12.9A4.9 4.9 0 0 1 11.3 6.1l.7.7.7-.7a4.9 4.9 0 0 1 7.1 6.8z" /></svg>
          </p>
        ) : null}
      </aside>
      <div className="exo-device-page-panels">{children}</div>
    </section>
  )
}

/** One column of panels on a device page. */
export function DeviceColumn({ children }: { children: ReactNode }) {
  return <div className="exo-device-page-column">{children}</div>
}

/** One panel: its name over its rows; a panel with no rows isn't drawn (styles/system.css). */
export function DevicePanel({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="exo-system-panel exo-device-page-panel" aria-label={title}>
      <h2>{title}</h2>
      <div className="exo-system-panel-body">{children}</div>
    </section>
  )
}
