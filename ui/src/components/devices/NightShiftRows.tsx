import { useEffect, useRef, useState } from 'react'
import { host, type LucidChange, type LucidSettings } from '../../lib/host'
import { report } from '../../lib/report'
import { Switch } from '../Switch'
import { Choice } from '../settings/Choice'
import { Row } from '../settings/parts'

/** Lucid's Night Shift times: every half hour, as its own picker offers them. */
const TIMES = Array.from({ length: 48 }, (_, i) => {
  const hour = Math.floor(i / 2), minute = i % 2 ? 30 : 0
  const value = `${String(hour).padStart(2, '0')}:${minute ? '30' : '00'}`
  return { value, label: `${hour % 12 || 12}:${minute ? '30' : '00'} ${hour < 12 ? 'AM' : 'PM'}` }
})

/**
 * Devices › Audio › Night Shift: Lucid's warmer sound late at night, on Windows' Night light schedule or its own hours.
 * Lucid reads its settings only when it starts, so a change closes it and starts it again out of sight (the host's
 * lucid change). Read each time the page shows, since Lucid's own window may have changed it.
 * Nothing without Lucid, or before Lucid has saved its settings.
 */
export function NightShiftRows({ active }: { active: boolean }) {
  const [lucid, setLucid] = useState<LucidSettings | null>(null)
  const [error, setError] = useState<{ row: string; text: string } | null>(null)
  const newest = useRef(0)

  useEffect(() => {
    if (!active) return
    let live = true
    host.lucid().then(state => { if (live) setLucid(state.found ? state.settings : null) }, (cause: unknown) => report(cause, 'NightShiftRows.state'))
    return () => { live = false }
  }, [active])

  /** Shows the change at once; Lucid's own file has the last word when it lands. */
  function change(row: string, patch: LucidChange, shown: Partial<LucidSettings>) {
    const mine = ++newest.current
    setError(null)
    setLucid(current => current ? { ...current, ...shown } : current)
    host.lucid('change', { change: patch }).then(next => {
      // Only the newest change's answer: an older one would snap a quick second change back until its own answer lands.
      if (mine === newest.current && next.settings) setLucid(next.settings)
      if (next.message) setError({ row, text: next.message })
    }, (cause: unknown) => {
      report(cause, 'NightShiftRows.change')
      setError({ row, text: "Lucid's settings didn't change. Try again." })
      void host.lucid().then(state => setLucid(state.settings), () => {})
    })
  }
  const errorFor = (row: string) => error?.row === row ? error.text : null

  if (!lucid) return null
  return (
    <>
      <Row label="Night Shift" labelId="pc-night-shift" error={errorFor('nightShift')}>
        <Switch checked={lucid.nightShift} labelledBy="pc-night-shift" onChange={on => change('nightShift', { nightShift: on }, { nightShift: on })} />
      </Row>
      {lucid.nightShift ? (
        <>
          <Row label="Follow Windows Night light" labelId="pc-night-sync" error={errorFor('nightSync')}>
            <Switch checked={lucid.nightSync} labelledBy="pc-night-sync" onChange={on => change('nightSync', { nightSync: on }, { nightSync: on })} />
          </Row>
          {lucid.nightSync ? null : (
            <Row label="Hours" labelId="pc-night-hours" error={errorFor('hours')}>
              <Choice label="Night Shift starts" value={lucid.nightStart} choices={TIMES}
                onChange={value => change('hours', { nightStart: value }, { nightStart: value })} />
              <Choice label="Night Shift ends" value={lucid.nightEnd} choices={TIMES}
                onChange={value => change('hours', { nightEnd: value }, { nightEnd: value })} />
            </Row>
          )}
        </>
      ) : null}
    </>
  )
}
