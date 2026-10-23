# Exo next: the direction

The research behind Exo's look and feel, and what is still ahead. It comes from Apple's 2026 design system (iOS, iPadOS, macOS, watchOS, tvOS and visionOS 27), the apps people love (Linear, Raycast), and what people hate about launchers, Discord and Spotify. `DESIGN.md` describes what ships; what shipped from here (floating panels and capsules, the Library around Continue, Friends are playing and one grid, art light, live states, Ctrl+K, the Exo Island) is described there now.

## What people hate (and Exo must never do)

- **Wasted space.** Spotify's redesign drew years of complaints for "massive rounded corners, large paddings and lots and lots of images" on desktop. Exo stays dense: compact rows, modest corners, content first.
- **Moving familiar things.** One button Discord moved drew 115 angry reviews in three weeks. Every Discord control stays where Discord users expect it; every Exo control stays put between releases.
- **Removing features.** People cancel over lost features. Restyle, never strip.
- **Slow, heavy, broken.** Launchers are hated for not starting games, eating memory and making people juggle apps (Owlcat pulled its launcher within a day). Exo is one app, fast and light.
- **Upsells and clutter.** No Nitro-style gift buttons, no promos.

## What people love

- **Instant.** Linear feels fast because the server is out of the way: changes show at once from local state and sync behind. Exo does the same everywhere.
- **One place to do anything.** Raycast's "fast, simple, delightful" and a single bar for everything.
- **One library.** Every store's games together, "jump back in", playtime and achievements across stores (what Xbox and GOG Galaxy reach for).

## Exo's identity

Apple's design language is how Exo looks and moves; what is on screen stays Exo's own, so no room reads as an Apple app with games in it. The Library is built around what players do, not around stores; floating panels and sidebars are for rooms that need navigation lists (Discord, Music, Settings), never for the Library. Live states (the green glow on the game you are playing, the band of light while it downloads or compiles, the check that draws itself) are Exo's signature.

## Feel

- **Never a blank screen:** every room draws its last state instantly, then updates.
- **Optimistic actions:** sending, reacting, liking show before the network answers.
- **Prefetch on intent:** hovering a game, channel or playlist starts loading it.
- **Only what is visible renders:** windowed lists everywhere.
- **Motion on the compositor only** (transform, opacity), interruptible, at the display's refresh rate, off under Reduce motion.

## Still ahead

1. **An A to Z scrubber** on the edge of All games, iOS-style.
2. **Game focus** (iOS Focus) as one switch: today it quiets Discord's notifications while a game runs, and Settings › Downloads can pause downloads while playing; launching a game should also go to performance mode, and quitting undo it.

Everything else (watch-style in-game complications, tvOS couch mode, Top Shelf hero, visionOS ornaments) waits until these are perfect.
