import { useState } from 'react'
import { Switch } from '../Switch'
import { Row } from '../settings/parts'
import { host, type GpuProfile, type LucidState } from '../../lib/host'
import { report } from '../../lib/report'

/**
 * First run's tuning: Exo's driver profile for this card and screen (NVIDIA, Radeon or Intel: lowest latency, the right sync), on by
 * default and applied when setup finishes, when there is one to apply; PC › Graphics can undo it. Then Lucid, the
 * optional headphone EQ app: Start with Exo (off until the user turns it on) and its setup when this PC has it, else
 * its download page.
 */
export function PcStep({ profile, tune, onTune, lucid, onLucid }: {
  profile: GpuProfile | null
  tune: boolean
  onTune: (on: boolean) => void
  lucid: LucidState | null
  onLucid: (next: LucidState) => void
}) {
  const screen = profile ? (profile.screen.name ? `${profile.screen.name} at ${profile.screen.hz} Hz` : `${profile.screen.hz} Hz`) : ''
  return (
    <div className="exo-set-list">
      {profile ? (
        <Row label={`Exo's profile for ${profile.gpu ?? 'your graphics card'}`} labelId="ob-nvidia"
          detail={`Lowest input latency and ${syncName(profile)} for ${screen}. Undo any time in PC › Graphics.`}>
          <Switch checked={tune} labelledBy="ob-nvidia" onChange={onTune} />
        </Row>
      ) : null}
      {lucid ? <LucidRows lucid={lucid} onLucid={onLucid} /> : null}
    </div>
  )
}

// The sync the profile sets, in its maker's words.
function syncName(profile: GpuProfile): string {
  if (profile.vendor === 'nvidia') return profile.gsync ? 'G-SYNC' : 'fixed refresh'
  if (!profile.screen.vrr) return 'fixed refresh'
  return profile.vendor === 'amd' ? 'FreeSync' : 'Adaptive Sync'
}

function LucidRows({ lucid, onLucid }: { lucid: LucidState; onLucid: (next: LucidState) => void }) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  function run(call: Promise<LucidState>, failed: string) {
    setBusy(true)
    setError(null)
    call.then(next => { onLucid(next); setError(next.message ?? null) }, (cause: unknown) => { report(cause, 'PcStep.lucid'); setError(failed) })
      .finally(() => setBusy(false))
  }
  if (!lucid.found) {
    return (
      <Row label="Lucid" detail="Optional headphone EQ that switches with your device and your game" error={error}>
        <button type="button" className="exo-set-btn" disabled={busy} onClick={() => run(host.lucid('get'), "The browser didn't open.")}>Get Lucid</button>
      </Row>
    )
  }
  const setUp = lucid.settings?.headphone.chosen === true
  return (
    <>
      <Row label="Start Lucid with Exo" labelId="ob-lucid" detail="Lucid, your headphone EQ, out of sight" error={error}>
        <Switch checked={lucid.startWithExo} disabled={busy} labelledBy="ob-lucid"
          onChange={on => run(host.lucid('set', { on }), "Lucid couldn't be changed.")} />
      </Row>
      {setUp ? null : (
        <Row label="Set up Lucid" detail="Its audio engine and your headphones, in Lucid's own window">
          <button type="button" className="exo-set-btn" disabled={busy} onClick={() => run(host.lucid('open'), "Lucid didn't open.")}>Set up</button>
        </Row>
      )}
    </>
  )
}
