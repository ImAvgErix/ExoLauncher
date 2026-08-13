import type { GameAchievementsResponse } from './host'

/** Shared so the game plate doesn’t re-fetch covers. */
export const achievementCache = new Map<string, GameAchievementsResponse>()

export function isUsefulAchievement(result: GameAchievementsResponse | null | undefined): boolean {
  if (!result?.ok) return false
  const summary = result.summary
  if (summary && (summary.total > 0 || summary.unlocked > 0)) return true
  if (summary && summary.total === 0 && summary.unlocked === 0 && result.coverage === 'complete')
    return true
  return (result.achievements?.length ?? 0) > 0
}

/** Honest “almost done”: known catalog, not empty, not perfected. */
export function almostDoneProgress(
  result: GameAchievementsResponse | null | undefined,
): { unlocked: number; total: number; percent: number } | null {
  if (!isUsefulAchievement(result) || result?.coverage === 'unsupported') return null
  const summary = result?.summary
  if (!summary || summary.total <= 0) return null
  const percent = Math.round((summary.unlocked / summary.total) * 100)
  if (percent < 60 || percent >= 100) return null
  return { unlocked: summary.unlocked, total: summary.total, percent }
}
