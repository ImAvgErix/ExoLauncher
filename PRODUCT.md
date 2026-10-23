# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

## Users

PC players who already own games across Steam, Epic, GOG, Riot and other stores. They sit at a Windows 11 desktop, often at night, and want one place to install, update and play without opening each store client.

Release positioning: Your games. Your music. Your space.

## Product Purpose

Exo Launcher launches, installs, updates and shows the library. Other store clients stay installed as invisible backends. Success is: signed-out and offline still work; Play / Install / Update is one action; store chrome stays hidden.

## Positioning

One UI. Other clients become invisible dependencies, not apps you open. Buy in the vendor's store if you must. Install, update and launch only in Exo.

## Operating Context

Windows 11 x64. Shipped copy at `%LOCALAPPDATA%\ExoLauncher\app\ExoLauncher.exe`. A WinUI 3 host with one WebView2. Rooms: Library, Store, Discord, Music, System, Devices (Exo Mouse). Settings is the gear. No Profile or Optimize room.

## Capabilities and Constraints

- Steam install/update/uninstall through `ExoLauncher.SteamIpc`. Not OCR, not DepotDownloader.
- Anti-cheat processes are never killed. Upscaler file swaps require an explicit user action and backups.
- No Exo account. No ads, tray agent or analytics.
- Discord is Exo's own client. Music is Exo's own player over the selected service running hidden. Devices is Exo Mouse, a separate AGPL program built on OpenMouse's protocol library, showing only the settings the connected mouse supports.
- Not a mod manager or streaming tool.

## Brand Commitments

- Same family as Exo OS. Quiet voice. Prefer "not yet" over vaporware.
- No emoji in UI chrome.
- Inter. One primary action per view: the ink's color, white on Void.
- Responsive desktop layout with configurable text size.
- Three Appearances (Light, Dark, Void black). One Bar of glass capsules with the window controls in its Tools capsule; the current tab lit `--exo-selected`.

## Evidence on Hand

Real library covers and Discord friends on this machine. Do not invent stores, friends or games.

## Product Principles

- Store clients stay invisible.
- Signed-out and offline stay complete.
- One primary verb per game.
- Chrome recedes; the library and rooms do the work.
- Honesty over finish theater.

## Accessibility & Inclusion

Pointer cursor on clickable chrome. Visible keyboard focus and Windows High Contrast support. No gamepad control of the UI.
