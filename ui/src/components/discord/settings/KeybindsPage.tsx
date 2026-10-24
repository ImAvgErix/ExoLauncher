import { useEffect, useRef, useState } from 'react'
import { Choice } from '../../settings/Choice'
import type { DiscordKeybind } from '../../../lib/discordPrefs'
import { Category, LoadError, Row, useClientSettings } from './parts'

/** The actions Exo's Discord client carries out from a keybind, with Discord's names. */
export const KEYBIND_ACTIONS = [
  { value: 'PUSH_TO_TALK', label: 'Push to talk (normal)' },
  { value: 'PUSH_TO_MUTE', label: 'Push to mute' },
  { value: 'TOGGLE_MUTE', label: 'Toggle mute' },
  { value: 'TOGGLE_DEAFEN', label: 'Toggle deafen' },
  { value: 'TOGGLE_VOICE_MODE', label: 'Toggle voice activity mode' },
  { value: 'TOGGLE_STREAMER_MODE', label: 'Toggle Streamer Mode' },
  { value: 'DISCONNECT_FROM_VOICE', label: 'Disconnect from voice' },
] as const

// Windows virtual-key codes, named as a keyboard shows them.
const NAMES: Record<number, string> = {
  4: 'Mouse 3', 5: 'Mouse 4', 6: 'Mouse 5', 8: 'Backspace', 9: 'Tab', 13: 'Enter', 16: 'Shift', 17: 'Ctrl', 18: 'Alt', 19: 'Pause',
  20: 'Caps lock', 27: 'Esc', 32: 'Space', 33: 'Page up', 34: 'Page down', 35: 'End', 36: 'Home', 37: 'Left', 38: 'Up', 39: 'Right',
  40: 'Down', 45: 'Insert', 46: 'Delete', 91: 'Win', 106: 'Numpad *', 107: 'Numpad +', 109: 'Numpad -', 110: 'Numpad .', 111: 'Numpad /',
  144: 'Num lock', 145: 'Scroll lock', 186: ';', 187: '=', 188: ',', 189: '-', 190: '.', 191: '/', 192: '`', 219: '[', 220: '\\', 221: ']', 222: "'",
}

function keyName(vk: number) {
  if ((vk >= 48 && vk <= 57) || (vk >= 65 && vk <= 90)) return String.fromCharCode(vk)
  if (vk >= 96 && vk <= 105) return `Numpad ${vk - 96}`
  if (vk >= 112 && vk <= 135) return `F${vk - 111}`
  return NAMES[vk] ?? `Key ${vk}`
}

/** "Ctrl + M": a keybind's keys, modifiers first. */
export function keybindName(keys: number[]) {
  const order = (k: number) => (k === 17 ? 0 : k === 16 ? 1 : k === 18 ? 2 : k === 91 ? 3 : 4)
  return [...keys].sort((a, b) => order(a) - order(b)).map(keyName).join(' + ')
}

// A key as Windows names it: Chromium's keyCode is the virtual-key code; mouse buttons 3-5 are VK_MBUTTON, XBUTTON1 and 2.
function vkOf(event: KeyboardEvent | MouseEvent): number | null {
  if (event instanceof KeyboardEvent) return event.keyCode > 0 && event.keyCode < 255 ? (event.keyCode === 92 ? 91 : event.keyCode) : null
  return ({ 1: 4, 3: 5, 4: 6 } as Record<number, number>)[event.button] ?? null
}

/** Discord's Record Keybind: press the keys (or a side mouse button) together; letting go keeps them. */
export function KeybindRecorder({ keys, label, onChange }: { keys: number[]; label: string; onChange: (keys: number[]) => void }) {
  const [recording, setRecording] = useState(false)
  const [held, setHeld] = useState<number[]>([])
  const changed = useRef(onChange)
  useEffect(() => { changed.current = onChange }, [onChange])
  useEffect(() => {
    if (!recording) return
    let pressed: number[] = []
    let down = new Set<number>()
    const press = (event: KeyboardEvent | MouseEvent) => {
      const vk = vkOf(event)
      if (vk === null) return
      event.preventDefault()
      event.stopPropagation()
      if (event instanceof KeyboardEvent && vk === 27 && pressed.length === 0) { setRecording(false); return }
      down.add(vk)
      if (!pressed.includes(vk) && pressed.length < 4) pressed = [...pressed, vk]
      setHeld(pressed)
    }
    const release = (event: KeyboardEvent | MouseEvent) => {
      const vk = vkOf(event)
      if (vk === null || !down.has(vk)) return
      event.preventDefault()
      down = new Set([...down].filter(k => k !== vk))
      if (down.size === 0 && pressed.length) { changed.current(pressed); setRecording(false) }
    }
    window.addEventListener('keydown', press, true)
    window.addEventListener('keyup', release, true)
    window.addEventListener('mousedown', press, true)
    window.addEventListener('mouseup', release, true)
    return () => {
      window.removeEventListener('keydown', press, true)
      window.removeEventListener('keyup', release, true)
      window.removeEventListener('mousedown', press, true)
      window.removeEventListener('mouseup', release, true)
    }
  }, [recording])
  const shown = recording ? (held.length ? keybindName(held) : 'Press keys') : keys.length ? keybindName(keys) : 'No keybind set'
  return (
    <span className={`exo-dc-settings-keybind${recording ? ' is-recording' : ''}`}>
      <span className="exo-dc-settings-keybind-keys">{shown}</span>
      <button type="button" className="exo-set-btn" aria-label={`${label}: ${recording ? 'Stop recording' : keys.length ? 'Edit keybind' : 'Record keybind'}`}
        onClick={() => { setHeld([]); setRecording(r => !r) }}>{recording ? 'Stop recording' : keys.length ? 'Edit keybind' : 'Record keybind'}</button>
    </span>
  )
}

/** Discord settings > System: Custom Keybinds, the actions held keys carry out anywhere in Windows during a call. */
export function SystemPage() {
  const { settings, save, error, reload } = useClientSettings()
  const binds = settings?.keybinds ?? []
  const set = (next: DiscordKeybind[]) => save({ keybinds: next })
  return (
    <>
      <h1>System</h1>
      <LoadError error={error} onRetry={reload} />
      <Category title="Custom keybinds" detail="Keybinds are custom keyboard shortcuts that let you perform specific actions in Discord without needing to click through menus. They work while you're in a call, even in a game.">
        {binds.map((bind, i) => (
          <Row key={i} label="Action">
            <span className="exo-dc-settings-buttons">
              <Choice label="Action" value={bind.action} choices={KEYBIND_ACTIONS}
                onChange={action => void set(binds.map((b, j) => (j === i ? { ...b, action } : b)))} />
              <KeybindRecorder label="Keybind" keys={bind.keys} onChange={keys => void set(binds.map((b, j) => (j === i ? { ...b, keys } : b)))} />
              <button type="button" className="exo-set-btn is-danger" aria-label="Delete keybind" onClick={() => void set(binds.filter((_, j) => j !== i))}>Delete</button>
            </span>
          </Row>
        ))}
        <NewKeybind disabled={!settings} onAdd={bind => void set([...binds, bind])} />
      </Category>
    </>
  )
}

// A keybind is kept once it has keys; until then it is only on screen.
function NewKeybind({ disabled, onAdd }: { disabled: boolean; onAdd: (bind: DiscordKeybind) => void }) {
  const [action, setAction] = useState<string | null>(null)
  if (action === null) {
    return (
      <Row label="Add a keybind">
        <button type="button" className="exo-set-btn" disabled={disabled} onClick={() => setAction('PUSH_TO_TALK')}>Add a keybind</button>
      </Row>
    )
  }
  return (
    <Row label="Action">
      <span className="exo-dc-settings-buttons">
        <Choice label="Action" value={action} choices={KEYBIND_ACTIONS} onChange={setAction} />
        <KeybindRecorder label="Keybind" keys={[]} onChange={keys => { onAdd({ action, keys }); setAction(null) }} />
        <button type="button" className="exo-set-btn" onClick={() => setAction(null)}>Cancel</button>
      </span>
    </Row>
  )
}
