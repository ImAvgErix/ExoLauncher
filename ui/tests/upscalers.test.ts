import assert from 'node:assert/strict'
import test from 'node:test'
import {
  upscalerRows,
  upscalerVisualState,
  type UpscalerStatusItem,
} from '../src/lib/upscalers.ts'

function row(item: UpscalerStatusItem) {
  const found = upscalerRows([item]).find((candidate) => candidate.fileName === item.fileName)
  assert.ok(found)
  return found
}

test('an installed but unusable FSR destination stays neutral', () => {
  const ineligible = row({
    fileName: 'amd_fidelityfx_vk.dll',
    present: true,
    eligible: false,
    currentVersion: '1.0.1.42386',
    currentDisplayVersion: '3.1.4',
    packVersion: '1.0.1.50000',
    packDisplayVersion: '3.1.5',
  })
  assert.equal(ineligible.usable, false)
  assert.equal(upscalerVisualState(ineligible), 'unknown')

  const unsupported = row({
    fileName: 'amd_fidelityfx_loader_dx12.dll',
    present: true,
    eligible: true,
    currentVersion: '4.0.0.604',
    packVersion: '4.0.0.700',
    unsupportedReason: 'FSR 4 requires an RDNA 4 GPU.',
  })
  assert.equal(unsupported.usable, false)
  assert.equal(upscalerVisualState(unsupported), 'unknown')
})

test('a usable older upscaler remains an update signal', () => {
  const usable = row({
    fileName: 'nvngx_dlss.dll',
    present: true,
    eligible: true,
    currentVersion: '310.4.0.0',
    packVersion: '310.7.0.0',
  })
  assert.equal(usable.usable, true)
  assert.equal(upscalerVisualState(usable), 'outdated')
})
