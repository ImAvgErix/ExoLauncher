import { useCapture } from '../../lib/capture'
import { CaptureKeyRows } from '../captures/CaptureSettings'

/** First run's captures: the keys for a replay, a recording and a snip, the same rows as Captures › Capture settings. */
export function CaptureStep() {
  const { state, settings } = useCapture()
  return (
    <div className="exo-set-list">
      <CaptureKeyRows state={state} settings={settings} />
    </div>
  )
}
