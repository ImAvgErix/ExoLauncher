import { useCapture } from '../../lib/capture'
import { CaptureKeyRows } from '../settings/CaptureSection'

/** First run's captures: the keys for a replay, a recording and a snip, the same rows as Settings > Capture. */
export function CaptureStep() {
  const { state, settings } = useCapture()
  return (
    <div className="exo-set-list">
      <CaptureKeyRows state={state} settings={settings} />
    </div>
  )
}
