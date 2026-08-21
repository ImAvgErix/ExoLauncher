import assert from 'node:assert/strict'
import test from 'node:test'
import {
  calculateGridWindow,
  captureGridMeasure,
  columnAfterGridMove,
  gridMeasuresEquivalent,
  intendedColumnAfterFocus,
  moveGridFocusIndex,
  resolveGridCardLayout,
  resolveActiveGameId,
  resolveGridFocusRestore,
  rovingGridTabIndex,
  shouldClearPendingGridFocus,
} from '../src/lib/gridWindow.ts'

const cssLayout = {
  viewportHeight: 1080,
  viewportStart: 0,
  columnGap: 12,
  rowGap: 16,
  paddingInline: 24,
  paddingTop: 8,
  paddingBottom: 40,
}

test('CSS card metrics produce the expected columns at supported widths', () => {
  const widths = [
    { containerWidth: 1100, cardWidth: 140, expected: 7 },
    { containerWidth: 1400, cardWidth: 148, expected: 8 },
    { containerWidth: 1920, cardWidth: 148, expected: 11 },
  ]

  for (const sample of widths) {
    const result = calculateGridWindow({
      ...cssLayout,
      ...sample,
      itemCount: 5_000,
      cardHeight: sample.cardWidth * 1.5 + 72,
    })
    assert.equal(result.columnCount, sample.expected)
  }
})

test('responsive card layout keeps normal-window covers large and reaches twelve only when wide', () => {
  assert.deepEqual(resolveGridCardLayout(1920), { columnCount: 12, cardWidth: 145 })
  assert.deepEqual(resolveGridCardLayout(1400), { columnCount: 8, cardWidth: 158.5 })
  assert.equal(resolveGridCardLayout(1100).columnCount, 6)
  assert.ok(resolveGridCardLayout(1100).cardWidth >= 144)
})

test('the row slice stays bounded for real, 1k, and 5k libraries', () => {
  for (const itemCount of [154, 1_000, 5_000]) {
    const result = calculateGridWindow({
      ...cssLayout,
      itemCount,
      containerWidth: 1920,
      cardWidth: 148,
      cardHeight: 294,
      // A partial first row is the largest possible 1080px window.
      viewportStart: 3_408,
    })

    assert.equal(result.columnCount, 11)
    assert.ok(result.endIndex - result.startIndex <= 99)
    assert.equal(result.startIndex % result.columnCount, 0)
    assert.ok(
      result.endIndex === itemCount || result.endIndex % result.columnCount === 0,
      'the window must end on a whole-row boundary',
    )
  }
})

test('before, rendered, and after spacers preserve exact total grid height', () => {
  const result = calculateGridWindow({
    ...cssLayout,
    itemCount: 154,
    containerWidth: 1920,
    cardWidth: 148,
    cardHeight: 294,
    viewportStart: 3_408,
  })

  assert.equal(result.rowCount, 14)
  assert.equal(result.rowStride, 310)
  assert.equal(result.beforeHeight, 2_488)
  assert.equal(result.renderedHeight, 1_844)
  assert.equal(result.afterHeight, 40)
  assert.equal(result.totalHeight, 4_372)
  assert.equal(
    result.beforeHeight + result.renderedHeight + result.afterHeight,
    result.totalHeight,
  )
})

test('an active game key survives reorder and falls back predictably when filtered out', () => {
  assert.equal(resolveActiveGameId(['c', 'a', 'b'], 'b', null), 'b')
  assert.equal(resolveActiveGameId(['a', 'c'], 'b', 'c'), 'c')
  assert.equal(resolveActiveGameId(['a', 'c'], 'b', null), 'a')
  assert.equal(resolveActiveGameId([], 'b', 'c'), null)
})

test('grid navigation follows visual rows without activating a game', () => {
  const move = (
    currentIndex: number,
    key: Parameters<typeof moveGridFocusIndex>[0]['key'],
    ctrlKey = false,
    intendedColumn?: number,
  ) =>
    moveGridFocusIndex({
      currentIndex,
      itemCount: 23,
      columnCount: 5,
      pageRowCount: 3,
      key,
      ctrlKey,
      intendedColumn,
    })

  assert.equal(move(7, 'ArrowRight'), 8)
  assert.equal(move(7, 'ArrowLeft'), 6)
  assert.equal(move(7, 'ArrowDown'), 12)
  assert.equal(move(7, 'ArrowUp'), 2)
  assert.equal(move(7, 'Home'), 5)
  assert.equal(move(7, 'End'), 9)
  assert.equal(move(7, 'Home', true), 0)
  assert.equal(move(7, 'End', true), 22)
  assert.equal(move(7, 'PageDown'), 22)
  assert.equal(move(17, 'PageUp'), 2)
  assert.equal(move(2, 'ArrowUp'), 2)
  assert.equal(move(18, 'ArrowDown'), 22)
  assert.equal(move(19, 'ArrowDown'), 22)
  assert.equal(move(1, 'PageUp'), 1)
  assert.equal(move(8, 'PageDown'), 22)
})

test('vertical moves clamp onto a partial last row and keep the intended column', () => {
  const columns = 5
  const step = (
    currentIndex: number,
    intendedColumn: number | null,
    key: Parameters<typeof moveGridFocusIndex>[0]['key'],
  ) => {
    const index = moveGridFocusIndex({
      currentIndex,
      itemCount: 23,
      columnCount: columns,
      pageRowCount: 3,
      key,
      intendedColumn: intendedColumn ?? undefined,
    })
    return {
      index,
      intendedColumn: columnAfterGridMove(key, index, columns, intendedColumn, currentIndex),
    }
  }

  let index = 18
  let intended: number | null = 3
  ;({ index, intendedColumn: intended } = step(index, intended, 'ArrowDown'))
  assert.equal(index, 22)
  assert.equal(intended, 3)
  ;({ index, intendedColumn: intended } = step(index, intended, 'ArrowUp'))
  assert.equal(index, 18)
  assert.equal(intended, 3)

  index = 19
  intended = 4
  ;({ index, intendedColumn: intended } = step(index, intended, 'ArrowDown'))
  assert.equal(index, 22)
  assert.equal(intended, 4)
  ;({ index, intendedColumn: intended } = step(index, intended, 'ArrowUp'))
  assert.equal(index, 19)
  assert.equal(intended, 4)

  index = 8
  intended = 3
  ;({ index, intendedColumn: intended } = step(index, intended, 'PageDown'))
  assert.equal(index, 22)
  assert.equal(intended, 3)
  ;({ index, intendedColumn: intended } = step(index, intended, 'PageUp'))
  assert.equal(index, 8)
  assert.equal(intended, 3)

  index = 2
  intended = 2
  ;({ index, intendedColumn: intended } = step(index, intended, 'ArrowUp'))
  assert.equal(index, 2)
  assert.equal(intended, 2)

  index = 1
  intended = 1
  ;({ index, intendedColumn: intended } = step(index, intended, 'PageUp'))
  assert.equal(index, 1)
  assert.equal(intended, 1)
})

test('an unrecorded intended column uses the focused index, not column 0', () => {
  const columns = 5
  const fromColumnThree = moveGridFocusIndex({
    currentIndex: 18,
    itemCount: 23,
    columnCount: columns,
    pageRowCount: 3,
    key: 'ArrowUp',
  })
  assert.equal(fromColumnThree, 13)
  assert.equal(columnAfterGridMove('ArrowUp', fromColumnThree, columns, null, 18), 3)

  const seededZero = moveGridFocusIndex({
    currentIndex: 18,
    itemCount: 23,
    columnCount: columns,
    pageRowCount: 3,
    key: 'ArrowUp',
    intendedColumn: 0,
  })
  assert.equal(seededZero, 10)
})

test('focus capture records the actual column unless a vertical clamp must be retained', () => {
  assert.equal(
    intendedColumnAfterFocus({
      focusedIndex: 18,
      columnCount: 5,
      currentIntendedColumn: null,
    }),
    3,
  )
  assert.equal(
    intendedColumnAfterFocus({
      focusedIndex: 22,
      columnCount: 5,
      currentIntendedColumn: 3,
      retainIntendedColumn: true,
    }),
    3,
  )
  assert.equal(
    intendedColumnAfterFocus({
      focusedIndex: 22,
      columnCount: 5,
      currentIntendedColumn: 3,
      retainIntendedColumn: false,
    }),
    2,
  )
  assert.equal(
    intendedColumnAfterFocus({
      focusedIndex: 7,
      columnCount: 5,
      currentIntendedColumn: 3,
      retainIntendedColumn: false,
    }),
    2,
  )
})

test('origin and viewport shifts invalidate the measure even when the window size is unchanged', () => {
  const base = captureGridMeasure({
    originTop: 120,
    originLeft: 24,
    viewportTop: 80,
    viewportLeft: 0,
    viewportHeight: 800,
    viewportWidth: 1400,
    columnCount: 8,
    itemCount: 154,
    rowHeight: 294,
    rowGap: 16,
  })
  const shiftedOrigin = captureGridMeasure({
    ...base,
    originTop: 360,
  })
  const shiftedViewport = captureGridMeasure({
    ...base,
    viewportTop: 40,
    viewportHeight: 720,
  })

  assert.equal(gridMeasuresEquivalent(base, { ...base }), true)
  assert.equal(gridMeasuresEquivalent(base, shiftedOrigin), false)
  assert.equal(gridMeasuresEquivalent(base, shiftedViewport), false)
  assert.equal(base.columnCount, shiftedOrigin.columnCount)
  assert.equal(base.itemCount, shiftedOrigin.itemCount)
})

test('pending focus clears on a no-op move and restore stays on the mounted control', () => {
  assert.equal(shouldClearPendingGridFocus(18, 18), true)
  assert.equal(shouldClearPendingGridFocus(18, 22), false)

  assert.equal(
    resolveGridFocusRestore({
      ownsFocus: false,
      pendingId: null,
      activeElementIsInsideGrid: false,
      activeElementIsGrid: false,
      mountedControlEnabled: true,
    }),
    null,
  )
  assert.equal(
    resolveGridFocusRestore({
      ownsFocus: true,
      pendingId: 'g-22',
      activeElementIsInsideGrid: true,
      activeElementIsGrid: false,
      mountedControlEnabled: true,
    }),
    null,
  )
  assert.equal(
    resolveGridFocusRestore({
      ownsFocus: true,
      pendingId: 'g-22',
      activeElementIsInsideGrid: true,
      activeElementIsGrid: true,
      mountedControlEnabled: true,
    }),
    'control',
  )
  assert.equal(
    resolveGridFocusRestore({
      ownsFocus: true,
      pendingId: 'g-22',
      activeElementIsInsideGrid: false,
      activeElementIsGrid: false,
      mountedControlEnabled: false,
    }),
    'grid',
  )
})

test('the grid stays in tab order when the active card cannot take focus', () => {
  assert.equal(rovingGridTabIndex(true), -1)
  assert.equal(rovingGridTabIndex(false), 0)
})
