# Exo next: the direction

Where Exo's look and feel goes next, from research into Apple's 2026 design system (iOS, iPadOS, macOS, watchOS, tvOS and visionOS 27), the apps people love (Linear, Raycast), and what people hate about launchers, Discord and Spotify. `DESIGN.md` stays the description of what ships; this is where it is heading.

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

## The look

Apple's three principles, hierarchy, harmony and consistency, drawn in Exo's AMOLED cream and black.

- **Two layers.** Content runs edge to edge in pure black. Navigation and controls float above it in rounded panels and capsules (the inset sidebar, the title pill, header tool groups, the composer).
- **Dense, not airy.** 8px gaps between floating panels and from the window edge; panel corners 18px; rows 32px with 12px corners (concentric: inner radius = outer minus padding); capsules for controls.
- **Tool groups.** Header actions sit in at most three capsules; icons and text never share one; one primary action, cream, on the trailing side.
- **Monochrome chrome.** Cream ink on black; color only from art, avatars, role colors, a red badge for real attention and green for what is live.
- **Soft edges, not divider lines,** where content scrolls under floating controls.
- **Bold, left-aligned titles.**
- **Glass only over art.** Real blur, saturation, a lensing rim and a specular edge, on the floating layer above game art, store heroes and Now Playing; solid everywhere else and while a game runs.

## Exo's identity

Apple's design language is how Exo looks and moves; what is on screen stays Exo's own, so no room reads as an Apple app with games in it.

- **The Library is built around what players do, not around stores.** No sidebar, no tabs, no Installed/Owned split, no store chips. Three things, top to bottom:
  1. **Continue:** the two or three games you are actually playing, as big landscape art with one-click Play, and their live state (playing, updating with a ring).
  2. **Friends are playing:** who is in what right now, from Discord, with Play or Install. Only Exo can show this.
  3. **All games:** one 2:3 cover grid, A to Z, with an iOS-style A–Z scrubber on the edge. Not installed is a property, not a place: those covers are dimmer with a small download mark, and clicking installs. Ctrl+K finds anything; store and sort live there, not in chips.
  Floating panels and sidebars are for rooms that need navigation lists (Discord, Music, Settings), never for the Library.
- **Art light.** The black around a hero or game page picks up a faint glow of that game's color. The page is still AMOLED black everywhere else.
- **Live states are Exo's signature.** The green glow on the game you are playing, the band of light that crosses art while it downloads or compiles, the check that draws itself when something lands.
- **Warm cream, not white.** Exo's ink and primary buttons are cream on black, never Apple's pure white or system blue.
- **The Exo mark and one title pill,** with the Exo Island growing out of it.

## Feel

- **Never a blank screen:** every room draws its last state instantly, then updates.
- **Optimistic actions:** sending, reacting, liking show before the network answers.
- **Prefetch on intent:** hovering a game, channel or playlist starts loading it.
- **Only what is visible renders:** windowed lists everywhere.
- **Motion on the compositor only** (transform, opacity), interruptible, at the display's refresh rate, off under Reduce motion.

## Three additions, and only three

1. **Ctrl+K, one bar for everything** (macOS Spotlight, Raycast): games, actions (graphics, Game focus), Discord channels and DMs, songs and playlists, settings. Enter does the obvious thing.
2. **The Exo Island** (iOS Dynamic Island): under the title pill, only while something is happening: a download, a Discord call with who is speaking, the song playing, a game running. Click to expand. Invisible otherwise.
3. **Game focus** (iOS Focus): launching a game quiets DMs, pauses downloads and goes to performance mode; quitting undoes it. One switch, on by default.

Everything else (watch-style in-game complications, tvOS couch mode, Top Shelf hero, visionOS ornaments) waits until these three are perfect.
