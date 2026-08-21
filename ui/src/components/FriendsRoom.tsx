import { useCallback, useEffect, useMemo, useState } from 'react'
import type { KeyboardEvent as ReactKeyboardEvent } from 'react'
import { Download } from '../brand/icons'
import {
  host,
  hostedBuyUrl,
  onHostEvent,
  type ExoPerson,
  type FriendSource,
  type FriendsResponse,
  type Game,
  type HostFriend,
  type OnlineDiagnostics,
  type OnlineBlock,
  type OnlineFriend,
  type OnlineFriendRequestPage,
  type OnlinePresenceEntry,
  type OnlineProfileMedia,
  type OnlinePresenceEvent,
  type OnlinePublicProfile,
} from '../lib/host'
import { CACHE_KEYS, peekCache, writeCache } from '../lib/cache'
import {
  CACHE_PRESENCE_NOTE,
  PRESENCE_LABEL,
  PROFILE_LIMITS,
  friendPresence,
  friendPlayingAction,
  groupFriends,
  handleProblem,
  highestQualityAvatarUrl,
  lastSeenLabel,
  normalizeHandle,
  presenceOf,
  steamPlayingCoverUrl,
  type FriendPlayingAction,
} from '../lib/social'
import { ggDealsUrl } from '../lib/stores'
import { applyPresenceEvent, downgradePresenceRoster, projectPresenceRoster } from '../lib/presence'
import { cn, monogram, storeLabel } from '../lib/utils'
import { CoverArt, HeroWash } from './CoverArt'
import { ServerBadgeRow } from './ProfileRoom'

/** Opens Settings → Stores and scrolls to the Steam Web API key row. */
function openSteamWebApiKeySettings() {
  document.querySelector<HTMLButtonElement>('[aria-label="Open settings"]')?.click()
  window.setTimeout(() => {
    const tab = document.getElementById('set-tab-stores')
    if (tab instanceof HTMLButtonElement) tab.click()
    window.setTimeout(() => {
      document
        .querySelector('[aria-label="Steam Web API key"]')
        ?.closest('.exo-set-row')
        ?.scrollIntoView({ block: 'center' })
    }, 50)
  }, 50)
}

/**
 * People, from two sources that are never mixed.
 *
 * "Exo" merges accepted online friends with the local fallback roster. Local
 * entries remain available signed out; only server friends carry requests,
 * public profiles, and privacy-safe Exo presence.
 *
 * "From your stores" is every store Exo can read. Steam names come from the
 * local cache; live status needs a Steam Web API key the user pasted.
 * personastate 0 is offline; a private row with no state stays unknown.
 * Epic is last-seen only. A Steam live answer
 * never makes an Epic row look live.
 */

type Source = 'exo' | 'stores'

type MergedPerson = ExoPerson & {
  onlineUserId?: string
  onlineSources?: string[]
  onlineHandleDisplay?: string | null
  onlineAvatarUrl?: string | null
  onlineOnly?: boolean
}

function mergedPersonLabel(person: MergedPerson): string {
  return person.name?.trim() || person.onlineHandleDisplay?.trim() ||
    (person.handle ? `@${person.handle}` : 'Exo connection')
}

function sortMergedPeople<T extends MergedPerson>(people: readonly T[]): T[] {
  return [...people].sort((a, b) =>
    mergedPersonLabel(a).localeCompare(mergedPersonLabel(b), undefined, { sensitivity: 'base' }),
  )
}

function mergeOnlinePeople(local: ExoPerson[], online: OnlineFriend[]): MergedPerson[] {
  const merged = local.map((person) => ({ ...person } as MergedPerson))
  for (const friend of online) {
    const normalized = friend.handle?.normalized?.trim().toLowerCase() ?? ''
    const existing = normalized
      ? merged.find((person) => person.handle.trim().toLowerCase() === normalized)
      : undefined
    if (existing) {
      existing.onlineUserId = friend.userId
      existing.onlineSources = [...friend.sources]
      existing.onlineHandleDisplay = friend.handle?.display ?? null
      existing.onlineAvatarUrl = friend.avatarUrl ?? null
      continue
    }
    merged.push({
      id: `online:${friend.userId}`,
      handle: normalized,
      name: null,
      note: null,
      addedUtc: friend.connectedAt ?? null,
      links: [],
      onlineUserId: friend.userId,
      onlineSources: [...friend.sources],
      onlineHandleDisplay: friend.handle?.display ?? null,
      onlineAvatarUrl: friend.avatarUrl ?? null,
      onlineOnly: true,
    })
  }
  return sortMergedPeople(merged)
}

const SOURCE_TAB_IDS: Record<Source, string> = {
  exo: 'friends-tab-exo',
  stores: 'friends-tab-stores',
}

const SOURCE_PANEL_IDS: Record<Source, string> = {
  exo: 'friends-panel-exo',
  stores: 'friends-panel-stores',
}

/** Names the store a row came from, so it never claims the wrong client. */
function sourceLabel(source: string | null | undefined): string {
  return source ? storeLabel(source) : 'Store'
}

/** Keep a playing row's art stable even when Steam names an app Exo has not
 * imported yet. The fallback is display-only; it never creates an installable
 * library entry or changes the privacy/presence decision. */
function friendArtForAction(action: FriendPlayingAction | null): Game | null {
  if (!action) return null
  if (action.game) return action.game as Game
  if (!action.steamAppId) return null
  return {
    id: `steam:${action.steamAppId}`,
    title: action.title,
    store: 'steam',
    installed: false,
    owned: false,
    primaryAction: 'none',
    coverUrl: steamPlayingCoverUrl(action.steamAppId),
    coverSource: 'steam-friend-cdn',
    status: 'Store',
    deps: [],
    launchNote: '',
  }
}

/** Store notes already name the store. The label sits beside them — do not print it twice. */
function noteWithoutStorePrefix(store: string, note: string): string {
  const label = sourceLabel(store)
  const text = note.trim()
  if (!text.toLowerCase().startsWith(label.toLowerCase())) return text
  return text.slice(label.length).replace(/^(?:[:—–-]\s*|\s+)/, '').trimStart()
}

/** A cache is a names/last-seen snapshot, never continuing presence authority. */
function staleFriendsResponse(result: FriendsResponse): FriendsResponse {
  return {
    ...result,
    live: false,
    note: CACHE_PRESENCE_NOTE,
    friends: (result.friends ?? []).map((friend) => ({
      ...friend,
      status: 'unknown',
      statusText: null,
      playingId: null,
      playingTitle: null,
      live: false,
      presenceFrom: null,
    })),
    sources: (result.sources ?? []).map((source) => ({
      ...source,
      live: false,
      note: CACHE_PRESENCE_NOTE,
    })),
    activeCount: 0,
  }
}

export function FriendsRoom({ active }: { active: boolean }) {
  const [people, setPeople] = useState<ExoPerson[] | null>(null)
  const [onlineFriends, setOnlineFriends] = useState<OnlineFriend[]>([])
  const [onlineDiagnostics, setOnlineDiagnostics] = useState<OnlineDiagnostics | null>(null)
  const [requests, setRequests] = useState<OnlineFriendRequestPage>({ incoming: [], outgoing: [] })
  const [blocks, setBlocks] = useState<OnlineBlock[]>([])
  const [presence, setPresence] = useState<Record<string, OnlinePresenceEntry>>({})
  const [publicProfile, setPublicProfile] = useState<OnlinePublicProfile | null>(null)
  const [profileBusy, setProfileBusy] = useState(false)
  const [profileProblem, setProfileProblem] = useState<string | null>(null)
  const [onlineBusy, setOnlineBusy] = useState<string | null>(null)
  const [rosterNote, setRosterNote] = useState<string | null>(null)
  const [friends, setFriends] = useState<HostFriend[] | null>(null)
  const [sources, setSources] = useState<FriendSource[]>([])
  const [storeNote, setStoreNote] = useState<string | null>(null)
  const [activeCount, setActiveCount] = useState(0)
  const [games, setGames] = useState<Game[]>([])
  const [source, setSource] = useState<Source>('exo')
  const [query, setQuery] = useState('')
  const [selectedPersonId, setSelectedPersonId] = useState<string | null>(null)
  const [selectedFriendId, setSelectedFriendId] = useState<string | null>(null)
  const [adding, setAdding] = useState(false)
  const [message, setMessage] = useState<string | null>(null)
  const [steamKeySet, setSteamKeySet] = useState<boolean | null>(null)

  useEffect(() => {
    if (!message) return
    const timer = window.setTimeout(() => setMessage(null), 4200)
    return () => window.clearTimeout(timer)
  }, [message])

  const loadRoster = useCallback(async () => {
    const result = await host.friendsRoster()
    setPeople(result.people ?? [])
    setRosterNote(result.note ?? null)
  }, [])

  const loadOnline = useCallback(async () => {
    const [friendsResult, requestsResult, blocksResult, presenceResult] = await Promise.allSettled([
      host.onlineFriends(),
      host.onlineFriendRequests(),
      host.onlineBlocks(),
      host.onlinePresence(),
    ])
    if (friendsResult.status === 'fulfilled') {
      setOnlineDiagnostics(friendsResult.value.diagnostics)
      if (friendsResult.value.value) setOnlineFriends(friendsResult.value.value.friends ?? [])
      else if (friendsResult.value.diagnostics.signedIn === false) setOnlineFriends([])
    } else {
      setOnlineDiagnostics((current) => current ?? {
        configured: true,
        signedIn: null,
        source: 'unavailable',
        lastSuccessfulSync: null,
        retryable: true,
        error: {
          code: 'TRANSPORT_UNAVAILABLE',
          message: 'Exo friends could not be refreshed.',
        },
      })
    }
    if (requestsResult.status === 'fulfilled') {
      if (requestsResult.value.value) setRequests(requestsResult.value.value)
      else if (requestsResult.value.diagnostics.signedIn === false) setRequests({ incoming: [], outgoing: [] })
    }
    if (blocksResult.status === 'fulfilled') {
      if (blocksResult.value.value) setBlocks(blocksResult.value.value.blocks ?? [])
      else if (blocksResult.value.diagnostics.signedIn === false) setBlocks([])
    }
    if (presenceResult.status === 'fulfilled') {
      const result = presenceResult.value
      if (result.diagnostics.signedIn === false) {
        setPresence({})
      } else if (result.value) {
        const roster = result.value
        const rows = roster.friends ?? []
        if (roster.unavailable && rows.length === 0) {
          setPresence((current) => downgradePresenceRoster(current))
        } else {
          setPresence(projectPresenceRoster(rows))
        }
      } else {
        setPresence((current) => downgradePresenceRoster(current))
      }
    } else {
      setPresence((current) => downgradePresenceRoster(current))
    }
  }, [])

  const applyFriends = useCallback((result: FriendsResponse) => {
    setFriends(result.friends ?? [])
    setSources(result.sources ?? [])
    setStoreNote(result.note ?? null)
    setActiveCount(result.activeCount ?? 0)
  }, [])

  const loadFriends = useCallback(async (live = false) => {
    const result = await host.friendsList(live)
    applyFriends(result)
    writeCache(CACHE_KEYS.friends, staleFriendsResponse(result))
  }, [applyFriends])

  const applyFriendsFailure = useCallback((error: unknown) => {
    const cached = peekCache<FriendsResponse>(CACHE_KEYS.friends)
    if (cached) {
      applyFriends(staleFriendsResponse(cached))
      return
    }
    applyFriends({
      ok: false,
      live: false,
      note: error instanceof Error ? error.message : 'Store friends could not be read.',
      friends: [],
      sources: [],
      count: 0,
      activeCount: 0,
    })
  }, [applyFriends])

  const loadFriendsCacheFirst = useCallback(
    async (isCancelled: () => boolean) => {
      try {
        await loadFriends(false)
      } catch (error) {
        applyFriendsFailure(error)
      }
      if (isCancelled()) return
      void loadFriends(true).catch(applyFriendsFailure)
    },
    [applyFriendsFailure, loadFriends],
  )

  useEffect(() => {
    void loadRoster().catch((error: unknown) => {
      setPeople([])
      setMessage(error instanceof Error ? error.message : 'The roster could not be read.')
    })
  }, [loadRoster])

  // Show the last answers straight away. The active-room effect below owns the
  // live refresh, so a hidden room does not make a presence request.
  useEffect(() => {
    let mounted = true
    const cachedFriends = peekCache<FriendsResponse>(CACHE_KEYS.friends)
    if (cachedFriends) applyFriends(staleFriendsResponse(cachedFriends))
    const cachedGames = peekCache<Game[]>(CACHE_KEYS.library)
    if (cachedGames) setGames(cachedGames)
    void host
      .getLibrary()
      .then((result) => {
        const rows = result.games ?? []
        writeCache(CACHE_KEYS.library, rows)
        if (mounted) setGames(rows)
      })
      .catch(() => {})
    return () => {
      mounted = false
    }
  }, [applyFriends])

  // Steam's Web API is requested only while this room is on screen.
  useEffect(() => {
    if (!active) return
    let cancelled = false
    void loadFriendsCacheFirst(() => cancelled)
    void host
      .getSettings()
      .then((settings) => setSteamKeySet(settings.steamWebApiKeySet === true))
      .catch(() => setSteamKeySet(false))
    const timer = window.setInterval(() => {
      void host
        .getSettings()
        .then((settings) => {
          const on = settings.steamWebApiKeySet === true
          setSteamKeySet(on)
          if (on) void loadFriends(true).catch(applyFriendsFailure)
        })
        .catch(() => {})
    }, 30_000)
    return () => {
      cancelled = true
      window.clearInterval(timer)
    }
  }, [active, applyFriendsFailure, loadFriends, loadFriendsCacheFirst])

  useEffect(() => {
    if (!active) return
    let cancelled = false
    void loadOnline().catch((error: unknown) => {
      if (cancelled) return
      setPresence((current) => downgradePresenceRoster(current))
      setOnlineDiagnostics((current) => current ?? {
        configured: false,
        signedIn: null,
        source: 'unavailable',
        lastSuccessfulSync: null,
        retryable: true,
        error: {
          code: 'TRANSPORT_UNAVAILABLE',
          message: error instanceof Error ? error.message : 'Online friends could not be refreshed.',
        },
      })
    })
    return () => {
      cancelled = true
    }
  }, [active, loadOnline])

  useEffect(
    () =>
      onHostEvent('online.presence', (data) => {
        const event = data as OnlinePresenceEvent
        if (!event?.kind) return
        setPresence((current) => applyPresenceEvent(current, event))
      }),
    [],
  )

  useEffect(
    () =>
      onHostEvent('library.updated', (data) => {
        const payload = data as { games?: Game[] }
        if (Array.isArray(payload?.games)) setGames(payload.games)
      }),
    [],
  )

  useEffect(() => {
    if (!selectedPersonId && !selectedFriendId) return
    function onKey(event: KeyboardEvent) {
      if (event.key !== 'Escape') return
      event.preventDefault()
      setSelectedPersonId(null)
      setSelectedFriendId(null)
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [selectedPersonId, selectedFriendId])

  const mergedPeople = useMemo(
    () => mergeOnlinePeople(people ?? [], onlineFriends),
    [onlineFriends, people],
  )
  const q = query.trim().toLowerCase()
  const visiblePeople = useMemo(
    () =>
      sortMergedPeople(
        mergedPeople.filter(
          (person) =>
            !q ||
            person.handle.includes(q) ||
            (person.onlineHandleDisplay ?? '').toLowerCase().includes(q) ||
            (person.name ?? '').toLowerCase().includes(q),
        ),
      ),
    [mergedPeople, q],
  )
  const visibleFriends = useMemo(
    () => (friends ?? []).filter((friend) => !q || friend.name.toLowerCase().includes(q)),
    [friends, q],
  )

  const friendArtGames = useMemo(() => {
    const candidates = [
      ...(friends ?? []).map((friend) =>
        friend.live === true
          ? friendArtForAction(friendPlayingAction(friend.playingId, friend.playingTitle, games))
          : null,
      ),
      ...onlineFriends.map((friend) => {
        const peer = presence[friend.userId]
        return peer?.available && peer.status === 'ingame'
          ? friendArtForAction(friendPlayingAction(peer.gameId, peer.gameTitle, games))
          : null
      }),
    ]
    const seen = new Set<string>()
    return candidates.filter((game): game is Game => {
      if (!game || seen.has(game.id)) return false
      seen.add(game.id)
      return true
    })
  }, [friends, games, onlineFriends, presence])

  useEffect(() => {
    if (
      source === 'exo' &&
      selectedPersonId &&
      !visiblePeople.some((person) => person.id === selectedPersonId)
    ) {
      setSelectedPersonId(null)
    }
    if (
      source === 'stores' &&
      selectedFriendId &&
      !visibleFriends.some((friend) => friend.id === selectedFriendId)
    ) {
      setSelectedFriendId(null)
    }
  }, [selectedFriendId, selectedPersonId, source, visibleFriends, visiblePeople])

  const selectedPerson = selectedPersonId
    ? mergedPeople.find((person) => person.id === selectedPersonId) ?? null
    : null
  const selectedFriend = selectedFriendId
    ? (friends ?? []).find((friend) => friend.id === selectedFriendId) ?? null
    : null

  useEffect(() => {
    const userId = selectedPerson?.onlineUserId
    const handle = (selectedPerson?.handle || selectedPerson?.onlineHandleDisplay || '').trim()
    if (!userId || !handle) {
      setPublicProfile(null)
      setProfileBusy(false)
      setProfileProblem(null)
      return
    }
    let cancelled = false
    setProfileBusy(true)
    setProfileProblem(null)
    void host.onlineProfile(handle, userId).then(
      (result) => {
        if (cancelled) return
        setPublicProfile(result.value ?? null)
        setProfileProblem(
          result.value
            ? result.diagnostics.error?.message ?? null
            : result.diagnostics.error?.message ?? 'That profile is not available.',
        )
        setProfileBusy(false)
      },
      (error: unknown) => {
        if (cancelled) return
        setPublicProfile(null)
        setProfileProblem(error instanceof Error ? error.message : 'That profile is not available.')
        setProfileBusy(false)
      },
    )
    return () => {
      cancelled = true
    }
  }, [selectedPerson?.handle, selectedPerson?.onlineHandleDisplay, selectedPerson?.onlineUserId])

  const running = games.find((game) => game.isRunning || game.canStop) ?? null
  const onlineAvailable =
    onlineDiagnostics?.configured === true && onlineDiagnostics.signedIn !== false

  function switchSource(next: Source) {
    setSource(next)
    setQuery('')
    setMessage(null)
  }

  function handleSourceTabKeyDown(event: ReactKeyboardEvent<HTMLButtonElement>) {
    let next: Source | null = null
    if (event.key === 'ArrowLeft') next = source === 'exo' ? 'stores' : 'exo'
    if (event.key === 'ArrowRight') next = source === 'stores' ? 'exo' : 'stores'
    if (event.key === 'Home') next = 'exo'
    if (event.key === 'End') next = 'stores'
    if (!next) return

    event.preventDefault()
    switchSource(next)
    document.getElementById(SOURCE_TAB_IDS[next])?.focus()
  }

  async function addPerson(handle: string, name: string, note: string) {
    try {
      if (onlineAvailable) {
        const result = await host.onlineFriendRequest(handle)
        setOnlineDiagnostics(result.diagnostics)
        setMessage(result.ok ? 'Friend request sent.' : result.diagnostics.error?.message ?? 'Could not send that request.')
        if (result.ok) await loadOnline()
        return result.ok
      }
      const result = await host.friendsAdd(handle, name, note)
      setPeople(result.people ?? [])
      setMessage(result.ok ? null : result.message ?? 'Could not add that handle.')
      return result.ok
    } catch (error) {
      setMessage(error instanceof Error ? error.message : 'Could not add that handle.')
      return false
    }
  }

  async function removePerson(person: MergedPerson) {
    try {
      if (person.onlineUserId) {
        const result = await host.onlineFriendRemove(person.onlineUserId)
        setOnlineDiagnostics(result.diagnostics)
        setSelectedPersonId(null)
        setMessage(result.ok ? null : result.diagnostics.error?.message ?? 'Could not remove that friend.')
        if (result.ok) await loadOnline()
        return
      }
      const result = await host.friendsRemove(person.id)
      setPeople(result.people ?? [])
      setSelectedPersonId(null)
      setMessage(result.ok ? null : result.message ?? 'Could not remove that person.')
      // Their claimed store rows come back to the store list.
      if (result.ok) await loadFriends(true)
    } catch (error) {
      setMessage(error instanceof Error ? error.message : 'Could not remove that person.')
    }
  }

  async function decideRequest(requestId: string, decision: 'accept' | 'decline') {
    if (onlineBusy) return
    setOnlineBusy(`${decision}:${requestId}`)
    setMessage(null)
    try {
      const result = decision === 'accept'
        ? await host.onlineFriendAccept(requestId)
        : await host.onlineFriendDecline(requestId)
      setOnlineDiagnostics(result.diagnostics)
      setMessage(result.ok ? null : result.diagnostics.error?.message ?? `Could not ${decision} that request.`)
      await loadOnline()
    } catch (error) {
      setMessage(error instanceof Error ? error.message : `Could not ${decision} that request.`)
    } finally {
      setOnlineBusy(null)
    }
  }

  async function setBlocked(person: MergedPerson, blocked: boolean) {
    if (!person.onlineUserId || onlineBusy) return
    setOnlineBusy(`block:${person.onlineUserId}`)
    setMessage(null)
    try {
      const result = blocked
        ? await host.onlineBlock(person.onlineUserId)
        : await host.onlineUnblock(person.onlineUserId)
      setOnlineDiagnostics(result.diagnostics)
      setMessage(result.ok ? null : result.diagnostics.error?.message ?? 'That block change did not complete.')
      if (result.ok) {
        await loadOnline()
        setSelectedPersonId(null)
      }
    } catch (error) {
      setMessage(error instanceof Error ? error.message : 'That block change did not complete.')
    } finally {
      setOnlineBusy(null)
    }
  }

  async function savePersonNote(id: string, note: string) {
    try {
      const result = await host.friendsSetNote(id, note)
      setPeople(result.people ?? [])
      setMessage(result.ok ? null : result.message ?? 'Could not save that note.')
    } catch (error) {
      setMessage(error instanceof Error ? error.message : 'Could not save that note.')
    }
  }

  /** Linking moves a row between the two lists, so both are re-read. */
  async function linkPerson(personId: string, friendId: string) {
    try {
      const result = await host.friendsLink(personId, friendId)
      setPeople(result.people ?? [])
      setMessage(result.ok ? null : result.message ?? 'Could not link that account.')
      if (!result.ok) return false
      setSelectedFriendId(null)
      await loadFriends(true)
      return true
    } catch (error) {
      setMessage(error instanceof Error ? error.message : 'Could not link that account.')
      return false
    }
  }

  async function unlinkPerson(personId: string, friendId: string) {
    try {
      const result = await host.friendsUnlink(personId, friendId)
      setPeople(result.people ?? [])
      setMessage(result.ok ? null : result.message ?? 'Could not unlink that account.')
      if (result.ok) await loadFriends(true)
    } catch (error) {
      setMessage(error instanceof Error ? error.message : 'Could not unlink that account.')
    }
  }

  const exoCount = mergedPeople.length
  const storeCount = friends?.length ?? 0
  const sourceExplanations = sources.filter(
    (entry) =>
      entry.count === 0 ||
      (friends ?? []).some(
        (friend) => friend.source === entry.store && friendPresence(friend) === 'unknown',
      ),
  )
  const steamNeedsKey =
    steamKeySet === false &&
    sources.some((entry) => entry.store === 'steam' && entry.count > 0 && !entry.live)

  return (
    <>
      <div className="exo-art-preload exo-friend-art-preload" aria-hidden="true" inert>
        {friendArtGames.slice(0, 12).map((game) => (
          <div key={game.id} className="exo-art-preload-item">
            <CoverArt game={game} preload />
            <HeroWash game={game} className="exo-friend-art-preload-hero" />
          </div>
        ))}
      </div>
    <main className="exo-friends">
      <aside className="exo-friend-list">
        <div className="exo-friend-list-head">
          <div className="mb-3">
            <h2 className="exo-section-label">People</h2>
          </div>

          <div className="exo-roster-tabs" role="tablist" aria-label="People source">
            <button
              type="button"
              data-controller-target=""
              data-controller-safe=""
              id={SOURCE_TAB_IDS.exo}
              role="tab"
              aria-selected={source === 'exo'}
              aria-controls={SOURCE_PANEL_IDS.exo}
              tabIndex={source === 'exo' ? 0 : -1}
              className={cn('exo-roster-tab', source === 'exo' && 'is-on')}
              onClick={() => switchSource('exo')}
              onKeyDown={handleSourceTabKeyDown}
            >
              Exo
              <span className="exo-roster-tab-count tabular-nums">{exoCount}</span>
            </button>
            <button
              type="button"
              data-controller-target=""
              data-controller-safe=""
              id={SOURCE_TAB_IDS.stores}
              role="tab"
              aria-selected={source === 'stores'}
              aria-controls={SOURCE_PANEL_IDS.stores}
              tabIndex={source === 'stores' ? 0 : -1}
              className={cn('exo-roster-tab', source === 'stores' && 'is-on')}
              onClick={() => switchSource('stores')}
              onKeyDown={handleSourceTabKeyDown}
            >
              Stores
              <span className="exo-roster-tab-count tabular-nums">{storeCount}</span>
            </button>
          </div>

          {(source === 'exo' ? exoCount : storeCount) > 8 ? (
            <input
              className="exo-field mt-3"
              value={query}
              onChange={(event) => setQuery(event.target.value)}
              placeholder={source === 'exo' ? 'Search your roster' : 'Search store names'}
              aria-label="Search people"
              spellCheck={false}
            />
          ) : null}

          {source === 'stores' && steamNeedsKey ? (
            <p className="exo-roster-steam-note">
              Steam status needs a Web API key.{' '}
              <button
                type="button"
                className="exo-roster-steam-go"
                aria-label="Open Steam Web API key in Settings"
                onClick={openSteamWebApiKeySettings}
              >
                Settings
              </button>
            </p>
          ) : null}
        </div>

        <div className="exo-friend-list-body">
          <div
            id={SOURCE_PANEL_IDS.exo}
            role="tabpanel"
            aria-labelledby={SOURCE_TAB_IDS.exo}
            hidden={source !== 'exo'}
            className="mt-3"
          >
            {adding ? (
              <AddPersonForm
                online={onlineAvailable}
                onCancel={() => {
                  setAdding(false)
                  setMessage(null)
                }}
                onAdd={async (handle, name, note) => {
                  const ok = await addPerson(handle, name, note)
                  if (ok) setAdding(false)
                  return ok
                }}
              />
            ) : (
              <button
                type="button"
                className="exo-roster-add"
                onClick={() => {
                  setMessage(null)
                  setAdding(true)
                }}
              >
                Add someone
              </button>
            )}

            {message ? <p className="exo-roster-message" role="status" aria-live="polite">{message}</p> : null}

            <OnlineRequests
              requests={requests}
              busy={onlineBusy}
              available={onlineAvailable}
              diagnostics={onlineDiagnostics}
              onDecision={(requestId, decision) => void decideRequest(requestId, decision)}
              onRetry={() => void loadOnline()}
            />

            {people === null ? (
              <p className="mt-3 text-[13px] text-faint">Reading your roster.</p>
            ) : exoCount === 0 ? (
              <p className="mt-3 text-[13px] leading-relaxed text-faint">
                {onlineAvailable
                  ? 'Nobody here yet. Search a reserved handle to send a request.'
                  : 'Nobody here yet. Add a local handle for offline use.'}
              </p>
            ) : visiblePeople.length === 0 ? (
              <p className="mt-3 text-[13px] text-faint">No handle matches that.</p>
            ) : (
              <div className="exo-friend-group mt-2">
                {visiblePeople.map((person) => (
                    <PersonRow
                      key={person.id}
                      person={person}
                      games={games}
                    selected={person.id === selectedPersonId}
                    onSelect={(id) => setSelectedPersonId((cur) => (cur === id ? null : id))}
                    presence={person.onlineUserId ? presence[person.onlineUserId] : undefined}
                  />
                ))}
              </div>
            )}
          </div>
          <div
            id={SOURCE_PANEL_IDS.stores}
            role="tabpanel"
            aria-labelledby={SOURCE_TAB_IDS.stores}
            hidden={source !== 'stores'}
            className="mt-3"
          >
            {message ? <p className="exo-roster-message">{message}</p> : null}

            {friends === null ? (
              <p className="text-[13px] text-faint">Reading your stores.</p>
            ) : storeCount === 0 ? (
              <p className="text-[13px] leading-relaxed text-faint">
                {storeNote ?? 'Sign in to a store client once so Exo can read its list.'}
              </p>
            ) : visibleFriends.length === 0 ? (
              <p className="text-[13px] text-faint">No name matches that.</p>
            ) : (
              <StoreGroups
                friends={visibleFriends}
                sources={sources}
                games={games}
                selectedId={selectedFriendId}
                onSelect={(id) => setSelectedFriendId((cur) => (cur === id ? null : id))}
              />
            )}

            {sourceExplanations.length > 0 && storeCount > 0 ? (
              <div className="exo-presence-notes">
                {sourceExplanations.map((entry) => (
                  <p key={entry.store} className="exo-presence-note">
                    <span className="exo-presence-note-store">{sourceLabel(entry.store)}</span>
                    {' '}
                    {noteWithoutStorePrefix(entry.store, entry.note)}
                  </p>
                ))}
              </div>
            ) : null}
          </div>
        </div>
      </aside>

      <section className="exo-friends-detail">
        {source === 'exo' && selectedPerson ? (
          selectedPerson.onlineUserId ? (
            <OnlinePersonPage
              person={selectedPerson}
              profile={publicProfile}
              profileBusy={profileBusy}
              profileProblem={profileProblem}
              presence={presence[selectedPerson.onlineUserId]}
              games={games}
              blocked={blocks.some((block) => block.userId === selectedPerson.onlineUserId)}
              busy={onlineBusy !== null}
              onRemove={() => void removePerson(selectedPerson)}
              onBlock={(next) => void setBlocked(selectedPerson, next)}
            />
          ) : (
            <PersonPage
              person={selectedPerson}
              onRemove={() => void removePerson(selectedPerson)}
              onNote={(note) => void savePersonNote(selectedPerson.id, note)}
              onUnlink={(friendId) => void unlinkPerson(selectedPerson.id, friendId)}
            />
          )
        ) : source === 'stores' && selectedFriend ? (
          <FriendPage
            key={selectedFriend.id}
            friend={selectedFriend}
            live={selectedFriend.live === true}
            note={
              sources.find((entry) => entry.store === selectedFriend.source)?.note ?? storeNote
            }
            games={games}
            people={people ?? []}
            onLink={(personId) => linkPerson(personId, selectedFriend.id)}
          />
        ) : (
          <NowPanel
            source={source}
            exoCount={exoCount}
            storeCount={storeCount}
            activeCount={activeCount}
            sources={sources}
            rosterNote={rosterNote}
            onlineDiagnostics={onlineDiagnostics}
            storeNote={storeNote}
            running={running}
          />
        )}
      </section>
    </main>
    </>
  )
}

/** Steam can be live while Epic stays last-seen. Never mix those. */
function StoreGroups({
  friends,
  sources,
  games,
  selectedId,
  onSelect,
}: {
  friends: HostFriend[]
  sources: FriendSource[]
  games: Game[]
  selectedId: string | null
  onSelect: (id: string) => void
}) {
  const order = sources.length > 0 ? sources.map((entry) => entry.store) : ['steam']
  const seen = new Set(order)
  for (const friend of friends) {
    if (friend.source && !seen.has(friend.source)) {
      seen.add(friend.source)
      order.push(friend.source)
    }
  }

  return (
    <>
      {order.map((store) => {
        const rows = friends.filter((friend) => friend.source === store)
        if (rows.length === 0) return null
        return groupFriends(rows).map((group) => (
          <div key={`${store}-${group.key}`} className="exo-friend-group">
            <p className="exo-roster-sticky">
              {sourceLabel(store)} · {group.label} · {group.friends.length}
            </p>
            {group.friends.map((friend) => (
              <FriendRow
                key={friend.id}
                friend={friend}
                live={friend.live === true}
                games={games}
                selected={friend.id === selectedId}
                onSelect={onSelect}
              />
            ))}
          </div>
        ))
      })}
    </>
  )
}

function AddPersonForm({
  online,
  onAdd,
  onCancel,
}: {
  online: boolean
  onAdd: (handle: string, name: string, note: string) => Promise<boolean>
  onCancel: () => void
}) {
  const [handle, setHandle] = useState('')
  const [name, setName] = useState('')
  const [note, setNote] = useState('')
  const [busy, setBusy] = useState(false)
  const problem = handle.length > 0
    ? online && (handle.length < 3 || !/[a-z]/.test(handle))
      ? 'Reserved handles use 3–24 letters, digits, or underscore.'
      : handleProblem(handle)
    : null

  async function submit() {
    if (problem || handle.length === 0) return
    setBusy(true)
    try {
      const ok = await onAdd(handle, name, note)
      if (ok) {
        setHandle('')
        setName('')
        setNote('')
      }
    } finally {
      setBusy(false)
    }
  }

  return (
    <form
      className="exo-roster-form"
      onSubmit={(event) => {
        event.preventDefault()
        void submit()
      }}
    >
      <div className="exo-profile-handle-field">
        <span aria-hidden>@</span>
        <input
          className="exo-field"
          value={handle}
          maxLength={PROFILE_LIMITS.handle}
          placeholder="exo handle"
          aria-label="Exo handle"
          spellCheck={false}
          autoFocus
          onChange={(event) => setHandle(normalizeHandle(event.target.value))}
        />
      </div>
      {!online ? (
        <>
          <input
            className="exo-field"
            value={name}
            maxLength={PROFILE_LIMITS.name}
            placeholder="Name (optional)"
            aria-label="Name"
            spellCheck={false}
            onChange={(event) => setName(event.target.value)}
          />
          <input
            className="exo-field"
            value={note}
            maxLength={PROFILE_LIMITS.note}
            placeholder="Note (optional)"
            aria-label="Note"
            onChange={(event) => setNote(event.target.value)}
          />
        </>
      ) : null}
      {problem ? <p className="exo-roster-message">{problem}</p> : null}
      <div className="flex gap-2">
        <button
          type="submit"
          className="exo-ghost-btn is-primary"
          disabled={busy || handle.length === 0 || !!problem}
        >
          {busy ? (online ? 'Sending' : 'Adding') : online ? 'Send request' : 'Add locally'}
        </button>
        <button type="button" className="exo-ghost-btn" disabled={busy} onClick={onCancel}>
          Cancel
        </button>
      </div>
    </form>
  )
}

function OnlineRequests({
  requests,
  busy,
  available,
  diagnostics,
  onDecision,
  onRetry,
}: {
  requests: OnlineFriendRequestPage
  busy: string | null
  available: boolean
  diagnostics: OnlineDiagnostics | null
  onDecision: (requestId: string, decision: 'accept' | 'decline') => void
  onRetry: () => void
}) {
  const incoming = requests.incoming.filter((request) => request.status === 'pending')
  const outgoing = requests.outgoing.filter((request) => request.status === 'pending')
  const lastSync = diagnostics?.lastSuccessfulSync && !Number.isNaN(Date.parse(diagnostics.lastSuccessfulSync))
    ? new Date(diagnostics.lastSuccessfulSync).toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' })
    : null

  if (diagnostics === null && incoming.length === 0 && outgoing.length === 0) {
    return (
      <div className="exo-online-state" role="status">
        <span>Checking Exo friends…</span>
      </div>
    )
  }

  if (!available && incoming.length === 0 && outgoing.length === 0) {
    return (
      <div className="exo-online-state" role="status">
        <span>
          {diagnostics?.signedIn === false
            ? 'Sign in to send and receive Exo friend requests.'
            : diagnostics?.error?.message ?? 'Online friends are unavailable. Local people stay here.'}
        </span>
        {diagnostics?.retryable ? (
          <button type="button" className="exo-roster-steam-go" onClick={onRetry}>Retry</button>
        ) : null}
      </div>
    )
  }

  if (incoming.length === 0 && outgoing.length === 0) {
    return diagnostics ? (
      <p className="exo-online-source" role="status">
        Exo · {diagnostics.source}{lastSync ? ` · synced ${lastSync}` : ''}
      </p>
    ) : null
  }

  return (
    <section className="exo-online-requests" aria-label="Exo friend requests">
      {incoming.length > 0 ? <p className="exo-roster-sticky">Incoming · {incoming.length}</p> : null}
      {incoming.map((request) => {
        const label = request.user.handle?.display ?? 'Handle not claimed'
        return (
          <div key={request.id} className="exo-online-request">
            <span className="min-w-0 flex-1 truncate">{label}</span>
            <button
              type="button"
              className="exo-ghost-btn is-primary"
              disabled={busy !== null}
              onClick={() => onDecision(request.id, 'accept')}
            >
              {busy === `accept:${request.id}` ? 'Accepting' : 'Accept'}
            </button>
            <button
              type="button"
              className="exo-ghost-btn"
              disabled={busy !== null}
              onClick={() => onDecision(request.id, 'decline')}
            >
              Decline
            </button>
          </div>
        )
      })}
      {outgoing.length > 0 ? <p className="exo-roster-sticky">Outgoing · {outgoing.length}</p> : null}
      {outgoing.map((request) => (
        <p key={request.id} className="exo-online-outgoing">
          {request.user.handle?.display ?? 'Handle not claimed'} · pending
        </p>
      ))}
    </section>
  )
}

function Avatar({
  name,
  avatarUrl,
  large,
}: {
  name: string
  avatarUrl?: string | null
  large?: boolean
}) {
  // Cached avatar hashes go stale, so a 404 must fall back to initials rather
  // than leaving an empty disc.
  const [broken, setBroken] = useState(false)
  useEffect(() => setBroken(false), [avatarUrl])
  const src = highestQualityAvatarUrl(avatarUrl)
  const showImage = !!src && !broken
  return (
    <span className={cn('exo-avatar', large && 'is-lg')}>
      {showImage ? (
        <img
          src={src!}
          alt=""
          draggable={false}
          decoding="async"
          loading={large ? 'eager' : 'lazy'}
          fetchPriority={large ? 'high' : 'auto'}
          onError={() => setBroken(true)}
        />
      ) : (
        <span className="exo-avatar-mono" aria-hidden>
          {monogram(name)}
        </span>
      )}
    </span>
  )
}

/** The title a friend is in, only from host fields or a real library match. */
function playingTitle(friend: HostFriend, games: Game[]): string | null {
  const fromHost = friend.playingTitle?.trim()
  if (fromHost) return fromHost
  if (!friend.playingId) return null
  return games.find((game) => game.id === friend.playingId)?.title ?? null
}

function PersonRow({
  person,
  games,
  selected,
  onSelect,
  presence,
}: {
  person: MergedPerson
  games: Game[]
  selected: boolean
  onSelect: (id: string) => void
  presence?: OnlinePresenceEntry
}) {
  const links = person.links ?? []
  const presenceMeta = presence?.available
    ? presence.status === 'ingame'
      ? presence.gameTitle || 'In game'
      : PRESENCE_LABEL[presence.status]
    : null
  const meta = presenceMeta ?? (person.name?.trim()
    ? person.handle ? `@${person.handle}` : 'Exo friend'
    : person.note?.trim() || (person.onlineUserId ? 'Presence unavailable' : 'Local fallback'))
  const onlinePlaying = presence?.available && presence.status === 'ingame'
    ? friendPlayingAction(presence.gameId, presence.gameTitle, games)
    : null
  const artGame = friendArtForAction(onlinePlaying)

  return (
    <button
      type="button"
      data-controller-target=""
      data-controller-safe=""
      className={cn('exo-friend-row', selected && 'is-on')}
      aria-pressed={selected}
      onClick={() => onSelect(person.id)}
    >
      <Avatar name={mergedPersonLabel(person)} avatarUrl={person.onlineAvatarUrl} />
      <span className="min-w-0 flex-1">
        <span className="exo-friend-name block truncate">{mergedPersonLabel(person)}</span>
        <span className="exo-friend-meta block">{meta}</span>
      </span>
      {artGame ? (
        <span className="exo-friend-game-art" aria-hidden>
          <CoverArt game={artGame} preload />
        </span>
      ) : null}
      {links.length > 0 ? (
        <span className="exo-friend-stores">
          {links.map((link) => (
            <span key={link.id} className="exo-friend-store">
              {sourceLabel(link.store)}
            </span>
          ))}
        </span>
      ) : person.onlineSources?.length ? (
        <span className="exo-friend-stores">
          {person.onlineSources.slice(0, 2).map((source) => (
            <span key={source} className="exo-friend-store">{sourceLabel(source)}</span>
          ))}
        </span>
      ) : null}
    </button>
  )
}

function FriendRow({
  friend,
  live,
  games,
  selected,
  onSelect,
}: {
  friend: HostFriend
  live: boolean
  games: Game[]
  selected: boolean
  onSelect: (id: string) => void
}) {
  const presence = presenceOf(friend.status)
  // A playing title without live presence is a name, not a session.
  const playing = live ? playingTitle(friend, games) : null
  const statusText = friend.statusText?.trim() || null
  const seen = lastSeenLabel(friend.lastSeenUtc)
  const meta =
    playing ??
    (live
      ? presence === 'offline'
        ? seen ?? statusText ?? PRESENCE_LABEL[presence]
        : statusText ?? PRESENCE_LABEL[presence]
      : seen)
  const playingAction = live
    ? friendPlayingAction(friend.playingId, friend.playingTitle, games)
    : null
  const artGame = friendArtForAction(playingAction)

  return (
    <button
      type="button"
      data-controller-target=""
      data-controller-safe=""
      className={cn('exo-friend-row', selected && 'is-on')}
      aria-pressed={selected}
      onClick={() => onSelect(friend.id)}
    >
      <Avatar
        name={friend.name}
        avatarUrl={friend.avatarUrl}
      />
      <span className="min-w-0 flex-1">
        <span className="exo-friend-name block truncate">{friend.name}</span>
        {meta ? (
          <span className="exo-friend-meta block">
            {playing ? <span className="text-good">{playing}</span> : meta}
          </span>
        ) : null}
      </span>
      {artGame ? (
        <span className="exo-friend-game-art" aria-hidden>
          <CoverArt game={artGame} preload />
        </span>
      ) : null}
    </button>
  )
}

/** What Exo can actually say right now: your own session, and what each store gave up. */
function NowPanel({
  source,
  exoCount,
  storeCount,
  activeCount,
  sources,
  rosterNote,
  onlineDiagnostics,
  storeNote,
  running,
}: {
  source: Source
  exoCount: number
  storeCount: number
  activeCount: number
  sources: FriendSource[]
  rosterNote: string | null
  onlineDiagnostics: OnlineDiagnostics | null
  storeNote: string | null
  running: Game | null
}) {
  const line =
    source === 'exo'
      ? onlineDiagnostics?.signedIn === false
        ? rosterNote ?? 'Signed out. Local people remain available on this PC.'
        : onlineDiagnostics?.error?.message ??
          (onlineDiagnostics?.source === 'cache'
            ? 'Showing the last successful Exo friend sync while refresh is unavailable.'
            : 'Exo friends and privacy-safe presence are available while signed in.')
      : sources.some((entry) => entry.store === 'steam' && entry.live)
        ? 'Steam presence is live when Steam returns a persona state. Epic is last-seen only.'
        : storeCount > 0
          ? 'No store here reports live presence, so nobody is counted as around.'
          : storeNote ?? 'Sign in to a store client once so Exo can read its list.'

  return (
    <div className="exo-friends-empty">
      <h2 className="exo-section-label">{source === 'exo' ? 'Exo' : 'From your stores'}</h2>
      <p className="exo-friends-empty-lead">{line}</p>
      <p className="exo-friends-empty-hint">Select someone in the list to open them here.</p>

      {source === 'exo' ? (
        <div className="exo-friends-card">
          <p className="exo-friends-card-copy">
            {exoCount} on your Exo list. Store friends stay under Stores until you say one of them is
            the same person.
          </p>
        </div>
      ) : (
        <>
          <div className="exo-friends-card">
            <div className="exo-presence-counts">
              <span className="exo-presence-count">
                <b className="tabular-nums">{storeCount}</b> across your stores
              </span>
              <span className="exo-presence-count">
                <b className="tabular-nums">{activeCount}</b> active now
              </span>
            </div>
          </div>
          {sources.length > 0 ? (
            <div className="exo-friends-card">
              <div className="exo-presence-notes">
                {sources.map((entry) => (
                  <p key={entry.store} className="exo-presence-note">
                    <span className="exo-presence-note-store">{sourceLabel(entry.store)}</span>
                    {' '}
                    {noteWithoutStorePrefix(entry.store, entry.note)}
                  </p>
                ))}
              </div>
            </div>
          ) : (
            <div className="exo-friends-card">
              <p className="exo-friends-card-copy">{storeNote ?? CACHE_PRESENCE_NOTE}</p>
            </div>
          )}
        </>
      )}

      {running ? (
        <div className="exo-friends-card">
          <h3 className="exo-section-label">You</h3>
          <div className="exo-friend-row mt-2">
            <span className="exo-avatar">
              <span className="exo-avatar-mono" aria-hidden>
                You
              </span>
            </span>
            <span className="min-w-0 flex-1">
              <span className="exo-friend-name block">Playing now</span>
              <span className="exo-friend-meta block text-good">{running.title}</span>
            </span>
            <span className="block h-10 w-7 shrink-0 overflow-hidden rounded-[4px]">
              <CoverArt game={running} className="h-full w-full" />
            </span>
          </div>
        </div>
      ) : null}
    </div>
  )
}

function OnlinePersonPage({
  person,
  profile,
  profileBusy,
  profileProblem,
  presence,
  games,
  blocked,
  busy,
  onRemove,
  onBlock,
}: {
  person: MergedPerson
  profile: OnlinePublicProfile | null
  profileBusy: boolean
  profileProblem: string | null
  presence?: OnlinePresenceEntry
  games: Game[]
  blocked: boolean
  busy: boolean
  onRemove: () => void
  onBlock: (blocked: boolean) => void
}) {
  const [confirming, setConfirming] = useState(false)
  const values = profile?.profile ?? {}
  const text = (key: string) => typeof values[key] === 'string' ? String(values[key]).trim() : ''
  const handle = profile?.handle?.display || person.onlineHandleDisplay || person.handle
  const displayName = text('displayName') || handle || 'Exo connection'
  const badges = profile?.badges
  const avatar = profile?.media.avatar?.available
    ? profile.media.avatar.url
    : person.onlineAvatarUrl ?? null
  const banner = profile?.media.banner?.available ? profile.media.banner.url : null
  const gallery = (['gallery0', 'gallery1', 'gallery2', 'gallery3', 'gallery4', 'gallery5'] as const)
    .map((key) => profile?.media[key])
    .filter((media): media is OnlineProfileMedia => media?.available === true && !!media.url)
  const bio = text('bio')
  const statusText = text('statusText')
  const presenceText = !presence?.available
    ? 'Presence unavailable'
    : presence.status === 'ingame'
      ? presence.gameTitle || 'In game'
      : PRESENCE_LABEL[presence.status]
  const playing = presence?.available && presence.status === 'ingame'
    ? friendPlayingAction(presence.gameId, presence.gameTitle, games)
    : null

  return (
    <div className={cn('exo-friend-page is-online', playing && 'has-playing')}>
      <header className={cn('exo-online-profile-hero', banner && 'has-banner')}>
        {banner ? <img src={banner} alt="" decoding="async" /> : null}
        <div className="exo-online-profile-veil" aria-hidden />
        <div className="exo-friend-identity">
          <Avatar
            name={displayName}
            avatarUrl={avatar}
            large
          />
          <div className="exo-friend-identity-copy">
            <ServerBadgeRow badges={badges} className="exo-friend-badges" />
            <h2 className="exo-friend-identity-name">{displayName}</h2>
            <p className="exo-friend-identity-meta">
              {handle ? `@${handle}` : 'Handle not claimed'} · {presenceText}
            </p>
            {statusText ? <p className="exo-friend-seen">{statusText}</p> : null}
          </div>
          <div className="exo-friend-actions">
            <button type="button" className="exo-ghost-btn" disabled={busy} onClick={() => onBlock(!blocked)}>
              {blocked ? 'Unblock' : 'Block'}
            </button>
            {confirming ? (
              <button type="button" className="exo-ghost-btn is-danger" disabled={busy} onClick={onRemove}>
                Confirm remove
              </button>
            ) : (
              <button type="button" className="exo-ghost-btn" disabled={busy} onClick={() => setConfirming(true)}>
                Remove
              </button>
            )}
          </div>
        </div>
      </header>

      {(profileBusy || profileProblem) ? (
        <p className="exo-friend-open-problem" role="status" aria-live="polite">
          {profileBusy ? 'Loading profile…' : profileProblem}
        </p>
      ) : null}

      <div className="exo-friend-store-grid">
        {playing ? <PlayingCard playing={playing} /> : (
          <section className="exo-friends-card">
            <h3 className="exo-section-label">About</h3>
            <p className="exo-friends-card-copy">
              {bio || (profile ? 'No public bio.' : 'Profile details are not available.')}
            </p>
          </section>
        )}
        <aside className="exo-friend-context">
          {playing && bio ? <p className="exo-friend-presence-note">{bio}</p> : null}
          <p className="exo-friend-seen">
            {(person.onlineSources ?? []).length > 0
              ? `Connected through ${(person.onlineSources ?? []).map(sourceLabel).join(', ')}.`
              : 'Direct Exo friend.'}
          </p>
          {!presence?.available ? (
            <p className="exo-friend-presence-note">Unavailable means Exo did not provide an authoritative state.</p>
          ) : null}
        </aside>
      </div>
      {gallery.length > 0 ? (
        <section className="exo-friends-card exo-friend-gallery" aria-label={`${displayName} gallery`}>
          <h3 className="exo-section-label">Gallery</h3>
          <div>
            {gallery.map((media) => <img key={media.url} src={media.url!} alt="" loading="lazy" decoding="async" />)}
          </div>
        </section>
      ) : null}
    </div>
  )
}

function PersonPage({
  person,
  onRemove,
  onNote,
  onUnlink,
}: {
  person: MergedPerson
  onRemove: () => void
  onNote: (note: string) => void
  onUnlink: (friendId: string) => void
}) {
  const [note, setNote] = useState(person.note ?? '')
  const [confirming, setConfirming] = useState(false)

  useEffect(() => {
    setNote(person.note ?? '')
    setConfirming(false)
  }, [person.id, person.note])

  const added = person.addedUtc && !Number.isNaN(Date.parse(person.addedUtc))
    ? new Date(person.addedUtc).toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric' })
    : null
  const links = person.links ?? []

  return (
    <div className="exo-friend-page is-person">
      <header className="exo-friend-identity">
        <span className="exo-avatar is-lg">
          <span className="exo-avatar-mono" aria-hidden>
            {monogram(mergedPersonLabel(person))}
          </span>
        </span>
        <div className="exo-friend-identity-copy">
          <h2 className="exo-friend-identity-name">{mergedPersonLabel(person)}</h2>
          <p className="exo-friend-identity-meta">@{person.handle} · on your Exo list</p>
        </div>
        <div className="exo-friend-actions">
          {confirming ? (
            <>
              <button type="button" className="exo-ghost-btn is-danger" onClick={onRemove}>
                Confirm remove
              </button>
              <button type="button" className="exo-ghost-btn" onClick={() => setConfirming(false)}>
                Keep
              </button>
            </>
          ) : (
            <button type="button" className="exo-ghost-btn" onClick={() => setConfirming(true)}>
              Remove
            </button>
          )}
        </div>
      </header>

      <div className="exo-friend-person-grid">
        <div className="exo-friend-context">
          <p className="exo-friend-presence-note">
            This is a local fallback entry. It has no online presence until it matches a reserved handle.
          </p>

          {links.length > 0 ? (
            <div className="exo-friend-links">
              <p className="exo-section-label">Same person on</p>
              {links.map((link) => (
                <div key={link.id} className="exo-friend-link">
                  <span className="exo-friend-store">{sourceLabel(link.store)}</span>
                  <span className="min-w-0 flex-1 truncate text-[13px]">
                    {link.name ?? 'Not readable right now'}
                  </span>
                  <button type="button" className="exo-ghost-btn" onClick={() => onUnlink(link.id)}>
                    Unlink
                  </button>
                </div>
              ))}
            </div>
          ) : null}
        </div>

        <div className="exo-friend-note-form">
          <label className="exo-friend-note-field">
            <span className="exo-friend-note-field-head">
              <span className="exo-section-label">Note</span>
              <span className="text-[11px] text-faint">
                {note.length}/{PROFILE_LIMITS.note}
              </span>
            </span>
            <input
              className="exo-field"
              value={note}
              maxLength={PROFILE_LIMITS.note}
              placeholder="Only you see this"
              onChange={(event) => setNote(event.target.value)}
            />
          </label>
          <div className="exo-friend-note-actions">
            <button
              type="button"
              className="exo-ghost-btn is-primary"
              disabled={note === (person.note ?? '')}
              onClick={() => onNote(note.trim())}
            >
              Save note
            </button>
            {added ? <span className="text-[11px] text-faint">Added {added}</span> : null}
          </div>
        </div>
      </div>
    </div>
  )
}

function FriendPage({
  friend,
  live,
  note,
  games,
  people,
  onLink,
}: {
  friend: HostFriend
  live: boolean
  note: string | null
  games: Game[]
  people: ExoPerson[]
  onLink: (personId: string) => Promise<boolean>
}) {
  const presence = presenceOf(friend.status)
  const playing = live ? friendPlayingAction(friend.playingId, friend.playingTitle, games) : null
  const seen = lastSeenLabel(friend.lastSeenUtc)
  const showPresenceNote = presence === 'unknown' || !live

  return (
    <div className={cn('exo-friend-page is-store', playing && 'has-playing')}>
      <header className="exo-friend-identity">
        <Avatar
          name={friend.name}
          avatarUrl={friend.avatarUrl}
          large
        />
        <div className="exo-friend-identity-copy">
          <h2 className="exo-friend-identity-name">{friend.name}</h2>
          <p className="exo-friend-identity-meta">
            {playing?.title ??
              (live
                ? friend.statusText?.trim() || PRESENCE_LABEL[presence]
                : `${sourceLabel(friend.source)} · presence not available`)}
          </p>
          {seen && (!live || presence === 'offline') ? (
            <p className="exo-friend-seen">{seen}</p>
          ) : null}
          {friend.presenceFrom ? (
            <p className="exo-friend-seen">
              Presence from {friend.presenceFrom === 'galaxy' ? 'GOG Galaxy' : sourceLabel(friend.presenceFrom)}
            </p>
          ) : null}
        </div>
      </header>

      <div className="exo-friend-store-grid">
        {playing ? <PlayingCard playing={playing} /> : null}

        <aside className="exo-friend-context">
          {showPresenceNote ? (
            <p className="exo-friend-presence-note">{note ?? CACHE_PRESENCE_NOTE}</p>
          ) : null}
          <LinkPicker people={people} store={friend.source} onLink={onLink} />
        </aside>
      </div>
    </div>
  )
}

/** The title they are in, plus the action Exo can honestly take right now. */
function playingBusyLabel(kind: FriendPlayingAction['kind']): string {
  switch (kind) {
    case 'play':
      return 'Starting'
    case 'stop':
      return 'Stopping'
    case 'install':
      return 'Installing'
    case 'update':
      return 'Updating'
    default:
      return 'Opening'
  }
}

function PlayingCard({ playing }: { playing: FriendPlayingAction<Game> }) {
  const [busy, setBusy] = useState(false)
  const [openingDeals, setOpeningDeals] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)
  const game = playing.game
  const steamId = playing.steamAppId ?? game?.id.match(/^steam:(\d+)$/i)?.[1] ?? null
  const artGame: Game | null = game
    ? steamId && !game.coverUrl
      ? {
          ...game,
          coverUrl: game.coverUrl ?? steamPlayingCoverUrl(steamId),
          coverSource: game.coverSource ?? 'steam-friend-cdn',
        }
      : game
    : steamId ? {
        id: `steam:${steamId}`,
        title: playing.title,
        store: 'steam',
        installed: false,
        owned: false,
        primaryAction: 'none',
        coverUrl: steamPlayingCoverUrl(steamId),
        coverSource: 'steam-cdn',
        status: 'Store',
        deps: [],
        launchNote: '',
      } : null
  const buyUrl = game ? hostedBuyUrl(game) : null
  const dealsUrl = game
    ? buyUrl
      ? ggDealsUrl(game)
      : null
    : playing.kind === 'buy' && playing.steamAppId
      ? ggDealsUrl({
          id: `steam:${playing.steamAppId}`,
          title: playing.title,
          store: 'steam',
          launchTarget: playing.steamAppId,
        })
      : null

  async function run() {
    setBusy(true)
    setProblem(null)
    try {
      switch (playing.kind) {
        case 'play': {
          if (!game) {
            setProblem('Exo could not launch that game.')
            break
          }
          const result = await host.launch(game.id)
          if (!result.ok) setProblem(result.message ?? 'Exo could not launch that game.')
          break
        }
        case 'stop': {
          if (!game) {
            setProblem('Exo could not stop that game.')
            break
          }
          const result = await host.stop(game.id)
          if (!result.ok) setProblem(result.message ?? 'Exo could not stop that game.')
          break
        }
        case 'install': {
          if (!game) {
            setProblem('Exo could not install that game.')
            break
          }
          const result = await host.install(game.id)
          if (!result.ok) setProblem(result.message ?? 'Exo could not install that game.')
          break
        }
        case 'update': {
          if (!game) {
            setProblem('Exo could not update that game.')
            break
          }
          const result = await host.update(game.id)
          if (!result.ok) setProblem(result.message ?? 'Exo could not update that game.')
          break
        }
        case 'buy': {
          if (!playing.url) {
            setProblem('Exo could not open that store page.')
            break
          }
          const result = await host.openUrl(playing.url)
          if (!result.ok) setProblem('Exo could not open that store page.')
          break
        }
        case 'none':
          setProblem(playing.reason ?? 'Exo cannot do anything with this title from here.')
          break
        default: {
          const _never: never = playing.kind
          return _never
        }
      }
    } catch (error) {
      setProblem(error instanceof Error ? error.message : 'Exo could not open that game.')
    } finally {
      setBusy(false)
    }
  }

  async function openDeals() {
    if (!dealsUrl) return
    setOpeningDeals(true)
    setProblem(null)
    try {
      const result = await host.openUrl(dealsUrl)
      if (!result.ok) setProblem('Exo could not open that key shop page.')
    } catch (error) {
      setProblem(error instanceof Error ? error.message : 'Exo could not open that key shop page.')
    } finally {
      setOpeningDeals(false)
    }
  }

  const busyLabel = playingBusyLabel(playing.kind)

  return (
    <section className="exo-friend-playing" aria-label={`${playing.title} activity`}>
      <div className="exo-friend-playing-card">
        {artGame ? (
          <div className="exo-friend-playing-banner" aria-hidden>
            <HeroWash game={artGame} />
          </div>
        ) : null}
        {artGame ? (
          <div className="exo-friend-playing-art" aria-hidden>
            <CoverArt game={artGame} className="h-full w-full" />
          </div>
        ) : null}
        <div className="exo-friend-playing-copy">
          <p className="exo-section-label">In game</p>
          <h3 className="exo-friend-playing-title">{playing.title}</h3>
          {playing.label || dealsUrl ? (
            <div className="exo-friend-playing-actions">
              {playing.label ? (
                <button
                  type="button"
                  className="exo-ghost-btn is-primary"
                  disabled={busy || openingDeals}
                  onClick={() => void run()}
                >
                  {busy ? busyLabel : playing.label}
                </button>
              ) : null}
              {dealsUrl ? (
                <button
                  type="button"
                  className="exo-ghost-btn exo-buy-key"
                  aria-label={`Buy cheapest key for ${playing.title} on gg.deals`}
                  disabled={busy || openingDeals}
                  onClick={() => void openDeals()}
                >
                  <span className="exo-action-state">
                    <span className="exo-action-content exo-action-idle">
                      <Download size={16} className="shrink-0" />
                      <span className="exo-action-copy">
                        {openingDeals ? 'Opening' : 'Buy cheapest key'}
                      </span>
                    </span>
                  </span>
                </button>
              ) : null}
            </div>
          ) : playing.reason ? (
            <p className="exo-friend-seen mt-2">{playing.reason}</p>
          ) : null}
        </div>
      </div>
      {/* Kept out of the card, which clips, so a failure is never hidden. */}
      {problem ? <p className="exo-friend-open-problem">{problem}</p> : null}
    </section>
  )
}

/**
 * Linking is a claim only the user can make: Exo cannot tell that two store
 * accounts are one human, so it never guesses.
 */
function LinkPicker({
  people,
  store,
  onLink,
}: {
  people: ExoPerson[]
  store?: string
  onLink: (personId: string) => Promise<boolean>
}) {
  const [open, setOpen] = useState(false)
  const [busy, setBusy] = useState(false)
  const sorted = useMemo(() => sortMergedPeople(people), [people])

  return (
    <div className="exo-friend-links">
      <p className="exo-section-label">Same person</p>
      {people.length === 0 ? (
        <p className="exo-friend-seen">
          Add someone to your Exo list first, then you can say this {sourceLabel(store)} account is
          them.
        </p>
      ) : open ? (
        <>
          <div className="exo-friend-link-picker">
            {sorted.map((person) => (
              <button
                key={person.id}
                type="button"
                className="exo-friend-link-pick"
                disabled={busy}
                onClick={async () => {
                  setBusy(true)
                  try {
                    await onLink(person.id)
                  } finally {
                    setBusy(false)
                  }
                }}
              >
                {mergedPersonLabel(person)}
                <span className="exo-friend-meta">@{person.handle}</span>
              </button>
            ))}
          </div>
          <button type="button" className="exo-ghost-btn mt-2" onClick={() => setOpen(false)}>
            Cancel
          </button>
        </>
      ) : (
        <>
          <p className="exo-friend-seen">
            If someone on your Exo list is this person, say so and this row moves to them.
          </p>
          <button type="button" className="exo-ghost-btn mt-2" onClick={() => setOpen(true)}>
            Link to someone on Exo
          </button>
        </>
      )}
    </div>
  )
}
