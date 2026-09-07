# Exo Launcher 3.2

Faster browsing, steadier controls, and more reliable downloads, music, and device profiles.

- **A more responsive app.** Library, Discover, Settings, and Devices do less repeated work. Recent data is reused when switching rooms, and hidden device-profile panels stop polling.
- **Controls stay put.** Save messages, validation feedback, and errors have reserved space across Settings, accounts, browsing, and Devices.
- **Better discovery.** Shared metadata requests reduce duplicate loading. Owned-game search stays accurate, and returning from game details preserves your browse position and focus.
- **Clearer downloads and updates.** Detected game updates appear in Downloads & updates with actions for the correct store source. Progress follows each download, installation, and Workshop phase. Steam cold-start uninstall handling is more reliable.
- **Smoother music and Discord.** Embedded surfaces resize and recover more reliably. Background resource use is reduced, Apple Music timing and full-library shuffle are corrected, and playback controls do less repeated work.
- **More reliable device profiles.** Game-focus switching, reconnect retries, and write ordering are improved. Stale reads cannot overwrite newer successful device changes.

Download **ExoLauncher-Setup.exe** below for **Windows 11 x64**. The app and installer both report **3.2.0**. Existing settings and sign-ins are retained.

Validation includes 271 UI tests, 2,150 native tests, packaged startup checks, and live layout checks. Physical mouse writes could not be retested because the connected receiver's mouse was unresponsive; automated profile/write regression coverage passed.
