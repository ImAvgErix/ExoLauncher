import { useSyncExternalStore } from 'react'
import { host } from './host'

/**
 * Discord's saved messages, as the message menu needs them: which messages are bookmarked, and when each reminder is
 * due. Read once, then kept as Exo saves and removes them.
 */
type Saved = { bookmarks: Set<string>; reminders: Map<string, number> }

let saved: Saved = { bookmarks: new Set(), reminders: new Map() }
let loading: Promise<void> | null = null
const listeners = new Set<() => void>()
const changed = (next: Saved) => { saved = next; listeners.forEach(listener => listener()) }

export function loadSaved(force = false) {
  if (loading && !force) return loading
  loading = host.discordSaved('state').then(result => {
    if (!result.ok) return
    const reminders = new Map<string, number>()
    for (const r of result.reminders ?? []) if (r.save_data.due_at) reminders.set(r.save_data.message_id, Date.parse(r.save_data.due_at))
    changed({ bookmarks: new Set(result.bookmarkIds ?? []), reminders })
  }).catch(() => { })
  return loading
}

/** Which messages are bookmarked and when reminders are due; read the first time it's asked. */
export function useSaved() {
  return useSyncExternalStore(listener => {
    listeners.add(listener)
    void loadSaved()
    return () => { listeners.delete(listener) }
  }, () => saved)
}

/** Bookmark a message (no due time) or set its reminder (due, ms); null when Discord took it, else why not. */
export async function saveMessage(channelId: string, messageId: string, due?: number) {
  const result = await host.discordSaved('save', { channelId, messageId, ...(due ? { dueAt: due } : {}) }).catch(() => null)
  if (!result?.ok) return result?.message ?? 'Discord did not save that.'
  const next = { bookmarks: new Set(saved.bookmarks), reminders: new Map(saved.reminders) }
  if (due) next.reminders.set(messageId, due); else next.bookmarks.add(messageId)
  changed(next)
  return null
}

/** Remove Bookmark, Remove Reminder or Mark Complete. */
export async function unsaveMessage(channelId: string, messageId: string) {
  const result = await host.discordSaved('remove', { channelId, messageId }).catch(() => null)
  if (!result?.ok) return result?.message ?? 'Discord did not take that off.'
  const next = { bookmarks: new Set(saved.bookmarks), reminders: new Map(saved.reminders) }
  next.bookmarks.delete(messageId)
  next.reminders.delete(messageId)
  changed(next)
  return null
}

/** Discord's Remind Me times: in 30 minutes, 1 hour, 4 hours, 9:00 the next morning (today, before 9), and Monday at 9:00. */
export function reminderTimes(now = new Date()): { label: string; at: number }[] {
  const nine = (day: Date) => { const d = new Date(day); d.setHours(9, 0, 0, 0); return d }
  const clock = nine(now).toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' })
  const morning = now.getHours() >= 9 ? nine(new Date(now.getTime() + 86_400_000)) : nine(now)
  const monday = nine(now)
  monday.setDate(monday.getDate() + ((8 - monday.getDay()) % 7 || 7))
  return [
    { label: 'In 30 minutes', at: now.getTime() + 30 * 60_000 },
    { label: 'In 1 hour', at: now.getTime() + 60 * 60_000 },
    { label: 'In 4 hours', at: now.getTime() + 4 * 60 * 60_000 },
    { label: `${now.getHours() >= 9 ? 'Tomorrow' : 'Today'} at ${clock}`, at: morning.getTime() },
    { label: `Monday at ${clock}`, at: monday.getTime() },
  ]
}
