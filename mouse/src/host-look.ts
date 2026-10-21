import { call, onHostEvent } from './exo-hid'

/**
 * Exo's text size and motion setting, followed live.
 *
 * Text size (Settings > General > Text size) is a factor on --exo-scale, which
 * every font size in mouse.css multiplies. Motion is Exo's `data-exo-motion`
 * ('reduce' when Exo's Reduce motion or Windows' is on), set on this frame's
 * root the same way, so mouse.css stops the same moves Exo stops. Standalone,
 * or with a host that does not answer, type stays at 1 and motion follows the
 * system's reduced-motion setting.
 */
const scale = (value: unknown) => {
  const n = Number(value)
  if (Number.isFinite(n) && n > 0.5 && n < 2) document.documentElement.style.setProperty('--exo-scale', String(n))
}

const motion = (value: unknown) => {
  if (value === 'reduce' || value === 'full') document.documentElement.dataset.exoMotion = value
}

export function followHostLook() {
  if (window.parent === window) return
  call('exo.textScale').then(reply => scale(reply.data), () => { })
  call('exo.motion').then(reply => motion(reply.data), () => { })
  onHostEvent('exo.textScale', data => scale((data as { scale?: unknown } | null)?.scale))
  onHostEvent('exo.motion', data => motion((data as { motion?: unknown } | null)?.motion))
}
