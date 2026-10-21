import { useState } from 'react'
import { host, type ReplacedApp } from '../../lib/host'
import { report } from '../../lib/report'
import { Row, State } from '../settings/parts'

function size(bytes: number): string {
  if (bytes >= 1e9) return `${(bytes / 1e9).toFixed(1)} GB`
  if (bytes >= 1e6) return `${Math.round(bytes / 1e6)} MB`
  return bytes > 0 ? `${Math.max(1, Math.round(bytes / 1e3))} KB` : ''
}

/**
 * First run's clean up: the apps Exo does the job of, each removed on request with everything it left (its folders,
 * registry keys and shortcuts), as Revo Uninstaller does it. Nothing goes without a press; what removed apps left in
 * this user's folders was already cleared when setup opened.
 */
export function CleanupStep({ apps, onApps }: { apps: ReplacedApp[]; onApps: (next: ReplacedApp[]) => void }) {
  return <div className="exo-set-list"><ReplacedAppRows apps={apps} onApps={onApps} /></div>
}

/** One row per replaced app on this PC, here and in Settings > Clean up. */
export function ReplacedAppRows({ apps, onApps }: { apps: ReplacedApp[]; onApps: (next: ReplacedApp[]) => void }) {
  const [busy, setBusy] = useState<string | null>(null)
  const [errors, setErrors] = useState<Record<string, string | undefined>>({})
  // The apps there when the step opened stay listed, a removed one marked Removed.
  const [listed] = useState(() => new Set(apps.filter(app => app.installed || app.leftovers > 0).map(app => app.id)))
  const shown = apps.filter(app => listed.has(app.id))

  async function remove(app: ReplacedApp) {
    if (busy) return
    setBusy(app.id)
    setErrors(current => ({ ...current, [app.id]: undefined }))
    try {
      const result = await host.replacedRemove(app.id)
      onApps(result.apps)
      if (!result.ok) setErrors(current => ({ ...current, [app.id]: result.message ?? `${app.name} wasn't removed.` }))
    } catch (error) {
      report(error, 'CleanupStep.remove')
      setErrors(current => ({ ...current, [app.id]: `${app.name} wasn't removed.` }))
    } finally {
      setBusy(null)
    }
  }

  if (shown.length === 0) return <Row label="Nothing to clean up" detail="None of the apps Exo replaces are on this PC." />
  return (
    <>
      {shown.map(app => {
        const gone = !app.installed && app.leftovers === 0
        const amount = size(app.bytes)
        return (
          <Row key={app.id} label={app.name} detail={app.installed || gone ? app.detail : `Left behind: ${app.leftovers} items${amount ? `, ${amount}` : ''}`} error={errors[app.id]}>
            {gone ? <State on>Removed</State> : (
              <>
                {app.installed && amount ? <State>{amount}</State> : null}
                <button type="button" className="exo-set-btn" disabled={busy !== null} onClick={() => void remove(app)}>
                  {busy === app.id ? 'Removing…' : app.installed ? 'Remove' : 'Clear'}
                </button>
              </>
            )}
          </Row>
        )
      })}
    </>
  )
}
