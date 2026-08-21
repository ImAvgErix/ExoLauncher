# ADR-0001: Native WinUI 3 shell

## Status

Superseded by [ADR-0002](0002-react-webview-shell.md) — 2026-08-17

## Context

Exo Launcher hosted React (Vite, Tailwind, Motion) inside WebView2 and talked to C# over JSON-RPC (`WebHostBridge` ↔ `host.ts`). A native WinUI 3 port replaced that shell.

## Decision

The native port was tried. The product UI is React again — see ADR-0002.

## Consequences

Leftover `ExoLauncher/Controls/` and `ExoLauncher/Ui/ShellViewModel.cs` are not the product UI.
