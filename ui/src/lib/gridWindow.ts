export const GRID_OVERSCAN_ROWS = 2
export const GRID_MAX_COLUMNS = 12
export const GRID_MIN_CARD_WIDTH = 144
export const GRID_MAX_CARD_WIDTH = 168

/**
 * Resolve the compact desktop card geometry from the actual grid width.
 * Twelve cards remain possible on genuinely wide windows. At the normal app
 * size we prefer fewer, larger covers so complete titles stay readable.
 */
export function resolveGridCardLayout(
  containerWidth: number,
  paddingInline = 24,
  columnGap = 12,
  minCardWidth = GRID_MIN_CARD_WIDTH,
  maxColumns = GRID_MAX_COLUMNS,
  maxCardWidth = GRID_MAX_CARD_WIDTH,
): { columnCount: number; cardWidth: number } {
  const width = Number.isFinite(containerWidth) ? Math.max(0, containerWidth) : 0
  const padding = Number.isFinite(paddingInline) ? Math.max(0, paddingInline) : 0
  const gap = Number.isFinite(columnGap) ? Math.max(0, columnGap) : 0
  const minimum = Number.isFinite(minCardWidth) ? Math.max(1, minCardWidth) : GRID_MIN_CARD_WIDTH
  const columnsLimit = Math.max(1, Math.floor(maxColumns))
  const innerWidth = Math.max(0, width - padding * 2)
  const columnCount = Math.max(
    1,
    Math.min(columnsLimit, Math.floor((innerWidth + gap) / (minimum + gap))),
  )
  const distributed = (innerWidth - Math.max(0, columnCount - 1) * gap) / columnCount
  return {
    columnCount,
    cardWidth: Math.max(1, Math.min(maxCardWidth, distributed)),
  }
}

export type GridWindowInput = {
  itemCount: number
  containerWidth: number
  viewportStart: number
  viewportHeight: number
  cardWidth: number
  cardHeight: number
  columnGap: number
  rowGap: number
  paddingInline: number
  paddingTop: number
  paddingBottom: number
  overscanRows?: number
}

export type GridWindow = {
  columnCount: number
  rowCount: number
  rowStride: number
  pageRowCount: number
  startRow: number
  endRow: number
  startIndex: number
  endIndex: number
  beforeHeight: number
  renderedHeight: number
  afterHeight: number
  totalHeight: number
}

export type GridNavigationKey =
  | 'ArrowLeft'
  | 'ArrowRight'
  | 'ArrowUp'
  | 'ArrowDown'
  | 'Home'
  | 'End'
  | 'PageUp'
  | 'PageDown'

export type GridFocusMove = {
  currentIndex: number
  itemCount: number
  columnCount: number
  pageRowCount: number
  key: GridNavigationKey
  ctrlKey?: boolean
  /** Visual column to keep across ArrowUp/ArrowDown/PageUp/PageDown. */
  intendedColumn?: number
}

export type GridMeasure = {
  originTop: number
  originLeft: number
  viewportTop: number
  viewportLeft: number
  viewportHeight: number
  viewportWidth: number
  columnCount: number
  itemCount: number
  rowHeight: number
  rowGap: number
  overscanRows: number
}

export type GridFocusRestoreTarget = 'control' | 'grid' | null

function finiteAtLeast(value: number, minimum: number): number {
  return Number.isFinite(value) ? Math.max(minimum, value) : minimum
}

function integerAtLeast(value: number, minimum: number): number {
  return Math.floor(finiteAtLeast(value, minimum))
}

function clamp(value: number, minimum: number, maximum: number): number {
  return Math.min(maximum, Math.max(minimum, value))
}

export function calculateGridWindow(input: GridWindowInput): GridWindow {
  const itemCount = integerAtLeast(input.itemCount, 0)
  const containerWidth = finiteAtLeast(input.containerWidth, 0)
  const viewportHeight = finiteAtLeast(input.viewportHeight, 0)
  const viewportStart = Number.isFinite(input.viewportStart) ? input.viewportStart : 0
  const cardWidth = finiteAtLeast(input.cardWidth, 1)
  const cardHeight = finiteAtLeast(input.cardHeight, 1)
  const columnGap = finiteAtLeast(input.columnGap, 0)
  const rowGap = finiteAtLeast(input.rowGap, 0)
  const paddingInline = finiteAtLeast(input.paddingInline, 0)
  const paddingTop = finiteAtLeast(input.paddingTop, 0)
  const paddingBottom = finiteAtLeast(input.paddingBottom, 0)
  const overscanRows = integerAtLeast(input.overscanRows ?? GRID_OVERSCAN_ROWS, 0)
  const innerWidth = Math.max(0, containerWidth - paddingInline * 2)
  const columnCount = Math.max(1, Math.floor((innerWidth + columnGap) / (cardWidth + columnGap)))
  const rowCount = Math.ceil(itemCount / columnCount)
  const rowStride = cardHeight + rowGap
  const pageRowCount = Math.max(1, Math.floor(viewportHeight / rowStride))

  const totalHeight =
    paddingTop +
    rowCount * cardHeight +
    Math.max(0, rowCount - 1) * rowGap +
    paddingBottom

  if (rowCount === 0) {
    return {
      columnCount,
      rowCount,
      rowStride,
      pageRowCount,
      startRow: 0,
      endRow: 0,
      startIndex: 0,
      endIndex: 0,
      beforeHeight: paddingTop,
      renderedHeight: 0,
      afterHeight: paddingBottom,
      totalHeight,
    }
  }

  const viewportEnd = viewportStart + viewportHeight
  let firstVisibleRow: number
  let endVisibleRow: number

  if (viewportEnd <= paddingTop) {
    firstVisibleRow = 0
    endVisibleRow = 1
  } else if (viewportStart >= totalHeight - paddingBottom) {
    firstVisibleRow = rowCount - 1
    endVisibleRow = rowCount
  } else {
    firstVisibleRow = clamp(
      Math.floor((viewportStart - paddingTop) / rowStride),
      0,
      rowCount - 1,
    )
    endVisibleRow = clamp(
      Math.ceil((viewportEnd - paddingTop) / rowStride),
      firstVisibleRow + 1,
      rowCount,
    )
  }

  const startRow = Math.max(0, firstVisibleRow - overscanRows)
  const endRow = Math.min(rowCount, endVisibleRow + overscanRows)
  const renderedRowCount = endRow - startRow
  const beforeHeight = paddingTop + startRow * rowStride
  const renderedHeight =
    renderedRowCount * cardHeight + Math.max(0, renderedRowCount - 1) * rowGap
  const afterHeight = totalHeight - beforeHeight - renderedHeight

  return {
    columnCount,
    rowCount,
    rowStride,
    pageRowCount,
    startRow,
    endRow,
    startIndex: startRow * columnCount,
    endIndex: Math.min(itemCount, endRow * columnCount),
    beforeHeight,
    renderedHeight,
    afterHeight,
    totalHeight,
  }
}

export function resolveActiveGameId(
  gameIds: readonly string[],
  activeGameId: string | null,
  selectedGameId: string | null,
): string | null {
  if (activeGameId && gameIds.includes(activeGameId)) return activeGameId
  if (selectedGameId && gameIds.includes(selectedGameId)) return selectedGameId
  return gameIds[0] ?? null
}

export function captureGridMeasure(input: {
  originTop: number
  originLeft: number
  viewportTop: number
  viewportLeft: number
  viewportHeight: number
  viewportWidth: number
  columnCount: number
  itemCount: number
  rowHeight: number
  rowGap: number
  overscanRows?: number
}): GridMeasure {
  return {
    originTop: input.originTop,
    originLeft: input.originLeft,
    viewportTop: input.viewportTop,
    viewportLeft: input.viewportLeft,
    viewportHeight: finiteAtLeast(input.viewportHeight, 0),
    viewportWidth: finiteAtLeast(input.viewportWidth, 0),
    columnCount: integerAtLeast(input.columnCount, 1),
    itemCount: integerAtLeast(input.itemCount, 0),
    rowHeight: finiteAtLeast(input.rowHeight, 1),
    rowGap: finiteAtLeast(input.rowGap, 0),
    overscanRows: integerAtLeast(input.overscanRows ?? GRID_OVERSCAN_ROWS, 0),
  }
}

export function viewportStartFromMeasure(measure: GridMeasure): number {
  return measure.viewportTop - measure.originTop
}

export function gridMeasuresEquivalent(left: GridMeasure, right: GridMeasure): boolean {
  return (
    left.originTop === right.originTop &&
    left.originLeft === right.originLeft &&
    left.viewportTop === right.viewportTop &&
    left.viewportLeft === right.viewportLeft &&
    left.viewportHeight === right.viewportHeight &&
    left.viewportWidth === right.viewportWidth &&
    left.columnCount === right.columnCount &&
    left.itemCount === right.itemCount &&
    left.rowHeight === right.rowHeight &&
    left.rowGap === right.rowGap &&
    left.overscanRows === right.overscanRows
  )
}

export function isVerticalGridKey(key: GridNavigationKey): boolean {
  return key === 'ArrowUp' || key === 'ArrowDown' || key === 'PageUp' || key === 'PageDown'
}

export function columnAfterGridMove(
  key: GridNavigationKey,
  nextIndex: number,
  columnCount: number,
  intendedColumn: number | null | undefined,
  currentIndex = nextIndex,
): number {
  const columns = integerAtLeast(columnCount, 1)
  if (isVerticalGridKey(key)) {
    if (intendedColumn == null) {
      const source = Number.isFinite(currentIndex) ? Math.trunc(currentIndex) : 0
      return ((source % columns) + columns) % columns
    }
    return clamp(integerAtLeast(intendedColumn, 0), 0, columns - 1)
  }
  return ((nextIndex % columns) + columns) % columns
}

export function intendedColumnAfterFocus(input: {
  focusedIndex: number
  columnCount: number
  currentIntendedColumn?: number | null
  retainIntendedColumn?: boolean
}): number {
  const columns = integerAtLeast(input.columnCount, 1)
  const actual = ((integerAtLeast(input.focusedIndex, 0) % columns) + columns) % columns
  if (!input.retainIntendedColumn || input.currentIntendedColumn == null) return actual
  return clamp(integerAtLeast(input.currentIntendedColumn, 0), 0, columns - 1)
}

export function shouldClearPendingGridFocus(currentIndex: number, nextIndex: number): boolean {
  return nextIndex === currentIndex
}

export function rovingGridTabIndex(activeCardIsTabbable: boolean): 0 | -1 {
  return activeCardIsTabbable ? -1 : 0
}

export function resolveGridFocusRestore(input: {
  ownsFocus: boolean
  pendingId: string | null
  activeElementIsInsideGrid: boolean
  activeElementIsGrid: boolean
  mountedControlEnabled: boolean
}): GridFocusRestoreTarget {
  if (!input.ownsFocus && input.pendingId == null) return null
  if (input.activeElementIsInsideGrid && !input.activeElementIsGrid) return null
  return input.mountedControlEnabled ? 'control' : 'grid'
}

export function moveGridFocusIndex(input: GridFocusMove): number {
  const itemCount = integerAtLeast(input.itemCount, 0)
  if (itemCount === 0) return -1

  const lastIndex = itemCount - 1
  const columnCount = integerAtLeast(input.columnCount, 1)
  const pageRowCount = integerAtLeast(input.pageRowCount, 1)
  const currentIndex = clamp(integerAtLeast(input.currentIndex, 0), 0, lastIndex)
  const currentRow = Math.floor(currentIndex / columnCount)
  const currentColumn = currentIndex % columnCount
  const column =
    input.intendedColumn == null
      ? currentColumn
      : clamp(integerAtLeast(input.intendedColumn, 0), 0, columnCount - 1)
  const rowStart = currentRow * columnCount
  const lastRow = Math.floor(lastIndex / columnCount)

  const indexAtRow = (row: number): number => {
    if (row < 0 || row > lastRow) return currentIndex
    const nextRowStart = row * columnCount
    const lastInRow = Math.min(lastIndex, nextRowStart + columnCount - 1)
    const candidate = nextRowStart + column
    return candidate <= lastInRow ? candidate : lastInRow
  }

  switch (input.key) {
    case 'ArrowLeft':
      return Math.max(0, currentIndex - 1)
    case 'ArrowRight':
      return Math.min(lastIndex, currentIndex + 1)
    case 'ArrowUp':
      return indexAtRow(currentRow - 1)
    case 'ArrowDown':
      return indexAtRow(currentRow + 1)
    case 'Home':
      return input.ctrlKey ? 0 : rowStart
    case 'End':
      return input.ctrlKey ? lastIndex : Math.min(lastIndex, rowStart + columnCount - 1)
    case 'PageUp':
      return indexAtRow(Math.max(0, currentRow - pageRowCount))
    case 'PageDown':
      return indexAtRow(Math.min(lastRow, currentRow + pageRowCount))
  }
}
