// Attack 1 (XSS) — structural scan. React only injects raw HTML through a
// known set of sinks. If none of them carry untrusted text, escaping above is
// the whole story. This fails if any HTML-injection sink appears in ui/src.
//
// Run:  node security/attacks/bridge/xss/scan-sinks.mjs
// Exit: 0 = no raw-HTML sinks found; 1 = a sink exists (inspect it by hand).

import { readdirSync, readFileSync, statSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import path from 'node:path'

const uiSrc = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../../../ui/src')

// Raw-HTML / script-execution sinks in a React + DOM codebase.
const SINKS = [
  /dangerouslySetInnerHTML/,
  /\.innerHTML\s*=/,
  /\.outerHTML\s*=/,
  /insertAdjacentHTML/,
  /document\.write/,
  /\beval\s*\(/,
  /new\s+Function\s*\(/,
]

function* files(dir) {
  for (const name of readdirSync(dir)) {
    const full = path.join(dir, name)
    if (statSync(full).isDirectory()) { yield* files(full); continue }
    if (/\.(ts|tsx|js|jsx)$/.test(name)) yield full
  }
}

const hits = []
for (const file of files(uiSrc)) {
  const text = readFileSync(file, 'utf8')
  text.split(/\r?\n/).forEach((line, i) => {
    for (const sink of SINKS) if (sink.test(line)) hits.push(`${path.relative(uiSrc, file)}:${i + 1}: ${line.trim()}`)
  })
}

console.log('\n=== XSS sink scan over ui/src ===')
if (hits.length === 0) {
  console.log('PASS | no dangerouslySetInnerHTML / innerHTML / insertAdjacentHTML / document.write / eval / new Function')
  process.exit(0)
}
console.log('FOUND raw-HTML/exec sinks (verify each feeds only trusted constants):')
for (const hit of hits) console.log('  ' + hit)
process.exit(1)
