import type { GraphicsRow } from '../../lib/host'
import { Choice } from '../settings/Choice'

/** One driver setting's dropdown. "Set elsewhere" (a value another tool wrote that Exo has no name for) is shown, never offered. */
export function GraphicsControl({ row, busy, onPick }: {
  row: GraphicsRow
  busy: boolean
  onPick: (choice: string) => void
}) {
  return (
    <Choice
      label={row.label}
      value={row.choice}
      choices={row.choices.filter(choice => choice.id !== 'custom').map(choice => ({ value: choice.id, label: choice.label }))}
      disabled={busy}
      onChange={onPick}
    />
  )
}
