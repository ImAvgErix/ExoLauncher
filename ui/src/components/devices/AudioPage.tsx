import { useEffect, useState, type ReactNode } from 'react'
import { Search } from '../../brand/icons'
import { host, type LucidHeadphone, type Peripheral } from '../../lib/host'
import { report } from '../../lib/report'
import { LucidAlertRows, LucidAppRows, LucidGameRows, LucidMissingRow, useLucid } from '../settings/LucidSection'
import { Row } from '../settings/parts'
import { NightShiftRows } from './NightShiftRows'

export type AudioLook = 'headphones' | 'iem' | 'headset'
/** What you wear on the jack, as you picked it ("Ziigaat Lush", in-ear); null until you do. */
export type Wearing = { name: string; look: AudioLook } | null

/** Lucid's kinds of headphone, as its catalog files them, to the picture of each. */
const LUCID_LOOK: Record<string, AudioLook> = { 'In-Ear Monitors': 'iem', 'Earbuds': 'iem', 'Gaming Headsets': 'headset', 'Headphones': 'headphones' }

/**
 * Your audio's page, laid out as Exo Mouse lays out a mouse (DESIGN.md › Devices): one screen, no scroll. The hero on
 * the left is Exo Mouse's: what you wear, its picture large in its own light, its name and how it's linked, a readout of
 * what Windows plays it through and hears you on, and thanks to Lucid's maker at its foot. Its controls stand in two
 * columns beside it: which pair it is and Lucid, then Lucid's modes (Game Mode and Night Shift) and its sounds.
 */
export function AudioPage({ name, status, art, output, mic, wearing, onWearing }: {
  name: string
  status: string
  art: ReactNode
  output?: Peripheral
  mic?: Peripheral
  wearing: Wearing
  onWearing: (wearing: Wearing) => void
}) {
  const lucid = useLucid()
  const found = lucid.state?.found === true
  const worn = output && output.model !== 'speakers' && output.model !== 'monitor'
  return (
    <section className="exo-audio" aria-label={name}>
      <aside className="exo-audio-hero">
        <div className="exo-device-hero-art">{art}</div>
        <div className="exo-audio-id">
          <h1>{name}</h1>
          <p>{status}</p>
        </div>
        <dl className="exo-audio-stats">
          {output ? <div><dt>Playing on</dt><dd>{output.name}</dd></div> : null}
          {mic ? <div><dt>Microphone</dt><dd>{mic.name}</dd></div> : null}
        </dl>
        <p className="exo-audio-credit">
          Built on Lucid by tekkusai
          <svg className="exo-audio-heart" viewBox="0 0 24 24" aria-hidden="true"><path d="M12 20.5 4.2 12.9A4.9 4.9 0 0 1 11.3 6.1l.7.7.7-.7a4.9 4.9 0 0 1 7.1 6.8z" /></svg>
        </p>
      </aside>
      <div className="exo-audio-panels">
        <div className="exo-audio-column">
          {worn ? <Panel title="Headphones"><HeadphonePicker wearing={wearing} onWearing={onWearing} /></Panel> : null}
          <Panel title="Lucid">{found ? <LucidAppRows lucid={lucid} /> : lucid.state ? <LucidMissingRow lucid={lucid} /> : null}</Panel>
        </div>
        <div className="exo-audio-column">
          {found ? <Panel title="Modes"><LucidGameRows lucid={lucid} /><NightShiftRows active /></Panel> : null}
          {found ? <Panel title="Sounds and notifications"><LucidAlertRows lucid={lucid} /></Panel> : null}
        </div>
      </div>
    </section>
  )
}

function Panel({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="exo-system-panel exo-audio-panel" aria-label={title}>
      <h2>{title}</h2>
      <div className="exo-system-panel-body">{children}</div>
    </section>
  )
}

/**
 * Which headphones you wear: a search of the models Lucid knows (hundreds of IEMs, headphones and headsets). The field
 * takes the row's button's place and its matches list under the row, across the panel.
 */
function HeadphonePicker({ wearing, onWearing }: { wearing: Wearing; onWearing: (wearing: Wearing) => void }) {
  const [list, setList] = useState<LucidHeadphone[] | null>(null)
  const [query, setQuery] = useState('')
  const [open, setOpen] = useState(false)
  useEffect(() => {
    if (!open || list) return
    host.lucidHeadphones().then(result => setList(result.ok ? result.headphones : []), (error: unknown) => {
      report(error, 'AudioPage.headphones', { level: 'warn' })
      setList([])
    })
  }, [open, list])
  const words = query.trim().toLowerCase()
  const hits = (list ?? []).filter(headphone => !words || `${headphone.brand} ${headphone.model}`.toLowerCase().includes(words)).slice(0, 5)
  const pick = (headphone: LucidHeadphone) => {
    onWearing({ name: `${headphone.brand} ${headphone.model}`.trim(), look: LUCID_LOOK[headphone.category] ?? 'headphones' })
    setOpen(false)
    setQuery('')
  }
  return (
    <>
      <Row label="Your headphones" detail={wearing ? wearing.name : 'Windows can\'t tell which pair is on the jack. Pick yours.'}>
        {open ? (
          <label className="exo-field exo-devices-pick-field">
            <Search size={14} />
            <input type="search" value={query} autoFocus placeholder="Ziigaat Lush" aria-label="Search headphones" onChange={event => setQuery(event.target.value)}
              onKeyDown={event => { if (event.key === 'Escape') setOpen(false) }} />
          </label>
        ) : (
          <button type="button" className="exo-set-btn" onClick={() => setOpen(true)}>{wearing ? 'Change' : 'Pick'}</button>
        )}
      </Row>
      {open ? (
        <ul className="exo-devices-pick-list" aria-label="Headphones">
          {list === null ? <li className="exo-devices-pick-wait">Reading Lucid's list</li>
            : hits.length === 0 ? <li className="exo-devices-pick-wait">No match</li>
            : hits.map(headphone => (
              <li key={headphone.id}>
                <button type="button" onClick={() => pick(headphone)}><strong>{headphone.brand} {headphone.model}</strong><span>{headphone.category}</span></button>
              </li>
            ))}
        </ul>
      ) : null}
    </>
  )
}
