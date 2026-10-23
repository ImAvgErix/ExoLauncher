# Cache ownership and recovery

Settings › About › Clear caches repairs installed sizes, then upscaler status. Game artwork has per-game refetch and reset controls. None of these delete settings, user-picked artwork, installed games or original DLL backups.

| Cache / owner | Key and bounds | Freshness, failures and invalidation |
|---|---|---|
| Artwork / CoverArtService | Canonical art identity; maintenance starts above 512 MiB or 2,048 files, aims for 384 MiB / 1,536 | Unreferenced age 90 days; 15-minute eviction grace. Active-library art and user profile images are protected, so these are maintenance targets rather than hard storage caps. Explicit per-game refetch replaces derived art; local overrides remain separate. Grid thumbnails (`thumb-<width>-<stamp>-<cover>.jpg`) are unprotected, regenerated on request, and replaced when their source's length or write time (or the thumbnail format) changes. |
| Installed sizes / InstalledSizeCache | Normalized install root; 2,048 entries and two background workers | 30-minute result TTL includes empty/unknown results. Each walk is bounded to 8,000 files, 2,048 directories, depth 64 and two seconds; skips reparse points. Partial totals are approximate. Library refresh uses current paths; Clear caches clears measurements and refreshes the library. |
| Upscaler status / DlssSwapService | Store + game + root/file metadata + app/schema version; 256 default entries, max 64 files/game, disk input ≤4 MiB | An entry over five minutes old is still served while a rescan runs behind it; empty results are cached too. Passive hits do not create backups. Swap/restore invalidates status. Clear caches clears only status entries and their file. Corrupt or old-schema caches start cold. |
| Upscaler UI / upscalerCache | Game id; 512 entries | Only the latest request may commit. Forced refresh and Clear caches invalidate old requests; selection guards prevent an old game's response updating the new game. |
| Store / StoreService | `store\home-v5.json`, `items-v2.json`, `tags-v1.json`, `wishlist-v2.json`; searches in memory, at most 40 | Card facts fresh 12 hours and kept two weeks; tag names a week; a game page 20 minutes; a search 5 minutes. Public, anonymous endpoints only (ARCHITECTURE.md › Store). |

Deleting all application data is not a cache repair procedure.
