import { useEffect, useState } from 'react'
import { host, type HdrGame } from '../../lib/host'
import { report } from '../../lib/report'
import { useHdrState } from '../../lib/hdr'
import { Choice } from '../settings/Choice'

const MODES = [
  { value: 'auto', label: 'Auto' },
  { value: 'on', label: 'Always' },
  { value: 'off', label: 'Never' },
] as const

/** Under Auto, whether the game has real HDR (what Auto follows); nothing when that isn't known. */
function hdrFact(hdr: string | null | undefined): string | null {
  if (hdr === 'true') return 'Has HDR'
  if (hdr === 'false') return 'No HDR'
  if (hdr === 'hackable') return 'HDR only with a mod'
  return null
}

/**
 * Game sheet > HDR: whether the game outputs real HDR (PCGamingWiki) and its Auto, Always or
 * Never choice for System > HDR > HDR for HDR games. While that is off nothing switches, so the
 * row says so and offers to turn it on. Shows only on a PC whose main screen can show HDR.
 */
export function HdrRow({ gameId, hdrForGames, onHdrForGames }: {
  gameId: string
  /** System > HDR > HDR for HDR games. */
  hdrForGames: boolean
  /** Turns it on; false when the save failed. */
  onHdrForGames: () => Promise<boolean>
}) {
  const screen = useHdrState()
  const [game, setGame] = useState<HdrGame | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (!screen?.supported) return
    let live = true
    setGame(null)
    host.hdrGame(gameId).then(answer => { if (live) setGame(answer) }).catch(e => report(e, 'HdrRow.load'))
    return () => { live = false }
  }, [gameId, screen?.supported])

  if (!screen?.supported || !game?.ok) return null
  const mode = game.mode ?? 'auto'
  const pick = (next: string) => {
    setBusy(true)
    setError(null)
    host.hdrGame(gameId, next)
      .then(answer => {
        if (answer.ok) setGame(answer)
        else setError('Could not save. Try again.')
      })
      .catch(e => { report(e, 'HdrRow.set'); setError('Could not save. Try again.') })
      .finally(() => setBusy(false))
  }
  const turnOn = () => {
    setBusy(true)
    setError(null)
    void onHdrForGames().then(ok => { if (!ok) setError('Could not save. Try again.') }).finally(() => setBusy(false))
  }
  const off = !hdrForGames && mode !== 'off'
  const say = error ?? (off ? 'HDR for HDR games is off, so it stays SDR' : mode === 'auto' ? hdrFact(game.hdr) : null)
  return (
    <section className="exo-graphics" aria-label="HDR">
      <div className="exo-graphics-head">
        <h3 className="exo-graphics-heading">HDR</h3>
        {say ? <span className={`exo-graphics-state${error ? ' is-bad' : ''}`} role={error ? 'alert' : undefined}>{say}</span> : null}
        {off ? <button type="button" className="exo-game-tool" disabled={busy} onClick={turnOn}>Turn on</button> : null}
      </div>
      <ul className="exo-graphics-list">
        <li className="exo-graphics-row">
          <span className="exo-graphics-name">HDR while playing</span>
          <Choice label="HDR while playing" value={mode} choices={MODES} disabled={busy} onChange={pick} />
        </li>
      </ul>
    </section>
  )
}
