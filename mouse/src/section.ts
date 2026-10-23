import type { MouseStatus } from '@openmouse/protocol/drivers/mouse-types'
import type { SupportedClient } from '@openmouse/protocol/drivers/registry'
import type { Step } from './mouse'

/**
 * What every settings section gets: the mouse's last read status, its driver
 * client (call setters only through apply/applyAll), and whether a write is
 * in flight. apply runs one setter and reads the mouse back; applyAll runs
 * several, then reads back once. Both resolve to what the mouse took, or null
 * when a step failed (the room shows the error).
 */
export type Section = {
  status: MouseStatus
  client: SupportedClient | null
  apply: (method: string, ...args: unknown[]) => Promise<MouseStatus | null>
  applyAll: (steps: Step[]) => Promise<MouseStatus | null>
  busy: boolean
}

/**
 * The room's sections, in order. Every panel belongs to one, so a mouse with
 * settings others lack still shows each of them, sorted where it reads best;
 * a panel no list names goes to Device rather than being dropped.
 */
export const GROUPS = ['Sensor', 'Buttons', 'Device', 'Profiles', 'Tests'] as const
export type Group = (typeof GROUPS)[number]

const BY_TITLE: Record<string, Group> = {
  Sensitivity: 'Sensor', 'CPI stages': 'Sensor', Sensor: 'Sensor', Performance: 'Sensor',
  'DPI acceleration': 'Sensor', 'DPI indicator': 'Sensor',
  Buttons: 'Buttons', 'Button controls': 'Buttons', 'Buttons and wheel': 'Buttons', 'Button assignments': 'Buttons',
  'Button response': 'Buttons', 'Main switches': 'Buttons', 'TMR-DS': 'Buttons', 'Magnetic switches': 'Buttons', Clicks: 'Buttons',
  'Multiclick filter': 'Buttons', 'Scroll wheel': 'Buttons', Scrolling: 'Buttons', 'Rapid fire': 'Buttons',
  Macros: 'Buttons', Haptics: 'Buttons',
  Profiles: 'Profiles',
  'Polling test': 'Tests', 'Click test': 'Tests', 'HITS speed': 'Tests',
}

export const groupOf = (title: string): Group => BY_TITLE[title] ?? 'Device'
