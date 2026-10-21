import { memo, useEffect, useState } from 'react'
import { host, type ReplacedApp } from '../../lib/host'
import { report } from '../../lib/report'
import { ReplacedAppRows } from '../onboarding/CleanupStep'
import { Row, Section } from './parts'

/**
 * Settings > Clean up: the apps Exo does the job of, removed on request with everything they leave, as in first-run
 * setup. Opening it clears what removed ones left in this user's folders.
 */
export const CleanupSection = memo(function CleanupSection() {
  const [apps, setApps] = useState<ReplacedApp[] | null>(null)
  useEffect(() => {
    let live = true
    host.replacedScan(true).then(result => { if (live) setApps(result.apps) }, (error: unknown) => {
      report(error, 'CleanupSection.scan')
      if (live) setApps([])
    })
    return () => { live = false }
  }, [])
  return (
    <Section id="cleanup" title="Clean up">
      {apps === null ? <Row label="Checking this PC…" /> : <ReplacedAppRows apps={apps} onApps={setApps} />}
    </Section>
  )
})
