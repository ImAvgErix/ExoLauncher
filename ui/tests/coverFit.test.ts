import assert from 'node:assert/strict'
import test from 'node:test'
import {
  isHeroShaped,
  isPortraitBitmap,
  shouldKeepCoverBitmap,
  shouldArmCoverTimeout,
} from '../src/lib/coverFit.ts'

test('standard Steam heroes stay full bleed in the home banner', () => {
  assert.equal(isHeroShaped(1920, 620), true)
  assert.equal(isHeroShaped(1920, 480), true)
})

test('host-sized posters stay accepted', () => {
  assert.equal(isPortraitBitmap(600, 900), true)
  assert.equal(isPortraitBitmap(300, 450), true)
  assert.equal(isPortraitBitmap(120, 180), true)
})

test('valid last-candidate covers are kept instead of a monogram', () => {
  assert.equal(shouldKeepCoverBitmap(180, 270, { lastCandidate: true }), true)
  assert.equal(shouldKeepCoverBitmap(96, 144, { lastCandidate: true }), true)
  assert.equal(shouldKeepCoverBitmap(32, 48, { lastCandidate: true }), true)
  assert.equal(shouldKeepCoverBitmap(0, 900, { lastCandidate: true }), false)
})

test('non-final candidates still prefer a real poster', () => {
  assert.equal(shouldKeepCoverBitmap(80, 80), false)
  assert.equal(shouldKeepCoverBitmap(1920, 620), false)
  assert.equal(shouldKeepCoverBitmap(600, 900), true)
})

test('icon covers keep a small but real bitmap', () => {
  assert.equal(shouldKeepCoverBitmap(32, 32, { icon: true }), true)
  assert.equal(shouldKeepCoverBitmap(16, 16, { icon: true }), false)
})

test('hidden lazy covers do not exhaust candidates before their room is visible', () => {
  assert.equal(shouldArmCoverTimeout({ eager: false, visible: false }), false)
  assert.equal(shouldArmCoverTimeout({ eager: false, visible: true }), true)
  assert.equal(shouldArmCoverTimeout({ eager: true, visible: false }), true)
})
