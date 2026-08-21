import { useLayoutEffect, useRef, useState, type KeyboardEvent } from 'react'
import type { LauncherSettings } from '../lib/host'
import { TrophyBanner } from './TrophyBanner'
import {
  trophyBannerCycle,
  trophyBannerDesign,
  trophyBannerLabel,
  trophyNotificationSlot,
  type TrophyBannerTier,
} from '../lib/trophyBanner'
import './TrophyNotificationSettings.css'

type TrophyNotificationSettingsProps = {
  settings: LauncherSettings | null
  onSettings: (next: LauncherSettings) => void
  onSave: (patch: Partial<LauncherSettings>) => Promise<void>
}

const anchors = [
  { id: 'top-left', label: 'Top left', x: 0, y: 0 },
  { id: 'top-center', label: 'Top center', x: 0.5, y: 0 },
  { id: 'top-right', label: 'Top right', x: 1, y: 0 },
  { id: 'center-left', label: 'Center left', x: 0, y: 0.5 },
  { id: 'center', label: 'Center', x: 0.5, y: 0.5 },
  { id: 'center-right', label: 'Center right', x: 1, y: 0.5 },
  { id: 'bottom-left', label: 'Bottom left', x: 0, y: 1 },
  { id: 'bottom-center', label: 'Bottom center', x: 0.5, y: 1 },
  { id: 'bottom-right', label: 'Bottom right', x: 1, y: 1 },
] as const

type TrophyAnchor = (typeof anchors)[number]

function radioTargetIndex(key: string, current: number, count: number, columns: number) {
  if (count <= 0) return null
  if (key === 'Home') return 0
  if (key === 'End') return count - 1

  let delta: number
  switch (key) {
    case 'ArrowLeft':
      delta = -1
      break
    case 'ArrowRight':
      delta = 1
      break
    case 'ArrowUp':
      delta = -columns
      break
    case 'ArrowDown':
      delta = columns
      break
    default:
      return null
  }
  return (current + delta + count) % count
}

function nearestAxis(value: number | undefined, fallback: number) {
  const safeValue = Number.isFinite(value) ? Math.min(1, Math.max(0, value!)) : fallback
  return safeValue < 0.25 ? 0 : safeValue < 0.75 ? 0.5 : 1
}

function currentAnchor(settings: LauncherSettings | null): TrophyAnchor {
  const x = nearestAxis(settings?.trophyNotificationPositionX, 1)
  const y = nearestAxis(settings?.trophyNotificationPositionY, 1)
  return anchors.find((anchor) => anchor.x === x && anchor.y === y) ?? anchors[8]
}

export function TrophyNotificationSettings({
  settings,
  onSettings,
  onSave,
}: TrophyNotificationSettingsProps) {
  const enabled = settings?.trophyNotificationsEnabled ?? true
  const selected = currentAnchor(settings)
  const cycle = trophyBannerCycle()
  const [tier, setTier] = useState<TrophyBannerTier>(cycle[2] ?? 'gold')
  const [previewRun, setPreviewRun] = useState(0)
  const [stageSize, setStageSize] = useState({ w: 0, h: 0 })
  const stageRef = useRef<HTMLDivElement>(null)
  const replayTimerRef = useRef<number | null>(null)
  const anchorRefs = useRef<Array<HTMLButtonElement | null>>([])
  const tierRefs = useRef<Array<HTMLButtonElement | null>>([])
  const sample = trophyBannerDesign.preview
  const previewScale = stageSize.w > 0
    ? Math.min(1, (stageSize.w - 16) / trophyBannerDesign.width)
    : 1
  const slot = trophyNotificationSlot(
    selected.x,
    selected.y,
    stageSize.w || 1,
    stageSize.h || 1,
    trophyBannerDesign.width * previewScale,
    trophyBannerDesign.height * previewScale,
    Math.max(8, trophyBannerDesign.overlayPad * previewScale),
  )

  useLayoutEffect(() => {
    const node = stageRef.current
    if (!node) return
    const read = () => setStageSize({ w: node.clientWidth, h: node.clientHeight })
    read()
    const observer = new ResizeObserver(read)
    observer.observe(node)
    return () => {
      observer.disconnect()
      if (replayTimerRef.current !== null) window.clearTimeout(replayTimerRef.current)
    }
  }, [])

  useLayoutEffect(() => {
    if (!enabled) return
    const motion = trophyBannerDesign.motion.tiers[tier]
    const holdMs = Math.max(1800, (motion?.enterMs ?? 220) + (motion?.settleMs ?? 0) + 2500)
    if (replayTimerRef.current !== null) window.clearTimeout(replayTimerRef.current)
    replayTimerRef.current = window.setTimeout(() => {
      replayTimerRef.current = null
      setPreviewRun((run) => run + 1)
    }, holdMs)
    return () => {
      if (replayTimerRef.current !== null) window.clearTimeout(replayTimerRef.current)
    }
  }, [enabled, selected.id, tier, previewRun])

  function selectAnchor(anchor: TrophyAnchor) {
    if (!settings) return
    const patch: Partial<LauncherSettings> = {
      trophyNotificationPosition: anchor.id,
      trophyNotificationPositionX: anchor.x,
      trophyNotificationPositionY: anchor.y,
    }
    onSettings({ ...settings, ...patch })
    void onSave(patch)
  }

  function selectTier(nextTier: TrophyBannerTier) {
    if (replayTimerRef.current !== null) window.clearTimeout(replayTimerRef.current)
    setTier(nextTier)
    setPreviewRun((run) => run + 1)
  }

  function queueReplay() {
    // Layout effect already owns the hold. A second timer here restarted the
    // preview while sheen/bloom were still running.
    if (replayTimerRef.current !== null) return
  }

  function moveAnchor(event: KeyboardEvent<HTMLButtonElement>, current: number) {
    const target = radioTargetIndex(event.key, current, anchors.length, 3)
    if (target === null) return
    event.preventDefault()
    selectAnchor(anchors[target])
    anchorRefs.current[target]?.focus()
  }

  function moveTier(event: KeyboardEvent<HTMLButtonElement>, current: number) {
    const target = radioTargetIndex(event.key, current, cycle.length, 1)
    if (target === null) return
    event.preventDefault()
    selectTier(cycle[target])
    tierRefs.current[target]?.focus()
  }

  return (
    <section className="exo-trophy-settings" aria-labelledby="trophies-heading">
      <div className="exo-trophy-heading-row">
        <h3 id="trophies-heading">Show unlocks</h3>
        <button
          type="button"
          role="switch"
          aria-checked={enabled}
          aria-label="Show unlocks"
          className="exo-trophy-switch"
          onClick={() => void onSave({ trophyNotificationsEnabled: !enabled })}
        >
          <span aria-hidden="true" />
        </button>
      </div>

      <fieldset className="exo-trophy-controls" disabled={!enabled || !settings}>
        <div className="exo-trophy-placement-heading">
          <span>Placement</span>
          <output aria-live="polite">{selected.label}</output>
        </div>

        <div className="exo-trophy-anchor-grid" role="radiogroup" aria-label="Achievement notification placement">
          {anchors.map((anchor, index) => (
            <button
              key={anchor.id}
              ref={(node) => { anchorRefs.current[index] = node }}
              type="button"
              role="radio"
              aria-checked={selected.id === anchor.id}
              aria-label={anchor.label}
              tabIndex={selected.id === anchor.id ? 0 : -1}
              className={selected.id === anchor.id ? 'is-selected' : undefined}
              onClick={() => selectAnchor(anchor)}
              onKeyDown={(event) => moveAnchor(event, index)}
            >
              <span aria-hidden="true" />
            </button>
          ))}
        </div>

        <div
          className="exo-trophy-tier-row"
          role="radiogroup"
          aria-label="Preview trophy tier"
          aria-orientation="horizontal"
        >
          {cycle.map((item, index) => (
            <button
              key={item}
              ref={(node) => { tierRefs.current[index] = node }}
              type="button"
              role="radio"
              aria-checked={tier === item}
              tabIndex={tier === item ? 0 : -1}
              className={tier === item ? 'is-selected' : undefined}
              onClick={() => selectTier(item)}
              onKeyDown={(event) => moveTier(event, index)}
            >
              {trophyBannerLabel(item)}
            </button>
          ))}
        </div>

        <div
          ref={stageRef}
          className="exo-trophy-preview-stage"
          aria-label={`Notification preview: ${selected.label}`}
        >
          <div
            key={`${selected.id}-${tier}-${previewRun}`}
            className="exo-trophy-preview-slot"
            style={{
              left: slot.left,
              top: slot.top,
              transform: previewScale < 1 ? `scale(${previewScale})` : undefined,
              transformOrigin: 'top left',
            }}
          >
            <TrophyBanner
              animate
              tier={tier}
              name={sample.achievementName}
              detail={sample.detail}
              game={sample.gameTitle}
              onAnimationComplete={queueReplay}
            />
          </div>
        </div>
        <p className="exo-trophy-note">The card replays here automatically. It sits above borderless fullscreen; Exclusive fullscreen cannot be covered.</p>
      </fieldset>
    </section>
  )
}
