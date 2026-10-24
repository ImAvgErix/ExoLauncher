import { useId, useRef, useState, type ComponentType, type KeyboardEvent } from 'react'
import { Capture, Gallery, History, type IconProps, Record, Scissors } from '../../brand/icons'
import { host } from '../../lib/host'
import { clock, keysLabel, openCaptures, useCapture } from '../../lib/capture'
import { report } from '../../lib/report'
import { usePageClock } from '../../lib/pageActivity'
import { reportFailure } from './useActionStatus'

type Action = 'saveReplay' | 'record' | 'snip' | 'captures'

const sinceStart = () => performance.now()

/** While recording: the time so far from the add-on's last report; it stops ticking while Exo is minimized. */
function RecordingClock({ ms, at }: { ms: number; at: number }) {
  const now = usePageClock(500, sinceStart)
  return <>{clock(ms + Math.max(0, now - at))}</>
}

/**
 * The title bar's Capture button, beside the mini player: a quiet green dot while
 * instant replay holds the last seconds; a short menu of Save replay, Record, Snip
 * and Open captures, each with its keys. While recording it is a red timer pill
 * that stops the recording. Nothing shows without Exo Capture.
 */
export function CaptureButton() {
  const { state, settings, at } = useCapture()
  const id = useId()
  const menuRef = useRef<HTMLDivElement>(null)
  const triggerRef = useRef<HTMLButtonElement>(null)
  const [open, setOpen] = useState(false)
  if (!state?.present) return null

  async function run(action: Action) {
    const menu = menuRef.current
    if (action === 'snip' && menu) {
      // The snip freezes the screen as it is: the menu goes at once, without its fade, and is off the screen
      // (three frames: drawn, handed to Windows, composed) before the snip starts.
      menu.style.transition = 'none'
      menu.hidePopover()
      for (let i = 0; i < 3; i++) await new Promise(requestAnimationFrame)
      menu.style.transition = ''
    } else menu?.hidePopover()
    if (action === 'captures') { openCaptures(); return }
    try {
      const result = await host.captureAct(action)
      if (!result.ok) {
        report(new Error(result.message ?? 'Capture failed.'), 'capture.act', { level: 'warn' })
        reportFailure(result.message ?? 'Capture failed.')
      }
    } catch (error) {
      report(error, 'capture.act', { level: 'warn' })
      reportFailure(error instanceof Error ? error.message : 'Capture failed.')
    }
  }

  if (state.recording) {
    return (
      <button type="button" className="exo-capture-rec" onClick={() => void run('record')} aria-label="Stop recording">
        <span className="exo-capture-rec-dot" aria-hidden />
        <RecordingClock ms={state.recordingMs} at={at} />
      </button>
    )
  }

  // Save replay needs instant replay holding the last seconds.
  const items: { action: Action; label: string; keys?: string; icon: ComponentType<IconProps>; disabled?: boolean }[] = [
    { action: 'saveReplay', label: 'Save replay', keys: settings?.saveReplayKeys, icon: History, disabled: !state.buffering },
    { action: 'record', label: 'Record', keys: settings?.recordKeys, icon: Record },
    { action: 'snip', label: 'Snip', keys: settings?.snipKeys, icon: Scissors },
    { action: 'captures', label: 'Open captures', icon: Gallery },
  ]
  const entries = () => Array.from(menuRef.current?.querySelectorAll<HTMLElement>('[role="menuitem"]') ?? [])
  function onKeyDown(event: KeyboardEvent<HTMLDivElement>) {
    const list = entries()
    const at = list.indexOf(document.activeElement as HTMLElement)
    const to = { ArrowDown: at + 1, ArrowUp: at - 1, Home: 0, End: list.length - 1 }[event.key]
    if (to !== undefined) {
      event.preventDefault()
      list[(to + list.length) % list.length]?.focus()
    } else if (event.key === 'Escape') {
      event.preventDefault()
      event.stopPropagation()
      menuRef.current?.hidePopover()
      triggerRef.current?.focus()
    }
  }
  const label = state.buffering ? 'Capture, instant replay on' : 'Capture'
  return (
    <>
      <button
        ref={triggerRef}
        type="button"
        className={`exo-capture-btn${open ? ' is-on' : ''}`}
        style={{ anchorName: '--exo-capture' }}
        aria-label={label}
        aria-haspopup="menu"
        aria-expanded={open}
        aria-controls={id}
        popoverTarget={id}
      >
        <Capture size={16} />
        {state.buffering ? <span className="exo-capture-live" aria-hidden /> : null}
      </button>
      <div
        ref={menuRef}
        id={id}
        popover="auto"
        role="menu"
        aria-label="Capture"
        className="exo-shelf-sort-menu exo-capture-menu"
        style={{ positionAnchor: '--exo-capture', marginTop: 8 }}
        onToggle={(event) => {
          setOpen(event.newState === 'open')
          if (event.newState === 'open') entries()[0]?.focus()
        }}
        onKeyDown={onKeyDown}
      >
        {items.map(item => (
          <div
            key={item.action}
            role="menuitem"
            tabIndex={-1}
            className="exo-menu-item"
            aria-disabled={item.disabled || undefined}
            onClick={() => { if (!item.disabled) void run(item.action) }}
            onKeyDown={(event) => {
              if (event.key !== 'Enter' && event.key !== ' ') return
              event.preventDefault()
              if (!item.disabled) void run(item.action)
            }}
          >
            <item.icon size={16} />
            <span className="exo-capture-menu-label">{item.label}</span>
            {item.keys ? <kbd className="exo-capture-keys">{keysLabel(item.keys)}</kbd> : null}
          </div>
        ))}
      </div>
    </>
  )
}
