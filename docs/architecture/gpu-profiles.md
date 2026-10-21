# Exo's NVIDIA global profiles

System > Graphics > Driver and profile writes one global driver profile, chosen by the GeForce series (GTX 10, GTX 16, RTX 20, RTX 30, RTX 40, RTX 50) and by the user's sync choice: **G-SYNC** (variable refresh on) or **No G-SYNC** (fixed refresh). The data lives in `ExoLauncher/Helpers/GpuProfile.cs` (`Rules`, `LeftAlone`); `ExoLauncher.Tests/GpuProfileTests.cs` checks that every id of the base profile below is decided.

Base: a real RTX 3070 global profile on driver 617.42 (Profile Inspector export, 96 settings). Every id was named with NVIDIA's `NvApiDriverSettings.h` (github.com/NVIDIA/nvapi, main, R615 SDK) and NVIDIA Profile Inspector 3.0.2.3 (pre-release, October 13, 2026) `CustomSettingNames.xml` / `Reference.xml`, read as data.

How a value is written: a number is set in the global profile; **default** removes the user's value so NVIDIA's own (predefined or built-in) value applies; **left alone** is never touched. Exo writes only ids the installed driver lists (`EnumAvailableSettingIds`), in one session and one save, after backing up every value it replaces (Undo restores exactly that, including "not set").

## Variants at a glance

| | G-SYNC | No G-SYNC |
| --- | --- | --- |
| G-SYNC | On, full screen only | Off |
| V-Sync (driver) | On (turn it off in games) | Game decides |
| Max frame rate | Reflex's cap: refresh x 3600 / (refresh + 3600), never closer than 3 below refresh (157 at 165 Hz, 138 at 144 Hz, 225 at 240 Hz, 57 at 60 Hz) | Off |
| Low latency mode | On | On |

| Series | DLSS SR preset | DLSS RR preset | DLSS FG preset | Overrides (newest DLL) | Model preset profile |
| --- | --- | --- | --- | --- | --- |
| GTX 10, GTX 16 | default | default | default | default | default |
| RTX 20, RTX 30 | K | F | C (for mods; no native DLSS FG) | SR, RR, FG on | Custom |
| RTX 40, RTX 50 | Recommended (M Performance, L Ultra Performance, K elsewhere) | Recommended (F today) | C | SR, RR, FG on | Custom |

Why per series (NVIDIA's DLSS SR guide 310.6 and DLSS-RR guide of June 2026, Performance mode, the DLSS pass alone):

- SR: on RTX 20/30 M costs about 1.7x and L about 3x K (RTX 2080 Ti at 4K: K 3.5 ms, M 7.5 ms, L 10.6 ms), 15-30% of the whole frame in games (TS, HH). RTX 40/50 run M and L at FP8, close to K, so NVIDIA's per-mode pick is the better image at little cost.
- RR: F (DLSS 4.5, second-generation transformer) against D/E (first generation): RTX 2080 Ti 16.4 vs 18.3 ms at 4K (F faster), RTX 3060 22.9 vs 23.3, RTX 3070 13.6 vs 13.3, RTX 3080 Ti 8.8 vs 8.8; on RTX 40/50 F is 0.06-0.3 ms slower (RTX 5090 at 4K 2.1 vs 1.8 ms) and holds about 25% more memory (579 vs 468 MB at 4K). In games the difference is within 1 fps on every generation (TSRR, KG), and F has clearly less ghosting and boiling. Recommended picks F today (NVIDIA's tables give "Default, F" one row).
- FG: C (DLSS 310.9.16, October 2026) swaps the second AI model (optical flow for UI and ray-traced effects) for a lighter algorithm: frame generation costs 10% less at 2x, 24% at 4x and 36% at 6x (RTX 5060, C386), with a minor image trade-off. RTX 50 (multi frame generation) gains the most. A is the default, B the UI-focused one (NPI).
- Model preset profile: under "Recommended" the driver ignores forced letters (NPI; NVIDIA's own steps for C start with Custom), so every RTX card gets Custom; a feature can still take NVIDIA's pick through the 0x00FFFFFF value.

## Every setting

User = the RTX 3070 profile's value. "All" means every series and both variants.

| Id (hex) | Setting | User | Exo | Why | Source |
| --- | --- | --- | --- | --- | --- |
| 390467 (0x0005F543) | Low latency mode (CPL state) | On | **On** (all) | One queued frame instead of the game's own queue (DX9/DX11, DX12 since driver 551.23; Vulkan ignores it). Reflex overrides it where present. Blur Busters' rule with a driver frame cap is On; Ultra only adds its own cap, and its just-in-time submit helps only when the GPU is at 97-99% and can deliver a frame late otherwise. | BB14, BBL |
| 277041152 (0x10835000) | Ultra low latency | Off | **Off** (all) | Part of "On" (state 1, ULL 0, pre-rendered 1). | NPI, NV app values |
| 8102046 (0x007BA09E) | Maximum pre-rendered frames | 1 | **1** (all) | Part of "On". | NV, BB14 |
| 269553971 (0x10111133) | VR pre-rendered frames | 1 | default (1) | Same as NVIDIA's default. | NV |
| 278196567 (0x1094F157) | G-SYNC global feature | Off | **On** G-SYNC / **Off** No G-SYNC | The switch the rest depend on. | NV, BB14 |
| 278196727 (0x1094F1F7) | G-SYNC global mode | Off | **Full screen only** / **Off** | Borderless DX12 games and DX10/11 games under Windows 11's windowed-game optimizations already count as full screen; "and windowed" let desktop apps start VRR (stutter, OLED/VA flicker). Changed from "full screen and windowed" after docs/architecture/display-best-settings.md. Earlier note: Trade-off: VRR can also engage for desktop apps (stutter when their windows are dragged or focused); Blur Busters keeps Full screen globally and windowed per game. DX12 borderless and Windows 11 windowed-game flip model already count as full screen. | BB15, NV |
| 294973784 (0x1194F158) | G-SYNC application mode | Off | **Full screen only** / **Off** | Kept in step with the global mode, as the control panel writes both. | NV |
| 279476686 (0x10A879CE) | Variable refresh rate control | Disabled | **Enabled** / **Disabled** | The VRR path itself; disabled blocks G-SYNC even when the switches say on. | NV (default Enabled), NPI |
| 279476687 (0x10A879CF) | G-SYNC application state | Force off | default (Allow) | Force off blocked G-SYNC in every game; Allow lets the global switch decide. | NV |
| 279476652 (0x10A879AC) | G-SYNC application requested state | Force off | default (Allow) | Same as above. | NV |
| 278262127 (0x1095F16F) | VRR overlay indicator | Disabled | **Disabled** (all) | No on-screen G-SYNC badge. | NV |
| 11041231 (0x00A879CF) | Vertical sync | Force off | **On** G-SYNC / default (game decides) No G-SYNC | With G-SYNC, driver V-Sync catches frame-time spikes over the top without added lag. Without, forcing it off made every game tear and broke pacing in games built around V-Sync; each game's own toggle now decides. | BB14 |
| 5912412 (0x005A375C) | V-Sync tear control | Standard | default (Standard) | Adaptive V-Sync tears below refresh; not wanted with either variant. | NV |
| 270198627 (0x101AE763) | V-Sync smooth AFR | Off | default (Off) | SLI only. | NV |
| 277041154 (0x10835002) | Max frame rate (FRL V3) | Off | **Reflex cap** G-SYNC / **Off** No G-SYNC | Keeps every frame inside the G-SYNC range; Reflex picks the same value in its games. On a fixed screen a cap under refresh still tears and holds back frames. | BB5, BBF |
| 277041162 (0x1083500A) | Max frame rate (control panel slider) | 0 | **Reflex cap** / default | Mirrors FRL so the control panel shows the same cap. | NPI |
| 277041157 (0x10835005) | Background max frame rate | Off | default (Off) | Same as default. | NV |
| 277041158 (0x10835006) | Background max frame rate (slider) | 0 | default | Same as default. | NPI |
| 277041199 (0x1083502F) | Dynamic frame rate limiter mode | 0 | default | Same as default. | NPI |
| 6600001 (0x0064B541) | Preferred refresh rate | Highest available | **Highest available** (all) | Exclusive-full-screen games that ask for 60 Hz get the screen's real rate; no effect in borderless. | NV, NPI |
| 6710836 (0x00666634) | Silk smoothness | Off | default (Off) | Same as default. | NPI |
| 538927519 (0x201F619F) | OpenGL buffer-flipping mode | Disabled | default | Same as default. | NV |
| 550932728 (0x20D690F8) | Vulkan/OpenGL present method | Auto | default (Auto) | Auto already uses DXGI flip where it helps. | NV |
| 553505273 (0x20FDD1F9) | Triple buffering (OpenGL) | Off | default (Off) | Same as default. | NV |
| 282663500 (0x10D91A4C) | Control display rearrangement | Disabled | default | Same as default. | NPI |
| 269573257 (0x10115C89) | Battery Boost | Disabled | default | Laptops on battery only. | NPI |
| 277041150 (0x10834FFE) | Platform Boost (AutoFL) | Enabled | default (Disabled) | An auto frame limiter for laptops; not a desktop speed-up. | NPI |
| 13510289 (0x00CE2691) | Texture filtering quality | High performance | **High performance** | Performance first: the most frames the driver gives, for a little texture sharpness. Anisotropic filtering and antialiasing stay the game's own. | NV; no measured gain found |
| 3066610 (0x002ECAF2) | Trilinear optimization | On | **On** (0, the driver's default) | Part of High performance. | NV, NPI |
| 8703344 (0x0084CD70) | Anisotropic filter optimization | On | **On** (driver default Off) | Part of High performance: bilinear instead of trilinear inside AF. | NV |
| 15151633 (0x00E73211) | Anisotropic sample optimization | On | **On** (driver default Off) | Part of High performance. | NV |
| 270426537 (0x101E61A9) | Anisotropic filtering level | Off (linear) | default | With the mode below it forced AF off in every game. | NV |
| 282245910 (0x10D2BB16) | Anisotropic filtering mode | User-defined | default (Application-controlled) | User-defined with level Off overrode games' own 16x AF: blurrier textures. | NV, NPI |
| 10011052 (0x0098C1AC) | MFAA | Off | default (Off) | Same as default. | NV |
| 276495451 (0x107AFC5B) | SLI AA | Disabled | default | SLI only. | NV |
| 276652957 (0x107D639D) | AA gamma correction | Off | default (On) | Only touches driver MSAA; NVIDIA's default. | NV |
| 276757595 (0x107EFC5B) | Antialiasing mode | Override | default (Application-controlled) | Override with setting Off forced MSAA off in older games that offer it. | NV, NPI |
| 282555346 (0x10D773D2) | Antialiasing setting | App/Off | default | Follows the mode. | NV |
| 282364549 (0x10D48A85) | Transparency supersampling | Off | default | Same as default. | NV |
| 283958146 (0x10ECDB82) | AA behavior flags | None | default | NVIDIA predefines this per profile. | NV |
| 284962204 (0x10FC2D9C) | Transparency multisampling | Disabled | default | Same as default. | NV |
| 545898348 (0x2089BF6C) | OpenGL line gamma | Disabled | default | Same as default. | NV |
| 271895433 (0x1034CB89) | FXAA allowed (NVIDIA predefined) | Disallowed | default | NVIDIA's own per-game flag. | NV |
| 276089202 (0x1074C972) | FXAA | Off | default (Off) | Same as default. | NV |
| 6714153 (0x00667329) | Ambient occlusion | Off | default (Off) | Same as default. | NV |
| 271965065 (0x1035DB89) | Ansel / Freestyle allowed | Disallow | default (Allow) | Costs nothing unless used; disallowed breaks the NVIDIA app's photo mode and filters. | NV |
| 276158834 (0x1075D972) | Ansel enabled | Off | default (On) | Same as above. | NV |
| 9963649 (0x00980881) | DeepDVC when offline | Disabled | default | NVIDIA app feature; default. | NPI |
| 9963670 (0x00980896) | Game filters | Off | default (Off) | Same as default. | NPI |
| 272909380 (0x10444444) | NVIDIA Image Scaling | Off | default (Off) | Upscales every game; a game's own DLSS/FSR looks better. | NV |
| 11250721 (0x00ABAC21) | NIS 2 mode | Off | default | Same as default. | NPI |
| 11250737 (0x00ABAC31) | NIS 2 enable | Off | default | Same as default. | NPI |
| 28027939 (0x01ABAC23) | NIS 2 notification | Disabled | default | Same as default. | NPI |
| 14019014 (0x00D5E9C6) | Auto VRS allowed | Disabled | default | Same as default. | NPI |
| 14019015 (0x00D5E9C7) | Auto VRS settings | Off | default | Same as default. | NPI |
| 274606621 (0x105E2A1D) | Freestyle multiplayer modes | Mode 0 | default | Same as default. | NPI |
| 2966161525 (0xB0CC0875) | Smooth Motion APIs | All | never touched | The player's, with Smooth Motion itself. | NPI |
| 2966652096 (0xB0D384C0) | Smooth Motion (not in the file) | - | never touched | Driver frame generation (RTX 40/50) adds latency to every game; the player's choice. | PCW |
| 983226 (0x000F00BA) | Resizable BAR (forced) | On | default | NVIDIA enables rBAR per game where it tested a gain; forced on everywhere some games lose frames or stability. | NPI, community reports |
| 983227 (0x000F00BB) | Resizable BAR options | 1 | default | Goes with the switch. | NPI |
| 784929 (0x000BFA21) | Resizable BAR v2 (driver 616.56+, not in the file) | - | default (Auto: NVIDIA's per-game list) | The newer switch; Enabled here would force rBAR everywhere past the old one. | NPI |
| 1675263 (0x00198FFF) | Shader cache | On | default (On) | Same as default. | NV |
| 11306135 (0x00AC8497) | Shader cache size | Unlimited | **100 GB** with 200 GB free, else what is set (or default); never smaller than set (Unlimited stays) | The driver's 16 GB default (0x4000 MB) still evicts big games' shaders, which then recompile mid-game with stutter; 100 GB fixed Borderlands 4's. | NV, PCG |
| 15389065 (0x00EAD189) | Compile shaders while idle | Medium | **High** (all) | NVIDIA's auto shader compilation (DX12, driver 595.97+): the CPU rebuilds shaders after a driver update while the PC is idle, not in game. High finishes sooner, so fewer games meet a driver update with an empty cache; it only spends idle time and power. | NPI, G3D |
| 2166816 (0x00211020) | ACE power mode CPU frequency | Default | default | Same as default. | NPI |
| 12991097 (0x00C63A79) | Max GPU command queue | Low | default (Off) | An untested tweak that limits queued command buffers; no measured gain. | NPI |
| 14366808 (0x00DB3858) | CPU-to-GPU upload buffer | Small (32) | default | Shrinks the driver's staging buffer from 256 KB banks to 32 bytes; risk without a measured gain. | NPI |
| 274197361 (0x1057EB71) | Power management mode | Prefer maximum performance | **Prefer maximum performance** | Performance first: the GPU never waits to clock up, so no first-frames lag after a quiet moment. Costs power and heat, which don't matter here. | pcgamecheck.com 2026 guide |
| 283962569 (0x10ECECC9) | CUDA sysmem fallback | Prefer fallback | default | Only CUDA apps (AI tools) use it; games do not. | Borncity |
| 1343646814 (0x50166C5E) | CUDA force P2 | Off | default (On) | Compute-only; NVIDIA keeps P2 for stability. | NPI |
| 549528094 (0x20C1221E) | Threaded optimization | Auto | default (Auto) | Same as default. | NV |
| 539527361 (0x202888C1) | OpenGL telemetry level | Disabled | default | Logging only; no measured effect. | NV |
| 547222078 (0x209DF23E) | OpenGL event log threshold | Disabled | default | Logging only. | NV |
| 540508738 (0x20378242) | OpenGL shared memory threshold | 128 | default | Untested tweak. | NPI |
| 543266006 (0x206194D6) | OpenGL pull buffer size | 128 | default | Untested tweak. | NPI |
| 543959236 (0x206C28C4) | OpenGL overlay | Off | default (Off) | Same as default. | NV |
| 544392611 (0x2072C5A3) | OpenGL GDI compatibility | Prefer disabled | default (Auto) | Auto is right for the few games that need GDI. | NV |
| 6505105 (0x00634291) | DLSS model preset profile | Custom | **Custom** on every RTX card, default GTX | Forced letters apply only under Custom; under Recommended, Frame Generation C would never apply. NVIDIA's pick per feature stays available (0x00FFFFFF). | NPI, C386 |
| 283385331 (0x10E41DF3) | DLSS SR preset | K | **Recommended** RTX 40/50 (M Performance, L Ultra Performance, K elsewhere), **K** RTX 20/30, default GTX | RTX 40/50 run M and L fast with FP8; on RTX 20/30 they cost 15-30%, and K is the best model that runs fast there. | TS, HH, NVSR, NPI |
| 283385345 (0x10E41E01) | DLSS SR newest DLL | On | **On** RTX, default GTX | Older DLSS games get the new model. | NV, NPI |
| 283385335 (0x10E41DF7) | DLSS RR preset | F | **Recommended** (F today) RTX 40/50, **F** RTX 20/30, default GTX | F, the second-generation transformer, is as fast as D/E on RTX 20/30 (faster on RTX 20) and within 1 fps on RTX 40/50, with clearly less ghosting. RTX 20/30 get F by letter so a future FP8-heavy model is never picked for them. | NVRR, TSRR, KG, PCGH |
| 283385346 (0x10E41E02) | DLSS RR newest DLL | On | **On** RTX, default GTX | As SR. | NV |
| 283385329 (0x10E41DF1) | DLSS FG preset | C | **C** on every RTX card, untouched GTX | NVIDIA's performance preset: up to 36% cheaper frame generation (most at 4x-6x on RTX 50), a minor image trade-off. On RTX 20/30 only mods that bring frame generation there read it. Where the DLL lacks C (before 310.9.16) the driver keeps its default. | C386, NPI |
| 283385347 (0x10E41E03) | DLSS FG newest DLL | On | **On** every RTX card, untouched GTX | As SR; C needs 310.9.16 or later. | NV |
| 283385348 (0x10E41E04) | DLSS NR newest DLL | On | default | Too new to judge; few games ship it. | NPI |
| 279951208 (0x10AFB768) | DLSS SR forced mode | Game decides | default | The game's mode choice stays. | NV |
| 280859683 (0x10BD9423) | DLSS RR forced mode | Game decides | default | Same. | NV |
| 283385333 (0x10E41DF5) | DLSS SR forced ratio | Off | default | Same. | NV |
| 281531554 (0x10C7D4A2) | DLSS RR forced ratio | Off | default | Same. | NV |
| 273507943 (0x104D6667) | Multi frame generation count | Off | never touched | Frame generation adds lag; the player's and the game's choice (RTX 50). | NV |
| 271614616 (0x10308298) | Frame generation mode (not in the file) | - | default | Same. | NV |
| 283385334 (0x10E41DF6) | FG scene change detection | Disable | default | Disabled lets generated frames smear across scene cuts. | NPI |
| 983295 (0x000F00FF) | Resizable BAR size limit | binary | left alone | A binary value Exo's backup cannot hold; harmless without the switch. | - |
| 6710885 (0x00666665) | Indicator: MFAA | On | left alone | Control panel's own grayed-out hint; it rewrites these. | NPI |
| 6776373 (0x00676635) | Indicator: triple buffering | On | left alone | Same. | NPI |
| 6776937 (0x00676869) | Indicator: GPU power mode | On | left alone | Same. | NPI |
| 281106605 (0x10C158AD) | Indicator: compute performance | On | left alone | Same. | NPI |
| 286335574 (0x11112256) | Post-processing engine flags | Off | left alone | NVIDIA private. | NPI |
| 286335539 (0x11112233) | Unnamed | 2 | left alone | Not in NVIDIA's header or Profile Inspector's lists. | - |


## Sources

- BB14: Blur Busters, G-SYNC 101 "Optimal G-SYNC Settings & Conclusion", blurbusters.com/gsync/gsync101-input-lag-tests-and-settings/14/
- BB15: Blur Busters, G-SYNC 101 "Closing FAQ" (#5, full screen and windowed), blurbusters.com/gsync/gsync101-input-lag-tests-and-settings/15/
- BBL: Blur Busters forum threads on Battle(non)sense's Low Latency Mode tests, forums.blurbusters.com/viewtopic.php?p=55508
- PCGH: PC Games Hardware, DLSS 4.5 Ray Reconstruction benchmarks on RTX 50/40/30/20, pcgameshardware.de (DLSS-45-Ray-Reconstruction-Release-Test-Benchmarks-1552042)
- BB5: Blur Busters, G-SYNC 101 "G-SYNC Ceiling vs. FPS Limit", blurbusters.com/gsync/gsync101-input-lag-tests-and-settings/5/
- BBF: Blur Busters forum, Reflex cap formula, forums.blurbusters.com/viewtopic.php?p=125563
- NV: NVIDIA `NvApiDriverSettings.h` (ids, value enums, defaults), github.com/NVIDIA/nvapi
- NPI: NVIDIA Profile Inspector 3.0.2.3 `CustomSettingNames.xml` and `Reference.xml`, github.com/Orbmu2k/nvidiaProfileInspector
- TS: TechSpot, "Nvidia DLSS 4.5 vs DLSS 4", techspot.com/article/3080-nvidia-dlss-45-vs-40/
- HH: HotHardware, "NVIDIA DLSS 4.5 Benchmarked Across GeForce RTX 50, 40 And 30 GPUs", hothardware.com/news/nvidia-dlss-45-benchmarked
- NVSR: NVIDIA DLSS Super Resolution Programming Guide 310.6 (execution time and memory per preset and GPU), github.com/NVIDIA/DLSS/blob/main/doc/DLSS_Programming_Guide_Release.pdf
- NVRR: NVIDIA DLSS-RR Integration Guide, June 2026 (adds Preset F; tables 1-4: memory and execution time for Default/F against D/E per GPU), github.com/NVIDIA/DLSS/blob/main/doc/DLSS-RR%20Integration%20Guide.pdf
- TSRR: TechSpot, "The Best DLSS 4.5 Update Yet: Ray Reconstruction" (F within 1 fps of D in five games), techspot.com/article/3164-nvidia-dlss-45-ray-reconstruction/
- KG: KitGuru, DLSS 4.5 Ray Reconstruction analysis, August 25, 2026 (negligible cost on RTX 5090/4090, a small gain on RTX 3090/2080 Ti), kitguru.net
- C386: Club386, "Nvidia claims it's boosted DLSS Frame Gen speed by 35% with new preset", October 14, 2026 (Custom, then Preset C, in the NVIDIA app), club386.com/nvidia-boosts-dlss-frame-gen-performance-preset-c/
- G3D: Guru3D, "NVIDIA Adds Auto Shader Compilation Beta" (driver 595.97; Low, Medium (default) or High idle load), guru3d.com
- PCW: PCWorld on Smooth Motion for RTX 40, pcworld.com/article/2847583/
- PCG: PC Gamer on Borderlands 4's shader stutter and a 100 GB cache (via iqondigital.com/learn/troubleshooting/how-to-fix-game-stuttering)
- Borncity: "Hardware-beschleunigte GPU-Planung und NVIDIA CUDA-Policy", borncity.com/blog/2023/11/26/
- pcgamecheck.com/blog/best-nvidia-control-panel-settings-2026 (power management: at most about 2 fps, 15-25 W at the desktop)

## Decisions taken where the evidence is not conclusive

Exo's pick for each, and why. Revisit when new measurements appear.

1. Frame cap under 100 Hz: never closer than 3 below refresh (57 at 60 Hz). Avoids V-Sync lag on frame-time spikes; nothing changes at 100 Hz and up.
2. G-SYNC full screen and windowed: covers every borderless game; desktop apps may stutter while a VRR window is focused.
3. Low latency mode On (not Ultra) in No G-SYNC: Ultra can deliver frames late when the CPU limits; steady frames come first.
4. V-Sync left to the game in No G-SYNC: forcing it off always tears and breaks pacing in some games.
5. Resizable BAR: NVIDIA's per-game list; forced-on gains are synthetic and some games lose frames.
6. DLSS presets per series, with the newest DLLs on every RTX card. Super Resolution: Recommended on RTX 40/50, K on RTX 20/30 (the user's decision; NVIDIA's timings agree). Ray Reconstruction: Recommended (F) on RTX 40/50 and F on RTX 20/30, replacing E: NVIDIA's own tables put F at the same speed or faster than E on RTX 20/30, and the 0.06-0.3 ms it adds on RTX 40/50 is within 1 fps in games for a clearly better image. Frame Generation: C everywhere (performance first).
7. Smooth Motion and a forced multi-frame count are never touched: frame generation stays the player's.
8. DLSS Streamline override (616.56): not written; what it swaps is undocumented.
9. Vulkan/OpenGL present method: Auto; layered on DXGI can break overlays and capture.
10. Background FPS limit and RTX 50 32-bit PhysX: driver default.
11. Model preset profile Custom on RTX 40/50 too (was Recommended): under Recommended the forced Frame Generation C is ignored.
12. Frame Generation preset and newest DLL on RTX 20/30 as well (the user's call): only mods use them there (Universal MFG Unlocker reads the NVIDIA app's preset). No report was found of the driver override breaking dlssg-to-fsr3 or OptiScaler; the one OptiScaler crash seen with the NVIDIA app override on (OptiScaler issue #1180, a hook bug in OptiScaler, closed) was not about frame generation or a series. If mod users report breakage, the RTX 20/30 frame generation rows are the first to drop.
13. Compile shaders while idle: High instead of NVIDIA's Medium; it spends only idle time and power.
14. The rest is the same on every series because nothing measured says otherwise: low latency On (Reflex overrides it where a game has it, GTX 10/16 included), High performance texture filtering, maximum performance power, Resizable BAR on NVIDIA's list (nothing to do on GTX and RTX 20, which lack it), 100 GB shader cache. Smooth Motion (RTX 40/50) and the multi frame count (RTX 50) stay the player's.
