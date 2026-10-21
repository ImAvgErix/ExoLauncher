import { useEffect, useMemo, type Dispatch, type SetStateAction } from 'react'
import { host, onHostEvent, type Game } from '../../lib/host'
import { beginGameRead, setExactRunState } from '../../lib/library'
import { onPageActivityChanged, pageIsActive } from '../../lib/pageActivity'
import { report } from '../../lib/report'

/** Safety net only: the host pushes `game.runState` on every binding change. */
const POLL_MS = 30_000

function anyRunning(games: Game[]): boolean {
  return games.some(
    (game) => game.isRunning || game.canStop || game.variants?.some((variant) => variant.isRunning || variant.canStop),
  )
}

function exactSelectedId(games: Game[], selectedId: string | null, variantId: string | null): string | null {
  const card = games.find((game) => game.id === selectedId)
  const hasVariant = !!variantId && !!card?.variants?.some((variant) => variant.id === variantId)
  return hasVariant ? variantId : selectedId
}

/**
 * Keep Stop current. The host pushes `game.runState` whenever an exact id
 * gains or loses its verified process (Exo launches, Stop, and external
 * sessions it notices). The selected title is also re-scanned on selection,
 * on return to the window, and every 30 s while a game runs or a launch is
 * pending, because an external game's exit is noticed only by the next host
 * check.
 */
export function useRunStatePoll(
  games: Game[],
  selectedId: string | null,
  selectedVariantId: string | null,
  busy: boolean,
  setGames: Dispatch<SetStateAction<Game[]>>,
): void {
  const armed = useMemo(() => busy || anyRunning(games), [busy, games])
  const exactId = exactSelectedId(games, selectedId, selectedVariantId)

  // A push is newer than any game.get still on the wire for that id; counting
  // it as an applied read drops those responses.
  useEffect(() => onHostEvent('game.runState', ({ id, isRunning, canStop }) => {
    beginGameRead(id)()
    setGames((items) => setExactRunState(items, id, isRunning, canStop))
  }), [setGames])

  useEffect(() => {
    if (!exactId) return
    let active = true
    let pending = false
    let timer: number | null = null
    const refresh = () => {
      if (!active || pending || !pageIsActive()) return
      pending = true
      const current = beginGameRead(exactId)
      void host.getGame(exactId).then((result) => {
        if (!active || !result.ok || !result.game || !current()) return
        const next = result.game
        setGames((items) => {
          let updated = setExactRunState(items, next.id, !!next.isRunning, !!next.canStop)
          for (const variant of next.variants ?? []) {
            updated = setExactRunState(updated, variant.id, !!variant.isRunning, !!variant.canStop)
          }
          return updated
        })
      }).catch((e: unknown) => report(e, 'useRunStatePoll.getGame', { level: 'warn' })).finally(() => { pending = false })
    }
    const stop = () => {
      if (timer !== null) window.clearInterval(timer)
      timer = null
    }
    const start = () => {
      if (!active || timer !== null || !armed || !pageIsActive()) return
      timer = window.setInterval(refresh, POLL_MS)
    }
    const onActivity = () => {
      if (!pageIsActive()) {
        stop()
        return
      }
      refresh()
      start()
    }
    refresh()
    start()
    const offActivity = onPageActivityChanged(onActivity)
    return () => {
      active = false
      stop()
      offActivity()
    }
  }, [armed, exactId, setGames])
}
