import { useEffect, useRef } from 'react'
import { host } from '../../lib/host'
import { musicPosition, playbackAnchor, type MusicPlayback } from '../../lib/music'
import type { MusicShortcuts } from '../../lib/musicAdapters'
import { report } from '../../lib/report'

/** What a service's keyboard shortcuts do, as the room does them. */
export type ShortcutActions = {
  shuffle: () => void
  repeat: () => void
  like: () => void
  dislike: () => void
  create: (what: 'playlist' | 'folder') => void
  search: () => void
  home: () => void
  songs: () => void
  back: () => void
  panel: (name: 'queue' | 'now' | 'devices') => void
  closePanel: () => void
}

type Live = { playback: MusicPlayback; at: number } | null

const control = (action: 'toggle' | 'next' | 'previous' | 'seek', options?: { positionMs: number }) =>
  void host.musicControl(action, options).catch(error => report(error, 'MusicShortcuts.control'))
const volume = (step: number) => void host.musicVolume().then(
  result => typeof result.level === 'number' ? host.musicVolume(Math.round(result.level * 100) + step) : null).catch(error => report(error, 'MusicShortcuts.volume'))
/** By ms from where the song is now; false where it can't seek. */
function seek(live: Live, by: number) {
  if (!live?.playback.canSeek) return false
  const at = musicPosition(live.playback, playbackAnchor(live.playback, live.at), performance.now())
  if (at === null) return false
  control('seek', { positionMs: Math.max(0, at + by) })
  return true
}

/**
 * A key as a shortcut list names it: modifiers first, then the key ("Ctrl+Shift+N"). A symbol is
 * named alone ("+", not "Shift+="), as it reads on the key whatever the keyboard needs for it.
 */
function combo(event: KeyboardEvent) {
  const key = event.key.length === 1 ? event.key.toUpperCase() : event.key
  const symbol = event.key.length === 1 && !/[a-z0-9 ]/i.test(event.key)
  return `${event.ctrlKey ? 'Ctrl+' : ''}${event.altKey ? 'Alt+' : ''}${event.shiftKey && !symbol ? 'Shift+' : ''}${key === ' ' ? 'Space' : key}`
}

/** The Music app's: Space plays or pauses, Ctrl+Right and Ctrl+Left skip, Ctrl+F finds, Ctrl+L shows the song playing, Ctrl+[ goes back. */
function musicApp(act: ShortcutActions): Record<string, () => unknown> {
  return {
    'Space': () => control('toggle'),
    'Ctrl+ArrowRight': () => control('next'),
    'Ctrl+ArrowLeft': () => control('previous'),
    'Ctrl+F': act.search,
    'Ctrl+L': () => act.panel('now'),
    'Ctrl+[': act.back,
  }
}

/**
 * Each service's own shortcuts; an entry answering false leaves the key alone.
 * Spotify's desktop app on Windows: Space plays or pauses, Ctrl+Right and Ctrl+Left skip, Shift+Right
 * and Shift+Left seek 5 s, Ctrl+Up and Ctrl+Down change the volume, Ctrl+S shuffles, Ctrl+R repeats,
 * Alt+Shift+B likes, Ctrl+N makes a playlist, Ctrl+Shift+N a folder, Ctrl+L searches, Alt+Shift+H goes
 * home, Alt+Shift+S to your songs, Alt+Shift+Q opens the queue, Alt+Shift+J what is playing,
 * Alt+Shift+D devices, Alt+Left goes back and Escape closes the panel.
 * YouTube Music's, beside the Music app's: K plays and pauses, J and L go back and ahead 10 s, N and P
 * the next and previous song, S shuffle, R repeat, + likes, − dislikes and / searches.
 */
function bindings(keys: MusicShortcuts, act: ShortcutActions, live: Live): Record<string, () => unknown> {
  if (keys === 'music') return musicApp(act)
  if (keys === 'youtube') {
    return {
      ...musicApp(act),
      'K': () => control('toggle'), 'N': () => control('next'), 'P': () => control('previous'),
      'J': () => seek(live, -10_000), 'L': () => seek(live, 10_000),
      'S': act.shuffle, 'R': act.repeat, '+': act.like, '=': act.like, '-': act.dislike, '_': act.dislike, '/': act.search,
    }
  }
  return {
    'Space': () => control('toggle'),
    'Ctrl+ArrowRight': () => control('next'),
    'Ctrl+ArrowLeft': () => control('previous'),
    'Shift+ArrowRight': () => seek(live, 5000),
    'Shift+ArrowLeft': () => seek(live, -5000),
    'Ctrl+ArrowUp': () => volume(10),
    'Ctrl+ArrowDown': () => volume(-10),
    'Ctrl+S': act.shuffle,
    'Ctrl+R': act.repeat,
    'Ctrl+N': () => act.create('playlist'),
    'Ctrl+Shift+N': () => act.create('folder'),
    'Ctrl+L': act.search,
    'Alt+Shift+B': act.like,
    'Alt+Shift+H': act.home,
    'Alt+Shift+S': act.songs,
    'Alt+Shift+Q': () => act.panel('queue'),
    'Alt+Shift+J': () => act.panel('now'),
    'Alt+Shift+D': () => act.panel('devices'),
    'Alt+ArrowLeft': act.back,
    'Escape': act.closePanel,
  }
}

/**
 * The service's own keyboard shortcuts (adapter.shortcuts), while the room is on screen with nothing
 * over it and the keys are not going into a field, a menu or a dialog.
 */
export function useMusicShortcuts(active: boolean, keys: MusicShortcuts, live: Live, actions: ShortcutActions) {
  const latest = useRef({ keys, live, actions })
  useEffect(() => { latest.current = { keys, live, actions } })
  useEffect(() => {
    if (!active) return
    const onKey = (event: KeyboardEvent) => {
      if (event.defaultPrevented || event.metaKey) return
      const target = event.target instanceof Element ? event.target : null
      if (target?.closest('input, textarea, select, [contenteditable="true"], [role="menu"], [popover]:popover-open, dialog, [aria-modal="true"]')) return
      const key = combo(event)
      // Space on a focused button presses it, as everywhere else.
      if (key === 'Space' && target?.closest('button, a')) return
      if (event.repeat && !key.startsWith('Ctrl+Arrow')) return
      const { keys: map, live: now, actions: act } = latest.current
      const run = bindings(map, act, now)[key]
      if (run && run() !== false) event.preventDefault()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [active])
}
