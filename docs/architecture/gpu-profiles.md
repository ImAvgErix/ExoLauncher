# Exo's NVIDIA global profiles

System > Graphics > Driver and profile writes one global driver profile, chosen by the GeForce series (GTX 10, GTX 16, RTX 20, RTX 30, RTX 40, RTX 50; workstation RTX cards take their generation's rules, a newer GeForce RTX 50's, anything else the GTX rules) and by the user's sync choice: **G-SYNC** (variable refresh on) or **No G-SYNC** (fixed refresh), preselected from what the driver says the main screen can do. The data lives in `ExoLauncher/Helpers/GpuProfile.cs` (`Rules`, `LeftAlone`, `NvidiaSeries`), the writing in `ExoLauncher/Services/GpuProfileService.cs`; `ExoLauncher.Tests/GpuProfileTests.cs` checks that every id of the base profile below is decided.

Base: a real RTX 3070 global profile on driver 617.42 (Profile Inspector export, 96 settings). Every id was named with NVIDIA's `NvApiDriverSettings.h` (github.com/NVIDIA/nvapi, main, R615 SDK) and NVIDIA Profile Inspector 3.0.2.3 (pre-release, October 13, 2026) `CustomSettingNames.xml` / `Reference.xml`, read as data.

How a value is written: a number is set in the global profile; **default** removes the user's value so NVIDIA's own (predefined or built-in) value applies; **left alone** is never touched. Exo writes only ids the installed driver lists (`EnumAvailableSettingIds`), in one session and one save, after backing up every value it replaces to `gpu-profile.json` (Undo restores exactly that, including "not set", even after a restart; a re-apply or a switch of variant keeps the first backup). Each read compares the driver with the applied variant, so a driver update or another tool shows as changed with Re-apply. With G-SYNC applied, the frame cap follows the main screen: when only the cap differs (a new refresh rate or main screen), it is written again (`GpuProfileService.FollowRefresh`).

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
| 278196727 (0x1094F1F7) | G-SYNC global mode | Off | **Full screen only** / **Off** | Borderless DX12 games and DX10/11 games under Windows 11's windowed-game optimizations already count as full screen; "and windowed" lets desktop apps start VRR (stutter, OLED/VA flicker). Blur Busters keeps Full screen globally and windowed per game. Changed from "full screen and windowed" after `display-best-settings.md`. | BB15, NV |
| 294973784 (0x1194F158) | G-SYNC application mode | Off | **Full screen only** / **Off** | Kept in step with the global mode, as the control panel writes both. | NV |
| 279476686 (0x10A879CE) | Variable refresh rate control | Disabled | **Enabled** / **Disabled** | The VRR path itself; disabled blocks G-SYNC even when the switches say on. | NV (default Enabled), NPI |
| 279476687 (0x10A879CF) | G-SYNC application state | Force off | default (Allow) | Force off blocked G-SYNC in every game; Allow lets the global switch decide. | NV |
| 279476652 (0x10A879AC) | G-SYNC application requested state | Force off | default (Allow) | Same as above. | NV |
| 278262127 (0x1095F16F) | VRR overlay indicator | Disabled | **Disabled** (all) | No on-screen G-SYNC badge. | NV |
| 11041231 (0x00A879CF) | Vertical sync | Force off | **On** G-SYNC / default (game decides) No G-SYNC | With G-SYNC, driver V-Sync catches frame-time spikes over the top without added lag. Without, forcing it off made every game tear and broke pacing in games built around V-Sync; each game's own toggle now decides. | BB14 |
| 5912412 (0x005A375C) | V-Sync tear control | Standard | default (Standard) | Adaptive V-Sync tears below refresh; not wanted with either variant. | NV |
| 270198627 (0x101AE763) | V-Sync smooth AFR | Off | default (Off) | SLI only. | NV |
| 277041154 (0x10835002) | Max frame rate (FRL V3) | Off | **Reflex cap** G-SYNC / **Off** No G-SYNC | Keeps every frame inside the G-SYNC range; Reflex picks the same value in its games. It is the safety net: a game's own limiter or Reflex adds less lag than the driver's (up to about a frame). On a fixed screen a cap under refresh still tears and holds back frames. | BB5, BBF |
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
| 13510289 (0x00CE2691) | Texture filtering quality | High performance | **High performance** | Performance first (the user's call, a preference): the most frames the driver gives, for a little texture sharpness. Anisotropic filtering and antialiasing stay the game's own. | NV; no measured gain found |
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
| 11306135 (0x00AC8497) | Shader cache size | Unlimited | **Unlimited** with 200 GB free on the cache's drive, else what is set (or default) | At any fixed size a big game evicts the others' shaders, which then recompile mid-game with stutter: the driver's 16 GB default (0x4000 MB) does, 100 GB fixed Borderlands 4's, and Marvel Rivals' whole set alone is about 80 GB. | NV, PCG |
| 15389065 (0x00EAD189) | Compile shaders while idle | Medium | **High** (all) | NVIDIA's auto shader compilation (DX12, driver 595.97+): the CPU rebuilds shaders after a driver update while the PC is idle, not in game. High finishes sooner, so fewer games meet a driver update with an empty cache; it only spends idle time and power. | NPI, G3D |
| 2166816 (0x00211020) | ACE power mode CPU frequency | Default | default | Same as default. | NPI |
| 12991097 (0x00C63A79) | Max GPU command queue | Low | default (Off) | An untested tweak that limits queued command buffers; no measured gain. | NPI |
| 14366808 (0x00DB3858) | CPU-to-GPU upload buffer | Small (32) | default | Shrinks the driver's staging buffer from 256 KB banks to 32 bytes; risk without a measured gain. | NPI |
| 274197361 (0x1057EB71) | Power management mode | Prefer maximum performance | **Prefer maximum performance** | Performance first: steadier clocks while a 3D app runs (forum reports; no latency measurement found). Costs power and heat, which don't matter here. | pcgamecheck.com 2026 guide |
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


## Checking the DLSS override took

While a library game runs, `MainWindow.Dlss.cs` asks the driver every 15 s (for 10 minutes) what its DLSS override did in the game in front (`NvAPI_NGX_GetNGXOverrideState`, R570+, `NV_NGX_DLSS_OVERRIDE_GET_STATE_PARAMS_V2`, 96 bytes). The first answer that shows DLSS running goes to app.log once per run ("DLSS in <game>: Super Resolution overridden; Preset K, …"). A process without DLSS answers `NVAPI_DATA_NOT_FOUND` (-121), read as nothing running. An override the driver refused shows its error bits.

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
2. G-SYNC full screen only (was full screen and windowed): flip-model borderless games already count as full screen, and windowed globally let desktop apps start VRR (`display-best-settings.md`). A legacy blt-model borderless game runs at fixed refresh.
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
14. The rest is the same on every series because nothing measured says otherwise: low latency On (Reflex overrides it where a game has it, GTX 10/16 included), High performance texture filtering, maximum performance power, Resizable BAR on NVIDIA's list (nothing to do on GTX and RTX 20, which lack it), Unlimited shader cache. Smooth Motion (RTX 40/50) and the multi frame count (RTX 50) stay the player's.

## Radeon

On an AMD GPU the same row writes Exo's Radeon profile through ADLX, the driver's own library (`amdadlx64.dll`), with its official `Set` calls only. The rules are `ExoLauncher/Helpers/GpuProfile.Radeon.cs`, the ADLX calls `ExoLauncher/Helpers/AmdAdlx.Settings.cs` (vtable slots from the ADLX 2.0 headers, each noted beside its call and checked by compiling the headers' C vtables with MSVC x64), the backup, Undo and launch switches `ExoLauncher/Services/RadeonProfileService.cs`; `ExoLauncher.Tests/RadeonProfileTests.cs` runs them against a fake driver. Nothing here has run on a Radeon yet.

ADLX settings are global per GPU (Fluid Motion Frames for every GPU), so the profile is global. Exo writes only what the driver offers: a setting whose interface the driver's ADLX lacks (`IADLX3DSettingsServices1` for Fluid Motion Frames, `…3` for the FSR override, asked for by QueryInterface) or that the GPU doesn't support is neither written nor backed up. Before writing, every value it replaces goes to `amd-profile.json` (a re-apply keeps the first backup). ADLX applies each `Set` at once, so a write that fails part way keeps the backup and Undo puts back what changed. Each read compares the driver with the profile; after an AMD Software update moved a setting, Exo applies the profile again at its next start.

| Setting | Exo | Why |
| --- | --- | --- |
| Radeon Chill | Off (written first) | Caps the frame rate to save power, and turns Anti-Lag and Boost off. |
| Radeon Boost | Off | Lowers the resolution while you move: blurrier motion. |
| Radeon Super Resolution | Off | Upscales every game; a game's own FSR, or the FSR override below, looks better. |
| Radeon Image Sharpening | Off (after Super Resolution, which holds it on some GPUs) | Sharpens every game on top of the game's own sharpening, which adds halos; a game that wants more has its own slider. |
| Enhanced Sync | Off | Long history of stutter and black screens; FreeSync with a cap is smoother. |
| Wait for Vertical Refresh | Off, unless application specifies (AMD's default; after Enhanced Sync, which turns V-Sync off) | V-Sync stays each game's. Not forced on as the G-SYNC profile's driver V-Sync is: Fluid Motion Frames needs V-Sync off, and the frame rate target already keeps frames inside the FreeSync range. |
| FreeSync | On, every screen whose driver reports it | No tearing and no V-Sync lag. |
| Frame rate target | With FreeSync on the main screen: on at `FrameCap(refresh)` (Exo's G-SYNC cap), inside the driver's range, and it follows a new refresh rate; without: off | Keeps every frame inside the FreeSync range, as the G-SYNC profile does; No G-SYNC leaves the cap off, and so does a screen without FreeSync. |
| FSR Upscaling override ("FSR 4 for FSR 3.1 games") | On for RX 9000 and RX 7000 (and Radeon PRO W9000/W7000), RX 6000 from AMD Software 26.10.1; else left alone | The driver upgrades signed FSR 3.1 DX12 games to FSR 4 (RX 7000 since 26.6.2, RX 6000 since 26.10.1). Exo never forces AMD's allowlist. |
| Anti-Lag | Choice beside Use: **While you play** (default), Always, Off | Globally on, the driver also hooks Windows' own processes (ApplicationFrameHost, the Start menu, WebView2) and raises CPU load (RadeonTuner's DriverNotes, item 5). ADLX has no per-game setting, so "While you play" keeps it off and turns it on just before a game Exo launches starts (`LaunchOrchestrator.GameLaunching`), off again once no game runs. Its level (Anti-Lag or Anti-Lag Next) is left as the driver has it. |
| Fluid Motion Frames | Never written by the profile; per game on the game's page | Adds lag and HUD artifacts, so it stays the player's, like NVIDIA's Smooth Motion. A game with it on gets it switched on before it starts and back once no game runs. It works only while AMD Software (RadeonSoftware.exe) runs, which the game's page says. |

The launch switches save what they replace to `amd-games.json` before switching, so a crash or a closed Exo mid-game puts them back at the next start. Apply and Undo drop a pending put-back for the settings they write. A game started outside Exo gets neither switch.

Sensors (`gpu.sensors`, the "Right now" line under the profile, every 3 s while the tab is open): ADLX performance monitoring's current GPU metrics: temperature, hotspot, total board power (else GPU power), clock, load, VRAM in use against the card's total, fan RPM, each only where the GPU reports it supported. Exo shows no NVIDIA or Intel sensors yet.

Smart Access Memory (AMD's Resizable BAR) is read only: `IADLXSystem.GetGPUTuningServices` (slot 8), `IADLXGPUTuningServices1.GetSmartAccessMemory` (18), `IADLXSmartAccessMemory.IsSupported`/`IsEnabled` (3, 4). Its `SetEnabled` is deprecated (ADLX_NOT_SUPPORTED: Resizable BAR is set in the BIOS). On a Radeon its answer decides System › This PC's Resizable BAR row, over Direct3D 12's upload heaps; when ADLX can't say, Direct3D 12's answer stays.

To check on a Radeon: each `Set` returns ADLX_OK without admin rights and shows in AMD Software; the values survive an AMD Software restart; FreeSync's `SetEnabled` reads back at once (it may switch the screen's mode); the FSR override shows in AMD Software and in a signed FSR 3.1 game's FSR overlay; Anti-Lag switched before launch is on in the game (Alt+R overlay) and off after; Fluid Motion Frames switched before launch generates frames with AMD Software running and none with it closed; the ADLX display names match Windows' names for the main screen (else the cap stays off); Image Sharpening and Wait for Vertical Refresh read back what was set and show in AMD Software; Smart Access Memory's IsEnabled matches AMD Software's and Direct3D 12's answer.

## Intel

On an Intel Arc or Iris Xe GPU the same row writes Exo's Intel profile through IGCL, the driver's own library (`ControlLib.dll`), with `ctlGetSet3DFeature` only. The rules are `ExoLauncher/Helpers/GpuProfile.Intel.cs`, the IGCL calls `ExoLauncher/Helpers/IntelIgcl.Settings.cs` (layouts from `igcl_api.h`, measured by compiling it with MSVC x64 and pinned by `IntelProfileTests`), the backup, Undo and launch switch `ExoLauncher/Services/IntelProfileService.cs`; `ExoLauncher.Tests/IntelProfileTests.cs` runs them against a fake driver. Nothing here has run on an Intel GPU yet.

The main Intel GPU is the first discrete one (`ctlGetDeviceProperties`, not CTL_ADAPTER_PROPERTIES_FLAG_INTEGRATED), else the first; its driver version (driver_version) marks a driver update. Exo asks the driver which 3D features it has and each one's value type (`ctlGetSupported3DCapabilities`): the header's notes disagree on some (Endurance Gaming is "integer" in `ctl_3d_feature_t` but a custom `ctl_endurance_gaming_t` in Intel's own sample; sharpening is "integer" with an off/on enum beside it), so the driver's answer decides how a value is read and written. Values are global (an empty application name). Each write reads the whole value first (a custom value keeps its other fields, a frame limit its fps when turned off), sets it and reads it back; a write the driver refuses or doesn't keep counts as a setting this driver doesn't have: it is logged, left out of the profile until Exo restarts, and doesn't stop the rest. The backup is `intel-profile.json`; after a driver update moved the profile, Exo applies it again at its next start.

| Setting | Exo | Why |
| --- | --- | --- |
| Endurance Gaming | Off (written first) | Caps frames on battery to save power, as Radeon Chill does. |
| Sharpening filter | Off | Sharpens every game on top of the game's own sharpening, which adds halos. |
| Low latency mode | Choice beside Use: **On** (default), On with boost, Off | One queued frame, as NVIDIA's Low latency On. Boost also keeps the GPU's clock up while the CPU limits, which costs power. |
| Adaptive Sync | On (`CTL_3D_FEATURE_ADAPTIVE_SYNC_PLUS`'s AdaptiveSync; Adaptive Balance and its strength left as they are) | No tearing and no V-Sync lag. Windows still decides which games use it. |
| Frame limit | With a main screen whose Intel display properties report VESA Adaptive Sync or HDMI VRR: on at `FrameCap(refresh)`, inside the driver's range, and it follows a new refresh rate; without: off | Keeps every frame inside the variable refresh range, as the G-SYNC profile does. |
| Download prebuilt shaders | On | The driver fetches Intel's compiled shaders for the games it has them for, so they don't compile on the PC while you play. The driver contacts Intel for them, not Exo. |
| XeSS frame generation | Never written by the profile; per game on the game's page: the game's own choice, 2x, 3x or 4x | The driver's override of XeSS frame generation's multiplier (`ctl_3d_frame_generation_override_t`), only in games with XeSS frame generation. More generated frames add lag, so it stays the player's. IGCL can keep it per application, but Exo knows a game by its library entry, not its executable's name, so it sets the global override just before a game Exo launches starts and puts it back once no game runs, saved first to `intel-games.json` so a crash puts it back at the next start, as on a Radeon. |

Left alone: frame pacing, flip modes, anisotropic filtering, CMAA, MSAA and texture filtering (each game's own), Intel's game profiles, and `CTL_3D_FEATURE_FRAME_GENERATION_CONTROL` (20), whose value the header doesn't document.

To check on an Intel GPU (Arc A and B, and an Iris Xe or Core Ultra iGPU): `ctlGetSupported3DCapabilities` lists the seven features with the value types Exo handles (Endurance Gaming and Adaptive Sync custom, low latency and frame generation enum, frame limit int, prebuilt shaders bool); each set returns CTL_RESULT_SUCCESS without admin rights and shows in Intel Graphics Software; the values survive a driver update; On with boost is offered on that GPU; the frame generation override switched before launch takes in an XeSS frame generation game and is put back after; the main screen's Windows target id matches IGCL's encoder id (else the cap stays off); on an Arc card beside Intel graphics, the Arc card is the one written.
