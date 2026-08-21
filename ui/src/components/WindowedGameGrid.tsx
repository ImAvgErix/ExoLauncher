import {
  useCallback,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
  type CSSProperties,
  type KeyboardEvent as ReactKeyboardEvent,
  type RefObject,
} from 'react'
import { type Game } from '../lib/host'
import {
  calculateGridWindow,
  captureGridMeasure,
  columnAfterGridMove,
  GRID_OVERSCAN_ROWS,
  resolveGridCardLayout,
  gridMeasuresEquivalent,
  intendedColumnAfterFocus,
  isVerticalGridKey,
  moveGridFocusIndex,
  resolveActiveGameId,
  resolveGridFocusRestore,
  rovingGridTabIndex,
  shouldClearPendingGridFocus,
  viewportStartFromMeasure,
  type GridMeasure,
  type GridNavigationKey,
  type GridWindow,
  type GridWindowInput,
} from '../lib/gridWindow'
import { GameCard } from './GameCard'

type WindowedGameGridProps = {
  games: Game[]
  selectedId: string | null
  activeGameId: string | null
  onActiveGameChange: (gameId: string) => void
  onSelect: (game: Game) => void
  onActivate?: (game: Game) => void
  transferFor?: (game: Game) => { percent: number | null } | null
  isDisabled?: (game: Game) => boolean
  queuedIds?: string[]
  loading?: boolean
  layoutKey: string
  scrollRootRef: RefObject<HTMLElement | null>
  labelledBy: string
}

type GridMeasurement = GridWindowInput & {
  window: GridWindow
  origin: GridMeasure
}

const DEFAULT_CARD_META_HEIGHT = 54
const DEFAULT_COLUMN_GAP = 12
const DEFAULT_ROW_GAP = 16
const DEFAULT_GUTTER = 24
const DEFAULT_PADDING_TOP = 8
const DEFAULT_PADDING_BOTTOM = 40

function initialMeasurement(itemCount: number): GridMeasurement {
  const cardLayout = resolveGridCardLayout(1400, DEFAULT_GUTTER, DEFAULT_COLUMN_GAP)
  const input: GridWindowInput = {
    itemCount,
    containerWidth: 1400,
    viewportStart: 0,
    viewportHeight: 900,
    cardWidth: cardLayout.cardWidth,
    cardHeight: cardLayout.cardWidth * 1.5 + DEFAULT_CARD_META_HEIGHT,
    columnGap: DEFAULT_COLUMN_GAP,
    rowGap: DEFAULT_ROW_GAP,
    paddingInline: DEFAULT_GUTTER,
    paddingTop: DEFAULT_PADDING_TOP,
    paddingBottom: DEFAULT_PADDING_BOTTOM,
    overscanRows: GRID_OVERSCAN_ROWS,
  }
  const window = calculateGridWindow(input)
  return {
    ...input,
    window,
    origin: captureGridMeasure({
      originTop: 0,
      originLeft: 0,
      viewportTop: 0,
      viewportLeft: 0,
      viewportHeight: input.viewportHeight,
      viewportWidth: input.containerWidth,
      columnCount: window.columnCount,
      itemCount,
      rowHeight: input.cardHeight,
      rowGap: input.rowGap,
      overscanRows: input.overscanRows,
    }),
  }
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
  return sameWindow(left.window, right.window) && gridMeasuresEquivalent(left.origin, right.origin)
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
  onActivate,
  transferFor,
  isDisabled,
  queuedIds = [],
  loading = false,
  layoutKey,
  scrollRootRef,
  labelledBy,
}: WindowedGameGridProps) {
  const rootRef = useRef<HTMLDivElement>(null)
  const pendingFocusId = useRef<string | null>(null)
  const focusedGameId = useRef<string | null>(null)
  const gridOwnsFocus = useRef(false)
  const intendedColumnRef = useRef<number | null>(null)
  const retainIntendedColumnRef = useRef(false)
  const scheduleMeasurementRef = useRef<() => void>(() => {})
  const [measurement, setMeasurement] = useState<GridMeasurement>(() =>
    initialMeasurement(games.length),
  )
  const gameIds = useMemo(() => games.map((game) => game.id), [games])
  const gameOrderKey = gameIds.join('\u001f')
  // The fallback is only the current roving tab stop. Do not write it back:
  // keeping the caller's key lets the same game regain focus after a filter.
  const resolvedActiveId = resolveActiveGameId(gameIds, activeGameId, selectedId)
  const activeIndex = resolvedActiveId ? gameIds.indexOf(resolvedActiveId) : -1
  const activeIsMounted =
    activeIndex >= measurement.window.startIndex && activeIndex < measurement.window.endIndex
  const activeIsDisabled = activeIndex >= 0 && (isDisabled?.(games[activeIndex]) ?? false)
  const activeCardIsTabbable = !!resolvedActiveId && activeIsMounted && !activeIsDisabled
  const findControl = useCallback((gameId: string) => {
    return Array.from(rootRef.current?.querySelectorAll<HTMLButtonElement>('button[data-game-id]') ?? []).find(
      (candidate) => candidate.dataset.gameId === gameId,
    )
  }, [])

  const measure = useCallback(() => {
    const gridRoot = rootRef.current
    const scrollRoot = scrollRootRef.current
    if (!gridRoot || !scrollRoot) return

    const style = getComputedStyle(gridRoot)
    const cardMetaHeight = cssPixels(
      style.getPropertyValue('--exo-card-meta-height'),
      DEFAULT_CARD_META_HEIGHT,
    )
    const cardLayout = resolveGridCardLayout(
      gridRoot.clientWidth,
      cssPixels(style.getPropertyValue('--exo-gutter'), DEFAULT_GUTTER),
      cssPixels(style.columnGap, DEFAULT_COLUMN_GAP),
    )
    const cardWidth = cardLayout.cardWidth
    const gridRect = gridRoot.getBoundingClientRect()
    const viewRect = scrollRoot.getBoundingClientRect()
    const cardHeight = cardWidth * 1.5 + cardMetaHeight
    const rowGap = cssPixels(style.rowGap, DEFAULT_ROW_GAP)
    const originProbe = captureGridMeasure({
      originTop: gridRect.top,
      originLeft: gridRect.left,
      viewportTop: viewRect.top,
      viewportLeft: viewRect.left,
      viewportHeight: scrollRoot.clientHeight,
      viewportWidth: scrollRoot.clientWidth,
      columnCount: 1,
      itemCount: games.length,
      rowHeight: cardHeight,
      rowGap,
      overscanRows: GRID_OVERSCAN_ROWS,
    })
    const input: GridWindowInput = {
      itemCount: games.length,
      containerWidth: gridRoot.clientWidth,
      viewportStart: viewportStartFromMeasure(originProbe),
      viewportHeight: originProbe.viewportHeight,
      cardWidth,
      cardHeight,
      columnGap: cssPixels(style.columnGap, DEFAULT_COLUMN_GAP),
      rowGap,
      paddingInline: cssPixels(style.getPropertyValue('--exo-gutter'), DEFAULT_GUTTER),
      paddingTop: cssPixels(
        style.getPropertyValue('--exo-grid-padding-top'),
        DEFAULT_PADDING_TOP,
      ),
      paddingBottom: cssPixels(
        style.getPropertyValue('--exo-grid-padding-bottom'),
        DEFAULT_PADDING_BOTTOM,
      ),
      overscanRows: GRID_OVERSCAN_ROWS,
    }
    const nextWindow = calculateGridWindow(input)
    const origin = captureGridMeasure({
      ...originProbe,
      columnCount: nextWindow.columnCount,
    })
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
    setMeasurement((current) => {
      const next = { ...input, window: nextWindow, origin }
      return sameMeasurement(current, next) ? current : next
    })
  }, [gameOrderKey, games.length, layoutKey, scrollRootRef])

  useLayoutEffect(() => {
    const gridRoot = rootRef.current
    const scrollRoot = scrollRootRef.current
    if (!gridRoot || !scrollRoot) return

    let frame = 0
    const scheduleMeasurement = () => {
      window.cancelAnimationFrame(frame)
      frame = window.requestAnimationFrame(measure)
    }
    scheduleMeasurementRef.current = scheduleMeasurement
    const observer = new ResizeObserver(scheduleMeasurement)
    const releaseFocusOwnership = (event: Event) => {
      const target = event.target
      if (event.type === 'focusin' && target === document.body) return
      if (target instanceof Node && !gridRoot.contains(target)) {
        gridOwnsFocus.current = false
        focusedGameId.current = null
      }
    }
    observer.observe(gridRoot)
    observer.observe(scrollRoot)
    scrollRoot.addEventListener('scroll', scheduleMeasurement, { passive: true })
    document.addEventListener('pointerdown', releaseFocusOwnership, true)
    document.addEventListener('focusin', releaseFocusOwnership, true)
    measure()

    return () => {
      scheduleMeasurementRef.current = () => {}
      window.cancelAnimationFrame(frame)
      observer.disconnect()
      scrollRoot.removeEventListener('scroll', scheduleMeasurement)
      document.removeEventListener('pointerdown', releaseFocusOwnership, true)
      document.removeEventListener('focusin', releaseFocusOwnership, true)
    }
  }, [measure, scrollRootRef])

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

  // Row wrappers are intentionally replaced as the window or column count
  // changes. Keep keyboard ownership on the same keyed game, or on the grid
  // proxy while that game is outside the mounted slice.
  useLayoutEffect(() => {
    applyFocusRestore()
  }, [
    applyFocusRestore,
    layoutKey,
    measurement.window.startIndex,
    measurement.window.endIndex,
    measurement.origin.originTop,
    measurement.origin.viewportTop,
  ])

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
      const viewportTop = scrollRoot.scrollTop
      const viewportBottom = viewportTop + scrollRoot.clientHeight

      if (rowTop < viewportTop) {
        scrollRoot.scrollTo({ top: rowTop })
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
      if (!game || (isDisabled?.(game) ?? false)) return
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
    [findControl, games, isDisabled, onActiveGameChange, revealIndex],
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
    if (isDisabled?.(games[nextIndex]) ?? false) {
      pendingFocusId.current = null
      retainIntendedColumnRef.current = false
      return
    }
    focusIndex(nextIndex)
  }

  const rows = []
  for (let row = measurement.window.startRow; row < measurement.window.endRow; row += 1) {
    const firstIndex = row * measurement.window.columnCount
    const rowGames = games.slice(firstIndex, firstIndex + measurement.window.columnCount)
    rows.push(
      <div className="exo-game-grid-row" role="row" aria-rowindex={row + 1} key={row}>
        {rowGames.map((game, column) => (
          <GameCard
            key={game.id}
            game={game}
            selected={
              game.id === selectedId ||
              !!game.variants?.some((variant) => variant.id === selectedId)
            }
            disabled={isDisabled?.(game) ?? false}
            transfer={transferFor?.(game) ?? null}
            queued={queuedIds.includes(game.id)}
            preload={row < measurement.window.startRow + 2}
            tabIndex={game.id === resolvedActiveId && !(isDisabled?.(game) ?? false) ? 0 : -1}
            gridPosition={{ row: row + 1, column: column + 1 }}
            onFocus={() => onActiveGameChange(game.id)}
            onSelect={() => onSelect(game)}
            onActivate={onActivate ? () => onActivate(game) : undefined}
          />
        ))}
      </div>,
    )
  }

  return (
      <div
        ref={rootRef}
      className="exo-windowed-game-grid"
      role="grid"
      aria-labelledby={labelledBy}
      aria-busy={loading || undefined}
      aria-rowcount={measurement.window.rowCount}
      aria-colcount={measurement.window.columnCount}
      tabIndex={rovingGridTabIndex(activeCardIsTabbable)}
      data-rendered-count={measurement.window.endIndex - measurement.window.startIndex}
      data-column-count={measurement.window.columnCount}
      style={{
        '--exo-grid-columns': measurement.window.columnCount,
        '--exo-card-w': `${measurement.cardWidth}px`,
      } as CSSProperties}
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
      <div className="exo-game-grid" role="rowgroup">
        {rows}
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
