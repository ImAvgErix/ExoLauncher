import assert from 'node:assert/strict'
import test from 'node:test'
import {
  blockedEntitlementLabel,
  canExposeBuyUrl,
  resolveEntitlementPrimaryAction,
  type EntitlementActionGame,
} from '../src/lib/entitlementActions.ts'

function steam(overrides: Partial<EntitlementActionGame> = {}): EntitlementActionGame {
  return {
    installed: true,
    owned: false,
    updateAvailable: false,
    canInstall: false,
    ...overrides,
  }
}

test('explicitly revoked install becomes Buy again, never Play', () => {
  const game = steam({ entitlementState: 'notOwned' })

  const action = resolveEntitlementPrimaryAction(game)
  assert.equal(action, 'none')
  assert.equal(canExposeBuyUrl(game), true)
  assert.equal(blockedEntitlementLabel(game, action), 'Buy again')
})

test('unavailable ownership stays unverified instead of inventing a revocation', () => {
  const game = steam({ entitlementState: 'unverified' })

  const action = resolveEntitlementPrimaryAction(game)
  assert.equal(action, 'none')
  assert.equal(canExposeBuyUrl(game), false)
  assert.equal(blockedEntitlementLabel(game, action), 'Unavailable')
})

test('legacy and verified-owned installed rows still Play', () => {
  assert.equal(resolveEntitlementPrimaryAction(steam()), 'play')
  assert.equal(resolveEntitlementPrimaryAction(steam({ entitlementState: 'owned', owned: true })), 'play')
})
