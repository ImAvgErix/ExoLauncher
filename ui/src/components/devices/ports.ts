import type { PeripheralPort } from '../../lib/host'

/** Where a device meets the PC, in words ("Through the chipset", "Processor's own USB"). */
export function portWords(port: PeripheralPort): string {
  const hubs = port.hubs === 1 ? 'a hub' : `${port.hubs} hubs`
  switch (port.where) {
    case 'cpu': return port.hubs ? `Processor's own USB, through ${hubs}` : "Processor's own USB"
    case 'chipset': return port.hubs ? `Through ${hubs} and the chipset` : 'Through the chipset'
    case 'addin': return port.hubs ? `Through ${hubs} and an extra USB chip` : 'On an extra USB chip'
    case 'dock': return 'Through a dock'
    case 'bluetooth': return 'Bluetooth'
    default: return 'Unknown'
  }
}

/**
 * The port in a word or two, for a card's line next to how it's linked ("Wired · Chipset USB"), always shown; null over
 * Bluetooth or when Exo can't tell. Find them gives the whole sentence while plugs move.
 */
export function portShort(port: PeripheralPort): string | null {
  const via = port.hubs ? ' via hub' : ''
  switch (port.where) {
    case 'cpu': return `CPU USB${via}`
    case 'chipset': return `Chipset USB${via}`
    case 'addin': return `Add-in USB${via}`
    case 'dock': return 'Dock'
    default: return null
  }
}

/** The chips between it and the processor, when that means something ("1 chip"). */
export function chipWords(port: PeripheralPort): string | null {
  if (port.chips == null) return null
  return port.chips === 0 ? 'Straight to the processor' : port.chips === 1 ? '1 chip in between' : `${port.chips} chips in between`
}
