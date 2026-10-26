// Ways the suite picker could fail without anyone seeing it: a change that reaches the app picks no suite (nothing
// checked), a change that cannot reach it picks some (30 minutes wasted), an app file no rule knows is dropped instead
// of named, a changed suite is not the one that runs. Run: node --test tools/e2e/lib/affected.test.mjs
import test from 'node:test'
import assert from 'node:assert/strict'
import { suitesFor, describePlan } from './affected.mjs'

const KNOWN = ['shell', 'rooms', 'library', 'store', 'discord', 'music', 'system', 'devices', 'capture', 'settings', 'appearance', 'layout', 'motion', 'perf', 'stress', 'zoom']
const pick = (...files) => suitesFor(files, KNOWN)

test('docs, tests, tools and add-on sources reach no suite', () => {
  const plan = pick('README.md', 'docs/architecture/vendors.md', 'ExoLauncher.Tests/HdrTests.cs', 'tools/swap-dev.ps1', '.github/workflows/ci.yaml', 'voice/probe2.py', 'security/attacks/x.cs')
  assert.deepEqual(plan.suites, [])
  assert.equal(plan.ignored.length, 7)
})

test('a store change runs store, and shell rides along', () => {
  assert.deepEqual(pick('ui/src/components/store/StoreGamePage.tsx').suites, ['shell', 'store'])
})

test('suites come back in run order, whatever order the files came in', () => {
  const plan = pick('ui/src/components/store/StoreRoom.tsx', 'ui/src/components/discord/Chat.tsx', 'ExoLauncher/Adapters/GogAdapter.cs')
  assert.deepEqual(plan.suites, ['shell', 'library', 'store', 'discord', 'layout'])
})

test('a token or glass change runs every look-and-feel suite', () => {
  const plan = pick('ui/src/tokens.css')
  for (const s of ['appearance', 'rooms', 'layout', 'motion', 'shell']) assert.ok(plan.suites.includes(s), s)
})

test('a host change in HDR and shaders runs system', () => {
  assert.ok(pick('ExoLauncher/MainWindow.Hdr.cs').suites.includes('system'))
  assert.ok(pick('ExoLauncher/Services/ShadersAddon.cs').suites.includes('system'))
})

test('Exo Mouse and Exo Capture run their own suites', () => {
  assert.ok(pick('mouse/src/main.tsx').suites.includes('devices'))
  assert.ok(pick('capture/src/main.cpp').suites.includes('capture'))
})

test('an app file no rule knows still runs shell and is named', () => {
  const plan = pick('ExoLauncher/Helpers/SomethingNew.cs')
  assert.deepEqual(plan.suites, ['shell'])
  assert.deepEqual(plan.unmapped, ['ExoLauncher/Helpers/SomethingNew.cs'])
  assert.match(describePlan(plan, KNOWN.length).join('\n'), /No rule for 1 app file/)
})

test('a changed suite runs itself, without shell', () => {
  assert.deepEqual(pick('tools/e2e/suites/music.mjs').suites, ['music'])
})

test('the runner or its libraries get the cheap shell run', () => {
  assert.deepEqual(pick('tools/e2e/lib/kit.mjs').suites, ['shell'])
  assert.deepEqual(pick('tools/e2e/run.mjs').suites, ['shell'])
})

test('a suite the runner does not know is never picked', () => {
  assert.deepEqual(suitesFor(['ui/src/components/store/StoreRoom.tsx'], ['shell']).suites, ['shell'])
})

test('nothing changed says so', () => {
  assert.match(describePlan(pick(), KNOWN.length)[0], /no suite to run/)
})
