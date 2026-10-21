import { useCallback, useEffect, useRef, useState } from 'react'
import { host, onHostEvent, type ShaderStatus } from '../../lib/host'
import { report } from '../../lib/report'
import { GameLine, type LineWord } from './GameLine'

export type ShaderPrecompileProps = {
  gameId: string
  /** Opens System > Shaders (Exo Shaders), where the game is compiled. */
  onOpenShaders: (gameId: string) => void
}

const SHADERS_RELEASES = 'https://github.com/ImAvgErix/exo-shaders/releases/latest'

/**
 * The game page's Shaders: one row from the Exo Shaders engine that opens System > Shaders, where
 * the compile runs with its queue and time. Compiled when it compiled the game for this driver, Not
 * supported when it can't. Without Exo Shaders, the row opens its releases page.
 */
export function ShaderPrecompile({ gameId, onOpenShaders }: ShaderPrecompileProps) {
  const status = useShaderStatus(gameId)
  const noApp = status?.state === 'noApp'
  return (
    <GameLine
      name="Shaders"
      word={wordFor(status)}
      state={status?.state ?? 'loading'}
      external={noApp}
      onOpen={() => { if (noApp) void host.openUrl(SHADERS_RELEASES); else onOpenShaders(gameId) }}
    />
  )
}

function wordFor(status: ShaderStatus | null): LineWord | null {
  if (!status) return { text: 'Checking…' }
  switch (status.state) {
    case 'warmed': return { text: 'Compiled', good: true }
    case 'stale': return { text: status.reason ?? 'Compile again' }
    case 'ready': case 'record': return { text: 'Not compiled' }
    case 'unsupported': return { text: 'Not supported' }
    case 'noApp': return { text: 'Get Exo Shaders' }
    case 'error': return { text: 'Could not check', bad: true }
    default: return null
  }
}

/** The game's line, read again whenever the app changes its list or a game's record. */
function useShaderStatus(gameId: string) {
  const [status, setStatus] = useState<ShaderStatus | null>(null)
  const current = useRef(gameId)
  current.current = gameId

  const load = useCallback(async () => {
    try {
      const next = await host.shadersStatus(gameId)
      if (current.current === gameId) setStatus(next)
    } catch (e) {
      report(e, 'ShaderPrecompile.status')
      if (current.current === gameId) setStatus({ ok: false, state: 'error' })
    }
  }, [gameId])

  useEffect(() => {
    setStatus(null)
    void load()
    return onHostEvent('shaders.changed', () => { void load() })
  }, [load])

  return status
}
