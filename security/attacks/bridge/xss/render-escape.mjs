// Attack 1 (XSS) — render the exact untrusted-text patterns the Exo UI uses
// and prove React escapes them. Renders with the app's own installed React 19.
//
// Run:  node security/attacks/bridge/xss/render-escape.mjs
// Exit: 0 = every payload rendered inert; 1 = a live tag/handler escaped.
//
// The patterns mirror real render sites (file:line in comments). Each is the
// JSX `{value}` text-child form React auto-escapes:
//   - game title            ui/src/components/* library cards        -> <div>{game.title}</div>
//   - store description      ui/src/components/store/StoreGamePage.tsx:171 -> <p>{game.description}</p>
//   - playlist/header desc   ui/src/components/music/pages.tsx:117   -> <p>{header.description}</p>
//   - song / track name      music lists                            -> <span>{track.name}</span>
//   - custom "Add folder" title                                     -> <div>{game.title}</div>

import { createRequire } from 'node:module'
import { fileURLToPath } from 'node:url'
import path from 'node:path'

// Resolve React from the UI package so we use the SHIPPED version, not a copy.
const uiDir = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../../../ui')
const req = createRequire(path.join(uiDir, 'package.json'))
const React = req('react')
const { renderToStaticMarkup } = req('react-dom/server')
const h = React.createElement

const PAYLOADS = {
  'img onerror':     '<img src=x onerror=alert(1)>',
  'script tag':      '<script>alert(document.domain)</script>',
  'javascript: href':'javascript:alert(1)',
  'svg onload':      '<svg/onload=alert(1)>',
  'closing + inject':'</p><img src=x onerror=alert(1)><p>',
  'attr breakout':   '" onmouseover="alert(1)',
  'long unicode':    'A‮​﻿' + '\u{1f480}'.repeat(500) + '<img src=x onerror=alert(1)>',
}

// The render sites, as functions of the payload string.
const SITES = {
  'game title':        v => h('div', { className: 'card-title' }, v),
  'store description': v => h('p', { className: 'exo-sgp-about' }, v),
  'playlist desc':     v => h('p', { className: 'exo-music-header-desc' }, v),
  'song name':         v => h('span', { className: 'track' }, v),
  'add-folder title':  v => h('div', { className: 'card-title' }, v),
}

let failures = 0
const rows = []

// A rendered fragment is SAFE when every `<`/`>` from the payload was escaped
// to &lt;/&gt;, so the payload lives entirely inside one text node and cannot
// start a tag or attribute. We isolate the text node (between the wrapper's
// opening `>` and closing `<`) and assert it carries no raw angle bracket:
// without a raw `<`, no tag, script or event handler can form from text.
function innerText(markup) {
  const open = markup.indexOf('>')
  const close = markup.lastIndexOf('<')
  return open >= 0 && close > open ? markup.slice(open + 1, close) : markup
}

for (const [siteName, site] of Object.entries(SITES)) {
  for (const [payloadName, payload] of Object.entries(PAYLOADS)) {
    let markup = ''
    let threw = null
    try { markup = renderToStaticMarkup(site(payload)) } catch (e) { threw = e }
    const inner = innerText(markup)
    const rawAngle = inner.includes('<') || inner.includes('>')
    const safe = !threw && !rawAngle
    if (!safe) failures++
    const sample = markup.length > 72 ? markup.slice(0, 72) + '…' : markup
    rows.push(`${safe ? 'PASS' : 'FAIL'} | ${siteName.padEnd(18)} | ${payloadName.padEnd(16)} | ${sample}`)
  }
}

console.log('\n=== XSS escape proof (Exo UI React 19 render) ===')
console.log('PASS = payload rendered as inert escaped text (no live tag/handler)\n')
for (const r of rows) console.log(r)
console.log(`\n--- ${rows.length} renders, ${failures} FAIL ---`)
process.exit(failures === 0 ? 0 : 1)
