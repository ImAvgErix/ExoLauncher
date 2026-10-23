import {
  useCallback,
  useEffect,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
  type CSSProperties,
  type KeyboardEvent as ReactKeyboardEvent,
  type MouseEvent,
  type RefObject,
} from 'react'
import { type Game, type InstallProgress } from '../lib/host'
import { isInstallingGame } from '../lib/library'
import {
  calculateGridWindow,
  columnAfterGridMove,
  GRID_OVERSCAN_ROWS,
  resolveGridCardLayout,
  intendedColumnAfterFocus,
  isVerticalGridKey,
  moveGridFocusIndex,
  resolveActiveGameId,
  resolveGridFocusRestore,
  rovingGridTabIndex,
  shouldClearPendingGridFocus,
  type GridNavigationKey,
  type GridWindow,
  type GridWindowInput,
} from '../lib/gridWindow'
import { GameCard } from './GameCard'
import { FolderCard } from './shell/FolderCard'
import { isFolderTile } from '../lib/folders'
import type { GameMenuAt } from './shell/GameMenu'

type WindowedGameGridProps = {
  games: Game[]
  selectedId: string | null
  activeGameId: string | null
  onActiveGameChange: (gameId: string) => void
  onSelect: (game: Game, event?: MouseEvent) => void
  onMenu?: (at: GameMenuAt) => void
  /** A folder dragged onto another moves to just before or after it. */
  onMoveFolder?: (folderId: string, toId: string, after: boolean) => void
  transferFor?: (game: Game) => InstallProgress | null
  queuedIds?: string[]
  /** Select mode: the picked games' ids; null outside it. */
  pickedIds?: ReadonlySet<string> | null
  layoutKey: string
  scrollRootRef: RefObject<HTMLElement | null>
  labelledBy: string
  /** The one card width the library pane measured, or null before it has. */
  cardWidth: number | null
}

type GridMeasurement = GridWindowInput & {
  window: GridWindow
}

/** Everything but the scroll position. Read on resize, reused on every scroll frame. */
type GridGeometry = Omit<GridWindowInput, 'itemCount' | 'viewportStart' | 'overscanRows'> & {
  /** The grid's top edge in the scroll root's content coordinates. */
  offset: number
}

const DEFAULT_CARD_META_HEIGHT = 52
const DEFAULT_COLUMN_GAP = 12
const DEFAULT_ROW_GAP = 12
const DEFAULT_GUTTER = 24
const DEFAULT_PADDING_TOP = 8
const DEFAULT_PADDING_BOTTOM = 40

function initialMeasurement(itemCount: number, measuredCardWidth: number | null): GridMeasurement {
  const cardWidth = measuredCardWidth ?? resolveGridCardLayout(1400, DEFAULT_GUTTER, DEFAULT_COLUMN_GAP).cardWidth
  const input: GridWindowInput = {
    itemCount,
    containerWidth: 1400,
    viewportStart: 0,
    viewportHeight: 900,
    cardWidth,
    cardHeight: cardWidth * 1.5 + DEFAULT_CARD_META_HEIGHT,
    columnGap: DEFAULT_COLUMN_GAP,
    rowGap: DEFAULT_ROW_GAP,
    paddingInline: DEFAULT_GUTTER,
    paddingTop: DEFAULT_PADDING_TOP,
    paddingBottom: DEFAULT_PADDING_BOTTOM,
    overscanRows: GRID_OVERSCAN_ROWS,
  }
  return { ...input, window: calculateGridWindow(input) }
}

function cssPixels(value: string, fallback: number): number {
  const parsed = Number.parseFloat(value)
  return Number.isFinite(parsed) && parsed >= 0 ? parsed : fallback
}

function sameWindow(left: GridWindow, right: GridWindow): boolean {
  return (
    left.columnCount === right.columnCount &&
    left.rowCount === right.rowCount &&
    left.pageRowCount === right.pageRowCount &&
    left.startRow === right.startRow &&
    left.endRow === right.endRow &&
    left.startIndex === right.startIndex &&
    left.endIndex === right.endIndex &&
    left.beforeHeight === right.beforeHeight &&
    left.renderedHeight === right.renderedHeight &&
    left.afterHeight === right.afterHeight &&
    left.totalHeight === right.totalHeight
  )
}

function sameMeasurement(left: GridMeasurement, right: GridMeasurement): boolean {
  // Scrolling inside an already mounted row changes its screen position, not
  // its React output. Keyboard reveal reads the live DOM when it needs it.
  return sameWindow(left.window, right.window) &&
    left.containerWidth === right.containerWidth && left.cardWidth === right.cardWidth &&
    left.cardHeight === right.cardHeight && left.viewportHeight === right.viewportHeight &&
    left.paddingInline === right.paddingInline && left.paddingTop === right.paddingTop &&
    left.paddingBottom === right.paddingBottom && left.columnGap === right.columnGap && left.rowGap === right.rowGap
}

function isNavigationKey(key: string): key is GridNavigationKey {
  return (
    key === 'ArrowLeft' ||
    key === 'ArrowRight' ||
    key === 'ArrowUp' ||
    key === 'ArrowDown' ||
    key === 'Home' ||
    key === 'End' ||
    key === 'PageUp' ||
    key === 'PageDown'
  )
}

export function WindowedGameGrid({
  games,
  selectedId,
  activeGameId,
  onActiveGameChange,
  onSelect,
  onMenu,
  onMoveFolder,
  transferFor,
  queuedIds = [],
  pickedIds = null,
  layoutKey,
  scrollRootRef,
  labelledBy,
  cardWidth: measuredCardWidth,
}: WindowedGameGridProps) {
  const rootRef = useRef<HTMLDivElement>(null)
  // Arrive (DESIGN.md › Motion): the first cards the grid shows rise in turn,
  // once, when it first has games. Cards mounted later by scrolling don't.
  const [arriving, setArriving] = useState(false)
  const arrived = useRef(false)
  useEffect(() => {
    if (arrived.current || games.length === 0) return
    arrived.current = true
    setArriving(true)
  }, [games.length])
  useEffect(() => {
    if (!arriving) return
    const timer = window.setTimeout(() => setArriving(false), 900)
    return () => window.clearTimeout(timer)
  }, [arriving])
  const pendingFocusId = useRef<string | null>(null)
  const focusedGameId = useRef<string | null>(null)
  const gridOwnsFocus = useRef(false)
  const intendedColumnRef = useRef<number | null>(null)
  const retainIntendedColumnRef = useRef(false)
  const scheduleMeasurementRef = useRef<() => void>(() => { })
  const geometryRef = useRef<GridGeometry | null>(null)
  const [measurement, setMeasurement] = useState<GridMeasurement>(() =>
    initialMeasurement(games.length, measuredCardWidth),
  )
  const measuredRef = useRef(measurement)
  const gameOrderKey = useMemo(() => games.map((game) => game.id).join('\u001f'), [games])
  const gameIds = useMemo(() => (gameOrderKey ? gameOrderKey.split('\u001f') : []), [gameOrderKey])

  // The fallback is only the current roving tab stop. Do not write it back:
  // keeping the caller's key lets the same game regain focus after a filter.
  const resolvedActiveId = resolveActiveGameId(gameIds, activeGameId, selectedId)
  const activeIndex = resolvedActiveId ? gameIds.indexOf(resolvedActiveId) : -1
  const activeIsMounted =
    activeIndex >= measurement.window.startIndex && activeIndex < measurement.window.endIndex
  const activeCardIsTabbable = !!resolvedActiveId && activeIsMounted
  const findControl = useCallback((gameId: string) => {
    return Array.from(rootRef.current?.querySelectorAll<HTMLButtonElement>('button[data-game-id]') ?? []).find(
      (candidate) => candidate.dataset.gameId === gameId,
    )
  }, [])

  /** Styles and rects: only when something resized, never per scroll frame. */
  const measureGeometry = useCallback((gridRoot: HTMLElement, scrollRoot: HTMLElement): GridGeometry => {
    const style = getComputedStyle(gridRoot)
    const paddingInline = cssPixels(style.getPropertyValue('--exo-gutter'), DEFAULT_GUTTER)
    const columnGap = cssPixels(style.columnGap, DEFAULT_COLUMN_GAP)
    const cardWidth = measuredCardWidth ?? resolveGridCardLayout(gridRoot.clientWidth, paddingInline, columnGap).cardWidth
    return {
      containerWidth: gridRoot.clientWidth,
      viewportHeight: scrollRoot.clientHeight,
      cardWidth,
      cardHeight: cardWidth * 1.5 + cssPixels(style.getPropertyValue('--exo-card-meta-height'), DEFAULT_CARD_META_HEIGHT),
      columnGap,
      rowGap: cssPixels(style.rowGap, DEFAULT_ROW_GAP),
      paddingInline,
      paddingTop: cssPixels(style.getPropertyValue('--exo-grid-padding-top'), DEFAULT_PADDING_TOP),
      paddingBottom: cssPixels(style.getPropertyValue('--exo-grid-padding-bottom'), DEFAULT_PADDING_BOTTOM),
      offset: gridRoot.getBoundingClientRect().top - scrollRoot.getBoundingClientRect().top + scrollRoot.scrollTop,
    }
  }, [measuredCardWidth])

  const measure = useCallback((resized: boolean) => {
    const gridRoot = rootRef.current
    const scrollRoot = scrollRootRef.current
    if (!gridRoot || !scrollRoot) return

    if (resized || !geometryRef.current) geometryRef.current = measureGeometry(gridRoot, scrollRoot)
    const { offset, ...geometry } = geometryRef.current
    const input: GridWindowInput = {
      ...geometry,
      itemCount: games.length,
      viewportStart: scrollRoot.scrollTop - offset,
      overscanRows: GRID_OVERSCAN_ROWS,
    }
    const nextWindow = calculateGridWindow(input)
    const activeElement = document.activeElement
    const focusedId =
      activeElement instanceof HTMLElement && gridRoot.contains(activeElement)
        ? activeElement.closest<HTMLElement>('[data-game-id]')?.dataset.gameId
        : undefined
    if (focusedId) {
      const focusedIndex = gameIds.indexOf(focusedId)
      const remainsMounted =
        focusedIndex >= nextWindow.startIndex && focusedIndex < nextWindow.endIndex
      if (!remainsMounted) gridRoot.focus({ preventScroll: true })
    }
    const next = { ...input, window: nextWindow }
    if (!sameMeasurement(measuredRef.current, next)) {
      measuredRef.current = next
      setMeasurement(next)
    }
  }, [gameIds, games.length, measureGeometry, scrollRootRef])

  // The scroll root belongs to an ancestor. React attaches that ref after
  // descendant layout effects, so subscribe after the full commit. Returning
  // early here used to leave the initial 1400px grid permanently unmeasured.
  useEffect(() => {
    const gridRoot = rootRef.current
    const scrollRoot = scrollRootRef.current
    if (!gridRoot || !scrollRoot) return

    let frame = 0
    let resized = false
    const schedule = (geometry: boolean) => {
      resized ||= geometry
      window.cancelAnimationFrame(frame)
      frame = window.requestAnimationFrame(() => {
        const full = resized
        resized = false
        measure(full)
      })
    }
    const onScroll = () => schedule(false)
    const onResize = () => schedule(true)
    scheduleMeasurementRef.current = onScroll
    const observer = new ResizeObserver(onResize)
    const releaseFocusOwnership = (event: Event) => {
      const target = event.target
      if (event.type === 'focusin' && target === document.body) return
      if (target instanceof Node && !gridRoot.contains(target)) {
        gridOwnsFocus.current = false
        focusedGameId.current = null
      }
    }
    // Shelves above this one move it without resizing it: watch every box
    // between the grid and the scroll root, and the root's own children.
    for (let node: Element | null = gridRoot; node && node !== scrollRoot; node = node.parentElement) observer.observe(node)
    for (const child of scrollRoot.children) observer.observe(child)
    observer.observe(scrollRoot)
    scrollRoot.addEventListener('scroll', onScroll, { passive: true })
    document.addEventListener('pointerdown', releaseFocusOwnership, true)
    document.addEventListener('focusin', releaseFocusOwnership, true)
    measure(true)

    return () => {
      scheduleMeasurementRef.current = () => { }
      window.cancelAnimationFrame(frame)
      observer.disconnect()
      scrollRoot.removeEventListener('scroll', onScroll)
      document.removeEventListener('pointerdown', releaseFocusOwnership, true)
      document.removeEventListener('focusin', releaseFocusOwnership, true)
    }
  }, [layoutKey, measure, scrollRootRef])

  const applyFocusRestore = useCallback(() => {
    const gridRoot = rootRef.current
    if (!gridRoot) return
    const activeElement = document.activeElement
    const activeElementIsInsideGrid =
      activeElement instanceof Node && gridRoot.contains(activeElement)
    const gameId = pendingFocusId.current ?? (gridOwnsFocus.current ? focusedGameId.current : null)
    const control = gameId ? findControl(gameId) : undefined
    const enabledControl = control && !control.disabled ? control : undefined
    const restore = resolveGridFocusRestore({
      ownsFocus: gridOwnsFocus.current,
      pendingId: pendingFocusId.current,
      activeElementIsInsideGrid,
      activeElementIsGrid: activeElement === gridRoot,
      mountedControlEnabled: !!enabledControl,
    })
    if (restore === 'control' && enabledControl) {
      pendingFocusId.current = null
      if (activeElement !== enabledControl) enabledControl.focus({ preventScroll: true })
      return
    }
    if (restore === 'grid' && activeElement !== gridRoot) {
      gridRoot.focus({ preventScroll: true })
    }
  }, [findControl])

  // Cards leave the mounted slice as the window moves. Keep keyboard
  // ownership on the same keyed game, or on the grid proxy while that game is
  // outside the slice.
  useLayoutEffect(() => {
    applyFocusRestore()
  }, [applyFocusRestore, layoutKey, measurement.window.startIndex, measurement.window.endIndex])

  const revealIndex = useCallback(
    (index: number) => {
      const gridRoot = rootRef.current
      const scrollRoot = scrollRootRef.current
      if (!gridRoot || !scrollRoot || index < 0) return

      const row = Math.floor(index / measurement.window.columnCount)
      const gridTop =
        scrollRoot.scrollTop +
        gridRoot.getBoundingClientRect().top -
        scrollRoot.getBoundingClientRect().top
      const rowTop = gridTop + measurement.paddingTop + row * measurement.window.rowStride
      const rowBottom = rowTop + measurement.cardHeight
      // The Library scrolls under the Bar: a row is in view only below it (its scroll-padding-top).
      const under = parseFloat(getComputedStyle(scrollRoot).scrollPaddingTop) || 0
      const viewportTop = scrollRoot.scrollTop + under
      const viewportBottom = scrollRoot.scrollTop + scrollRoot.clientHeight

      if (rowTop < viewportTop) {
        scrollRoot.scrollTo({ top: rowTop - under })
      } else if (rowBottom > viewportBottom) {
        scrollRoot.scrollTo({ top: rowBottom - scrollRoot.clientHeight })
      }
      scheduleMeasurementRef.current()
    },
    [measurement, scrollRootRef],
  )

  const focusIndex = useCallback(
    (index: number) => {
      const game = games[index]
      if (!game) return
      onActiveGameChange(game.id)
      focusedGameId.current = game.id
      gridOwnsFocus.current = true
      revealIndex(index)
      const mountedButton = findControl(game.id)
      if (mountedButton) {
        pendingFocusId.current = null
        mountedButton.focus({ preventScroll: true })
      } else {
        pendingFocusId.current = game.id
      }
    },
    [findControl, games, onActiveGameChange, revealIndex],
  )

  useLayoutEffect(() => {
    applyFocusRestore()
  }, [activeGameId, applyFocusRestore, gameOrderKey, measurement.window.startIndex, measurement.window.endIndex])

  const onGridKeyDown = (event: ReactKeyboardEvent<HTMLDivElement>) => {
    const target = event.target instanceof HTMLElement ? event.target : null
    const focusedId = target?.closest<HTMLElement>('[data-game-id]')?.dataset.gameId
    if (!isNavigationKey(event.key) || event.altKey || event.metaKey) return

    const navigationId = pendingFocusId.current ?? focusedId ?? resolvedActiveId
    const currentIndex = navigationId ? gameIds.indexOf(navigationId) : 0
    const nextIndex = moveGridFocusIndex({
      currentIndex,
      itemCount: games.length,
      columnCount: measurement.window.columnCount,
      pageRowCount: measurement.window.pageRowCount,
      key: event.key,
      ctrlKey: event.ctrlKey,
      intendedColumn: intendedColumnRef.current ?? undefined,
    })
    if (nextIndex < 0) return
    event.preventDefault()
    event.stopPropagation()
    intendedColumnRef.current = columnAfterGridMove(
      event.key,
      nextIndex,
      measurement.window.columnCount,
      intendedColumnRef.current,
      currentIndex,
    )
    retainIntendedColumnRef.current = isVerticalGridKey(event.key)
    if (shouldClearPendingGridFocus(currentIndex, nextIndex)) {
      applyFocusRestore()
      pendingFocusId.current = null
      retainIntendedColumnRef.current = false
      return
    }
    focusIndex(nextIndex)
  }

  const { startIndex, endIndex, columnCount } = measurement.window
  const cards = games.slice(startIndex, endIndex).map((game, offset) => {
    const index = startIndex + offset
    if (isFolderTile(game)) return (
      <FolderCard
        key={game.id}
        tile={game}
        tabIndex={game.id === resolvedActiveId ? 0 : -1}
        position={index + 1}
        setSize={games.length}
        arriveIndex={arriving ? offset : undefined}
        onActiveGameChange={onActiveGameChange}
        onSelect={onSelect}
        onMenu={onMenu}
        onMove={onMoveFolder}
      />
    )
    return (
      <GameCard
        key={game.id}
        game={game}
        selected={game.id === selectedId || !!game.variants?.some((variant) => variant.id === selectedId)}
        transfer={transferFor?.(game) ?? null}
        queued={queuedIds.some(id => isInstallingGame(game, id))}
        preload={index < startIndex + 2 * columnCount}
        tabIndex={game.id === resolvedActiveId ? 0 : -1}
        position={index + 1}
        setSize={games.length}
        arriveIndex={arriving ? offset : undefined}
        picked={pickedIds && pickedIds.has(game.id)}
        onActiveGameChange={onActiveGameChange}
        onSelect={onSelect}
        onMenu={onMenu}
      />
    )
  })

  return (
    <div
      ref={rootRef}
      className="exo-windowed-game-grid"
      role="list"
      aria-labelledby={labelledBy}
      tabIndex={rovingGridTabIndex(activeCardIsTabbable)}
      data-rendered-count={endIndex - startIndex}
      data-column-count={columnCount}
      style={{ '--exo-grid-columns': columnCount } as CSSProperties}
      onKeyDown={onGridKeyDown}
      onPointerDownCapture={(event) => {
        const target = event.target instanceof HTMLElement ? event.target : null
        const pointerId = target?.closest<HTMLElement>('[data-game-id]')?.dataset.gameId
        if (!pointerId) return
        const pointerIndex = gameIds.indexOf(pointerId)
        if (pointerIndex >= 0) {
          retainIntendedColumnRef.current = false
          intendedColumnRef.current = pointerIndex % measurement.window.columnCount
        }
      }}
      onFocusCapture={(event) => {
        gridOwnsFocus.current = true
        const target = event.target instanceof HTMLElement ? event.target : null
        const cardId = target?.closest<HTMLElement>('[data-game-id]')?.dataset.gameId
        const focusedId = cardId
        const sameGame = !!focusedId && focusedId === focusedGameId.current
        if (cardId) {
          focusedGameId.current = cardId
        }
        if (!focusedId) return
        const focusedIndex = gameIds.indexOf(focusedId)
        if (focusedIndex < 0) return
        intendedColumnRef.current = intendedColumnAfterFocus({
          focusedIndex,
          columnCount: measurement.window.columnCount,
          currentIntendedColumn: intendedColumnRef.current,
          retainIntendedColumn: retainIntendedColumnRef.current || sameGame,
        })
        retainIntendedColumnRef.current = false
      }}
    >
      <div
        className="exo-grid-spacer"
        role="presentation"
        style={{ height: measurement.window.beforeHeight }}
        aria-hidden="true"
      />
      <div className="exo-game-grid" role="presentation">
        {cards}
      </div>
      <div
        className="exo-grid-spacer"
        role="presentation"
        style={{ height: measurement.window.afterHeight }}
        aria-hidden="true"
      />
    </div>
  )
}
