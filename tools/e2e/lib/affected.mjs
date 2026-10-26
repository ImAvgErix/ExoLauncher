// Which suites a change can reach: the files that changed, mapped to the suites that look at them. A full run takes
// about 30 minutes and restarts Exo, so `run.mjs --changed` runs only these (and `--plan` only says which).
//
// A wrong mapping skips checks without a sound, so it errs wide: every matching rule adds its suites, a path that
// belongs to the app but to no rule still runs `shell` (startup, app.log and the console clean) and is named, and
// `shell` rides along with any app change. Extend RULES when a plan misses a suite.
import { execFileSync } from 'node:child_process'

/** Paths that never reach the running app (docs, tests, tools, add-on sources the installed app does not load from here). */
const NO_APP = [
  /^docs\//, /\.md$/i, /^\.github\//, /^\.agents\//, /^security\//, /^e2e-reports\//, /^ExoLauncher\.Tests\//,
  /^tools\//, /^voice\//, /^perf-audit\//, /^brand\//, /^setup\//, /^out\//, /^artifacts\//, /^TestResults\//,
  /^ui\/(scripts|contracts|\.audit-fixtures)\//, /^\.gitignore$|^\.gitattributes$|^\.editorconfig$|^\.cursorignore$|^\.gitleaks\.toml$/,
  /^LICENSE|^VERSION$|^BannedSymbols\.txt$|^global\.json$|^Directory\.Build\./, /^ExoLauncher\.sln$/,
]

/** [path, suites]: every rule that matches adds its suites. */
const RULES = [
  [/^ui\/src\/components\/store\/|^ui\/src\/styles\/(store|shelf)\.css$|StoreService|WebHostBridge\.Store|StoreLinks/, ['store']],
  [/^ui\/src\/components\/discord\/|^ui\/src\/styles\/discord[\w-]*\.css$|Services\/DiscordClient\/|WebHostBridge\.Discord|MainWindow\.Discord/, ['discord', 'layout']],
  [/^ui\/src\/components\/(music\/|MusicRoom\.tsx|HostedRoom\.tsx)|ui\/src\/lib\/(musicAdapters|player|music)\.ts$|^ui\/src\/styles\/music[\w-]*\.css$|Helpers\/(Spotify|YouTubeMusic|AppleMusic|EmbeddedClient)|MainWindow\.(Music|SpotifyRoom|WebMusicRoom)|MediaSessionService|MusicAppVolume|MusicNowPlaying/, ['music', 'layout']],
  [/^ui\/src\/components\/(gpu|graphics|shaders|system)\/|^ui\/src\/styles\/system\.css$|Services\/(GpuProfile|RadeonProfile|IntelProfile|ShadersAddon|ScsKiller|DlssSwap|NvidiaTrayIcon)|Helpers\/(Gpu|Nvidia|Amd|Intel|WindowsHdr|SdrInHdr|SystemSpecs)|MainWindow\.(Hdr|Shaders|Dlss)|WebHostBridge\.(Gpu|Display|Hdr|Shaders|PcHealth)/, ['system', 'layout']],
  [/^mouse\/|DevicesRoom\.tsx|ui\/src\/lib\/mouse(Relay|State)\.ts$|Services\/Hid\/|WebHostBridge\.Hid/, ['devices']],
  [/^capture\/|MainWindow\.Capture|Services\/CaptureAddon|ui\/src\/lib\/capture\.ts$|ui\/src\/components\/captures\//, ['capture']],
  [/SettingsPanel|ui\/src\/components\/(settings|onboarding)\/|^ui\/src\/styles\/settings\.css$|SettingsService|Models\/AppSettings/, ['settings']],
  [/^ui\/src\/components\/(game|upscalers)\/|^ui\/src\/components\/(GameCard|GamePage|WindowedGameGrid|CoverArt|ActivateKeySheet|UpscalerFiles)\.tsx$|shell\/(LibraryPane|FolderPanel|FolderSheet)|^ui\/src\/styles\/(library|game[\w-]*|home)\.css$|^ExoLauncher\/Adapters\/|LaunchOrchestrator|LibraryService|CoverArtService|ui\/src\/lib\/(library|folders|coverThumb)\.ts$/, ['library']],
  [/^ui\/src\/components\/(shell\/|ui\/|LauncherApp)|ExoIsland|BootGate|Switch\.tsx$|MainWindow\.xaml|^ExoLauncher\/(App\.xaml|Program\.cs)|WebHostBridge\.cs$|ui\/src\/lib\/host/, ['rooms', 'stress']],
  [/^ui\/src\/(tokens|exo-shell|index)\.css$|^ui\/src\/styles\/(glass|chrome|rooms|hosted-room|motion)[\w-]*\.css$|ui\/src\/lib\/(glass|appearance|exoTheme)\.ts$/, ['appearance', 'rooms', 'layout', 'motion']],
  [/^ui\/src\/styles\/|^ui\/src\/.*\.css$/, ['layout']],
  [/MainWindow\.xaml\.cs$|PageScale|exo-shell\.css$/, ['zoom']],
  [/Helpers\/(BackgroundTrim|ManagedProcess|ForegroundWatcher|WebViewMemoryPolicy|HostPriority)|MainWindow\.xaml\.cs$/, ['perf']],
]

/** `tools/e2e` itself: a changed suite runs; the runner or its libraries get the cheap `shell` sanity run. */
function e2eRule(path) {
  const suite = /^tools\/e2e\/suites\/([\w-]+)\.mjs$/.exec(path)
  if (suite) return [suite[1]]
  return /^tools\/e2e\/(run\.mjs|cdp\.mjs|lib\/)/.test(path) ? ['shell'] : null
}

/** The app's own source: a path here that no rule knows is named in the plan, not silently dropped. */
const APP_SOURCE = /^(ExoLauncher|ui\/src|mouse\/src|capture\/src)\//

/**
 * @param {string[]} files repo-relative paths, forward slashes
 * @param {string[]} known the suite names that exist, in run order
 * @returns {{ suites: string[], why: Record<string, string[]>, unmapped: string[], ignored: string[] }}
 */
export function suitesFor(files, known) {
  const why = {}
  const unmapped = []
  const ignored = []
  let firstApp = null
  const add = (suite, file) => { if (known.includes(suite)) (why[suite] ??= []).push(file) }
  for (const file of files) {
    const e2e = e2eRule(file)
    if (e2e) { for (const s of e2e) add(s, file); continue }
    if (NO_APP.some(re => re.test(file))) { ignored.push(file); continue }
    firstApp ??= file
    const hits = RULES.filter(([re]) => re.test(file))
    if (!hits.length) { if (APP_SOURCE.test(file)) unmapped.push(file); else ignored.push(file); continue }
    for (const [, suites] of hits) for (const s of suites) add(s, file)
  }
  if (unmapped.length) for (const f of unmapped) add('shell', f)
  // shell rides along with any app change, even when every rule pointed at a suite the runner does not know
  if (firstApp) add('shell', firstApp + ' (any app change)')
  return { suites: known.filter(s => why[s]), why, unmapped, ignored }
}

/** Files changed against `base` (default HEAD: staged and unstaged edits) plus untracked ones, from the repo root. */
export function changedFiles(repo, base = 'HEAD') {
  // stderr is dropped: git warns about line endings once per changed file, which buries the plan.
  const git = (...args) => execFileSync('git', args, { cwd: repo, encoding: 'utf8', maxBuffer: 64 << 20, stdio: ['ignore', 'pipe', 'ignore'] }).split(/\r?\n/).filter(Boolean)
  const tracked = git('diff', '--name-only', '--no-renames', base, '--')
  const untracked = git('ls-files', '--others', '--exclude-standard')
  return [...new Set([...tracked, ...untracked])].map(f => f.replace(/\\/g, '/')).sort()
}

/** The plan as lines for the console. */
export function describePlan({ suites, why, unmapped, ignored }, total) {
  const lines = [suites.length
    ? `${suites.length} of ${total} suites can be reached by this change: ${suites.join(', ')}`
    : 'Nothing in this change reaches the running app: no suite to run.']
  for (const s of suites) {
    const files = [...new Set(why[s])]
    lines.push(`  ${s.padEnd(10)} ${files.slice(0, 3).join(', ')}${files.length > 3 ? `, +${files.length - 3} more` : ''}`)
  }
  if (unmapped.length) lines.push(`No rule for ${unmapped.length} app file(s), so only the shell suite covers them (add a rule in lib/affected.mjs): ${unmapped.slice(0, 5).join(', ')}${unmapped.length > 5 ? ', ...' : ''}`)
  if (ignored.length) lines.push(`${ignored.length} file(s) never reach the running app (docs, tests, tools, add-on sources).`)
  return lines
}
