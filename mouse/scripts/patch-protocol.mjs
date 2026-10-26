// Fixes in @openmouse/protocol that Exo Mouse needs before upstream ships them.
// Each patch names the version it was written against and stops the build when
// that changes, so a fixed release is noticed and the patch deleted.
import { readFileSync, writeFileSync } from 'node:fs'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../node_modules/@openmouse/protocol')
const version = JSON.parse(readFileSync(resolve(root, 'package.json'), 'utf8')).version
if (version !== '0.47.0') throw new Error(`@openmouse/protocol is ${version}; check scripts/patch-protocol.mjs against it.`)

const files = {
  'dist/finalmouse/index.js': [
    // Starlight X actuation (command 53): the decoder dropped the whole reply unless it also carried
    // rapid trigger, so a firmware that answers the two actuation values alone lost its actuation too.
    [
      '            if (payload.length < 8)\n                return null;\n            const thrR',
      '            if (payload.length < 4)\n                return null;\n            const thrR',
    ],
    // A receiver whose mouse has not linked yet (just back from its cable) answers battery 255:
    // not known, so the last reading stays instead of "255 %".
    [
      '        case 38: {\n            if (payload.length < 3)\n                return null;',
      '        case 38: {\n            if (payload.length < 3 || payload[0] > 100)\n                return null;',
    ],
    [
      '            return thrR === null || thrL === null || hystR === null || hystL === null\n                ? null\n                : { tmrThrR: thrR, tmrThrL: thrL, tmrHystR: hystR, tmrHystL: hystL };',
      '            return thrR === null || thrL === null\n                ? null\n                : { tmrThrR: thrR, tmrThrL: thrL, ...(hystR === null || hystL === null ? {} : { tmrHystR: hystR, tmrHystL: hystL }) };',
    ],
  ],
  // Starlight X receiver light, as xpanel has it (xpanel.finalmouse.com, dongle LED
  // page and its protocol chunk): command 20 takes Off 0, Battery 1, Custom colour 2
  // and TMR-DS 4 (Starlight X only); the custom colour is command 54, three bytes
  // r g b, read by sending 54 with no payload. The driver refused mode 4 and never
  // asked for the colour, so neither could be shown or set.
  'dist/drivers/finalmouse/hid.js': [
    [
      '        if (![0, 1, 2].includes(mode))\n            throw new Error("Unsupported Finalmouse dongle LED mode.");',
      '        if (![0, 1, 2, ...(this.isSlx ? [4] : [])].includes(mode))\n            throw new Error("Unsupported Finalmouse dongle LED mode.");',
    ],
    [
      '            await this.write(tx, FINALMOUSE_COMMAND.pawLod);\n        }',
      '            await this.write(tx, FINALMOUSE_COMMAND.pawLod);\n            await this.write(tx, FINALMOUSE_COMMAND.indicatorColor);\n        }',
    ],
    [
      '            finalmouseDongleLedMode: state.dongleLedMode ?? null,\n',
      '            finalmouseDongleLedMode: state.dongleLedMode ?? null,\n            finalmouseIndicatorColor: state.indicatorColor ? { ...state.indicatorColor } : null,\n',
    ],
    [
      '        await this.write(this.txReport, FINALMOUSE_COMMAND.indicatorColor, encodeFinalmouseIndicatorColor(r, g, b));\n    }',
      '        await this.write(this.txReport, FINALMOUSE_COMMAND.indicatorColor, encodeFinalmouseIndicatorColor(r, g, b));\n        this.telemetry.indicatorColor = { r, g, b };\n    }',
    ],
    // A Starlight X on its cable (361d:0310/0311) speaks the receiver's protocol on the same vendor
    // page and report IDs (xpanel's transport treats slx_mouse and slx_dongle alike), but the driver
    // took only receivers. On the cable it says so, and has no receiver signal.
    [
      'FINALMOUSE_SLX_DONGLE_PRODUCT_IDS, FINALMOUSE_VENDOR_ID as PROTOCOL_VENDOR_ID',
      'FINALMOUSE_SLX_DONGLE_PRODUCT_IDS, FINALMOUSE_SLX_MOUSE_PRODUCT_IDS, FINALMOUSE_VENDOR_ID as PROTOCOL_VENDOR_ID',
    ],
    [
      '        return FINALMOUSE_SLX_DONGLE_PRODUCT_IDS.includes(device.productId)\n            && device.collections',
      '        return [...FINALMOUSE_SLX_DONGLE_PRODUCT_IDS, ...FINALMOUSE_SLX_MOUSE_PRODUCT_IDS].includes(device.productId)\n            && device.collections',
    ],
    [
      '        return FINALMOUSE_SLX_DONGLE_PRODUCT_IDS.includes(this.device.productId);',
      '        return [...FINALMOUSE_SLX_DONGLE_PRODUCT_IDS, ...FINALMOUSE_SLX_MOUSE_PRODUCT_IDS].includes(this.device.productId);',
    ],
    [
      '        const connectionDetail = [\n            "2.4 GHz receiver",',
      '        const cabled = FINALMOUSE_SLX_MOUSE_PRODUCT_IDS.includes(this.device.productId);\n        const connectionDetail = cabled ? "Cable" : [\n            "2.4 GHz receiver",',
    ],
    ['            connectionType: "Wireless",', '            connectionType: cabled ? "Wired" : "Wireless",'],
    [
      '            signalStrength: finalmouseSignalStrength(state.rssiDbm, state.linkState),',
      '            signalStrength: cabled ? null : finalmouseSignalStrength(state.rssiDbm, state.linkState),',
    ],
    // A Starlight X that did not answer was called an UltralightX.
    [
      '            throw new Error("The Finalmouse UltralightX did not return its current settings.',
      '            throw new Error(`The ${this.displayName()} did not return its current settings.',
    ],
    [
      ' Make sure the mouse is on and close xpanel or other Finalmouse software.");',
      ' Make sure the mouse is on and close xpanel or other Finalmouse software.`);',
    ],
  ],
  // With the receiver in too, the cabled Starlight X is the one picked: it reaches the mouse directly.
  'dist/drivers/registry.js': [
    [
      'create: (device) => new FinalmouseHidClient(device), score: () => 10 },',
      'create: (device) => new FinalmouseHidClient(device), score: (device) => (device.productId === 0x0310 || device.productId === 0x0311 ? 11 : 10) },',
    ],
  ],
}

let changed = 0
for (const [path, patches] of Object.entries(files)) {
  const file = resolve(root, path)
  let code = readFileSync(file, 'utf8')
  const before = code
  for (const [from, to] of patches) {
    if (code.includes(to)) continue
    if (!code.includes(from)) throw new Error(`patch-protocol: ${path} changed; update the patch.`)
    code = code.replace(from, to)
  }
  if (code !== before) { writeFileSync(file, code); changed++ }
}
console.log(changed ? 'patch-protocol: Finalmouse Starlight X driver patched.' : 'patch-protocol: already patched.')
