// Every control on the page is small glass (styles/glass.css › "Only backgrounds are AMOLED black"). The
// controls are the rules that draw with --exo-fill; this rewrites the :where() list in glass.css from them.
// Run it after adding or removing a control: npm run glass:controls (check-only with --check).
import fs from 'node:fs'
import path from 'node:path'

const root = path.resolve(import.meta.dirname, '../src')
const glass = path.join(root, 'styles/glass.css')
const files = [
  ...fs.readdirSync(path.join(root, 'styles')).filter(f => f.endsWith('.css') && f !== 'glass.css').sort().map(f => path.join(root, 'styles', f)),
  path.join(root, 'exo-shell.css'),
]
// Discord's dock draws your bar on its own glass (system.css), so the bar itself is not a control.
// A color swatch shows its own color: a backdrop would dim the color and pixelate the 28px square.
const skip = new Set(['.exo-dc .exo-dc-me', '.exo-dc-swatch'])

function splitTop(selector) {
  const out = []
  let depth = 0, current = ''
  for (const ch of selector) {
    if (ch === '(') depth++
    else if (ch === ')') depth--
    if (ch === ',' && depth === 0) { out.push(current); current = '' } else current += ch
  }
  out.push(current)
  return out.map(s => s.split(/\s+/).join(' ').trim()).filter(Boolean)
}

const controls = []
for (const file of files) {
  const text = fs.readFileSync(file, 'utf8').replace(/\/\*[\s\S]*?\*\//g, '')
  for (const [, selector, body] of text.matchAll(/([^{}]+)\{([^{}]*)\}/g)) {
    if (!/background(-color)?:\s*var\(--exo-fill\)/.test(body)) continue
    for (let s of splitTop(selector)) {
      if (s.startsWith('@')) continue
      s = s.replace(/:(hover|focus-visible|focus|active|disabled)\b/g, '').trim()
      if (s && !skip.has(s) && !controls.includes(s)) controls.push(s)
    }
  }
}

// Read with LF line ends: a Windows checkout (CI's runner, core.autocrlf) has CRLF, where the markers below never matched.
const css = fs.readFileSync(glass, 'utf8').replace(/\r\n/g, '\n')
const start = css.indexOf(':where(\n')
const end = css.indexOf('\n) {\n  backdrop-filter: var(--exo-control-glass);', start)
if (start < 0 || end < 0) throw new Error('glass.css has no control list to rewrite')
const next = css.slice(0, start) + ':where(\n  ' + controls.join(',\n  ') + css.slice(end)
if (process.argv.includes('--check')) {
  if (next !== css) { console.error('glass.css: the control list is out of date; run npm run glass:controls'); process.exit(1) }
} else if (next !== css) fs.writeFileSync(glass, next)
console.log(`${controls.length} controls`)
