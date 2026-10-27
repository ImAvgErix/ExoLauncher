import type { CpuPorts, Peripheral, PeripheralPort } from '../../lib/host'

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

/** The chips between it and the processor, when that means something ("1 chip"). */
export function chipWords(port: PeripheralPort): string | null {
  if (port.chips == null) return null
  return port.chips === 0 ? 'Straight to the processor' : port.chips === 1 ? '1 chip in between' : `${port.chips} chips in between`
}

/** What a device's kind is called in a sentence ("your mouse", "your keyboard"). */
const NOUN: Partial<Record<Peripheral['kind'], string>> = { mouse: 'mouse', keyboard: 'keyboard', controller: 'controller' }

/**
 * The one line the Devices room says about ports, only when something would help: the devices that would gain from
 * the processor's own USB (mouse first, then keyboard, then controller) and how many of its ports are free; or a hub
 * one of them goes through. Null when everything is where it should be.
 */
export function portAdvice(devices: Peripheral[], cpu: CpuPorts | null | undefined): string | null {
  const wanting = devices.filter(d => d.port?.better === 'free' && NOUN[d.kind]).sort((a, b) => order(a.kind) - order(b.kind))
  const hubbed = devices.find(d => d.port?.better === 'hub' && NOUN[d.kind])
  if (wanting.length && cpu && cpu.free > 0) {
    const names = [...new Set(wanting.map(d => NOUN[d.kind]!))]
    const one = names.length === 1
    const who = one ? `Your ${names[0]}` : `Your ${names.slice(0, -1).join(', ')} and ${names[names.length - 1]}`
    const how = wanting.every(d => d.port?.where === 'chipset') ? `${one ? 'goes' : 'go'} through the chipset` : `${one ? "isn't" : "aren't"} on the processor’s own USB`
    const free = cpu.free === 1 ? '1 port on the processor’s own USB is free' : `${cpu.free} ports on the processor’s own USB are free`
    return `${who} ${how}. ${free}.`
  }
  if (hubbed) return `Your ${NOUN[hubbed.kind]} goes through a hub. Plug it straight into the PC.`
  return null
}

const order = (kind: Peripheral['kind']) => kind === 'mouse' ? 0 : kind === 'keyboard' ? 1 : 2
