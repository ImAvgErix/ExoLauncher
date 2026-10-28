import { useCallback, useEffect, useRef, useState } from 'react'
import { host, onHostEvent, type GameSettingsState } from '../../lib/host'
import { report } from '../../lib/report'
import { GameLine, type LineWord } from './GameLine'

/**
 * The game page's Optimize: one line, like Shaders, for a game Exo has best settings for (Modern Warfare 4 first). A
 * press writes Exo's values for this PC into the game's own settings file (game.settings), and the line says Applied
 * in green once every one is in place. Nothing changes while the game runs or before it has written its file, and the
 * line says which.
 */
export function OptimizeLine({ gameId }: { gameId: string }) {
  const [state, setState] = useState<GameSettingsState | null>(null)
  const [working, setWorking] = useState(false)
  const [failed, setFailed] = useState(false)
  const current = useRef(gameId)
  current.current = gameId

  const load = useCallback(() => host.gameSettings(gameId)
    .then(next => { if (current.current === gameId) setState(next) })
    .catch(e => report(e, 'OptimizeLine.load')), [gameId])
  useEffect(() => {
    setState(null)
    setFailed(false)
    void load()
  }, [load])
  // Starting or closing the game opens or closes the line.
  useEffect(() => onHostEvent('game.runState', event => { if (event.id === gameId) void load() }), [gameId, load])

  if (state && (!state.ok || !state.supported)) return null
  const applied = isApplied(state)
  const optimize = () => {
    setWorking(true)
    setFailed(false)
    host.gameSettings(gameId, { all: true, on: true })
      .then(answer => {
        if (current.current !== gameId) return
        if (answer.ok) setState(answer)
        else setFailed(true)
      })
      .catch(e => { report(e, 'OptimizeLine.apply'); setFailed(true) })
      .finally(() => setWorking(false))
  }
  return (
    <GameLine name="Optimize" word={wordFor(state, working, failed)} state={applied ? 'applied' : state?.running ? 'running' : 'ready'} action
      disabled={!state || working || applied || state.running === true || !state.found} onOpen={optimize} />
  )
}

const isApplied = (state: GameSettingsState | null) => !!state?.groups?.length && state.groups.every(group => group.on)

function wordFor(state: GameSettingsState | null, working: boolean, failed: boolean): LineWord | null {
  if (!state) return null
  if (working) return { text: 'Optimizing', working: true }
  if (failed) return { text: 'Could not optimize', bad: true }
  if (state.running) return { text: `Close ${state.title ?? 'the game'} first` }
  if (!state.found) return { text: `Play ${state.title ?? 'it'} once first` }
  return isApplied(state) ? { text: 'Applied', good: true } : { text: 'Not optimized' }
}
