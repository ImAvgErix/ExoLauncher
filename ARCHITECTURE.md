# Architecture

WinUI 3 + .NET 10 host. React UI in WebView2. Adapters own stores.

```
MainWindow (WebView2)
  ui/ (React · Tailwind · React Bits Pro) → ExoLauncher/wwwroot
        │  JSON-RPC
  WebHostBridge
        │
  AppServices
    LibraryService · LaunchOrchestrator · CoverArtService · achievements · settings
    Steam IPC · Legendary · gogdl · Riot patch API
```

GOG login uses a second WebView2 profile (`GogAuthService`). Store backends do not change.

Cover files live on disk. The React shell loads them through the WebView resource handler / allowlisted HTTPS. Steam Now wash uses `steamHeroUrls`.
