# ADR-0010: Exo 3.0 application shell

## Status

Accepted — 2026-09-06. Supersedes ADR-0009.

## Decision

Exo 3.0 ships a React/WebView2 interface in a WinUI 3 host. Library, Friends, and Music are the main rooms; Settings opens from the gear. (Discover and Devices shipped in 3.0 and were later removed; see CHANGELOG.) Native C# services implement store actions, local persistence, and validated host messages.

Friends embeds Discord's website. Music embeds the selected provider in a separate WebView2 profile, with supported mini-player controls and playback across room changes. Hidden paused views can suspend after an idle delay. The native Discord Social SDK integration is not the shipped Friends experience.

There is no Optimize or Profile room. The optional Exo account that 3.0 kept in Settings was removed in 3.4.0.

## Validation and limits

Release validation includes native tests, UI tests, backend tests, a production build, and packaged startup/navigation checks. Physical-device writes, live vendor downloads, and the full live self-update matrix require separate test environments. See [supported features](../SUPPORT_MATRIX.md).
