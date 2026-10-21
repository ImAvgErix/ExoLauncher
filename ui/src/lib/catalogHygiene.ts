const MARKETPLACE = [
  'actor locker',
  'actorlocker',
  'auto footstep',
  'autofootstep',
  'async loading',
  'ai for npc',
  'metahuman',
  'unreal engine',
  'c++',
  'sequence manager',
  'montage sequence',
  'electric dreams',
  'code flow',
  'quixel',
  'megascan',
  'twinmotion',
  'lyra starter',
  'city sample',
  'content example',
  'starter content',
  'vehicle variety',
  'fab plugin',
  'unreal marketplace',
]

const MARKETPLACE_PACKED = MARKETPLACE.map((needle) => needle.replace(/\s+/g, ''))

const EXACT_DROP = new Set(['discord', 'epiconlineservices', 'eosoverlay'])

const TOOLS = [
  'soundtrack',
  'dedicated server',
  'software development kit',
]

function compact(title: string): string {
  return title.toLowerCase().replace(/[^a-z0-9]+/g, '')
}

function isMarketplaceOrEngineTitle(title: string): boolean {
  const lower = title.toLowerCase()
  const packed = compact(title)
  if (EXACT_DROP.has(packed)) return true
  return MARKETPLACE.some((needle, i) => lower.includes(needle) || packed.includes(MARKETPLACE_PACKED[i]))
}

function isToolOrExtraTitle(title: string): boolean {
  const padded = ` ${title.toLowerCase()} `
  return TOOLS.some((needle) => padded.includes(needle))
    || /\bdemo\b/i.test(title)
    || /\(demo\)/i.test(title)
}

export function isPlayableLibraryGame(game: {
  title: string
  id?: string
  isAddPortable?: boolean
}, showTools = false): boolean {
  if (game.isAddPortable || game.id === 'local:add') return false
  if (!(game.id ?? '').toLowerCase().startsWith('steam:') && isMarketplaceOrEngineTitle(game.title)) return false
  // With Show tools and soundtracks on, the host's own filter decides.
  if (!showTools && isToolOrExtraTitle(game.title)) return false
  if (compact(game.title) === 'emergence' && (game.id ?? '').toLowerCase().startsWith('epic:'))
    return false
  return true
}
