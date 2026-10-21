# ADR-0001: Native WinUI 3 shell

## Status

Superseded by [ADR-0002](0002-react-webview-shell.md) — 2026-08-17. Current product UI is [ADR-0009](0009-native-winui-pixel-shell.md).

## Context

Exo Launcher hosted React (Vite, Tailwind, Motion) inside WebView2 and talked to C# over JSON-RPC (`WebHostBridge` ↔ `host.ts`). A native WinUI 3 port replaced that shell.

## Decision

The native port was tried, then React (ADR-0002) became the product UI. That sequence is historical.

## Consequences

The unused WinUI twin (`Controls/`, `ShellController`, `ShellViewModel`) was removed. Current chrome is [ADR-0009](0009-native-winui-pixel-shell.md): native WinUI 3; React only with `EXO_WEB_SHELL=1`.
