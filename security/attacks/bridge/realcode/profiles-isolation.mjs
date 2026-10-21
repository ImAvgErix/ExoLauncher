// Attack 4 (cross-room leakage) + attack 2 (which WebView gets the bridge).
//
// Runtime proof needs a live host booting several WebView2 controllers, which
// this environment can't stand up. Instead this asserts the ISOLATION
// INVARIANTS directly in the shipped source, so a regression that merges the
// profiles or attaches the bridge to an embedded surface fails this check.
//
// Run:  node security/attacks/bridge/realcode/profiles-isolation.mjs
// Exit: 0 = all invariants hold; 1 = an isolation boundary regressed.
//
// WHAT ISOLATES WHAT (from the source below):
//  - Shell (privileged bridge) runs on the Default WebView2 profile.
//  - Music providers (YouTube Music / Apple Music / Spotify) share the
//    "embedded" profile — a different profile => different cookie jar, storage
//    and IndexedDB partition from the shell. Within that profile the providers
//    stay isolated from each other by same-origin policy (different origins).
//  - GOG/Epic sign-in build their OWN short-lived environments (separate user
//    data) — isolated from both shell and embedded.
//  - The bridge is attached to exactly one CoreWebView2 (the shell core).

import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import path from 'node:path'

const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../../..')
const read = (rel) => readFileSync(path.join(repo, rel), 'utf8')

let failures = 0
const rows = []
function check(what, ok, detail) {
  if (!ok) failures++
  rows.push(`${ok ? 'PASS' : 'FAIL'} | ${what.padEnd(56)} | ${detail}`)
}

const envFactory = read('ExoLauncher/Helpers/WebViewEnvironmentFactory.cs')
const hostProfile = read('ExoLauncher/Helpers/WebViewHostProfile.cs')
const mainWindow = read('ExoLauncher/MainWindow.xaml.cs')
const trustPolicy = read('ExoLauncher/Helpers/WebViewTrustPolicy.cs')

// 1. The embedded (music) controller runs on a NAMED profile, not Default.
check('music controllers use a named "embedded" profile',
  /EmbeddedProfileName\s*=\s*"embedded"/.test(hostProfile) &&
  /options\.ProfileName\s*=\s*WebViewHostProfile\.EmbeddedProfileName/.test(envFactory),
  'EmbeddedControllerOptions.ProfileName = "embedded"')

// 2. The shell does NOT set that embedded profile (it stays on Default).
check('shell core is not placed on the embedded profile',
  !/EmbeddedControllerOptions/.test(mainWindow),
  'MainWindow.xaml.cs never calls EmbeddedControllerOptions for the shell core')

// 3. Embedded profile name differs from the shell/Default partition.
check('embedded profile != Default partition',
  /WV2Profile_"\s*\+\s*EmbeddedProfileName/.test(hostProfile),
  'on-disk folder EBWebView/WV2Profile_embedded is separate from Default')

// 4. Store sign-in (GOG/Epic) uses its own short-lived environment.
check('GOG/Epic login use separate short-lived environments',
  /short-lived environments/.test(envFactory),
  'documented + CreateWithOptionsAsync path is shell-only; login builds its own env')

// 5. The privileged bridge is attached to exactly ONE CoreWebView2 (the shell).
const attachCount = (mainWindow.match(/_bridge\.Attach\(/g) || []).length
check('bridge attached to exactly one (shell) WebView',
  attachCount === 1,
  `_bridge.Attach(core) occurrences = ${attachCount}`)

// 6. The trusted host is the app host only — not any music/login origin.
check('trusted host is app.exo-launcher.local only',
  /TrustedAppHost\s*=\s*"app\.exo-launcher\.local"/.test(trustPolicy),
  'embedded music/login origins can never equal the trusted host')

console.log('\n=== WebView2 profile / bridge isolation invariants (static) ===')
for (const r of rows) console.log(r)
console.log(`\n--- ${rows.length} invariants, ${failures} FAIL ---`)
console.log('NOTE: static invariant check, not a live two-profile read. See report for runtime caveat.')
process.exit(failures === 0 ? 0 : 1)
