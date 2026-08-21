import assert from 'node:assert/strict'
import test from 'node:test'
import {
  advanceGamepadFrame,
  canControllerActivate,
  createGamepadFrameState,
  installGamepadNavigation,
  isTypingContext,
  muteGamepadFrame,
  readGamepadInput,
  spatialTargetIndex,
  type ControllerElementLike,
  type GamepadNavigationEnvironment,
} from '../src/lib/gamepadNavigation.ts'

function buttons(pressed: number[] = []) {
  return Array.from({ length: 16 }, (_, index) => ({
    pressed: pressed.includes(index),
    value: pressed.includes(index) ? 1 : 0,
  }))
}

test('standard buttons and the left stick produce one normalized input sample', () => {
  assert.deepEqual(
    readGamepadInput({ buttons: buttons([0, 12]), axes: [0.72, 0.18] }),
    { up: true, down: false, left: false, right: true, activate: true, back: false },
  )

  assert.deepEqual(
    readGamepadInput({ buttons: buttons(), axes: [0.31, -0.42] }),
    { up: false, down: false, left: false, right: false, activate: false, back: false },
  )
})

test('directions fire on the rising edge and then use a bounded repeat cadence', () => {
  let state = createGamepadFrameState()
  let frame = advanceGamepadFrame({ right: true }, state, 100)
  assert.deepEqual(frame.actions, ['right'])
  state = frame.state

  frame = advanceGamepadFrame({ right: true }, state, 300)
  assert.deepEqual(frame.actions, [])
  state = frame.state

  frame = advanceGamepadFrame({ right: true }, state, 430)
  assert.deepEqual(frame.actions, ['right'])
  state = frame.state

  frame = advanceGamepadFrame({ right: false }, state, 440)
  assert.deepEqual(frame.actions, [])
  frame = advanceGamepadFrame({ right: true }, frame.state, 450)
  assert.deepEqual(frame.actions, ['right'])
})

test('Back wins simultaneous input and A never repeats while held', () => {
  let state = createGamepadFrameState()
  let frame = advanceGamepadFrame({ activate: true, back: true, down: true }, state, 0)
  assert.deepEqual(frame.actions, ['back'])
  state = frame.state

  frame = advanceGamepadFrame({ activate: true, back: true, down: true }, state, 500)
  assert.deepEqual(frame.actions, ['down'])
  state = advanceGamepadFrame({}, frame.state, 510).state

  frame = advanceGamepadFrame({ activate: true }, state, 520)
  assert.deepEqual(frame.actions, ['activate'])
  frame = advanceGamepadFrame({ activate: true }, frame.state, 900)
  assert.deepEqual(frame.actions, [])
})

test('muted typing or resume frames require release before any held control acts', () => {
  let state = muteGamepadFrame({ activate: true, right: true })
  let frame = advanceGamepadFrame({ activate: true, right: true }, state, 10_000)
  assert.deepEqual(frame.actions, [])

  state = advanceGamepadFrame({}, frame.state, 10_010).state
  frame = advanceGamepadFrame({ activate: true, right: true }, state, 10_020)
  assert.deepEqual(frame.actions, ['activate'])
})

test('spatial navigation prefers aligned targets in the requested half-plane', () => {
  const rects = [
    { left: 0, top: 0, right: 20, bottom: 20 },
    { left: 100, top: 0, right: 120, bottom: 20 },
    { left: 28, top: 48, right: 48, bottom: 68 },
    { left: 0, top: 100, right: 20, bottom: 120 },
  ]

  assert.equal(spatialTargetIndex(rects, 0, 'right'), 1)
  assert.equal(spatialTargetIndex(rects, 0, 'down'), 3)
  assert.equal(spatialTargetIndex(rects, 0, 'left'), null)
})

test('typing contexts suppress the controller and activation needs both explicit attributes', () => {
  assert.equal(isTypingContext({ tagName: 'INPUT' }), true)
  assert.equal(isTypingContext({ tagName: 'DIV', isContentEditable: true }), true)
  assert.equal(isTypingContext({ tagName: 'SPAN', closest: () => ({}) }), true)
  assert.equal(isTypingContext({ tagName: 'BUTTON', closest: () => null }), false)

  const element = (attributes: string[], disabled = false): ControllerElementLike => ({
    disabled,
    hasAttribute: (name) => attributes.includes(name),
    getAttribute: () => null,
    closest: () => null,
  })
  assert.equal(canControllerActivate(element(['data-controller-target', 'data-controller-safe'])), true)
  assert.equal(canControllerActivate(element(['data-controller-target'])), false)
  assert.equal(canControllerActivate(element(['data-controller-safe'])), false)
  assert.equal(canControllerActivate(element(['data-controller-target', 'data-controller-safe'], true)), false)
})

test('no-pad probing stops until visibility or a connection event changes', () => {
  let visibility: DocumentVisibilityState = 'hidden'
  let polls = 0
  let nextFrame = 1
  const frames = new Map<number, FrameRequestCallback>()
  const documentListeners = new Map<string, EventListener>()
  const gamepadListeners = new Map<string, EventListener>()
  const cancelled: number[] = []

  const environment: GamepadNavigationEnvironment = {
    document: {
      get visibilityState() { return visibility },
      activeElement: null,
      addEventListener: (type, listener) => documentListeners.set(type, listener),
      removeEventListener: (type) => documentListeners.delete(type),
      querySelector: () => null,
    },
    navigator: {
      getGamepads: () => {
        polls += 1
        return []
      },
    },
    requestAnimationFrame: (callback) => {
      const id = nextFrame++
      frames.set(id, callback)
      return id
    },
    cancelAnimationFrame: (id) => {
      cancelled.push(id)
      frames.delete(id)
    },
    dispatchBack: () => {},
    gamepadEvents: {
      addEventListener: (type, listener) => gamepadListeners.set(type, listener),
      removeEventListener: (type) => gamepadListeners.delete(type),
    },
  }

  const dispose = installGamepadNavigation(environment)
  assert.equal(frames.size, 0)
  assert.equal(polls, 0)

  visibility = 'visible'
  documentListeners.get('visibilitychange')?.({} as Event)
  assert.equal(frames.size, 1)
  const first = frames.entries().next().value as [number, FrameRequestCallback]
  frames.delete(first[0])
  first[1](16)
  assert.equal(polls, 1)
  assert.equal(frames.size, 0)

  gamepadListeners.get('gamepadconnected')?.({} as Event)
  assert.equal(frames.size, 1)
  const probe = frames.entries().next().value as [number, FrameRequestCallback]
  frames.delete(probe[0])
  probe[1](32)
  assert.equal(polls, 2)
  assert.equal(frames.size, 0)

  visibility = 'hidden'
  documentListeners.get('visibilitychange')?.({} as Event)
  assert.equal(frames.size, 0)
  assert.equal(cancelled.length, 0)
  dispose()
  assert.equal(documentListeners.has('visibilitychange'), false)
  assert.equal(gamepadListeners.has('gamepadconnected'), false)
  assert.equal(gamepadListeners.has('gamepaddisconnected'), false)
})
