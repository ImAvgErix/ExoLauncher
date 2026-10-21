# OBS Studio game capture files

Exo Capture uses OBS Studio's graphics hook to take a game's frames from inside the game, the way OBS's Game Capture does. These are OBS's own release files, unchanged and signed by OBS Project, LLC, taken from `OBS-Studio-32.2.2-Windows-x64.zip` (`data/obs-plugins/win-capture`):

| File | SHA-256 |
|---|---|
| graphics-hook64.dll | 49c0ddeac72b130d4f8ae90510219949022c1f992adb48d81a4972f2cd6c2585 |
| graphics-hook32.dll | 566b095dd1de495a3f0233cb08d75ab3e7d7b184c80e2fc23f23f22ca8558632 |
| inject-helper64.exe | a785ae14b5debae83404942d5ef7a51c83a53c603d6f5a23162d7f52ff96d9ff |
| inject-helper32.exe | 226da6b414470d17a4b4d368bb55916ab3922b1ae10bb2325ea627f63b1aea71 |
| get-graphics-offsets64.exe | 79ed6d2a983cc93a7ad6b96eb9d0fae21871988a2823cce11d4060ace35753fb |
| get-graphics-offsets32.exe | 4627f12b8295b1ebafd9909dfd0eaa46583e68e8976c8fef76e64122fd2149b6 |

OBS Studio is free software under the GNU General Public License, version 2 or later. Its source for these files is at https://github.com/obsproject/obs-studio/tree/32.2.2 (`plugins/win-capture`, `shared/obs-hook-config`, `shared/obs-inject-library`). `build.ps1` copies them to `out\obs`, and Exo's installer puts them in `addons\capture\obs`.

To update: take the same files from a newer OBS release, check each is signed by OBS Project, LLC, check `graphics-hook-ver.h` still has major version 1 and `hook_info` is still 648 bytes, and update the table.
