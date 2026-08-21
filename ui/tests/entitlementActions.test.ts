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
  const missing = steam({ installed: false, entitlementState: 'unverified' })
  const missingAction = resolveEntitlementPrimaryAction(missing)
  assert.equal(missingAction, 'none')
  assert.equal(canExposeBuyUrl(missing), false)
  assert.equal(blockedEntitlementLabel(missing, missingAction), 'Unavailable')

  const installed = steam({ installed: true, entitlementState: 'unverified' })
  assert.equal(resolveEntitlementPrimaryAction(installed), 'play')
  assert.equal(canExposeBuyUrl(installed), false)
  assert.equal(blockedEntitlementLabel(installed, 'play'), null)
})

test('legacy and verified-owned installed rows still Play', () => {
  assert.equal(resolveEntitlementPrimaryAction(steam()), 'play')
  assert.equal(resolveEntitlementPrimaryAction(steam({ entitlementState: 'owned', owned: true })), 'play')
})
