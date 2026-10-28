import { useState } from 'react'
import { host, type LeftoverSummary, type ReplacedApp } from '../../lib/host'
import { report } from '../../lib/report'
import { Row, State } from '../settings/parts'
import { Spinner } from '../ui/Spinner'

function size(bytes: number): string {
  if (bytes >= 1e9) return `${(bytes / 1e9).toFixed(1)} GB`
  if (bytes >= 1e6) return `${Math.round(bytes / 1e6)} MB`
  return bytes > 0 ? `${Math.max(1, Math.round(bytes / 1e3))} KB` : ''
}

/** "3 folders (1.2 GB), 9 registry entries and 2 shortcuts": what an app left, or what was cleared of it. */
function said(left: LeftoverSummary): string {
  const count = (n: number, one: string, many: string) => `${n} ${n === 1 ? one : many}`
  const parts: string[] = []
  if (left.folders > 0) parts.push(count(left.folders, 'folder', 'folders'))
  if (left.files > 0) parts.push(count(left.files, 'file', 'files'))
  const amount = size(left.bytes)
  if (amount && parts.length > 0) parts[parts.length - 1] += ` (${amount})`
  if (left.registry > 0) parts.push(count(left.registry, 'registry entry', 'registry entries'))
  if (left.shortcuts > 0) parts.push(count(left.shortcuts, 'shortcut', 'shortcuts'))
  return parts.length < 2 ? parts.join('') : `${parts.slice(0, -1).join(', ')} and ${parts[parts.length - 1]}`
}

/**
 * First run's clean up: the apps Exo does the job of, each removed on request as Revo Uninstaller does it: its own
 * uninstaller, then a deep clean of what it leaves (folders, registry entries, shortcuts, and what Windows keeps about it).
 * Nothing goes without a press; what removed apps left in this user's folders was already cleared when setup opened, and
 * each says what went.
 */
export function CleanupStep({ apps, onApps }: { apps: ReplacedApp[]; onApps: (next: ReplacedApp[]) => void }) {
  return <div className="exo-set-list"><ReplacedAppRows apps={apps} onApps={onApps} /></div>
}

/** One row per replaced app on this PC, here and in Settings > Clean up. */
export function ReplacedAppRows({ apps, onApps }: { apps: ReplacedApp[]; onApps: (next: ReplacedApp[]) => void }) {
  const [busy, setBusy] = useState<string | null>(null)
  const [errors, setErrors] = useState<Record<string, string | undefined>>({})
  // The apps there when the step opened (or cleared as it opened) stay listed, a removed one marked Removed.
  const [listed] = useState(() => new Set(apps.filter(app => app.installed || app.leftovers > 0 || app.cleared).map(app => app.id)))
  const shown = apps.filter(app => listed.has(app.id))

  async function remove(app: ReplacedApp) {
    if (busy) return
    setBusy(app.id)
    setErrors(current => ({ ...current, [app.id]: undefined }))
    try {
      const result = await host.replacedRemove(app.id)
      onApps(result.apps)
      if (!result.ok && !result.declined) setErrors(current => ({ ...current, [app.id]: result.message ?? `${app.name} wasn't removed.` }))
    } catch (error) {
      report(error, 'CleanupStep.remove')
      setErrors(current => ({ ...current, [app.id]: `${app.name} wasn't removed.` }))
    } finally {
      setBusy(null)
    }
  }

  if (shown.length === 0) return <Row label="Nothing to clean up" />
  return (
    <>
      {shown.map(app => {
        const gone = !app.installed && app.leftovers === 0
        const amount = size(app.bytes)
        const detail = app.installed ? undefined
          : !gone ? `Left behind: ${said(app.found)}`
          : app.cleared ? `Cleared ${said(app.cleared)}` : undefined
        return (
          <Row key={app.id} label={app.name} detail={detail} error={errors[app.id]}>
            {gone ? <State on>Removed</State> : (
              <>
                {app.installed && amount ? <State>{amount}</State> : null}
                <button type="button" className="exo-set-btn" disabled={busy !== null} onClick={() => void remove(app)}>
                  {busy === app.id ? <Spinner label={`Removing ${app.name}`} /> : null}
                  {app.installed ? 'Remove' : 'Clear'}
                </button>
              </>
            )}
          </Row>
        )
      })}
    </>
  )
}
