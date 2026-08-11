import { useState } from 'react'
import type { LauncherSettings } from '../lib/host'
import './TrophyNotificationSettings.css'

type TrophyNotificationSettingsProps = {
  settings: LauncherSettings | null
  previewBusy: boolean
  onSettings: (next: LauncherSettings) => void
  onSave: (patch: Partial<LauncherSettings>) => Promise<void>
  onPreview: () => Promise<void>
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
  previewBusy,
  onSettings,
  onSave,
  onPreview,
}: TrophyNotificationSettingsProps) {
  const enabled = settings?.trophyNotificationsEnabled ?? true
  const selected = currentAnchor(settings)
  const [previewRun, setPreviewRun] = useState(0)

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

  return (
    <section className="exo-trophy-settings" aria-labelledby="trophies-heading">
      <div className="exo-trophy-heading-row">
        <h3 id="trophies-heading">Achievement notifications</h3>
        <button
          type="button"
          role="switch"
          aria-checked={enabled}
          aria-label="Achievement notifications"
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
          {anchors.map((anchor) => (
            <button
              key={anchor.id}
              type="button"
              role="radio"
              aria-checked={selected.id === anchor.id}
              aria-label={anchor.label}
              className={selected.id === anchor.id ? 'is-selected' : undefined}
              onClick={() => selectAnchor(anchor)}
            >
              <span aria-hidden="true" />
            </button>
          ))}
        </div>

        <div className="exo-trophy-preview-stage" aria-label={`Notification preview: ${selected.label}`}>
          <div key={`${selected.id}-${previewRun}`} className={`exo-trophy-preview-card is-${selected.id}`} aria-hidden="true">
            <span className="exo-trophy-preview-rail" />
            <span className="exo-trophy-preview-mark">
              <span className="exo-trophy-preview-art"><span /></span>
            </span>
            <span className="exo-trophy-preview-copy">
              <i>EXO // UNLOCKED</i>
              <b>First light</b>
              <em>Exo Launcher</em>
            </span>
            <span className="exo-trophy-preview-tier">GOLD</span>
          </div>
        </div>

        <button type="button" className="exo-trophy-preview" disabled={previewBusy} onClick={() => {
          setPreviewRun((run) => run + 1)
          void onPreview()
        }}>
          {previewBusy ? 'Showing…' : 'Preview'}
        </button>
      </fieldset>
    </section>
  )
}
