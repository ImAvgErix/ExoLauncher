export type UpscalerKind =
  | 'DLSS'
  | 'Frame Generation'
  | 'Ray Reconstruction'
  | 'FSR'
  | 'FSR 4'
  | 'FSR FG'
  | 'FSR RR'
  | 'FSR RC'
  | 'XeSS'
  | 'XeSS FG'
  | 'XeLL'

export type UpscalerDest = {
  fileName: string
  kind: UpscalerKind
  /** Official vendor name. The DLL name is detail, never the headline. */
  label: string
  /** Set only where one official name covers more than one destination. */
  api?: string
}

/** Same 14 dests as `DlssSwapService.PackSpecs`. Overlay always shows every row. */
export const UPSCALER_DESTS: readonly UpscalerDest[] = [
  { fileName: 'nvngx_dlss.dll', kind: 'DLSS', label: 'DLSS Super Resolution' },
  { fileName: 'nvngx_dlssg.dll', kind: 'Frame Generation', label: 'DLSS Frame Generation' },
  { fileName: 'nvngx_dlssd.dll', kind: 'Ray Reconstruction', label: 'DLSS Ray Reconstruction' },
  { fileName: 'amd_fidelityfx_dx12.dll', kind: 'FSR', label: 'FSR 3.1', api: 'DirectX 12' },
  { fileName: 'amd_fidelityfx_vk.dll', kind: 'FSR', label: 'FSR 3.1', api: 'Vulkan' },
  { fileName: 'amd_fidelityfx_loader_dx12.dll', kind: 'FSR 4', label: 'FSR 4', api: 'Loader' },
  { fileName: 'amd_fidelityfx_upscaler_dx12.dll', kind: 'FSR 4', label: 'FSR 4', api: 'Upscaler' },
  { fileName: 'amd_fidelityfx_framegeneration_dx12.dll', kind: 'FSR FG', label: 'FSR Frame Generation' },
  { fileName: 'amd_fidelityfx_denoiser_dx12.dll', kind: 'FSR RR', label: 'FSR Ray Regeneration' },
  { fileName: 'amd_fidelityfx_radiancecache_dx12.dll', kind: 'FSR RC', label: 'FSR Radiance Cache' },
  { fileName: 'libxess.dll', kind: 'XeSS', label: 'XeSS', api: 'DirectX 12' },
  { fileName: 'libxess_dx11.dll', kind: 'XeSS', label: 'XeSS', api: 'DirectX 11' },
  { fileName: 'libxess_fg.dll', kind: 'XeSS FG', label: 'XeSS Frame Generation' },
  { fileName: 'libxell.dll', kind: 'XeLL', label: 'XeLL' },
]

export type UpscalerStatusItem = {
  fileName?: string
  displayName?: string
  present?: boolean
  eligible?: boolean
  currentVersion?: string | null
  fileVersion?: string | null
  currentDisplayVersion?: string | null
  /** Newest file Exo can put here. Null when Exo has nothing for this dest. */
  packVersion?: string | null
  packDisplayVersion?: string | null
  canRestore?: boolean
  unsupportedReason?: string | null
  skipReason?: string | null
}

/** Whole-game conditions that switch every row off. */
export type UpscalerGate = {
  antiCheat?: boolean
  gameRunning?: boolean
  installing?: boolean
}

export type UpscalerAction = {
  enabled: boolean
  /** Why the action cannot run. Always set when disabled. */
  reason: string | null
}

export type UpscalerRow = {
  fileName: string
  label: string
  api: string | null
  present: boolean
  /** Read off disk, or a dash. Exo never prints a version it did not read. */
  version: string
  rawVersion: string
  packVersion: string | null
  packDisplayVersion: string | null
  /** Exo has nothing newer than what this destination already holds. */
  isNewest: boolean
  /** False when the destination is detected but Exo cannot safely act on it. */
  usable: boolean
  note: string | null
  /** True where one Newest press would act on this destination. */
  updatable: boolean
  /** True where Exo captured the shipped file, so Restore has an original. */
  restorable: boolean
}

/** One Newest and one Restore for the whole game, next to the count. */
export type UpscalerGroup = {
  /** Destinations a Newest press would look at. */
  updatable: number
  restorable: number
  newest: UpscalerAction
  restore: UpscalerAction
}

export function gateReason(gate?: UpscalerGate): string | null {
  if (gate?.antiCheat) return 'Anti-cheat title'
  if (gate?.gameRunning) return 'Close the game first'
  if (gate?.installing) return 'Install in progress'
  return null
}

export function upscalerRows(items?: UpscalerStatusItem[] | null, gate?: UpscalerGate): UpscalerRow[] {
  const byName = new Map<string, UpscalerStatusItem>()
  for (const item of items ?? []) {
    const name = item.fileName?.trim()
    if (!name) continue
    const key = name.toLowerCase()
    if (!byName.has(key)) byName.set(key, item)
  }

  const blockedByGame = gateReason(gate)

  return UPSCALER_DESTS.map((dest) => {
    const hit = byName.get(dest.fileName.toLowerCase())
    const present = hit?.present === true
    const rawVersion = present ? (hit?.currentVersion || hit?.fileVersion || '').trim() || '—' : '—'
    const version = present ? (hit?.currentDisplayVersion || rawVersion).trim() || '—' : '—'
    const packVersion = hit?.packVersion?.trim() || null
    const packDisplayVersion = hit?.packDisplayVersion?.trim() || packVersion
    const unsupported = hit?.unsupportedReason?.trim() || null
    const skip = present ? hit?.skipReason?.trim() || null : null
    // FSR 3.1 DLL resources intentionally report a 1.0.x Windows file
    // version while the manifest exposes the meaningful 3.1.x SDK version.
    // Compare the same semantic family the user sees, not the raw resource
    // number (which made a game's FSR 2.0 appear newer than FSR 3.1).
    const currentComparable = present ? (hit?.currentDisplayVersion?.trim() || rawVersion) : '—'
    const packComparable = packDisplayVersion || packVersion
    const isNewest = present && packVersion != null && !!packComparable && versionAtLeast(currentComparable, packComparable)
    const eligible = hit?.eligible !== false
    const blocked = present
      ? unsupported ?? skip ?? (eligible ? null : 'Unavailable')
      : 'Not used'
    const usable = present && !blockedByGame && !blocked && packVersion != null
    const note = blocked ?? (isNewest ? null : packDisplayVersion ? `Exo has ${packDisplayVersion}` : 'Exo has no file')

    return {
      fileName: dest.fileName,
      label: hit?.displayName?.trim() || dest.label,
      api: dest.api ?? null,
      present,
      version,
      rawVersion,
      packVersion,
      packDisplayVersion,
      isNewest,
      usable,
      note,
      updatable: usable && !isNewest,
      restorable: !blockedByGame && !blocked && hit?.canRestore === true,
    }
  })
}

export type UpscalerVisualState = 'current' | 'outdated' | 'unknown'

/** Unusable destinations are neutral even when a newer catalog file exists. */
export function upscalerVisualState(row: UpscalerRow): UpscalerVisualState {
  if (!row.usable) return 'unknown'
  if (row.isNewest) return 'current'
  if (row.rawVersion !== '—' && row.packVersion) return 'outdated'
  return 'unknown'
}

export function upscalerGroup(rows: UpscalerRow[], gate?: UpscalerGate): UpscalerGroup {
  const blockedByGame = gateReason(gate)
  const updatable = rows.filter((row) => row.updatable).length
  const restorable = rows.filter((row) => row.restorable).length
  const allNewest = rows.some((row) => row.present) && rows.every((row) => !row.present || row.isNewest)
  return {
    updatable,
    restorable,
    newest: action(blockedByGame, updatable > 0, allNewest ? 'Already newest' : 'Nothing to update'),
    restore: action(blockedByGame, restorable > 0, 'No shipped copy saved'),
  }
}

/**
 * Numeric per part, and false unless both sides read cleanly. File versions
 * here run 310.4.0.0, 1.0.1.41314 and 2.3.0.2740, which a text compare orders
 * wrong, and a dash means Exo could not read one at all.
 */
export function versionAtLeast(version: string, other: string): boolean {
  const a = parseVersion(version)
  const b = parseVersion(other)
  if (!a || !b) return false
  for (let i = 0; i < 4; i += 1) {
    if (a[i] !== b[i]) return a[i] > b[i]
  }
  return true
}

function parseVersion(value: string): [number, number, number, number] | null {
  const parts = value.trim().split('.').filter((part) => part.length > 0)
  if (parts.length === 0) return null
  const numbers: [number, number, number, number] = [0, 0, 0, 0]
  for (let i = 0; i < parts.length; i += 1) {
    if (!/^\d+$/.test(parts[i])) return null
    if (i < 4) numbers[i] = Number(parts[i])
  }
  return numbers
}

function action(blocked: string | null, ready: boolean, notReady: string): UpscalerAction {
  if (blocked) return { enabled: false, reason: blocked }
  if (!ready) return { enabled: false, reason: notReady }
  return { enabled: true, reason: null }
}
