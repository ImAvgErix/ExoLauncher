import { useId } from 'react'
import { ChevronDown } from '../../brand/icons'
import { Menu } from '../ui/Menu'

/**
 * A setting with a set of choices: always a dropdown, however few or many
 * (on/off is a switch, an amount is a slider). A value Exo has no name for
 * shows as "Set elsewhere" and is never offered.
 */
export function Choice({ label, value, choices, disabled = false, onChange }: {
  label: string
  value: string
  choices: readonly { value: string; label: string }[]
  disabled?: boolean
  onChange: (value: string) => void
}) {
  const anchor = `--exo-choice-${useId().replace(/[^a-zA-Z0-9]/g, '')}` as const
  const current = choices.find(choice => choice.value === value)?.label ?? 'Set elsewhere'
  return (
    <Menu
      label={label}
      anchor={anchor}
      offset={4}
      triggerClassName="exo-shelf-sort-trigger exo-graphics-menu"
      trigger={<>{current}<ChevronDown size={12} /></>}
      items={choices.map(choice => ({ id: choice.value, label: choice.label }))}
      checked={value}
      disabled={disabled}
      onAction={id => { if (!disabled && id !== value) onChange(id) }}
    />
  )
}
