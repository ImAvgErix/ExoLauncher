# Contributing to Exo Launcher

Exo Launcher is source-available, not open source ([LICENSE](LICENSE)). Contributions are welcome all the same: a bug fix, a store or game that is read wrongly, a clearer sentence, a translation of the help. This page says what you are agreeing to and what a change needs.

## What you agree to

By sending a pull request, patch or any other contribution you confirm that:

1. you wrote it, or have the right to submit it;
2. it does not include code from a project whose license forbids that (copy-left code belongs in the add-ons, not in Exo Launcher), and it adds no dependency without saying so;
3. you grant the copyright holder a perpetual, worldwide, royalty-free and irrevocable license to use, change and distribute it as part of Exo Launcher under any terms, which is the clause in [LICENSE](LICENSE). You keep your own copyright.

Add a `Signed-off-by: Your Name <email>` line to each commit (`git commit -s`) to say you agree. The add-ons are different: a contribution to [Exo Capture](https://github.com/ImAvgErix/ExoLauncher/blob/main/capture), [Exo Mouse](https://github.com/ImAvgErix/ExoLauncher/blob/main/mouse) or Exo Shaders is under that program's own license (GPL-3.0-or-later or AGPL-3.0-or-later), as its README says.

## Before you start

- A small fix: open the pull request. A new feature, a new store, or anything that changes what Exo sends over the network: open an [issue](https://github.com/ImAvgErix/ExoLauncher/issues) first so you do not build something that cannot ship.
- Read [BUILDING.md](BUILDING.md) to build it and [ARCHITECTURE.md](https://github.com/ImAvgErix/ExoLauncher/blob/main/ARCHITECTURE.md) for the shape. [DESIGN.md](https://github.com/ImAvgErix/ExoLauncher/blob/main/DESIGN.md) is the brief for anything visual.

## What a change needs

- It builds and passes `npm run check`, `npm run dead` and the host tests ([BUILDING.md](BUILDING.md) lists them). Several rules are enforced by those checks, not by review.
- The smallest complete change: reuse before adding, delete what the change replaces.
- A bug fix comes with a test that fails without it, when the failure is silent (a parser, a path rule, an update or network policy).
- A change a user will notice gets a [CHANGELOG](CHANGELOG.md) line in plain words, ending with how it was checked.

## The lines that do not move

These are not up for discussion; a change that crosses one is closed.

- Exo never edits a game's executable or touches anti-cheat, never kills a process that is anti-cheat, and never modifies Riot games. The one file change it makes, an upscaler swap, uses the vendor's signed files only, keeps the original, and asks first for a game with anti-cheat.
- Nothing downloads, installs or elevates without the user's say-so.
- No ads, analytics, telemetry, tray agents or accounts. Every host native code contacts is listed in [PRIVACY.md](PRIVACY.md).
- Secrets and file paths stay in native code; the interface gets bounded values.
- Exo itself never captures the screen or sends synthetic input; that lives in Exo Capture.

## Security problems

See [SECURITY.md](https://github.com/ImAvgErix/ExoLauncher/blob/main/SECURITY.md). Do not put tokens, account exports or personal paths in a public issue.

## Names and logos

See [TRADEMARKS.md](TRADEMARKS.md).
