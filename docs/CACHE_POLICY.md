# Cache ownership and recovery

Settings → About & updates offers separate installed-size and upscaler-status repair. Game artwork has per-game refetch/reset controls. These operations do not delete settings, user-picked artwork, installed games or original DLL backups.

| Cache / owner | Key and bounds | Freshness, failures and invalidation |
|---|---|---|
| Artwork / CoverArtService | Canonical art identity; maintenance starts above 512 MiB or 2,048 files, aims for 384 MiB / 1,536 | Unreferenced age 90 days; 15-minute eviction grace. Active-library art and user profile images are protected, so these are maintenance targets rather than hard storage caps. Explicit per-game refetch replaces derived art; local overrides remain separate. Grid thumbnails (`thumb-<width>-<source stamp>-<cover>.jpg`) are unprotected, regenerated on request, and replaced when their source's length or write time changes. |
| Installed sizes / InstalledSizeCache | Normalized install root; 2,048 entries and two background workers | 30-minute result TTL includes empty/unknown results. Each walk is bounded to 8,000 files, 2,048 directories, depth 64 and two seconds; skips reparse points. Partial totals are approximate. Library refresh uses current paths; repair clears measurements and refreshes the library. |
| Upscaler status / DlssSwapService | Store + game + root/file metadata + app/schema version; 256 default entries, max 32 files/game, disk input ≤4 MiB | Five-minute non-sliding TTL includes empty results. Passive hits do not start another scan or create backups. Swap/restore invalidates status. Repair clears only status entries/file. Corrupt or old-schema caches start cold. |
| Upscaler UI / upscalerCache | Game id; 512 entries | Only the latest request may commit. Forced refresh and cache repair invalidate old requests; selection guards prevent an old game's response updating the new game. |

Catalog in-memory facts are capped at 2,048 entries and product details at 1,024, with oldest entries evicted. Deleting all application data is not a cache repair procedure.
