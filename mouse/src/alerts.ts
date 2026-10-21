import { useEffect, useRef } from 'react'
import type { MouseStatus } from '@openmouse/protocol/drivers/mouse-types'
import { tell } from './exo-hid'

/** The low-battery line and how often it may repeat (OpenMouse Bridge's defaults: 20 %, once in 6 hours). */
const LOW_BATTERY = 20
const BATTERY_REPEAT_MS = 6 * 60 * 60 * 1000
/** A weak signal has to last three reads in a row, and is mentioned at most every 30 minutes. */
const WEAK_READS = 3
const SIGNAL_REPEAT_MS = 30 * 60 * 1000

const charging = (s: MouseStatus) => s.batteryState.startsWith('Charging')

/**
 * Signal as 0-1: drivers report 1-4 bars (Finalmouse) or a percentage. When a
 * driver gives none but the link line carries dBm (a Starlight X reports link
 * state 0 while connected, which the driver reads as no signal), the dBm is
 * banded the way Finalmouse's own scale does: under 40, 50, 65 dB of loss.
 */
export function signalLevel(s: MouseStatus): number | null {
  // A driver that says its signal figure means nothing on this mouse.
  if (s.ui?.hideSignalCard) return null
  const v = s.signalStrength
  if (v != null && v >= 0) return v <= 4 ? v / 4 : Math.min(1, v / 100)
  const dbm = /(-\d+)\s*dBm/.exec(s.connectionDetail ?? '')
  if (!dbm) return null
  const loss = Math.abs(Number(dbm[1]))
  return loss < 40 ? 1 : loss < 50 ? 0.75 : loss < 65 ? 0.5 : 0.25
}

export function signalWord(level: number): string {
  return level >= 0.75 ? 'Excellent' : level >= 0.5 ? 'Good' : level > 0.25 ? 'Fair' : 'Weak'
}

function lastTold(key: string): number {
  try { return Number(localStorage.getItem(key) ?? 0) } catch { return 0 }
}

function told(key: string) {
  try { localStorage.setItem(key, String(Date.now())) } catch { /* the repeat window resets with the frame */ }
}

/**
 * Keeps Exo told about the mouse (its battery, for the title bar) and says
 * once when the battery runs low or the signal stays weak, room open or not.
 */
export function useMouseAlerts(status: MouseStatus | null) {
  const weakReads = useRef(0)
  useEffect(() => () => tell({ state: null }), [])
  useEffect(() => {
    if (!status) { tell({ state: null }); return }
    const battery = status.batteryPercent
    const low = battery != null && battery <= LOW_BATTERY && !charging(status)
    tell({ state: { name: status.name, battery, charging: charging(status), low } })
    if (low && Date.now() - lastTold('exo-mouse.alert.battery') > BATTERY_REPEAT_MS) {
      told('exo-mouse.alert.battery')
      tell({ notice: `Your ${status.name} is at ${battery}%. Charge it soon.` })
    }
    const level = signalLevel(status)
    weakReads.current = level != null && level <= 0.25 ? weakReads.current + 1 : 0
    if (weakReads.current >= WEAK_READS && Date.now() - lastTold('exo-mouse.alert.signal') > SIGNAL_REPEAT_MS) {
      told('exo-mouse.alert.signal')
      tell({ notice: `Your ${status.name}'s signal is weak. Move the receiver closer or away from USB 3 ports.` })
    }
  }, [status])
}
