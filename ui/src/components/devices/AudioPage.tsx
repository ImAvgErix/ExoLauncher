import { useEffect, useState, type ReactNode } from 'react'
import { Search } from '../../brand/icons'
import { host, type LucidHeadphone, type Peripheral } from '../../lib/host'
import { report } from '../../lib/report'
import { LucidAlertRows, LucidAppRows, LucidGameRows, LucidGuessRow, LucidHeadphoneRow, LucidMissingRow, useLucid, type Lucid } from '../settings/LucidSection'
import { Row } from '../settings/parts'
import { DeviceColumn, DevicePage, DevicePanel as Panel } from './DevicePage'
import { NightShiftRows } from './NightShiftRows'

export type AudioLook = 'headphones' | 'iem' | 'headset'
/** What you wear on the jack, as you picked it ("Ziigaat Lush", in-ear; Lucid's id for it); null until you do. */
export type Wearing = { name: string; look: AudioLook; id?: string } | null

/** Lucid's kinds of headphone, as its catalog files them, to the picture of each. */
const LUCID_LOOK: Record<string, AudioLook> = { 'In-Ear Monitors': 'iem', 'Earbuds': 'iem', 'Gaming Headsets': 'headset', 'Headphones': 'headphones' }

const wearingOf = (headphone: LucidHeadphone): Wearing =>
  ({ name: `${headphone.brand} ${headphone.model}`.trim(), look: LUCID_LOOK[headphone.category] ?? 'headphones', id: headphone.id })

/**
 * The card follows Lucid: a headphone chosen in Lucid's own window (or before Exo kept Lucid's id) names the card and
 * draws its picture, once Lucid's list says what kind it is.
 */
function useFollowLucid(tunedId: string | null, wearing: Wearing, onWearing: (wearing: Wearing) => void) {
  const shownId = wearing?.id
  useEffect(() => {
    if (!tunedId || tunedId === shownId) return
    let live = true
    host.lucidHeadphones().then(result => {
      const headphone = result.ok ? result.headphones.find(h => h.id === tunedId) : undefined
      if (live && headphone) onWearing(wearingOf(headphone))
    }, (error: unknown) => report(error, 'AudioPage.followLucid', { level: 'warn' }))
    return () => { live = false }
  }, [tunedId, shownId, onWearing])
}

/**
 * Your audio's page, laid out as Exo Mouse lays out a mouse (DESIGN.md › Devices): one screen, no scroll. The hero on
 * the left is Exo Mouse's: what you wear, its picture large in its own light, its name and how it's linked, a readout of
 * what Windows plays it through and hears you on, and thanks to Lucid's maker at its foot. Its controls stand in two
 * columns beside it: which pair it is and Lucid, then Lucid's modes (Game Mode and Night Shift) and its sounds. With
 * Lucid, which pair it is is one pick: Lucid's EQ is tuned for it, and the card's name and picture follow it.
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
  const lucid = useLucid(output?.id)
  const found = lucid.state?.found === true
  const worn = !!output && output.model !== 'speakers' && output.model !== 'monitor'
  return (
    <DevicePage name={name} status={status} art={art}
      stats={[{ label: 'Playing on', value: output?.name ?? null }, { label: 'Microphone', value: mic?.name ?? null }]}
      credit="Built on Lucid by tekkusai">
      <PairAndLucid lucid={lucid} worn={worn} wearing={wearing} onWearing={onWearing} />
      <DeviceColumn>
        {found ? <Panel title="Modes"><LucidGameRows lucid={lucid} /><NightShiftRows active /></Panel> : null}
        {found ? <Panel title="Sounds and notifications"><LucidAlertRows lucid={lucid} /></Panel> : null}
      </DeviceColumn>
    </DevicePage>
  )
}

/**
 * The page's first column: which pair you wear, then Lucid. With Lucid's settings the pick is Lucid's own (its EQ is
 * tuned for it); without them, Exo's picker only names the card. On speakers there is nothing worn, and Lucid's pick
 * stays in its panel as the pair its EQ is tuned for.
 */
function PairAndLucid({ lucid, worn, wearing, onWearing }: { lucid: Lucid; worn: boolean; wearing: Wearing; onWearing: (wearing: Wearing) => void }) {
  const found = lucid.state?.found === true
  const tuned = found && lucid.state?.settings ? lucid.state.settings.headphone : null
  useFollowLucid(tuned?.chosen ? tuned.id : null, wearing, onWearing)
  const pickInLucid = <LucidHeadphoneRow lucid={lucid} label={worn ? 'Your headphones' : 'EQ tuned for'} onPicked={headphone => onWearing(wearingOf(headphone))} />
  return (
    <DeviceColumn>
      {worn ? <Panel title="Headphones"><LucidGuessRow lucid={lucid} onPicked={headphone => onWearing(wearingOf(headphone))} />{tuned ? pickInLucid : <HeadphonePicker wearing={wearing} onWearing={onWearing} />}</Panel> : null}
      <Panel title="Lucid">{found ? <><LucidAppRows lucid={lucid} />{worn ? null : pickInLucid}</> : lucid.state ? <LucidMissingRow lucid={lucid} /> : null}</Panel>
    </DeviceColumn>
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
    onWearing(wearingOf(headphone))
    setOpen(false)
    setQuery('')
  }
  return (
    <>
      <Row label="Your headphones" detail={wearing?.name}>
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
