# Exo's end-to-end suite

The installed Exo, driven the way a person drives it and checked the way a person would see it. Node 22+, no
packages, Windows with PowerShell 7.

```
node tools/e2e/run.mjs                       # everything (about 30 minutes)
node tools/e2e/run.mjs --suites rooms,stress # some suites
node tools/e2e/run.mjs --skip zoom --quick   # fewer rounds, no restarts as a 4K screen
node tools/e2e/run.mjs --attach --keep       # drive the Exo already running on --port 9339 and leave it there
node tools/e2e/run.mjs --allow-game                # run although a game is playing (its real clicks and window moves may take focus from the game)
node tools/e2e/run.mjs --changed --plan      # which suites the changed files can reach; touches nothing
node tools/e2e/run.mjs --changed             # run only those (changes against HEAD, untracked files included)
node tools/e2e/run.mjs --changed main        # the same against another base
```

`--changed` maps files to suites in `lib/affected.mjs` (tests: `node --test tools/e2e/lib/affected.test.mjs`). It errs
wide: any app change also runs `shell`, and an app file no rule knows is named in the plan instead of dropped. `--suites`
names suites outright and wins over `--changed`.

The report lands in `e2e-reports/<time>/`: `summary.md` (read this), `report.json` (every check with its evidence and
numbers), `screenshots/` and `SHA256SUMS.txt` (`sha256sum -c SHA256SUMS.txt` in Git Bash checks the folder). Each check
is **PASS**, **WARN** (works, but a budget or a soft rule was missed), **FAIL** or **SKIP** (couldn't be tried here,
and why). Exit code 0: nothing failed; 1: a check failed; 2: the run itself couldn't go on.

## What it checks

| Suite | What |
| --- | --- |
| `shell` | Startup time (from app.log's milestones), app.log and the console clean, **the screen showing the whole page** (a screenshot of the screen against the page's own picture), the window filling its monitor, the Bar's parts, the window buttons clicked for real. |
| `rooms` | Every Appearance chosen through Settings, then every room in it: layout (nothing cut off by the window, no sideways scroll, no control under something), **contrast measured from pixels** (words against what is really behind them, glass and art included), no block in the other theme's color, pictures loaded, controls named, no console errors. |
| `library` | Every sort, a game's sheet by click and keyboard, its menu, Ctrl+K to a game, a folder made, opened, recolored and deleted, the + menu, Continue, the whole grid scrolled. |
| `store` | Shelves, the banner's picks, a shelf's arrows, search, the calendar (days, exact dates only, its filters), a game's page with its requirements. Nothing bought or wishlisted; Free on Epic (Epic's own site) is never clicked. |
| `discord` | Signed in, the lists ending clear of the user panel, the test channel opened, a message sent, reacted to and deleted, invite links (live and dead), server pages, Discord's settings, a profile. Only in the test place, the Exo server: #mod-chat (the test message is deleted even when a step fails), and a call in hangout joined muted and deafened, then left, mute and deafen put back as they were. |
| `music` | Home, search, a playlist and back, the player. Nothing is played. |
| `system` | The PC room: Overview's About card and each of its rows opening its page, the room coming back to the page it was left on, the driver and its newest version on Graphics, Display's menus (opened, never changed), Shaders' games, and Shaders' engine leaving once the board is off screen. |
| `devices` | Exo Mouse loads, finds the mouse, wears Exo's Appearance. |
| `capture` | Capture settings (the Captures sheet's own) from the Capture menu and the sheet's button, showing Make recordings smaller as saved. Snip from the title bar's Capture menu, round after round (Exo Capture's overlay is given Return, so each snip is of the screen under the cursor, and goes to the Recycle Bin after): the overlay showing within 150 ms, the editor drawn no larger than it is shown, the page's memory and WebView2's back where they were after, and no `webview-process-failed` in app.log. |
| `settings` | Every section, every switch in General and Downloads turned and turned back, Text size, Escape. |
| `appearance` | What floats (a game's sheet, Ctrl+K, a menu, Settings, Discord's settings) in every theme; Match Windows following Windows live; the first frames after a start already in Light. |
| `layout` | Every room and what it opens (a game's sheet, menus, Ctrl+K, each Settings section, a Store page, Music's search, a playlist and panels, each page of PC, Discord's channel with its member list, threads, pins, inbox, server pages, a forum and each settings section) in every Appearance, then at three window sizes and the largest text size (and `zoom` as on a 1440p and a 4K screen): no two controls over each other, no control cut by what holds it, no words cut off (`lib/page.mjs` › `layoutFaults`, also part of every full look). Each fault names the element's path and box and is cropped into `screenshots/`. |
| `motion` | Frames timed against the screen's own rate while Exo moves (rooms, a sheet, menus, Ctrl+K); no animation over 0.7 s; Reduce motion stops the movement. |
| `perf` | Room changes (p95), switching into Discord, Library wheel scrolling, the page's memory over rounds of use, idle CPU of Exo and its WebView2, host call times. |
| `stress` | Rooms clicked faster than they open, sheets, Ctrl+K and menus opened and shut over and over, the window resized again and again, the theme flipped; then Exo must be whole, error-free, and not bigger. |
| `zoom` | Exo restarted as on a 1440p and a 4K screen (`EXO_PAGE_SCREEN`): the whole page on screen, real clicks landing on the tabs, a smaller window; then each monitor. |

## Rules it keeps

- Never during a Discord call (it would end it) or while a game runs: it stops before starting.
- It closes Exo normally, never kills it, and starts it with a loopback-only debug port in its own environment.
- `settings.json`, `web-client-preferences.json` and `window-placement.json` are saved first and put back byte for byte
  at the end (`lib/exo.mjs` › GUARDED), then Exo starts again normally.
- Nothing is bought, installed, played aloud or posted outside the Discord test place; real mouse clicks and window
  moves happen on Exo's own window only (keep hands off the mouse while `shell`, `stress` and `zoom` run).
- Nothing personal reaches the report: e-mail addresses and long ids are masked.

## How it is built

`run.mjs` runs the suites in `suites/`. `lib/check.mjs` records checks and writes the report; `lib/exo.mjs` starts,
closes and reads Exo (its log, its host); `lib/win.ps1` (through `lib/win.mjs`) is the screen and the real mouse:
captures of what the screen shows, clicks, window placement, monitors, process CPU and memory; `lib/page.mjs` holds
the probes that run inside the page; `lib/contrast.mjs` measures words against the pixels behind them;
`lib/image.mjs` compares the screen with the page; `lib/kit.mjs` is what every suite uses (going to a room, closing
what floats, choosing an Appearance, the full look at a view). `cdp.mjs` is the DevTools client.

A new check goes in the suite it belongs to: `run.check(suite, id, name, async (c) => [...failures])`, with
`c.warn`, `c.skip`, `c.metric(name, value, unit, budget)`, `c.shot(label)` and `c.evidence`. Look at the screen, not
only the page: `screenMatchesPage` (suites/shell.mjs) and `t.win.capture` are how.
