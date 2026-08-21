export type ControllerDirection = 'up' | 'down' | 'left' | 'right'
export type ControllerAction = ControllerDirection | 'activate' | 'back'

export type ControllerInput = {
  up: boolean
  down: boolean
  left: boolean
  right: boolean
  activate: boolean
  back: boolean
}

export type GamepadFrameState = {
  pressed: ControllerInput
  repeatAt: Record<ControllerDirection, number>
}

export type ControllerRect = {
  left: number
  top: number
  right: number
  bottom: number
}

export type ControllerElementLike = {
  tagName?: string
  isContentEditable?: boolean
  disabled?: boolean
  hidden?: boolean
  hasAttribute: (name: string) => boolean
  getAttribute: (name: string) => string | null
  closest: (selector: string) => unknown
}

type ControllerDomElement = ControllerElementLike & {
  querySelectorAll: (selector: string) => ArrayLike<unknown>
  getBoundingClientRect: () => DOMRect
  getClientRects?: () => ArrayLike<unknown>
  focus?: (options?: FocusOptions) => void
  scrollIntoView?: (options?: ScrollIntoViewOptions) => void
  click?: () => void
}

type GamepadLike = {
  axes: ArrayLike<number>
  buttons: ArrayLike<{ pressed?: boolean; value?: number }>
  connected?: boolean
  id?: string
  index?: number
  mapping?: string
}

type ControllerDocumentLike = {
  readonly visibilityState: DocumentVisibilityState
  readonly activeElement: unknown
  addEventListener: (type: 'visibilitychange', listener: EventListener) => void
  removeEventListener: (type: 'visibilitychange', listener: EventListener) => void
  querySelector: (selector: string) => unknown
}

export type GamepadNavigationEnvironment = {
  document: ControllerDocumentLike
  navigator: { getGamepads: () => ArrayLike<GamepadLike | null> }
  requestAnimationFrame: (callback: FrameRequestCallback) => number
  cancelAnimationFrame: (id: number) => void
  dispatchBack: () => void
  gamepadEvents: {
    addEventListener: (type: 'gamepadconnected' | 'gamepaddisconnected', listener: EventListener) => void
    removeEventListener: (type: 'gamepadconnected' | 'gamepaddisconnected', listener: EventListener) => void
  }
}

const DIRECTIONS: readonly ControllerDirection[] = ['up', 'down', 'left', 'right']
const EMPTY_INPUT: ControllerInput = {
  up: false,
  down: false,
  left: false,
  right: false,
  activate: false,
  back: false,
}
const TARGET_SELECTOR = '[data-controller-target]'
const SCOPE_SELECTOR = '[data-controller-scope]'
const MAX_CONTROLLER_TARGETS = 256
const DEFAULT_DEADZONE = 0.55
const INITIAL_REPEAT_MS = 300
const REPEAT_MS = 100

function normalizeInput(input: Partial<ControllerInput>): ControllerInput {
  return {
    up: input.up === true,
    down: input.down === true,
    left: input.left === true,
    right: input.right === true,
    activate: input.activate === true,
    back: input.back === true,
  }
}

function hasControllerInput(input: ControllerInput) {
  return input.up || input.down || input.left || input.right || input.activate || input.back
}

function buttonPressed(buttons: GamepadLike['buttons'], index: number) {
  const button = buttons[index]
  return button?.pressed === true || (button?.value ?? 0) >= 0.5
}

export function readGamepadInput(gamepad: Pick<GamepadLike, 'axes' | 'buttons'>, deadzone = DEFAULT_DEADZONE): ControllerInput {
  const axisX = Number(gamepad.axes[0] ?? 0)
  const axisY = Number(gamepad.axes[1] ?? 0)
  let up = buttonPressed(gamepad.buttons, 12) || axisY <= -deadzone
  let down = buttonPressed(gamepad.buttons, 13) || axisY >= deadzone
  let left = buttonPressed(gamepad.buttons, 14) || axisX <= -deadzone
  let right = buttonPressed(gamepad.buttons, 15) || axisX >= deadzone

  // A malformed controller should not make focus oscillate between opposites.
  if (up && down) up = down = false
  if (left && right) left = right = false

  return {
    up,
    down,
    left,
    right,
    activate: buttonPressed(gamepad.buttons, 0),
    back: buttonPressed(gamepad.buttons, 1),
  }
}

export function createGamepadFrameState(): GamepadFrameState {
  return {
    pressed: { ...EMPTY_INPUT },
    repeatAt: { up: 0, down: 0, left: 0, right: 0 },
  }
}

export function muteGamepadFrame(input: Partial<ControllerInput>): GamepadFrameState {
  const pressed = normalizeInput(input)
  return {
    pressed,
    repeatAt: {
      up: pressed.up ? Number.POSITIVE_INFINITY : 0,
      down: pressed.down ? Number.POSITIVE_INFINITY : 0,
      left: pressed.left ? Number.POSITIVE_INFINITY : 0,
      right: pressed.right ? Number.POSITIVE_INFINITY : 0,
    },
  }
}

export function advanceGamepadFrame(
  input: Partial<ControllerInput>,
  previous: GamepadFrameState,
  now: number,
): { actions: ControllerAction[]; state: GamepadFrameState } {
  const pressed = normalizeInput(input)
  const repeatAt = { ...previous.repeatAt }
  const movement: ControllerDirection[] = []

  for (const direction of DIRECTIONS) {
    if (!pressed[direction]) {
      repeatAt[direction] = 0
      continue
    }
    if (!previous.pressed[direction]) {
      movement.push(direction)
      repeatAt[direction] = now + INITIAL_REPEAT_MS
      continue
    }
    if (now >= previous.repeatAt[direction]) {
      movement.push(direction)
      repeatAt[direction] = now + REPEAT_MS
    }
  }

  const backEdge = pressed.back && !previous.pressed.back
  const activateEdge = pressed.activate && !previous.pressed.activate
  const actions: ControllerAction[] = backEdge
    ? ['back']
    : activateEdge
      ? ['activate']
      : movement

  return { actions, state: { pressed, repeatAt } }
}

function intervalGap(aStart: number, aEnd: number, bStart: number, bEnd: number) {
  if (aEnd < bStart) return bStart - aEnd
  if (bEnd < aStart) return aStart - bEnd
  return 0
}

export function spatialTargetIndex(
  rects: readonly ControllerRect[],
  currentIndex: number,
  direction: ControllerDirection,
): number | null {
  const current = rects[currentIndex]
  if (!current) return null
  const currentX = (current.left + current.right) / 2
  const currentY = (current.top + current.bottom) / 2
  const vertical = direction === 'up' || direction === 'down'
  const candidates: Array<{ index: number; primary: number; cross: number }> = []

  rects.forEach((rect, index) => {
    if (index === currentIndex) return
    const x = (rect.left + rect.right) / 2
    const y = (rect.top + rect.bottom) / 2
    if (direction === 'up' && y >= currentY) return
    if (direction === 'down' && y <= currentY) return
    if (direction === 'left' && x >= currentX) return
    if (direction === 'right' && x <= currentX) return

    const primary = vertical ? Math.abs(y - currentY) : Math.abs(x - currentX)
    const cross = vertical
      ? intervalGap(current.left, current.right, rect.left, rect.right)
      : intervalGap(current.top, current.bottom, rect.top, rect.bottom)
    candidates.push({ index, primary, cross })
  })

  if (candidates.length === 0) return null
  const aligned = candidates.filter((candidate) => candidate.cross === 0)
  const pool = aligned.length > 0 ? aligned : candidates
  pool.sort((a, b) => (a.primary + a.cross * 2.5) - (b.primary + b.cross * 2.5) || a.index - b.index)
  return pool[0]?.index ?? null
}

export function isTypingContext(target: unknown): boolean {
  if (!target || typeof target !== 'object') return false
  const element = target as Partial<ControllerElementLike>
  const tag = element.tagName?.toUpperCase()
  if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || element.isContentEditable === true) return true
  try {
    return Boolean(element.closest?.('input, textarea, select, [contenteditable]:not([contenteditable="false"])'))
  } catch {
    return false
  }
}

export function canControllerActivate(element: ControllerElementLike | null): boolean {
  if (!element) return false
  if (!element.hasAttribute('data-controller-target') || !element.hasAttribute('data-controller-safe')) return false
  if (element.disabled || element.hidden || element.getAttribute('aria-disabled') === 'true') return false
  try {
    return !element.closest('[inert], [hidden], [aria-hidden="true"]')
  } catch {
    return false
  }
}

function asDomElement(value: unknown): ControllerDomElement | null {
  if (!value || typeof value !== 'object') return null
  const element = value as Partial<ControllerDomElement>
  return typeof element.hasAttribute === 'function' && typeof element.closest === 'function'
    ? element as ControllerDomElement
    : null
}

function controllerScope(documentLike: ControllerDocumentLike) {
  const active = asDomElement(documentLike.activeElement)
  return asDomElement(active?.closest(SCOPE_SELECTOR)) ?? asDomElement(documentLike.querySelector(SCOPE_SELECTOR))
}

function availableTargets(scope: ControllerDomElement) {
  const nodes = scope.querySelectorAll(TARGET_SELECTOR)
  const targets: ControllerDomElement[] = []
  for (let index = 0; index < nodes.length && targets.length < MAX_CONTROLLER_TARGETS; index += 1) {
    const element = asDomElement(nodes[index])
    if (!element || element.disabled || element.hidden || element.getAttribute('aria-disabled') === 'true') continue
    if (element.closest('[inert], [hidden], [aria-hidden="true"]')) continue
    const rect = element.getBoundingClientRect()
    if (rect.width <= 0 || rect.height <= 0 || element.getClientRects?.().length === 0) continue
    targets.push(element)
  }
  return targets
}

function moveControllerFocus(documentLike: ControllerDocumentLike, direction: ControllerDirection) {
  const scope = controllerScope(documentLike)
  if (!scope) return
  const targets = availableTargets(scope)
  if (targets.length === 0) return
  const active = asDomElement(documentLike.activeElement)
  const activeTarget = asDomElement(active?.closest(TARGET_SELECTOR))
  const currentIndex = activeTarget ? targets.indexOf(activeTarget) : -1
  const nextIndex = currentIndex < 0
    ? 0
    : spatialTargetIndex(targets.map((target) => target.getBoundingClientRect()), currentIndex, direction)
  if (nextIndex === null || nextIndex < 0) return
  const next = targets[nextIndex]
  next?.focus?.({ preventScroll: true })
  next?.scrollIntoView?.({ block: 'nearest', inline: 'nearest' })
}

function activateControllerTarget(documentLike: ControllerDocumentLike) {
  const active = asDomElement(documentLike.activeElement)
  const target = asDomElement(active?.closest(TARGET_SELECTOR))
  if (!canControllerActivate(target)) return
  target?.click?.()
}

function firstConnectedGamepad(gamepads: ArrayLike<GamepadLike | null>) {
  const connected = Array.from(gamepads).filter((gamepad): gamepad is GamepadLike => gamepad?.connected !== false)
  return connected.find((gamepad) => gamepad.mapping === 'standard') ?? connected[0] ?? null
}

function browserEnvironment(): GamepadNavigationEnvironment | null {
  if (typeof document === 'undefined' || typeof navigator === 'undefined' || typeof window === 'undefined') return null
  if (typeof navigator.getGamepads !== 'function') return null
  return {
    document,
    navigator,
    requestAnimationFrame: (callback) => window.requestAnimationFrame(callback),
    cancelAnimationFrame: (id) => window.cancelAnimationFrame(id),
    dispatchBack: () => window.dispatchEvent(new KeyboardEvent('keydown', {
      key: 'Escape',
      code: 'Escape',
      bubbles: true,
      cancelable: true,
    })),
    gamepadEvents: window,
  }
}

export function installGamepadNavigation(environment = browserEnvironment()): () => void {
  if (!environment) return () => {}
  const runtime = environment
  let frameId: number | null = null
  let disposed = false
  let primed = false
  let activePad = ''
  let state = createGamepadFrameState()

  const schedule = () => {
    if (disposed || frameId !== null || runtime.document.visibilityState !== 'visible') return
    frameId = runtime.requestAnimationFrame(tick)
  }

  const reset = () => {
    primed = false
    activePad = ''
    state = createGamepadFrameState()
  }

  const stop = () => {
    if (frameId !== null) runtime.cancelAnimationFrame(frameId)
    frameId = null
    reset()
  }

  function tick(now: number) {
    frameId = null
    if (disposed) return
    if (runtime.document.visibilityState !== 'visible') {
      reset()
      return
    }
    let gamepad: GamepadLike | null = null
    try {
      gamepad = firstConnectedGamepad(runtime.navigator.getGamepads())
    } catch {
      reset()
      return
    }
    if (!gamepad) {
      reset()
      return
    }

    const padKey = `${gamepad.index ?? 0}:${gamepad.id ?? ''}`
    const input = readGamepadInput(gamepad)
    if (!primed || activePad !== padKey) {
      primed = true
      activePad = padKey
      state = muteGamepadFrame(input)
      schedule()
      return
    }

    if (!hasControllerInput(input)) {
      if (hasControllerInput(state.pressed)) state = advanceGamepadFrame(input, state, now).state
      schedule()
      return
    }
    if (isTypingContext(runtime.document.activeElement)) {
      state = muteGamepadFrame(input)
      schedule()
      return
    }

    const frame = advanceGamepadFrame(input, state, now)
    state = frame.state
    for (const action of frame.actions) {
      if (action === 'back') runtime.dispatchBack()
      else if (action === 'activate') activateControllerTarget(runtime.document)
      else moveControllerFocus(runtime.document, action)
    }
    schedule()
  }

  const onVisibilityChange: EventListener = () => {
    if (runtime.document.visibilityState === 'visible') schedule()
    else stop()
  }
  const onGamepadConnectionChange: EventListener = () => schedule()
  runtime.document.addEventListener('visibilitychange', onVisibilityChange)
  runtime.gamepadEvents.addEventListener('gamepadconnected', onGamepadConnectionChange)
  runtime.gamepadEvents.addEventListener('gamepaddisconnected', onGamepadConnectionChange)
  schedule()

  return () => {
    disposed = true
    stop()
    runtime.document.removeEventListener('visibilitychange', onVisibilityChange)
    runtime.gamepadEvents.removeEventListener('gamepadconnected', onGamepadConnectionChange)
    runtime.gamepadEvents.removeEventListener('gamepaddisconnected', onGamepadConnectionChange)
  }
}
