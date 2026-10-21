# ADR-0009: Native WinUI pixel shell

## Status

Superseded for 3.0 — see [ADR-0010](0010-exo-3-shell.md). The text below records the earlier native-shell experiment.

## Context

ADR-0002 put the product UI in React inside WebView2. That path worked, and it cost about 1.9 GB idle (host plus two `msedgewebview2.exe` trees). The same product — Library, Discover, Friends, Devices, Music, Settings — now exists as WinUI 3 pages that call the same C# services. A leftover `#2997FF` Fluent twin is gone; tokens copy the React inventory (Geist, cream CTA, AMOLED).

GOG login may still use a hidden WebView2 profile. That is the only WebView2 the product chrome is allowed to keep. React `ui/` and the chrome WebView2 path are gone.

## Decision

The default pixel shell is native WinUI 3 in `ExoLauncher/Shell/`. `MainWindow` does not declare a `WebView2`. Tokens are void black (`#000`), one 52px titleband, one chrome pill, filled `#171717` tabs, and custom min / max / close in the pill. No NavigationView. No Segoe as brand. No Mica.

Store backends do not change. Steam install/update/uninstall stays on `ExoLauncher.SteamIpc`. Nav stays Library, Discover, Friends, Devices, Music; Settings is the gear. No Profile room. No Optimize room.

## Consequences

- Idle Library has no `msedgewebview2.exe` until Music catalog or a store login needs one.
- Native startup calls `StartDeferredServices()` after `native-shell-ready` so library watchers and hidden-store observers run without a WebView.
- `ui/` and `wwwroot` are deleted. (Trophy banners were later retired entirely, in 3.3.0.)
- ADR-0002 is superseded. ADR-0001 remains historical.
