# Contributing to Exo Launcher

Exo Launcher is free for personal use, and from 3.18.0 its source isn't published ([LICENSE](LICENSE)), so there are no pull requests or patches to send. Issues are welcome all the same: a bug, a store or game that is read wrongly, an idea, or a clearer sentence or a translation for the help. This page says how, and what you agree to when you send one.

## Sending an issue

- **A bug, or a game or store read wrongly:** [open an issue](https://github.com/UhhErix/ExoLauncher/issues/new/choose) with the version (Settings > About), what you did and what happened.
- **An idea:** open an issue and say what you'd do with it. Anything that would change what Exo sends over the network gets an issue before anything else.
- **The help:** an issue with the sentence as it is and as you'd have it, or with a translation.

Don't put passwords, tokens, account exports or personal paths in an issue.

## What you agree to

By sending an issue, a suggestion, a translation or any other contribution you confirm that:

1. you wrote it, or have the right to send it;
2. you grant the copyright holder a perpetual, worldwide, royalty-free and irrevocable license to use, change and distribute it as part of Exo Launcher under any terms, which is the clause in [LICENSE](LICENSE). You keep your own copyright.

**The open-source add-ons are different.** [Exo Mouse](https://github.com/UhhErix/exo-mouse) (AGPL-3.0-or-later), [Exo Shaders](https://github.com/UhhErix/exo-shaders) (GPL-3.0-or-later) and [Exo Shield](https://github.com/UhhErix/exo-shield) (GPL-3.0) have their source public in their own repositories; code for one of them goes there, under its license, as its README says.

## The lines that do not move

An idea that crosses one of these won't ship.

- Exo never edits a game's executable or touches anti-cheat, never kills a process that is anti-cheat, and never modifies Riot games. The one file change it makes, an upscaler swap, uses the vendor's signed files only, keeps the original, and asks first for a game with anti-cheat.
- Nothing downloads, installs or elevates without the user's say-so.
- No ads, analytics, telemetry or tray agents. Every host Exo contacts is listed in [PRIVACY.md](PRIVACY.md).
- Exo itself never captures the screen or sends synthetic input; that lives in Exo Capture.

## Security problems

See [SECURITY.md](https://github.com/UhhErix/ExoLauncher/blob/main/SECURITY.md). Do not put tokens, account exports or personal paths in a public issue.

## Names and logos

See [TRADEMARKS.md](TRADEMARKS.md).
