import { useEffect, useId, useState } from 'react'
import { host, type GpuGame, type IntelFrameGen } from '../../lib/host'
import { report } from '../../lib/report'
import { Choice } from '../settings/Choice'
import { Switch } from '../Switch'

const FRAME_GEN = [
  { value: 'game', label: 'Game’s choice' },
  { value: '2x', label: '2x' },
  { value: '3x', label: '3x' },
  { value: '4x', label: '4x' },
] as const

/**
 * Game sheet > the driver's frame generation for this one game. On a Radeon, Fluid Motion Frames: off by default; on,
 * Exo turns it on just before the game starts and back off once no game runs (AMD's setting is global). It works only
 * while AMD Software runs, so the row says so when it doesn't. On an Intel GPU, XeSS frame generation: the game's own
 * choice by default, or 2x, 3x or 4x, which Exo sets in Intel's driver just before the game starts and puts back once
 * no game runs. Shows only when the GPU's driver has it.
 */
export function FrameGenRow({ gameId }: { gameId: string }) {
  const labelId = useId()
  const [game, setGame] = useState<GpuGame | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let live = true
    setGame(null)
    host.gpuGame(gameId).then(answer => { if (live) setGame(answer) }).catch(e => report(e, 'FrameGenRow.load'))
    return () => { live = false }
  }, [gameId])

  if (!game?.ok || !game.supported) return null
  const intel = game.vendor === 'intel'
  const pick = (change: { afmf?: boolean; frameGen?: IntelFrameGen }) => {
    setBusy(true)
    setError(null)
    host.gpuGame(gameId, change)
      .then(answer => {
        if (answer.ok) setGame(answer)
        else setError('Could not save. Try again.')
      })
      .catch(e => { report(e, 'FrameGenRow.set'); setError('Could not save. Try again.') })
      .finally(() => setBusy(false))
  }
  const frameGen = game.frameGen ?? 'game'
  const on = game.afmf === true
  // Only what stops it: an error, or AMD Software not running while it is on.
  const say = error ?? (!intel && on && !game.amdSoftware ? 'On, but AMD Software isn’t running, so no frames are added until it is' : null)
  const heading = intel ? 'XeSS frame generation' : 'Fluid Motion Frames'
  return (
    <section className="exo-graphics" aria-label={heading}>
      <div className="exo-graphics-head">
        <h3 className="exo-graphics-heading">{heading}</h3>
        {say ? <span className={`exo-graphics-state${error ? ' is-bad' : ''}`} role={error ? 'alert' : undefined}>{say}</span> : null}
      </div>
      <ul className="exo-graphics-list">
        <li className="exo-graphics-row">
          <span className="exo-graphics-name" id={labelId}>{intel ? 'Generated frames' : 'Frame generation while playing'}</span>
          {intel
            ? <Choice label="Generated frames" value={frameGen} choices={FRAME_GEN} disabled={busy} onChange={value => pick({ frameGen: value as IntelFrameGen })} />
            : <Switch checked={on} disabled={busy} labelledBy={labelId} onChange={next => pick({ afmf: next })} />}
        </li>
      </ul>
    </section>
  )
}
