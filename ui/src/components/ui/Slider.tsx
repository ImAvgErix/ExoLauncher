import type { ComponentProps, CSSProperties } from 'react'
import { cn } from '../../lib/utils'

/** Exo's one slider: a native range (keyboard and screen readers come with it), drawn by chrome.css, its
    track filled up to the value. compact: the mini player's size; vertical: the equalizer's bands. */
export function Slider({ value, min = 0, max = 100, compact, vertical, className, style, ...rest }:
  Omit<ComponentProps<'input'>, 'type' | 'value' | 'min' | 'max' | 'title'> & { value: number; min?: number; max?: number; compact?: boolean; vertical?: boolean }) {
  const fill = max > min ? Math.min(100, Math.max(0, ((value - min) / (max - min)) * 100)) : 0
  return (
    <input {...rest} type="range" min={min} max={max} value={value}
      className={cn(compact && 'is-compact', vertical && 'is-vertical', className)}
      style={{ ...style, '--fill': `${fill}%` } as CSSProperties} />
  )
}
