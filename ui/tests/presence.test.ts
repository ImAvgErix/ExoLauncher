import assert from 'node:assert/strict'
import test from 'node:test'
import {
  applyPresenceEvent,
  downgradePresenceRoster,
  projectPresenceRoster,
  type PresenceByUser,
} from '../src/lib/presence.ts'

const successfulRoster = (): PresenceByUser => ({
  alpha: {
    userId: 'alpha',
    status: 'online',
    gameId: null,
    gameTitle: null,
    lastSeen: '2026-08-21T00:00:00Z',
    available: true,
  },
  bravo: {
    userId: 'bravo',
    status: 'ingame',
    gameId: 'steam:42',
    gameTitle: 'Example Game',
    lastSeen: null,
    available: true,
  },
})

test('a roster failure after success atomically makes every live claim unavailable', () => {
  const current = successfulRoster()
  const next = downgradePresenceRoster(current)

  assert.notEqual(next, current)
  assert.deepEqual(Object.keys(next), ['alpha', 'bravo'])
  assert.deepEqual(next.alpha, {
    ...current.alpha,
    status: 'unknown',
    gameId: null,
    gameTitle: null,
    available: false,
  })
  assert.deepEqual(next.bravo, {
    ...current.bravo,
    status: 'unknown',
    gameId: null,
    gameTitle: null,
    available: false,
  })
  assert.equal(current.bravo.status, 'ingame', 'the transition must not mutate the prior snapshot')
})

test('a transport error with no user id invalidates the whole roster', () => {
  const next = applyPresenceEvent(successfulRoster(), {
    kind: 'transportError',
    presence: null,
    error: { code: 'TRANSPORT_UNAVAILABLE', message: 'Presence is unavailable.' },
  })

  assert.equal(next.alpha.available, false)
  assert.equal(next.bravo.available, false)
  assert.equal(next.bravo.gameId, null)
  assert.equal(next.bravo.gameTitle, null)
})

test('a per-user failure downgrades only that user and clears only their activity', () => {
  const current = successfulRoster()
  const next = applyPresenceEvent(current, {
    kind: 'presence',
    presence: {
      userId: 'bravo',
      status: 'ingame',
      gameId: 'stale-game',
      gameTitle: 'Stale Game',
      available: false,
    },
  })

  assert.equal(next.alpha, current.alpha)
  assert.deepEqual(next.bravo, {
    userId: 'bravo',
    status: 'unknown',
    gameId: null,
    gameTitle: null,
    available: false,
  })
})

test('a mixed REST roster keeps authoritative users and downgrades only unavailable users', () => {
  const current = successfulRoster()
  const next = projectPresenceRoster([
    current.alpha,
    { ...current.bravo, available: false },
  ])

  assert.equal(next.alpha.status, 'online')
  assert.equal(next.alpha.available, true)
  assert.equal(next.bravo.status, 'unknown')
  assert.equal(next.bravo.available, false)
  assert.equal(next.bravo.gameId, null)
  assert.equal(next.bravo.gameTitle, null)
})

test('a later authoritative event restores only its user', () => {
  const unavailable = downgradePresenceRoster(successfulRoster())
  const next = applyPresenceEvent(unavailable, {
    kind: 'presence',
    presence: {
      userId: 'alpha',
      status: 'away',
      gameId: null,
      gameTitle: null,
      available: true,
    },
  })

  assert.equal(next.alpha.status, 'away')
  assert.equal(next.alpha.available, true)
  assert.equal(next.bravo.status, 'unknown')
  assert.equal(next.bravo.available, false)
})
