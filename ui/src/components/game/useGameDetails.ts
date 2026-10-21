import { useCallback, useEffect, useRef, useState } from 'react'
import {
  host,
  onHostEvent,
  type DlssStatus,
  type Game,
} from '../../lib/host'
import { report } from '../../lib/report'
import { loadUpscalerStatus, peekUpscalerStatus } from '../../lib/upscalerCache'

export type Achievements = { unlocked: number; total: number }

/** One exact store entry. Two cards for the same title never share a fetch. */
function selectionKeyFor(game: Pick<Game, 'id' | 'store'>): string {
  return `${game.id}\u001f${game.store}`
}

/**
 * Optional controls load after the sheet renders, keyed by the exact store
 * entry. Library details never request catalog text or block the Play action.
 */
export function useGameDetails(selected: Game) {
  const selectionKey = selectionKeyFor(selected)
  const selectionKeyRef = useRef(selectionKey)
  const upscalerGeneration = useRef(Symbol())
  selectionKeyRef.current = selectionKey
  const { id, installed } = selected

  const [achievements, setAchievements] = useState<Achievements | null>(null)
  const [dlss, setDlss] = useState<DlssStatus | null>(() => (installed ? peekUpscalerStatus(id) : null))

  // One read answers the cached achievements; the host pushes a newer achievements read.
  useEffect(() => {
    let active = true
    let pushed = false
    setAchievements(null)
    void host.gameExtras(id).then((extras) => {
      if (!active) return
      if (!pushed && extras.achievementsTotal && extras.achievementsUnlocked != null)
        setAchievements({ unlocked: extras.achievementsUnlocked, total: extras.achievementsTotal })
    }).catch((e: unknown) => report(e, 'useGameDetails.extras', { level: 'warn' }))
    const off = onHostEvent('game.achievements', (data) => {
      if (!active || data.id !== id || data.total <= 0) return
      pushed = true
      setAchievements({ unlocked: data.unlocked, total: data.total })
    })
    return () => { active = false; off() }
  }, [id, installed, selectionKey])

  const refreshDlss = useCallback(async (force = false) => {
    const requestKey = selectionKey
    const generation = Symbol()
    upscalerGeneration.current = generation
    // Keep the current rows while verified versions are read back from disk.
    await loadUpscalerStatus(id, force)
      .then((status) => {
        if (selectionKeyRef.current === requestKey && upscalerGeneration.current === generation)
          setDlss(previous => status.ok === false
            ? {...status, message:status.message || 'Versions could not be refreshed. Showing the last checked files.', items:previous?.items ?? status.items}
            : status)
      })
      .catch((e: unknown) => {
        report(e, 'useGameDetails.refreshDlss')
        if (selectionKeyRef.current === requestKey && upscalerGeneration.current === generation)
          setDlss(previous => ({...previous, ok:false, message:'Versions could not be refreshed. Showing the last checked files.', items:previous?.items ?? []}))
      })
  }, [id, selectionKey])

  useEffect(() => {
    const generation = Symbol()
    upscalerGeneration.current = generation
    if (!installed) {
      setDlss(null)
      return
    }
    let active = true
    setDlss(peekUpscalerStatus(id))
    const current = () => active && upscalerGeneration.current === generation
    // Newest versions ship with Exo, so one read answers fully.
    void loadUpscalerStatus(id).then(status => {
      if (current()) setDlss(status)
    }).catch((e: unknown) => {
      report(e, 'useGameDetails.upscalerStatus')
      if (current()) setDlss({ok:false, message:'Upscaler files could not be checked. Try refreshing.', items:[]})
    })
    return () => { active = false; upscalerGeneration.current = Symbol() }
  }, [id, installed, selectionKey])

  return { selectionKey, achievements, dlss, refreshDlss }
}
