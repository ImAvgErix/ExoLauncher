import { useRef, type ReactNode } from 'react'
import { Button } from '../ui/Button'
import { Spinner } from '../ui/Spinner'
import { useGlide } from '../../lib/glide'
import { useLens } from '../../lib/glass'
import { ExoMark } from '../../brand/ExoMark'
import { Chats, Gpu, Library, Mouse, MusicNotes, Settings, StoreBag } from '../../brand/icons'
import { warmMusic, type MusicService } from '../../lib/music'
import { DiscordBadge } from './DiscordBadge'
import { MusicMiniPlayer } from './MusicMiniPlayer'
import { MouseCapsule } from './MouseCapsule'
import { CaptureButton } from './CaptureButton'
import { WindowChrome } from '../WindowChrome'

export type View =
  'library' | 'store' | 'friends' | 'music' | 'system' | 'devices' | 'settings'

const NAV_TABS: ReadonlyArray<{
  id: View
  label: string
  Icon: typeof Library
}> = [
    { id: 'library', label: 'Library', Icon: Library },
    { id: 'store', label: 'Store', Icon: StoreBag },
    { id: 'friends', label: 'Discord', Icon: Chats },
    { id: 'music', label: 'Music', Icon: MusicNotes },
    { id: 'system', label: 'PC', Icon: Gpu },
    { id: 'devices', label: 'Devices', Icon: Mouse },
  ]

/** The rooms in tab order: Ctrl+1 is the first. */
export const ROOM_ORDER: readonly View[] = NAV_TABS.map(tab => tab.id)

export type TitleBarProps = {
  view: View
  /** A room being built for its first showing: its tab shows the ring in its glyph's place until it opens. */
  opening?: View | null
  onView: (view: View) => void
  /** The mini player: the Music room on Now playing. */
  onNowPlaying: () => void
  /** Gear: opens Settings, or returns to the room Settings was opened from. */
  onToggleSettings: () => void
  musicService: MusicService
  /** Wishlisted games on sale you don't own, shown on the Store tab. */
  storeSales?: number
  /** The Exo Island, left of the Rooms: a call outside Discord, or a download. */
  island?: ReactNode
  /** The Search capsule: opens Ctrl+K. */
  onSearch: () => void
}

/** A room tab: icon and name, filled while it is the current room, with an optional count. */
function NavTab({ label, Icon, active, opening = false, badge, onOpen, onWarm }: {
  label: string
  Icon: typeof Library
  active: boolean
  opening?: boolean
  badge?: ReactNode
  onOpen: () => void
  onWarm?: () => void
}) {
  return (
    <button
      type="button"
      className={`exo-nav-btn${active ? ' is-on' : ''}`}
      aria-current={active ? 'page' : undefined}
      aria-busy={opening || undefined}
      onClick={onOpen}
      onPointerEnter={onWarm}
    >
      {opening ? <Spinner size={16} label={`Opening ${label}`} /> : <Icon size={16} current={active} />}
      <span className="exo-nav-label">{label}</span>
      {badge}
    </button>
  )
}

function RoomNavigation({ view, opening, onView, storeSales }: Pick<TitleBarProps, 'view' | 'opening' | 'onView' | 'storeSales'>) {
  const glide = useGlide<HTMLElement>()
  useLens(glide)
  return (
    <nav ref={glide} className="exo-glass exo-top-rooms exo-glide" aria-label="Rooms">
      {NAV_TABS.map((tab) => (
        <NavTab
          key={tab.id}
          label={tab.label}
          Icon={tab.Icon}
          active={view === tab.id}
          opening={opening === tab.id}
          badge={tab.id === 'friends' ? <DiscordBadge /> : tab.id === 'store' && storeSales ? <span className="exo-discord-notification-count" aria-label={`${storeSales} wishlisted game${storeSales === 1 ? '' : 's'} on sale`}>{storeSales > 99 ? '99+' : storeSales}</span> : null}
          onOpen={() => onView(tab.id)}
          onWarm={tab.id === 'music' ? warmMusic : undefined}
        />
      ))}
    </nav>
  )
}

/**
 * The Bar (DESIGN.md › Exo's language), one row of evenly spaced capsules: Search (the Exo mark;
 * Ctrl+K), the call or a download, the Rooms, the song, the mouse, and Tools (Capture and Settings:
 * what applies everywhere; a room's own controls stay in the room) with the window controls in the
 * same capsule. Each live capsule has a slot of its own and slides in and out there, so the tabs never move.
 */
function NavDock({ view, opening, onView, onToggleSettings, onNowPlaying, onSearch, storeSales, island, ...music }: TitleBarProps) {
  const search = useRef<HTMLButtonElement>(null)
  const tools = useRef<HTMLDivElement>(null)
  useLens(search)
  useLens(tools)
  return (
    <div className="exo-bar">
      <div className="exo-bar-start">
        <button ref={search} type="button" className="exo-glass exo-bar-search" onClick={onSearch} aria-label="Search Exo" aria-keyshortcuts="Control+K">
          <ExoMark size={18} className="exo-brand-logo" />
          <span className="exo-bar-search-label">Search</span>
        </button>
      </div>
      <div className="exo-bar-center">
        <div className="exo-bar-side is-start">{island}</div>
        <RoomNavigation view={view} opening={opening} onView={onView} storeSales={storeSales} />
        <div className="exo-bar-side is-end">
          <MusicMiniPlayer key={music.musicService} service={music.musicService} hidden={view === 'music'} onOpen={onNowPlaying} />
          <MouseCapsule onOpen={() => onView('devices')} />
        </div>
      </div>
      <div className="exo-bar-end">
        <div ref={tools} className="exo-glass exo-titlebar-tools">
          <CaptureButton />
          <Button
            type="button"
            variant="ghost"
            size="sm"
            isIconOnly
            className={`exo-settings-btn${view === 'settings' ? ' is-on' : ''}`}
            aria-label="Open settings"
            aria-pressed={view === 'settings'}
            onPress={onToggleSettings}
          >
            <Settings size={16} />
          </Button>
          <span className="exo-titlebar-divider" aria-hidden="true" />
          <WindowChrome />
        </div>
      </div>
    </div>
  )
}

export function TitleBar({
  view,
  onView,
  onToggleSettings,
  ...music
}: TitleBarProps) {
  return (
    <header className="exo-titlebar">
      <div className="exo-titlebar-main">
        <NavDock
          view={view}
          onView={onView}
          onToggleSettings={onToggleSettings}
          {...music}
        />
      </div>
    </header>
  )
}
