# ADR-0010: Exo 3.0 application shell

## Status

Accepted — 2026-09-06. Supersedes ADR-0009. The shell decision is in force (AGENTS.md › Stack): one WebView2 running React in a WinUI 3 host, native C# services behind validated host messages.

Since then: the rooms are Library, Store, Discord, Music, System and Devices. Friends became the Discord room, Exo's own client rather than Discord's website (3.10.0); Music is Exo's own player over each service's hidden app or page (ARCHITECTURE.md › Music); the Discord Social SDK was deleted (3.2.4).

## Decision

Exo 3.0 ships a React/WebView2 interface in a WinUI 3 host. Library, Friends, and Music are the main rooms; Settings opens from the gear. (Discover and Devices shipped in 3.0 and were removed in 3.2.7; 3.4.0 added the Store and brought Devices back as Exo Mouse.) Native C# services implement store actions, local persistence, and validated host messages.

Friends embeds Discord's website. Music embeds the selected provider in a separate WebView2 profile, with supported mini-player controls and playback across room changes. Hidden paused views can suspend after an idle delay. The native Discord Social SDK integration is not the shipped Friends experience.

There is no Optimize or Profile room. The optional Exo account that 3.0 kept in Settings was removed in 3.4.0.

## Validation and limits

Release validation is the gates in AGENTS.md (`npm run check`, `npm run dead`, the production build, `dotnet test ExoLauncher.Tests`) and the end-to-end run of the installed app, `node tools/e2e/run.mjs`. Physical-device writes, live vendor downloads, and the full live self-update matrix require separate test environments. See [supported features](../SUPPORT_MATRIX.md).
