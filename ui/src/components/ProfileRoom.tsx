import {
  useCallback,
  useEffect,
  useId,
  useMemo,
  useRef,
  useState,
  type CSSProperties,
  type ReactNode,
} from 'react'
import { AnimatePresence, motion, useReducedMotion } from 'motion/react'
import { PencilSimpleIcon } from '@phosphor-icons/react/dist/csr/PencilSimple'
import { DotsSixVerticalIcon } from '@phosphor-icons/react/dist/csr/DotsSixVertical'
import { EyeIcon } from '@phosphor-icons/react/dist/csr/Eye'
import { EyeSlashIcon } from '@phosphor-icons/react/dist/csr/EyeSlash'
import { XIcon } from '@phosphor-icons/react/dist/csr/X'
import {
  host,
  onHostEvent,
  type AccountState,
  type Game,
  type GameAchievementEntry,
  type GameAchievementsResponse,
  type ProfileBannerHeight,
  type ProfileGalleryKind,
  type ProfileImageKind,
  type ProfileLayout,
  type ProfileLookPatch,
  type ProfilePatch,
  type ProfileResponse,
  type ProfileShowcaseEntry,
  type OnlinePublicProfile,
} from '../lib/host'
import { CACHE_KEYS, peekCache, writeCache } from '../lib/cache'
import { isUsefulAchievement } from '../lib/achievements'
import {
  ACCENTS,
  DEFAULT_ACCENT,
  PROFILE_LIMITS,
  accentHex,
  steamPlayingCoverUrl,
} from '../lib/social'
import { cn, formatPlaytime, formatRelativeLastPlayed, monogram, storeLabel } from '../lib/utils'
import { Check } from '../brand/icons'
import { CoverArt, HeroWash } from './CoverArt'

/**
 * The Exo profile. This is the user's own identity, not a store persona: name,
 * handle, pronouns, status, bio, accent, pictures, showcase, and the shape of
 * the page itself are all authored here and kept by the host in settings.json.
 *
 * Every number is counted from the real library or reported by the host. Where
 * Exo cannot know something — an unlock total it never read, for instance — it
 * prints a dash.
 */

type Mode = 'view' | 'edit'

/** Tiles rendered into a picker before search has to do the narrowing. */
const PICK_GRID_MAX = 48

/** Below this a picker is short enough to read without a search field. */
const PICK_SEARCH_MIN = 18

const GALLERY_SLOTS: ProfileGalleryKind[] = ['gallery0', 'gallery1', 'gallery2', 'gallery3', 'gallery4', 'gallery5']

type Draft = {
  name: string
  pronouns: string
  statusText: string
  bio: string
  accent: string
}

type ServerBadge = {
  key: string
  label: string
  description: string | null
  tone: string | null
}

function normalizeServerBadges(value: unknown): ServerBadge[] {
  if (!Array.isArray(value)) return []
  const badges: ServerBadge[] = []
  for (const candidate of value.slice(0, 8)) {
    if (!candidate || typeof candidate !== 'object') continue
    const item = candidate as Record<string, unknown>
    const key = typeof item.key === 'string' ? item.key.trim().slice(0, 48) : ''
    const label = typeof item.label === 'string' ? item.label.trim().slice(0, 48) : ''
    if (!key || !label) continue
    if (key.toLocaleLowerCase() === 'ceo') continue
    badges.push({
      key,
      label,
      description: typeof item.description === 'string' ? item.description.trim().slice(0, 160) || null : null,
      tone: typeof item.tone === 'string' ? item.tone.trim().slice(0, 32) || null : null,
    })
  }
  return badges
}

function profileBadges(profile: ProfileResponse | null): unknown {
  return (profile as (ProfileResponse & { badges?: unknown }) | null)?.badges
}

function accountAchievementScope(account: AccountState): string {
  if (!account.ok || !account.signedIn) return 'signed-out'
  return ['signed-in', account.id?.trim() || account.handle?.trim() || 'anonymous', account.provider ?? 'unknown'].join('\u001f')
}

function linkedStoreAchievementScope(profile: ProfileResponse): string {
  return (profile.storeAccounts ?? [])
    .map((account) => [account.store.trim().toLocaleLowerCase(), account.accountName?.trim() || ''].join(':'))
    .sort()
    .join('\u001f')
}

/** Visual only: roles and grant authority remain server/native concerns. */
export function ServerBadgeRow({ badges, className }: { badges: unknown; className?: string }) {
  const safe = normalizeServerBadges(badges)
  if (safe.length === 0) return null
  return (
    <div className={cn('exo-identity-badges', className)} aria-label="Profile badges">
      {safe.map((badge) => {
        const founder = badge.key.toLocaleLowerCase() === 'founder' || badge.tone?.toLocaleLowerCase() === 'founder'
        return (
          <span
            key={badge.key}
            className={cn('exo-identity-badge', founder && 'is-founder')}
            aria-label={badge.description ? `${badge.label}. ${badge.description}` : badge.label}
          >
            <i aria-hidden="true" />
            {badge.label}
          </span>
        )
      })}
    </div>
  )
}

const SECTIONS: ReadonlyArray<{ key: string; label: string; hint: string }> = [
  { key: 'facts', label: 'Activity', hint: 'Stats and recent play' },
  { key: 'about', label: 'About', hint: 'Whatever you wrote' },
  { key: 'showcase', label: 'Showcase', hint: 'Up to ten pinned games' },
]

const PROFILE_LAYOUTS: ReadonlyArray<[ProfileLayout, string]> = [
  ['left', 'Left'],
  ['center', 'Center'],
]

const BANNER_HEIGHTS: ReadonlyArray<[ProfileBannerHeight, string]> = [
  ['short', 'Compact'],
  ['standard', 'Standard'],
  ['tall', 'Tall'],
]

function isRealGame(game: Game): boolean {
  return !game.isAddPortable && game.id !== 'local:add'
}

function byLastPlayed(a: Game, b: Game): number {
  return Date.parse(b.lastPlayedUtc ?? '') - Date.parse(a.lastPlayedUtc ?? '')
}

function draftFrom(profile: ProfileResponse | null): Draft {
  return {
    name: profile?.name ?? '',
    pronouns: profile?.pronouns ?? '',
    statusText: profile?.statusText ?? '',
    bio: profile?.bio ?? '',
    accent: profile?.accent ?? DEFAULT_ACCENT,
  }
}

/** The user's order, with any section they never moved left at the end. */
function sectionOrder(profile: ProfileResponse | null): string[] {
  const known = SECTIONS.map((section) => section.key)
  const saved = (profile?.sections ?? []).filter((key) => known.includes(key))
  return [...saved, ...known.filter((key) => !saved.includes(key))]
}

/** Titles ranked so pinned and installed games are reachable without searching. */
function pickerPool(games: Game[], pinned: ReadonlySet<string>): Game[] {
  const rank = (game: Game) => (pinned.has(game.id) ? 0 : game.installed ? 1 : 2)
  return [...games].sort(
    (a, b) =>
      rank(a) - rank(b) ||
      byLastPlayed(a, b) ||
      a.title.localeCompare(b.title, undefined, { sensitivity: 'base' }),
  )
}

export function ProfileRoom({
  games,
  active,
}: {
  games: Game[]
  active: boolean
}) {
  const [profile, setProfile] = useState<ProfileResponse | null>(null)
  const [profileReady, setProfileReady] = useState(false)
  const [showcase, setShowcase] = useState<string[]>([])
  const [mode, setMode] = useState<Mode>('view')
  const [draft, setDraft] = useState<Draft>(() => draftFrom(null))
  const [busy, setBusy] = useState<string | null>(null)
  const [note, setNote] = useState<string | null>(null)
  const [enlarged, setEnlarged] = useState(false)
  const [lightboxInstant, setLightboxInstant] = useState(false)
  const [onlineMediaCapable, setOnlineMediaCapable] = useState(false)
  const [avatarFailed, setAvatarFailed] = useState(false)
  const [bannerFailed, setBannerFailed] = useState(false)
  const [serverBadges, setServerBadges] = useState<unknown>(null)
  const [achievementByGame, setAchievementByGame] = useState<Map<string, GameAchievementsResponse>>(
    () => new Map(),
  )
  const [achievementScopeRevision, setAchievementScopeRevision] = useState(0)
  const [trophiesLoading, setTrophiesLoading] = useState(false)
  const accountAchievementScopeRef = useRef<string | null>(null)
  const linkedStoreAchievementScopeRef = useRef<string | null>(null)
  const lastSavedIdentity = useRef('')
  const modeRef = useRef<Mode>('view')
  modeRef.current = mode

  const apply = useCallback(
    (next: ProfileResponse) => {
      const nextStoreScope = linkedStoreAchievementScope(next)
      const previousStoreScope = linkedStoreAchievementScopeRef.current
      linkedStoreAchievementScopeRef.current = nextStoreScope
      if (previousStoreScope !== null && previousStoreScope !== nextStoreScope) {
        setAchievementByGame(new Map())
        setAchievementScopeRevision((revision) => revision + 1)
      }
      setProfile(next)
      setProfileReady(true)
      if (modeRef.current === 'view') {
        const loaded = draftFrom(next)
        setDraft(loaded)
        lastSavedIdentity.current = [loaded.name, loaded.pronouns, loaded.statusText, loaded.bio, loaded.accent].join('\u0000')
      }
      setShowcase((next.showcase ?? []).slice(0, PROFILE_LIMITS.showcase))
    },
    [],
  )

  const reload = useCallback(async () => {
    const next = await host.profileGet()
    writeCache(CACHE_KEYS.profile, next)
    apply(next)
  }, [apply])

  const syncProfileToExo = useCallback(async () => {
    const account = await host.accountGet()
    if (!account.ok || !account.signedIn || !account.handle) return true
    const result = await host.accountSetProfile()
    if (result.ok) return true
    setNote(result.message ?? 'Saved on this PC; Exo sync will retry later.')
    return false
  }, [])

  // Paint the last answer first so a second visit is instant, then refresh.
  useEffect(() => {
    const cached = peekCache<ProfileResponse>(CACHE_KEYS.profile)
    if (cached) apply(cached)
    void reload().catch((error: unknown) => {
      setNote(error instanceof Error ? error.message : 'Profile could not be read.')
    })
  }, [apply, reload])

  useEffect(() => {
    let mounted = true
    let sawAccountEvent = false
    const accept = (account: AccountState) => {
      if (!mounted) return
      setServerBadges(account.ok && account.signedIn ? account.badges : [])
      const nextScope = accountAchievementScope(account)
      const previousScope = accountAchievementScopeRef.current
      accountAchievementScopeRef.current = nextScope
      if (previousScope === null || previousScope === nextScope) return
      setAchievementByGame(new Map())
      setAchievementScopeRevision((revision) => revision + 1)
    }
    void host.accountGet().then((account) => {
      if (!sawAccountEvent) accept(account)
    }).catch(() => undefined)
    const offAccount = onHostEvent('account.updated', (account) => {
      sawAccountEvent = true
      accept(account)
    })
    const offProfile = onHostEvent('profile.updated', apply)
    return () => {
      mounted = false
      offAccount()
      offProfile()
    }
  }, [apply])

  useEffect(() => {
    let active = true
    void host.onlineHealth().then((result) => {
      if (active) setOnlineMediaCapable(result.value?.capabilities.media === true)
    }).catch(() => {
      if (active) setOnlineMediaCapable(false)
    })
    return () => {
      active = false
    }
  }, [])

  useEffect(() => {
    if (!note) return
    const noteTimer = window.setTimeout(() => setNote(null), 3600)
    return () => window.clearTimeout(noteTimer)
  }, [note])

  const real = useMemo(() => games.filter(isRealGame), [games])
  const installed = useMemo(() => real.filter((game) => game.installed), [real])
  const recentGames = useMemo(
    () =>
      [...real]
        .filter((game) => !!game.lastPlayedUtc)
        .sort(byLastPlayed)
        .slice(0, 2),
    [real],
  )
  const running = real.find((game) => game.isRunning || game.canStop) ?? null
  const playing = running

  // The library read is the fresher, complete source; host counts only stand in
  // until it lands, so nothing on the page can contradict the shelf below it.
  const libraryMinutes = real.reduce((total, game) => total + Math.max(0, game.playtimeMinutes ?? 0), 0)
  const minutes =
    real.length > 0 ? (libraryMinutes > 0 ? libraryMinutes : null) : profile?.playtimeMinutes ?? null
  const installedCount = real.length > 0 ? installed.length : profile?.installedCount ?? 0
  const gameCount = real.length > 0 ? real.length : profile?.gameCount ?? 0
  const name = (mode === 'edit' ? draft.name : profile?.name)?.trim() || null
  const pronouns = (mode === 'edit' ? draft.pronouns : profile?.pronouns)?.trim() || null
  const statusText = (mode === 'edit' ? draft.statusText : profile?.statusText)?.trim() || null
  const accent = accentHex(mode === 'edit' ? draft.accent : profile?.accent)
  const profileLayout: ProfileLayout = profile?.layout === 'left' ? 'left' : 'center'
  const showcaseStyle = profile?.showcaseStyle === 'rows' ? 'rows' : 'grid'
  const bannerHeight: ProfileBannerHeight =
    profile?.bannerHeight === 'short' || profile?.bannerHeight === 'tall'
      ? profile.bannerHeight
      : 'standard'
  const avatarImage = profile?.avatarImageUrl ?? null
  const bannerImage = profile?.bannerImageUrl ?? null
  const effectiveAvatarImage = avatarImage && !avatarFailed ? avatarImage : null
  const effectiveBannerImage = bannerImage && !bannerFailed ? bannerImage : null
  useEffect(() => setAvatarFailed(false), [profile?.avatarImageUrl])
  useEffect(() => setBannerFailed(false), [profile?.bannerImageUrl])
  const galleryImages = (profile?.galleryImages ?? []).filter((image) =>
    GALLERY_SLOTS.includes(image.slot) && !!image.url,
  )
  const nextGallerySlot = GALLERY_SLOTS.find((slot) => !galleryImages.some((image) => image.slot === slot)) ?? null
  const bannerGame = profile?.bannerGameId
    ? real.find((game) => game.id === profile.bannerGameId) ?? null
    : null
  const canEnlarge = !!effectiveAvatarImage

  const showcaseGames = useMemo(
    () =>
      showcase
        .map((id) => real.find((game) => game.id === id))
        .filter((game): game is Game => game !== undefined),
    [showcase, real],
  )
  const entryById = useMemo(() => {
    const map = new Map<string, ProfileShowcaseEntry>()
    for (const entry of profile?.showcaseEntries ?? []) map.set(entry.id, entry)
    return map
  }, [profile?.showcaseEntries])

  const showcaseAchievementKey = showcaseGames.map((game) => game.id).join('\u001f')

  // The profile is pre-mounted for instant navigation, but provider refreshes
  // begin only while the profile is visible. Two-at-a-time keeps the host and
  // store caches responsive while still filling the trophy cabinet quickly.
  useEffect(() => {
    if (!active || showcaseGames.length === 0) return
    let cancelled = false
    const load = async () => {
      setTrophiesLoading(true)
      try {
        const candidates = showcaseGames.slice(0, PROFILE_LIMITS.showcase)
        for (let index = 0; index < candidates.length && !cancelled; index += 2) {
          await Promise.all(candidates.slice(index, index + 2).map(async (game) => {
            let result: GameAchievementsResponse | null = null
            try {
              result = await host.getAchievements(game.id)
            } catch {
              result = null
            }
            if (!isUsefulAchievement(result)) {
              try {
                result = await host.refreshAchievements(game.id)
              } catch {
                result = null
              }
            }
            if (cancelled || !isUsefulAchievement(result)) return
            setAchievementByGame((current) => {
              const next = new Map(current)
              next.set(game.id, result as GameAchievementsResponse)
              return next
            })
          }))
        }
      } finally {
        if (!cancelled) setTrophiesLoading(false)
      }
    }
    void load()
    return () => {
      cancelled = true
    }
  }, [active, achievementScopeRevision, showcaseAchievementKey])

  const trophyItems = useMemo(() => {
    const items: Array<{ game: Game; achievement: GameAchievementEntry }> = []
    for (const game of showcaseGames) {
      for (const achievement of achievementByGame.get(game.id)?.achievements ?? []) {
        if (achievement.unlocked && !achievement.hidden) items.push({ game, achievement })
      }
    }
    return items
      .sort((left, right) => {
        const date = Date.parse(right.achievement.unlockedAt ?? '') - Date.parse(left.achievement.unlockedAt ?? '')
        if (date) return date
        return (left.achievement.rarityPercent ?? 101) - (right.achievement.rarityPercent ?? 101)
      })
      .slice(0, 12)
  }, [achievementByGame, showcaseGames])

  const pinned = useMemo(() => new Set(showcase), [showcase])
  const pickPool = useMemo(() => pickerPool(real, pinned), [real, pinned])

  async function persistShowcase(ids: string[]) {
    if (busy) return
    const previous = showcase
    setShowcase(ids)
    setBusy('showcase')
    try {
      apply(await host.profileSetShowcase(ids))
      if (await syncProfileToExo()) setNote(null)
    } catch (error) {
      setShowcase(previous)
      setNote(error instanceof Error ? error.message : 'Showcase could not be saved.')
    } finally {
      setBusy(null)
    }
  }

  function toggleShowcase(id: string) {
    if (!showcase.includes(id) && showcase.length >= PROFILE_LIMITS.showcase) {
      setNote(`The showcase holds ${PROFILE_LIMITS.showcase} games. Remove one first.`)
      return
    }
    const next = showcase.includes(id)
      ? showcase.filter((pick) => pick !== id)
      : [...showcase, id]
    void persistShowcase(next)
  }

  function movePick(index: number, delta: number) {
    const target = index + delta
    if (target < 0 || target >= showcase.length) return
    const next = [...showcase]
    ;[next[index], next[target]] = [next[target], next[index]]
    void persistShowcase(next)
  }

  async function saveLook(patch: ProfileLookPatch) {
    setBusy('look')
    try {
      apply(await host.profileSetLook(patch))
      if (await syncProfileToExo()) setNote(null)
    } catch (error) {
      setNote(error instanceof Error ? error.message : 'That change could not be saved.')
    } finally {
      setBusy(null)
    }
  }

  /** The host owns the dialog and the copy. The UI never names a file. */
  async function uploadImage(kind: ProfileImageKind) {
    setBusy(`image:${kind}`)
    try {
      const result = await host.profilePickImage(kind)
      if (result.profile) apply(result.profile)
      if (result.cancelled) {
        setNote(null)
      } else if (!result.ok) {
        setNote(result.message ?? 'That picture could not be used.')
      } else if (onlineMediaCapable && profile?.handleSource === 'server') {
        const uploaded = await host.onlineUploadMedia(kind)
        const label = kind === 'avatar' ? 'Avatar' : kind === 'banner' ? 'Banner' : 'Gallery media'
        setNote(uploaded.ok ? `${label} auto-saved to Exo.` : uploaded.diagnostics.error?.message ?? 'Saved on this PC; online media will retry later.')
      } else {
        setNote(null)
      }
    } catch (error) {
      setNote(error instanceof Error ? error.message : 'That picture could not be used.')
    } finally {
      setBusy(null)
    }
  }

  async function clearImage(kind: ProfileImageKind) {
    setBusy(`image:${kind}`)
    try {
      const result = await host.profileClearImage(kind)
      if (result.profile) apply(result.profile)
      if (!result.ok) {
        setNote(result.message ?? 'That picture could not be removed.')
      } else if (onlineMediaCapable && profile?.handleSource === 'server') {
        const deleted = await host.onlineDeleteMedia(kind)
        setNote(deleted.ok ? null : deleted.diagnostics.error?.message ?? 'Removed on this PC; online cleanup will retry later.')
      } else {
        setNote(null)
      }
    } catch (error) {
      setNote(error instanceof Error ? error.message : 'That picture could not be removed.')
    } finally {
      setBusy(null)
    }
  }

  async function persistIdentity(close = false) {
    const key = [draft.name.trim(), draft.pronouns.trim(), draft.statusText.trim(), draft.bio.trim(), draft.accent].join('\u0000')
    if (key === lastSavedIdentity.current) {
      if (close) setMode('view')
      return
    }
    setBusy('profile')
    try {
      const patch: ProfilePatch = {
        name: draft.name.trim(),
        pronouns: draft.pronouns.trim(),
        statusText: draft.statusText.trim(),
        bio: draft.bio.trim(),
        accent: draft.accent,
      }
      apply(await host.profileSet(patch))
      lastSavedIdentity.current = key
      if (await syncProfileToExo()) setNote(null)
      if (close) setMode('view')
    } catch (error) {
      setNote(error instanceof Error ? error.message : 'Profile could not be saved.')
    } finally {
      setBusy(null)
    }
  }

  // Stable no-argument entry point; edits are persisted by the debounced
  // effect below while the close action can flush pending changes first.
  async function saveIdentity() {
    await persistIdentity(false)
  }

  useEffect(() => {
    if (mode !== 'edit' || !profileReady) return
    const timer = window.setTimeout(() => void saveIdentity(), 450)
    return () => window.clearTimeout(timer)
  }, [draft, mode, profileReady])

  const hiddenSections = new Set(profile?.hiddenSections ?? [])
  const visibleSections = sectionOrder(profile).filter((key) => !hiddenSections.has(key))
  const railSections = visibleSections.filter((key) => key !== 'showcase')
  const showShowcase = visibleSections.includes('showcase')
  const showTrophyStage = showShowcase && showcaseGames.length > 0

  function renderSection(key: string): ReactNode {
    if (hiddenSections.has(key)) return null
    switch (key) {
      case 'facts':
        return (
          <section key={key} className="exo-profile-block is-activity" data-profile-section="facts">
            <div className="exo-home-head">
              <h3 className="exo-section-label">Activity</h3>
            </div>
            <dl className="exo-profile-statline">
              {([
                ['Time played', formatPlaytime(minutes)],
                ['Games', gameCount.toLocaleString()],
                ['Installed', installedCount.toLocaleString()],
              ] as const).map(([label, value]) => (
                <div key={label}>
                  <dt>{label}</dt>
                  <dd>{value}</dd>
                </div>
              ))}
            </dl>
            {recentGames.length > 0 ? (
              <ol className="exo-profile-activity">
                {recentGames.map((game) => (
                  <li key={game.id}>
                    <i style={{ background: accent }} aria-hidden />
                    <span className="min-w-0">
                      <strong>{game.title}</strong>
                      <span>{formatRelativeLastPlayed(game.lastPlayedUtc)}</span>
                    </span>
                  </li>
                ))}
              </ol>
            ) : (
              <p className="exo-profile-note">Recent play will appear here after Exo observes it.</p>
            )}
          </section>
        )
      case 'about':
        return profile?.bio?.trim() ? (
          <section key={key} className="exo-profile-block is-about" data-profile-section="about">
            <div className="exo-home-head">
              <h3 className="exo-section-label">About</h3>
            </div>
            <p className="exo-profile-bio">{profile.bio.trim()}</p>
          </section>
        ) : null
      case 'showcase':
        return (
          <section key={key} className="exo-profile-block is-showcase" data-profile-section="showcase">
            <div className="exo-home-head">
              <h3 className="exo-section-label">Showcase</h3>
              {showcaseGames.length > 0 ? (
                <span className="exo-profile-count">
                  {showcaseGames.length} of {PROFILE_LIMITS.showcase}
                </span>
              ) : null}
            </div>
            {showcaseGames.length > 0 ? (
              <div className={cn('exo-showcase', showcaseStyle === 'rows' && 'is-rows')}>
                <ShowcaseFeature game={showcaseGames[0]} entry={entryById.get(showcaseGames[0].id)} />
                <div className="exo-showcase-grid">
                {showcaseGames.slice(1).map((game) => (
                  <ShowcaseEntry
                    key={game.id}
                    game={game}
                    entry={entryById.get(game.id)}
                    style={showcaseStyle}
                  />
                ))}
                </div>
              </div>
            ) : (
              <p className="exo-profile-note">
                {real.length > 0
                  ? 'Nothing pinned yet. Open edit to choose up to ten games.'
                  : 'No games yet. Install from a store, or add a folder from the library.'}
              </p>
            )}
          </section>
        )
      default:
        return null
    }
  }

  const avatarInner = (
    <>
      {effectiveAvatarImage ? (
        <img src={effectiveAvatarImage} alt="" onError={() => setAvatarFailed(true)} />
      ) : (
        <span className="exo-avatar-mono" aria-hidden>
          {monogram(name ?? 'Exo')}
        </span>
      )}
    </>
  )

  return (
    <main
      className={cn(
        'exo-profile min-h-0 flex-1',
        `is-${profileLayout}`,
        mode === 'view' && 'is-view',
        mode === 'edit' && 'is-edit',
      )}
      style={{ '--profile-accent': accent } as CSSProperties}
    >
      <header
        className={cn(
          'exo-profile-hero',
          effectiveBannerImage || bannerGame ? 'has-banner' : 'is-empty',
          `is-${bannerHeight}`,
        )}
      >
        <div className="exo-profile-hero-fallback" aria-hidden="true" />
        {effectiveBannerImage ? (
          <img
            className="exo-profile-hero-image"
            src={bannerImage ?? undefined}
            alt=""
            decoding="async"
            onError={() => setBannerFailed(true)}
          />
        ) : bannerGame ? (
          <div className="exo-profile-hero-game-art" aria-hidden="true">
            <HeroWash game={bannerGame} />
          </div>
        ) : null}
        <div className="exo-profile-hero-veil" aria-hidden="true" />

        <div className={cn('exo-profile-head exo-profile-hero-content', `is-${profileLayout}`)}>
          {canEnlarge ? (
            <button
              type="button"
              className="exo-avatar is-lg"
              style={{ boxShadow: `0 0 0 3px #000, 0 0 0 4px ${accent}` }}
              aria-label="Enlarge profile picture"
              onClick={(event) => {
                setLightboxInstant(event.detail === 0)
                setEnlarged(true)
              }}
            >
              {avatarInner}
            </button>
          ) : (
            <span
              className="exo-avatar is-lg"
              style={{ boxShadow: `0 0 0 3px #000, 0 0 0 4px ${accent}` }}
            >
              {avatarInner}
            </span>
          )}

          <div className="exo-profile-id min-w-0">
            <ServerBadgeRow badges={serverBadges ?? profileBadges(profile)} className="exo-profile-badges" />
            <h2 className={cn('exo-profile-name', !name && 'is-unset')}>
              {name ?? 'Your profile'}
            </h2>
            {pronouns ? <p className="exo-profile-pronouns">{pronouns}</p> : null}
            {statusText || playing ? (
              <div className="exo-profile-presence">
                {statusText ? <p className="exo-profile-status">{statusText}</p> : null}
                {playing ? <p className="exo-profile-playing">Playing {playing.title}</p> : null}
              </div>
            ) : null}
          </div>
        </div>

        <div className="exo-profile-actions">
          <button
            type="button"
            data-controller-target=""
            data-controller-safe=""
            className={cn('exo-profile-edit', mode === 'edit' && 'is-on')}
            aria-label={mode === 'edit' ? 'Close editor' : 'Edit profile'}
            aria-pressed={mode === 'edit'}
            disabled={!profileReady || busy !== null}
            onClick={() => {
              setNote(null)
              if (mode === 'edit') {
                setMode('view')
                return
              }
              setDraft(draftFrom(profile))
              setMode('edit')
            }}
          >
            <PencilSimpleIcon size={16} weight="regular" color="currentColor" aria-hidden style={{ display: 'block' }} />
          </button>
        </div>
      </header>

      {note ? <p className="exo-profile-note" role="status" aria-live="polite">{note}</p> : null}

      <div className="exo-profile-body">
        {mode === 'edit' ? (
          <EditorPanel
            draft={draft}
            games={pickPool}
            avatarImage={avatarImage}
            bannerImage={bannerImage}
            galleryImages={galleryImages}
            busy={busy}
            profileLayout={profileLayout}
            bannerHeight={bannerHeight}
            showcaseStyle={showcaseStyle}
            order={sectionOrder(profile)}
            hidden={hiddenSections}
            showcaseGames={showcaseGames}
            showcaseIds={showcase}
            onChange={setDraft}
            onUploadAvatar={() => void uploadImage('avatar')}
            onRemoveAvatar={() => void clearImage('avatar')}
            onUploadBanner={() => void uploadImage('banner')}
            onRemoveBanner={() => void clearImage('banner')}
            onAddGallery={nextGallerySlot ? () => void uploadImage(nextGallerySlot) : undefined}
            onRemoveGallery={(slot) => void clearImage(slot)}
            onlineMediaCapable={onlineMediaCapable && profile?.handleSource === 'server'}
            onLook={(patch) => void saveLook(patch)}
            onToggleShowcase={toggleShowcase}
            onMovePick={movePick}
            onCancel={() => {
              void persistIdentity(true)
            }}
          />
        ) : (
          <div
            className={cn(
              'exo-profile-view',
              !showShowcase && 'is-rail-only',
              railSections.length === 0 && 'is-showcase-only',
              showTrophyStage && 'has-trophies',
            )}
          >
            {railSections.length > 0 ? (
              <aside className="exo-profile-rail" aria-label="Profile details">
                {railSections.map(renderSection)}
              </aside>
            ) : null}
            <div className="exo-profile-stage">
              {showShowcase ? renderSection('showcase') : null}
              {galleryImages.length > 0 ? (
                <section className="exo-profile-block is-gallery" aria-labelledby="exo-profile-gallery">
                  <div className="exo-home-head">
                    <h3 className="exo-section-label" id="exo-profile-gallery">Gallery</h3>
                    <span className="exo-profile-count">{galleryImages.length} of {GALLERY_SLOTS.length}</span>
                  </div>
                  <div className="exo-profile-gallery">
                    {galleryImages.map((image) => (
                      <figure key={image.slot}>
                        <img src={image.url} alt="" loading="lazy" decoding="async" />
                      </figure>
                    ))}
                  </div>
                </section>
              ) : null}
            </div>
            {showTrophyStage ? (
              <aside className="exo-profile-trophy-stage" aria-label="Profile achievements">
                <TrophyCabinet items={trophyItems} loading={trophiesLoading} />
              </aside>
            ) : null}
          </div>
        )}
      </div>

      <AvatarLightbox
        open={enlarged && canEnlarge}
        instant={lightboxInstant}
        photo={!!avatarImage}
        onClose={(instant) => {
          setLightboxInstant(instant)
          setEnlarged(false)
        }}
      >
        {effectiveAvatarImage ? <img src={effectiveAvatarImage} alt="" /> : null}
      </AvatarLightbox>
    </main>
  )
}

function peerText(values: Record<string, unknown>, key: string): string {
  const value = values[key]
  return typeof value === 'string' ? value.trim() : ''
}

function peerList(values: Record<string, unknown>, key: string): string[] {
  const value = values[key]
  return Array.isArray(value)
    ? value.filter((item): item is string => typeof item === 'string' && item.trim().length > 0)
    : []
}

function peerShowcaseGame(id: string, games: Game[]): Game {
  const catalog = peerCatalogGame(id)
  const local = games.find((game) => game.id.toLowerCase() === id.toLowerCase())
  if (!local) return catalog
  return {
    ...catalog,
    title: local.title || catalog.title,
    coverUrl: catalog.coverUrl || local.coverUrl,
    coverSource: catalog.coverSource || local.coverSource,
  }
}

function peerCatalogGame(id: string, title?: string): Game {
  const trimmed = id.trim()
  const steam = /^steam:(\d+)$/i.exec(trimmed)
  if (steam) {
    return {
      id: trimmed,
      title: title?.trim() || 'Steam game',
      store: 'steam',
      installed: false,
      owned: false,
      primaryAction: 'none',
      coverUrl: steamPlayingCoverUrl(steam[1]),
      coverSource: 'steam-friend-cdn',
      status: 'Steam',
      deps: [],
      launchNote: '',
    }
  }
  const colon = trimmed.indexOf(':')
  const store = colon > 0 ? trimmed.slice(0, colon) : 'local'
  return {
    id: trimmed,
    title: title?.trim() || (colon > 0 ? trimmed.slice(colon + 1) : trimmed),
    store,
    installed: false,
    owned: false,
    primaryAction: 'none',
    status: 'Showcase',
    deps: [],
    launchNote: '',
  }
}

/**
 * A friend's public Exo profile, painted with the same chrome they see on
 * their own page. Edit, local activity, and this PC's trophy cabinet stay off.
 */
export function PeerProfileView({
  name,
  handle,
  profile,
  games,
  avatarUrl,
  bannerUrl,
  presencePlaying,
  presenceLabel,
  actions,
}: {
  name: string
  handle?: string | null
  profile: OnlinePublicProfile | null
  games: Game[]
  avatarUrl: string | null
  bannerUrl: string | null
  presencePlaying?: string | null
  presenceLabel?: string | null
  actions?: ReactNode
}) {
  const [avatarFailed, setAvatarFailed] = useState(false)
  const [bannerFailed, setBannerFailed] = useState(false)
  const [enlarged, setEnlarged] = useState(false)
  const [lightboxInstant, setLightboxInstant] = useState(false)
  useEffect(() => setAvatarFailed(false), [avatarUrl])
  useEffect(() => setBannerFailed(false), [bannerUrl])

  const values = (profile?.profile ?? {}) as Record<string, unknown>
  const accent = accentHex(peerText(values, 'accent') || DEFAULT_ACCENT)
  const layout: ProfileLayout = peerText(values, 'layout') === 'left' ? 'left' : 'center'
  const bannerHeight: ProfileBannerHeight =
    peerText(values, 'bannerHeight') === 'short' || peerText(values, 'bannerHeight') === 'tall'
      ? (peerText(values, 'bannerHeight') as ProfileBannerHeight)
      : 'standard'
  const showcaseStyle = peerText(values, 'showcaseStyle') === 'rows' ? 'rows' : 'grid'
  const pronouns = peerText(values, 'pronouns') || null
  const statusText = peerText(values, 'statusText') || null
  const bio = peerText(values, 'bio')
  const hidden = new Set(peerList(values, 'hiddenSections'))
  const known = SECTIONS.map((section) => section.key).filter((key) => key !== 'facts')
  const saved = peerList(values, 'sections').filter((key) => known.includes(key))
  const visible = [...saved, ...known.filter((key) => !saved.includes(key))].filter((key) => !hidden.has(key))
  const railSections = visible.filter((key) => key === 'about' && bio)
  const showShowcase = visible.includes('showcase')
  const effectiveAvatar = avatarUrl && !avatarFailed ? avatarUrl : null
  const effectiveBanner = bannerUrl && !bannerFailed ? bannerUrl : null
  const bannerGameId = peerText(values, 'bannerGameId')
  const bannerGame = bannerGameId ? peerShowcaseGame(bannerGameId, games) : null
  const showcaseGames = (() => {
    const ids = peerList(values, 'showcase')
    const seen = new Set<string>()
    const out: Game[] = []
    for (const id of ids) {
      const key = id.toLowerCase()
      if (seen.has(key)) continue
      seen.add(key)
      out.push(peerShowcaseGame(id, games))
    }
    return out
  })()
  const gallery = GALLERY_SLOTS.flatMap((slot) => {
    const media = profile?.media[slot]
    return media?.available && media.url ? [{ slot, url: media.url }] : []
  })
  const canEnlarge = !!effectiveAvatar
  const avatarInner = effectiveAvatar ? (
    <img src={effectiveAvatar} alt="" onError={() => setAvatarFailed(true)} />
  ) : (
    <span className="exo-avatar-mono" aria-hidden>
      {monogram(name || handle || 'Exo')}
    </span>
  )

  return (
    <main
      className={cn('exo-profile min-h-0 flex-1 is-view is-peer', `is-${layout}`)}
      style={{ '--profile-accent': accent } as CSSProperties}
    >
      <header
        className={cn(
          'exo-profile-hero',
          effectiveBanner || bannerGame ? 'has-banner' : 'is-empty',
          `is-${bannerHeight}`,
        )}
      >
        <div className="exo-profile-hero-fallback" aria-hidden="true" />
        {effectiveBanner ? (
          <img
            className="exo-profile-hero-image"
            src={bannerUrl ?? undefined}
            alt=""
            decoding="async"
            onError={() => setBannerFailed(true)}
          />
        ) : bannerGame ? (
          <div className="exo-profile-hero-game-art" aria-hidden="true">
            <HeroWash game={bannerGame} />
          </div>
        ) : null}
        <div className="exo-profile-hero-veil" aria-hidden="true" />
        <div className={cn('exo-profile-head exo-profile-hero-content', `is-${layout}`)}>
          {canEnlarge ? (
            <button
              type="button"
              className="exo-avatar is-lg"
              style={{ boxShadow: `0 0 0 3px #000, 0 0 0 4px ${accent}` }}
              aria-label="Enlarge profile picture"
              onClick={(event) => {
                setLightboxInstant(event.detail === 0)
                setEnlarged(true)
              }}
            >
              {avatarInner}
            </button>
          ) : (
            <span
              className="exo-avatar is-lg"
              style={{ boxShadow: `0 0 0 3px #000, 0 0 0 4px ${accent}` }}
            >
              {avatarInner}
            </span>
          )}
          <div className="exo-profile-id min-w-0">
            <ServerBadgeRow badges={profile?.badges} className="exo-profile-badges" />
            <h2 className={cn('exo-profile-name', !name && 'is-unset')}>{name || 'Exo profile'}</h2>
            {pronouns ? <p className="exo-profile-pronouns">{pronouns}</p> : null}
            {statusText || presenceLabel || presencePlaying ? (
              <div className="exo-profile-presence">
                {statusText ? <p className="exo-profile-status">{statusText}</p> : presenceLabel ? (
                  <p className="exo-profile-status">{presenceLabel}</p>
                ) : null}
                {presencePlaying ? <p className="exo-profile-playing">Playing {presencePlaying}</p> : null}
              </div>
            ) : null}
          </div>
        </div>
        {actions ? <div className="exo-profile-actions">{actions}</div> : null}
      </header>

      <div className="exo-profile-body">
        <div
          className={cn(
            'exo-profile-view',
            !showShowcase && 'is-rail-only',
            railSections.length === 0 && 'is-showcase-only',
          )}
        >
          {railSections.length > 0 ? (
            <aside className="exo-profile-rail" aria-label="Profile details">
              {railSections.map((key) =>
                key === 'about' && bio ? (
                  <section key={key} className="exo-profile-block is-about" data-profile-section="about">
                    <div className="exo-home-head">
                      <h3 className="exo-section-label">About</h3>
                    </div>
                    <p className="exo-profile-bio">{bio}</p>
                  </section>
                ) : null,
              )}
            </aside>
          ) : null}
          <div className="exo-profile-stage">
            {showShowcase ? (
              <section className="exo-profile-block is-showcase" data-profile-section="showcase">
                <div className="exo-home-head">
                  <h3 className="exo-section-label">Showcase</h3>
                  {showcaseGames.length > 0 ? (
                    <span className="exo-profile-count">
                      {showcaseGames.length} of {PROFILE_LIMITS.showcase}
                    </span>
                  ) : null}
                </div>
                {showcaseGames.length > 0 ? (
                  <div className={cn('exo-showcase', showcaseStyle === 'rows' && 'is-rows')}>
                    <ShowcaseFeature game={showcaseGames[0]} entry={undefined} peer />
                    <div className="exo-showcase-grid">
                      {showcaseGames.slice(1).map((game) => (
                        <ShowcaseEntry key={game.id} game={game} entry={undefined} style={showcaseStyle} peer />
                      ))}
                    </div>
                  </div>
                ) : (
                  <p className="exo-profile-note">Nothing pinned on this profile.</p>
                )}
              </section>
            ) : null}
            {gallery.length > 0 ? (
              <section className="exo-profile-block is-gallery" aria-labelledby="exo-peer-gallery">
                <div className="exo-home-head">
                  <h3 className="exo-section-label" id="exo-peer-gallery">Gallery</h3>
                  <span className="exo-profile-count">{gallery.length} of {GALLERY_SLOTS.length}</span>
                </div>
                <div className="exo-profile-gallery">
                  {gallery.map((image) => (
                    <figure key={image.slot}>
                      <img src={image.url} alt="" loading="lazy" decoding="async" />
                    </figure>
                  ))}
                </div>
              </section>
            ) : null}
          </div>
        </div>
      </div>

      <AvatarLightbox
        open={enlarged && canEnlarge}
        instant={lightboxInstant}
        photo={!!effectiveAvatar}
        onClose={(instant) => {
          setLightboxInstant(instant)
          setEnlarged(false)
        }}
      >
        {effectiveAvatar ? <img src={effectiveAvatar} alt="" /> : null}
      </AvatarLightbox>
    </main>
  )
}

function ShowcaseFeature({
  game,
  entry,
  peer = false,
}: {
  game: Game
  entry: ProfileShowcaseEntry | undefined
  peer?: boolean
}) {
  const unlocked = peer ? null : entry?.achievementsUnlocked ?? null
  const total = peer ? null : entry?.achievementsTotal ?? null
  const completion = unlocked != null && total != null && total > 0
    ? Math.round((unlocked / total) * 100)
    : null
  const minutes = peer ? null : (entry?.playtimeMinutes ?? game.playtimeMinutes)

  return (
    <article className="exo-showcase-feature">
      <div className="exo-showcase-feature-wash" aria-hidden="true">
        <HeroWash game={game} />
      </div>
      <div className="exo-showcase-feature-veil" aria-hidden="true" />
      <span className="exo-showcase-feature-cover">
        <CoverArt game={game} preload className="h-full w-full" />
      </span>
      <div className="exo-showcase-feature-copy">
        <span className="exo-showcase-eyebrow">Featured game</span>
        <h4>{game.title}</h4>
        <p>
          {storeLabel(entry?.store ?? game.store)}
          {peer ? null : <> · {formatPlaytime(minutes)}</>}
        </p>
        {completion != null ? (
          <div className="exo-showcase-progress" aria-label={`${unlocked} of ${total} achievements unlocked`}>
            <span><b>{unlocked}/{total}</b> achievements</span>
            <span>{completion}%</span>
            <i style={{ '--showcase-progress': `${completion}%` } as CSSProperties} aria-hidden />
          </div>
        ) : peer ? null : (
          <span className="exo-showcase-unknown">Achievement progress unavailable</span>
        )}
      </div>
    </article>
  )
}

function TrophyCabinet({
  items,
  loading,
}: {
  items: Array<{ game: Game; achievement: GameAchievementEntry }>
  loading: boolean
}) {
  return (
    <section className="exo-profile-block is-trophies" aria-labelledby="exo-profile-trophies">
      <div className="exo-home-head">
        <div>
          <span className="exo-showcase-eyebrow">Collected across your showcase</span>
          <h3 className="exo-section-label" id="exo-profile-trophies">Trophy cabinet</h3>
        </div>
        {items.length > 0 ? <span className="exo-profile-count">Latest {items.length}</span> : null}
      </div>
      {items.length > 0 ? (
        <div className="exo-profile-trophies">
          {items.map(({ game, achievement }) => (
            <article className="exo-profile-trophy" key={`${game.id}:${achievement.id}`}>
              <span className="exo-profile-trophy-icon">
                {achievement.iconUrl ? (
                  <img
                    src={achievement.iconUrl}
                    alt=""
                    loading="lazy"
                    decoding="async"
                    onError={(event) => { event.currentTarget.hidden = true }}
                  />
                ) : null}
                <i aria-hidden>◆</i>
              </span>
              <span className="exo-profile-trophy-copy">
                <strong>{achievement.name}</strong>
                <span>{game.title}</span>
                <small>{achievementRarity(achievement)}</small>
              </span>
            </article>
          ))}
        </div>
      ) : (
        <p className="exo-profile-note">
          {loading ? 'Gathering unlocked trophies…' : 'Unlocked trophies appear here when a supported store reports them.'}
        </p>
      )}
    </section>
  )
}

function achievementRarity(achievement: GameAchievementEntry): string {
  if (achievement.rarityPercent != null) {
    return `${achievement.rarityPercent.toFixed(1)}% of players`
  }
  return achievement.tier?.trim() || 'Unlocked'
}

/** One pinned game with what Exo actually recorded for it. */
function ShowcaseEntry({
  game,
  entry,
  style,
  peer = false,
}: {
  game: Game
  entry: ProfileShowcaseEntry | undefined
  style: 'grid' | 'rows'
  peer?: boolean
}) {
  const minutes = peer ? null : (entry?.playtimeMinutes ?? game.playtimeMinutes ?? null)
  const store = storeLabel(entry?.store ?? game.store)

  return (
    <article className={cn('exo-showcase-item', style === 'rows' && 'exo-showcase-row')}>
      <span className="exo-showcase-art">
        <CoverArt game={game} className="h-full w-full" />
        <span className="exo-showcase-sheen" aria-hidden />
      </span>
      <div className="exo-showcase-copy min-w-0">
        <h4 className="exo-showcase-title">{game.title}</h4>
        <p className="exo-showcase-meta">
          <span>{store}</span>
          {peer ? null : (
            <>
              <span aria-hidden>·</span>
              <span>{formatPlaytime(minutes)}</span>
            </>
          )}
        </p>
        {!peer && entry?.achievementsTotal != null ? (
          <p className="exo-showcase-achievements">
            {entry.achievementsUnlocked ?? 0}/{entry.achievementsTotal} achievements
          </p>
        ) : null}
      </div>
    </article>
  )
}

function EditorPanel({
  draft,
  games,
  avatarImage,
  bannerImage,
  galleryImages,
  busy,
  profileLayout,
  bannerHeight,
  showcaseStyle,
  order,
  hidden,
  showcaseGames,
  showcaseIds,
  onChange,
  onUploadAvatar,
  onRemoveAvatar,
  onUploadBanner,
  onRemoveBanner,
  onAddGallery,
  onRemoveGallery,
  onlineMediaCapable,
  onLook,
  onToggleShowcase,
  onMovePick,
  onCancel,
}: {
  draft: Draft
  games: Game[]
  avatarImage: string | null
  bannerImage: string | null
  galleryImages: Array<{ slot: ProfileGalleryKind; url: string }>
  busy: string | null
  profileLayout: ProfileLayout
  bannerHeight: ProfileBannerHeight
  showcaseStyle: 'grid' | 'rows'
  order: string[]
  hidden: ReadonlySet<string>
  showcaseGames: Game[]
  showcaseIds: string[]
  onChange: (next: Draft) => void
  onUploadAvatar: () => void
  onRemoveAvatar: () => void
  onUploadBanner: () => void
  onRemoveBanner: () => void
  onAddGallery?: () => void
  onRemoveGallery: (slot: ProfileGalleryKind) => void
  onlineMediaCapable: boolean
  onLook: (patch: ProfileLookPatch) => void
  onToggleShowcase: (id: string) => void
  onMovePick: (index: number, delta: number) => void
  onCancel: () => void
}) {
  const set = (patch: Partial<Draft>) => onChange({ ...draft, ...patch })
  const saving = busy === 'profile'
  const lookBusy = busy === 'look'
  const [draggedSection, setDraggedSection] = useState<string | null>(null)
  const [draggedShowcase, setDraggedShowcase] = useState<string | null>(null)

  function moveSection(index: number, delta: number) {
    const key = order[index]
    if (key === 'showcase') return
    const railOrder = order.filter((entry) => entry !== 'showcase')
    const railIndex = railOrder.indexOf(key)
    const target = railIndex + delta
    if (target < 0 || target >= railOrder.length) return
    ;[railOrder[railIndex], railOrder[target]] = [railOrder[target], railOrder[railIndex]]
    let cursor = 0
    const next = order.map((entry) => (entry === 'showcase' ? entry : railOrder[cursor++]))
    onLook({ sections: next })
  }

  function toggleSection(key: string) {
    const next = hidden.has(key)
      ? [...hidden].filter((entry) => entry !== key)
      : [...hidden, key]
    onLook({ hiddenSections: next })
  }

  function dropSection(targetKey: string) {
    if (!draggedSection || draggedSection === targetKey || targetKey === 'showcase') return
    const next = [...order]
    const from = next.indexOf(draggedSection)
    const to = next.indexOf(targetKey)
    if (from < 0 || to < 0) return
    next.splice(from, 1)
    next.splice(to, 0, draggedSection)
    onLook({ sections: next })
    setDraggedSection(null)
  }

  function dropShowcase(targetId: string) {
    if (!draggedShowcase || draggedShowcase === targetId) return
    const from = showcaseIds.indexOf(draggedShowcase)
    const to = showcaseIds.indexOf(targetId)
    if (from < 0 || to < 0) return
    onMovePick(from, to - from)
    setDraggedShowcase(null)
  }

  return (
    <div className="exo-profile-form">
      <div className="exo-profile-form-head">
        <div>
          <span className="exo-profile-editor-kicker">Profile studio</span>
          <h3>Make this page yours</h3>
          <p>Auto-saved. Drag sections and showcase cards; eye icons hide them.</p>
        </div>
        <div className="exo-profile-form-actions">
          <button
            type="button"
            data-controller-target=""
            data-controller-safe=""
            className="exo-ghost-btn"
            disabled={saving}
            onClick={onCancel}
          >
            Close
          </button>
        </div>
      </div>

      <section className="exo-profile-editor-block is-identity">
        <h3 className="exo-section-label">Identity</h3>
        <div className="exo-profile-form-grid">
          <Field label="Display name" htmlFor="exo-profile-name" hint={`${draft.name.length}/${PROFILE_LIMITS.name}`}>
            <input
              id="exo-profile-name"
              className="exo-field"
              value={draft.name}
              maxLength={PROFILE_LIMITS.name}
              placeholder="What people see"
              spellCheck={false}
              onChange={(event) => set({ name: event.target.value })}
            />
          </Field>
          <Field label="Pronouns" htmlFor="exo-profile-pronouns">
            <input
              id="exo-profile-pronouns"
              className="exo-field"
              value={draft.pronouns}
              maxLength={PROFILE_LIMITS.pronouns}
              placeholder="Optional"
              spellCheck={false}
              onChange={(event) => set({ pronouns: event.target.value })}
            />
          </Field>
          <Field label="Status" htmlFor="exo-profile-status" hint={`${draft.statusText.length}/${PROFILE_LIMITS.status}`}>
            <input
              id="exo-profile-status"
              className="exo-field"
              value={draft.statusText}
              maxLength={PROFILE_LIMITS.status}
              placeholder="One line, yours to write"
              onChange={(event) => set({ statusText: event.target.value })}
            />
          </Field>
        </div>
        <Field label="Bio" htmlFor="exo-profile-bio" hint={`${draft.bio.length}/${PROFILE_LIMITS.bio}`}>
          <textarea
            id="exo-profile-bio"
            className="exo-field exo-profile-bio-field"
            value={draft.bio}
            maxLength={PROFILE_LIMITS.bio}
            rows={4}
            placeholder="Longer, if you want it"
            onChange={(event) => set({ bio: event.target.value })}
          />
        </Field>
      </section>

      <section className="exo-profile-editor-block is-appearance">
        <h3 className="exo-section-label">Appearance</h3>
        <div className="exo-profile-look-controls">
          <Field label="Alignment">
            <Choice
              value={profileLayout}
              options={PROFILE_LAYOUTS}
              disabled={lookBusy}
              onPick={(next) => onLook({ layout: next })}
            />
          </Field>
          <Field label="Banner height">
            <Choice
              value={bannerHeight}
              options={BANNER_HEIGHTS}
              disabled={lookBusy}
              onPick={(next) => onLook({ bannerHeight: next })}
            />
          </Field>
          <Field label="Showcase style">
            <Choice
              value={showcaseStyle}
              options={[['grid', 'Grid'], ['rows', 'Rows']] as const}
              disabled={lookBusy}
              onPick={(next) => onLook({ showcaseStyle: next })}
            />
          </Field>
        </div>
        <Field label="Accent">
          <div className="exo-profile-accents">
            {ACCENTS.map((option) => (
              <button
                key={option.key}
                type="button"
                className={cn('exo-profile-accent', draft.accent === option.key && 'is-on')}
                style={{ background: option.hex }}
                aria-pressed={draft.accent === option.key}
                aria-label={option.label}
                onClick={() => set({ accent: option.key })}
              />
            ))}
          </div>
        </Field>
        <div className="exo-profile-image-controls">
          <div className="exo-profile-image-control is-avatar">
            <Field label="Avatar" hint={onlineMediaCapable ? 'PNG, JPEG, WebP, or GIF. Auto-saved to Exo.' : 'PNG, JPEG, WebP, or GIF. Saved on this PC.'}>
              <div className="exo-picker-head">
                <button
                  type="button"
                  className="exo-ghost-btn"
                  disabled={busy === 'image:avatar'}
                  onClick={onUploadAvatar}
                >
                  {busy === 'image:avatar' ? 'Opening' : avatarImage ? 'Replace avatar' : 'Upload avatar'}
                </button>
                {avatarImage ? (
                  <button
                    type="button"
                    className="exo-ghost-btn"
                    disabled={busy === 'image:avatar'}
                    onClick={onRemoveAvatar}
                  >
                    Remove avatar
                  </button>
                ) : null}
              </div>
            </Field>
          </div>
          <div className="exo-profile-image-control is-banner">
            <Field label="Banner" hint={onlineMediaCapable ? 'PNG, JPEG, WebP, or GIF. Auto-saved to Exo.' : 'PNG, JPEG, WebP, or GIF. Saved on this PC.'}>
              <div className="exo-picker-head">
                <button
                  type="button"
                  className="exo-ghost-btn"
                  disabled={busy === 'image:banner'}
                  onClick={onUploadBanner}
                >
                  {busy === 'image:banner' ? 'Opening' : bannerImage ? 'Replace banner' : 'Upload banner'}
                </button>
                {bannerImage ? (
                  <button
                    type="button"
                    className="exo-ghost-btn"
                    disabled={busy === 'image:banner'}
                    onClick={onRemoveBanner}
                  >
                    Remove banner
                  </button>
                ) : null}
              </div>
            </Field>
          </div>
        </div>
        <Field label="Gallery" hint={onlineMediaCapable ? 'Up to six pictures or GIFs. New media auto-saves to Exo.' : 'Up to six pictures or GIFs on this PC.'}>
          <div className="exo-profile-gallery-editor">
            {galleryImages.map((image) => (
              <figure key={image.slot}>
                <img src={image.url} alt="" loading="lazy" decoding="async" />
                <button
                  type="button"
                  className="exo-profile-gallery-remove"
                  aria-label="Remove gallery media"
                  disabled={busy !== null}
                  onClick={() => onRemoveGallery(image.slot)}
                >
                  Remove
                </button>
              </figure>
            ))}
            {onAddGallery ? (
              <button
                type="button"
                className="exo-profile-gallery-add"
                disabled={busy !== null}
                onClick={onAddGallery}
              >
                {busy?.startsWith('image:gallery') ? 'Opening' : 'Add media'}
              </button>
            ) : null}
          </div>
        </Field>
        <Field label="Sections" hint="Order and visibility">
          <ol className="exo-profile-sections">
            {order.map((key, index) => {
              const section = SECTIONS.find((entry) => entry.key === key)
              if (!section) return null
              const off = hidden.has(key)
              const railOrder = order.filter((entry) => entry !== 'showcase')
              const railIndex = railOrder.indexOf(key)
              const canReorder = key !== 'showcase'
              return (
                <li
                  key={key}
                  className={cn('exo-profile-section', off && 'is-off', draggedSection === key && 'is-dragging')}
                  draggable={canReorder}
                  onDragStart={() => canReorder && setDraggedSection(key)}
                  onDragEnd={() => setDraggedSection(null)}
                  onDragOver={(event) => { if (canReorder) event.preventDefault() }}
                  onDrop={() => dropSection(key)}
                >
                  <button
                    type="button"
                    className="exo-profile-drag-handle"
                    aria-label={`Reorder ${section.label}`}
                    tabIndex={canReorder ? 0 : -1}
                    onKeyDown={(event) => {
                      if (event.key === 'ArrowUp') { event.preventDefault(); moveSection(index, -1) }
                      if (event.key === 'ArrowDown') { event.preventDefault(); moveSection(index, 1) }
                    }}
                  >
                    <DotsSixVerticalIcon size={16} aria-hidden />
                  </button>
                  <span className="min-w-0 flex-1">
                    <span className="block truncate text-[13px]">{section.label}</span>
                    <span className="block truncate text-[11px] text-faint">{section.hint}</span>
                  </span>
                  {canReorder ? (
                    <>
                      <span className="exo-profile-section-slot" aria-hidden>{railIndex + 1}</span>
                    </>
                  ) : (
                    <span className="exo-profile-section-slot">Main stage</span>
                  )}
                  <button
                    type="button"
                    className="exo-profile-visibility-btn is-compact"
                    disabled={lookBusy}
                    onClick={() => toggleSection(key)}
                    aria-label={`${off ? 'Show' : 'Hide'} ${section.label}`}
                  >
                    {off ? <EyeSlashIcon size={16} aria-hidden /> : <EyeIcon size={16} aria-hidden />}
                  </button>
                </li>
              )
            })}
          </ol>
        </Field>
      </section>

      <section className="exo-profile-editor-block is-showcase">
        <h3 className="exo-section-label">Showcase</h3>
        {showcaseGames.length > 0 ? (
          <ol className="exo-profile-picks">
            {showcaseGames.map((game, index) => (
              <li
                key={game.id}
                className={cn('exo-profile-pick', draggedShowcase === game.id && 'is-dragging')}
                draggable
                onDragStart={() => setDraggedShowcase(game.id)}
                onDragEnd={() => setDraggedShowcase(null)}
                onDragOver={(event) => event.preventDefault()}
                onDrop={() => dropShowcase(game.id)}
              >
                <button
                  type="button"
                  className="exo-profile-drag-handle"
                  aria-label={`Reorder ${game.title}`}
                  onKeyDown={(event) => {
                    if (event.key === 'ArrowUp') { event.preventDefault(); onMovePick(index, -1) }
                    if (event.key === 'ArrowDown') { event.preventDefault(); onMovePick(index, 1) }
                  }}
                >
                  <DotsSixVerticalIcon size={16} aria-hidden />
                </button>
                <span className="exo-profile-pick-art">
                  <CoverArt game={game} className="h-full w-full" />
                </span>
                <span className="min-w-0 flex-1 truncate text-[13px]">{game.title}</span>
                <button
                  type="button"
                  className="exo-profile-visibility-btn is-compact"
                  aria-label={`Remove ${game.title} from showcase`}
                  onClick={() => onToggleShowcase(game.id)}
                >
                  <XIcon size={16} aria-hidden />
                </button>
              </li>
            ))}
          </ol>
        ) : (
          <p className="exo-profile-note">Pick up to ten games from your library.</p>
        )}
        <div className="exo-profile-picker-wrap">
          <ArtPicker
            games={games}
            isOn={(id) => showcaseIds.includes(id)}
            onPick={onToggleShowcase}
            label="Search your library"
          />
        </div>
      </section>
    </div>
  )
}

/** Scrolling library-cover picker for the showcase. */
function ArtPicker({
  games,
  isOn,
  onPick,
  label,
}: {
  games: Game[]
  isOn: (id: string) => boolean
  onPick: (id: string) => void
  label: string
}) {
  const [query, setQuery] = useState('')
  const needle = query.trim().toLowerCase()
  const matches = needle
    ? games.filter((game) => game.title.toLowerCase().includes(needle))
    : games
  const visible = matches.slice(0, PICK_GRID_MAX)

  return (
    <div className="exo-picker-shell">
      {games.length > PICK_SEARCH_MIN ? (
        <div className="exo-picker-head">
          <input
            className="exo-field exo-picker-search"
            value={query}
            onChange={(event) => setQuery(event.target.value)}
            placeholder={label}
            aria-label={label}
            spellCheck={false}
          />
        </div>
      ) : null}

      <div className="exo-picker">
        {visible.map((game) => (
          <button
            key={game.id}
            type="button"
            className={cn('exo-picker-item', isOn(game.id) && 'is-on')}
            aria-pressed={isOn(game.id)}
            aria-label={game.title}
            onClick={() => onPick(game.id)}
          >
            <CoverArt game={game} className="h-full w-full" />
            <span className="exo-picker-label">{game.title}</span>
            {isOn(game.id) ? (
              <span className="exo-picker-mark" aria-hidden>
                <Check size={14} />
              </span>
            ) : null}
          </button>
        ))}
      </div>

      {matches.length > visible.length ? (
        <p className="exo-picker-more">
          Showing {visible.length} of {matches.length}. Search to narrow it.
        </p>
      ) : null}
      {games.length === 0 ? <p className="exo-picker-more">No library art yet.</p> : null}
    </div>
  )
}

function Choice<T extends string>({
  value,
  options,
  disabled,
  onPick,
}: {
  value: string
  options: ReadonlyArray<[T, string]>
  disabled?: boolean
  onPick: (value: T) => void
}) {
  return (
    <div className="exo-profile-choice" role="group">
      {options.map(([key, label]) => (
        <button
          key={key}
          type="button"
          className={cn('exo-profile-choice-btn', value === key && 'is-on')}
          aria-pressed={value === key}
          disabled={disabled}
          onClick={() => onPick(key)}
        >
          {label}
        </button>
      ))}
    </div>
  )
}

function Field({
  label,
  htmlFor,
  hint,
  children,
}: {
  label: string
  htmlFor?: string
  hint?: string | null
  children: ReactNode
}) {
  const labelId = useId()
  return (
    <div
      className="exo-profile-field"
      role={htmlFor ? undefined : 'group'}
      aria-labelledby={htmlFor ? undefined : labelId}
    >
      <span className="exo-profile-field-head">
        {htmlFor ? (
          <label id={labelId} className="exo-section-label" htmlFor={htmlFor}>{label}</label>
        ) : (
          <span id={labelId} className="exo-section-label">{label}</span>
        )}
        {hint ? <span className="exo-profile-hint">{hint}</span> : null}
      </span>
      {children}
    </div>
  )
}

/**
 * Same dismiss pattern as the game overlay: a real Close scrim, Escape on the
 * window, focus on the dialog. No backdrop-filter — this is a picture, not a card.
 */
function AvatarLightbox({
  open,
  instant,
  photo,
  onClose,
  children,
}: {
  open: boolean
  instant: boolean
  photo: boolean
  onClose: (instant: boolean) => void
  children: ReactNode
}) {
  const reduce = useReducedMotion()
  const ref = useRef<HTMLDivElement>(null)
  const closeRef = useRef<HTMLButtonElement>(null)
  const returnFocusRef = useRef<HTMLElement | null>(null)

  useEffect(() => {
    if (!open) return
    returnFocusRef.current = document.activeElement instanceof HTMLElement ? document.activeElement : null
    closeRef.current?.focus({ preventScroll: true })
    return () => {
      returnFocusRef.current?.focus({ preventScroll: true })
      returnFocusRef.current = null
    }
  }, [open])

  useEffect(() => {
    if (!open) return
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        event.preventDefault()
        onClose(true)
        return
      }
      if (event.key !== 'Tab') return
      const focusable = Array.from(
        ref.current?.querySelectorAll<HTMLElement>('button:not([disabled]):not([tabindex="-1"]), [href], [tabindex]:not([tabindex="-1"])') ?? [],
      )
      if (focusable.length === 0) {
        event.preventDefault()
        ref.current?.focus({ preventScroll: true })
        return
      }
      const first = focusable[0]
      const last = focusable[focusable.length - 1]
      if (event.shiftKey && (document.activeElement === first || document.activeElement === ref.current)) {
        event.preventDefault()
        last.focus({ preventScroll: true })
      } else if (!event.shiftKey && document.activeElement === last) {
        event.preventDefault()
        first.focus({ preventScroll: true })
      }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [open, onClose])

  const chrome = (
    <>
      <button
        type="button"
        className="exo-profile-lightbox-scrim"
        tabIndex={-1}
        aria-label="Close profile picture"
        onClick={() => onClose(false)}
      />
      <button
        ref={closeRef}
        type="button"
        className="exo-profile-lightbox-close"
        onClick={() => onClose(false)}
      >
        Close
      </button>
    </>
  )

  if (reduce || instant) {
    return open ? (
      <div
        ref={ref}
        className="exo-profile-lightbox"
        role="dialog"
        aria-modal="true"
        aria-label="Profile picture"
        tabIndex={-1}
      >
        {chrome}
        <div className={cn('exo-profile-lightbox-stage', photo ? 'is-photo' : 'is-art')}>{children}</div>
      </div>
    ) : null
  }

  return (
    <AnimatePresence>
      {open ? (
        <motion.div
          key="avatar-lightbox"
          ref={ref}
          className="exo-profile-lightbox"
          role="dialog"
          aria-modal="true"
          aria-label="Profile picture"
          tabIndex={-1}
          initial={false}
        >
          {chrome}
          <motion.div
            className={cn('exo-profile-lightbox-stage', photo ? 'is-photo' : 'is-art')}
            initial={{ opacity: 0, transform: 'scale(0.96)' }}
            animate={{ opacity: 1, transform: 'scale(1)' }}
            exit={{ opacity: 0, transform: 'scale(0.97)' }}
            transition={{ duration: 0.16, ease: [0.23, 1, 0.32, 1] }}
          >
            {children}
          </motion.div>
        </motion.div>
      ) : null}
    </AnimatePresence>
  )
}
