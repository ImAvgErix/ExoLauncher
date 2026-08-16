import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { test } from 'node:test'
import { fileURLToPath } from 'node:url'
import { presentStoreClients } from '../../ui/src/lib/storeClients.ts'

const repoRoot = join(dirname(fileURLToPath(import.meta.url)), '..', '..')

function readUi(...relative: string[]): string {
  return readFileSync(join(repoRoot, 'ui', ...relative), 'utf8')
}

test('empty stores stay empty — no default roster', () => {
  assert.deepEqual(presentStoreClients([]), [])
})

test('only clientPresent === true gets a row', () => {
  assert.deepEqual(
    presentStoreClients([
      { store: 'steam', clientPresent: true },
      { store: 'epic', clientPresent: false },
      { store: 'gog', agentPresent: true },
      { store: 'riot' },
      { store: 'xbox', clientPresent: true, agentPresent: false },
      { store: 'local', clientPresent: true },
    ]),
    [
      { store: 'steam', clientPresent: true },
      { store: 'xbox', clientPresent: true, agentPresent: false },
    ],
  )
})

test('agentPresent never creates a row, including as a fallback', () => {
  assert.deepEqual(
    presentStoreClients([
      { store: 'gog', agentPresent: true, clientPresent: false },
      { store: 'epic', agentPresent: true },
    ]),
    [],
  )
})

test('Settings and Onboarding only render present clients', () => {
  const settings = readUi('src', 'components', 'SettingsPanel.tsx')
  const onboarding = readUi('src', 'components', 'OnboardingPanel.tsx')
  const helper = readUi('src', 'lib', 'storeClients.ts')
  const panels = settings + onboarding

  assert.match(helper, /clientPresent === true/)
  assert.doesNotMatch(helper, /agentPresent/)
  assert.doesNotMatch(helper, /\?\?/)

  assert.match(settings, /presentStoreClients/)
  assert.match(onboarding, /presentStoreClients/)
  assert.match(settings, /storeRows\.length > 0/)
  assert.match(onboarding, /rows\.length > 0/)

  assert.doesNotMatch(panels, /clientPresent \?\?/)
  assert.doesNotMatch(panels, /displayName: 'Steam'/)
  assert.doesNotMatch(panels, /Not installed/)
  assert.doesNotMatch(panels, /stores\.length \? stores/)
})
