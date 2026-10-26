import type { ComponentType } from 'react'
import { Appearance, Calendar, Chats, Cover, Devices, Download, EyeOff, Folder, FolderOpen, Gamepad, Gift, GraphicsCard, Hash, ImageUpload, Library, Live, Mic, Monitor, Mouse, MusicNotes, New, Shaders, Sliders, Smile, StoreBag, Users, Volume, type IconProps } from '../brand/icons'
import data from './whatsNew.json'
import type { WhatsNewWire } from './hostWire'

/**
 * What's New: after an update, Exo's first opening shows what changed since the version you last saw (WhatsNewSheet),
 * and Settings › About › What's new shows every release up to yours. While an update is offered, its own notes (the
 * whats-new.json beside its installer, read by the host) lead the sheet. Each release adds its items to whatsNew.json,
 * newest first, beside its CHANGELOG entry: an icon named below, a plain-words title and one short sentence on what it
 * does for you. tools/Build-Release.ps1 ships the release's entry as that whats-new.json.
 */
export type WhatsNewItem = { icon: ComponentType<IconProps>; title: string; detail: string }
export type WhatsNewRelease = { version: string; items: WhatsNewItem[] }

/** The window event Settings › About and the update prompt send to open the sheet. */
export const WHATS_NEW = 'exo-whats-new'

/** The icons a release's items may name. A name this build doesn't know (a newer release's) draws as New. */
const ICONS: Record<string, ComponentType<IconProps>> = {
  Appearance, Calendar, Chats, Cover, Devices, Download, EyeOff, Folder, FolderOpen, Gamepad, Gift, GraphicsCard, Hash, ImageUpload,
  Library, Live, Mic, Monitor, Mouse, MusicNotes, New, Shaders, Sliders, Smile, StoreBag, Users, Volume,
}

/** A release as data (whatsNew.json, or the host's validated copy of an update's) with its icons. */
export function toRelease(wire: WhatsNewWire): WhatsNewRelease {
  return { version: wire.version, items: wire.items.map(({ icon, title, detail }) => ({ icon: ICONS[icon] ?? New, title, detail })) }
}

export const RELEASES: WhatsNewRelease[] = (data as WhatsNewWire[]).map(toRelease)

/** Numeric X.Y.Z order; anything after the numbers (a -dev suffix) is ignored. */
function compareVersions(a: string, b: string): number {
  const parts = (v: string) => v.split(/[^\d.]/, 1)[0].split('.').map(Number)
  const [x, y] = [parts(a), parts(b)]
  for (let i = 0; i < Math.max(x.length, y.length); i++) {
    const d = (x[i] || 0) - (y[i] || 0)
    if (d) return d
  }
  return 0
}

/**
 * What changed since the version last seen, newest first: every release after `seen` up to the running one. A new
 * install (nothing seen) gets only its own version's; nothing once the running version is seen.
 */
export function missedReleases(version: string, seen: string | null | undefined): WhatsNewRelease[] {
  if (seen === version) return []
  if (!seen) return RELEASES.filter(release => release.version === version)
  return RELEASES.filter(release => compareVersions(release.version, seen) > 0 && compareVersions(release.version, version) <= 0)
}

/** Every release up to the running version, newest first (all of them on a build older than any, like a dev build). */
export function releasesThrough(version: string): WhatsNewRelease[] {
  const through = RELEASES.filter(release => compareVersions(release.version, version) <= 0)
  return through.length ? through : RELEASES
}
