# Best display settings: what the evidence says

Research for System > Display ("Best for this screen") and a check of Exo's NVIDIA profile (`gpu-profiles.md`, `Helpers/GpuProfile.cs`). The goals are the lowest input lag, the best performance and the best image on the widest range of PCs and screens. Researched October 17, 2026; the notes on what Exo does were checked against the code on October 21, 2026.

How to read it: each topic gives a **recommendation**, the **conditions** it holds under, the **evidence** with links, and a **confidence**:

- **High**: measured by a reputable tester or stated by the vendor that owns the feature, and nothing credible says otherwise.
- **Medium**: a vendor statement or a mechanism that is well understood, but no independent measurement, or measurements that are old.
- **Low**: forum reports, single-author tests, or reasoning from how the feature works.

The evidence base has gaps. Blur Busters' G-SYNC 101 is still the only large published latency study of driver sync settings. Most driver-setting "benchmarks" newer than 2021 are SEO guides with no method. Several pages (blurbusters.com, rtings.com test bodies) refused automated fetches, so where a claim relies on them it is cited through search-indexed text or the existing `gpu-profiles.md` sources. Where the evidence is weak, this says so instead of filling the gap.

---

## 1. Scaling

**Recommendation**

- **At native resolution: scaling does not matter.** There is nothing to scale. Leave whatever is set and don't spend a mode set (a screen blink) changing it.
- **For games run below native: GPU scaling, Aspect ratio.** Aspect ratio keeps 4:3 or 16:10 games from stretching. GPU scaling behaves the same on every monitor, while monitor scalers range from good to bad.
- **Integer scaling** only when the user asks for it (pixel-art games, or 1080p on a 4K panel for a sharp image). Never as "best", because it leaves black borders at any ratio that isn't a whole number.
- **"Override the scaling mode set by games and programs": On** when GPU scaling is chosen, so a game asking for "stretch" doesn't override aspect. Low confidence; this is a preference more than a measured gain.

**Conditions**

- Display scaling is sometimes the only option on DSC links and older cards. On TVs, the TV's own scaler is usually good and adds little lag in Game mode.
- Integer scaling needs NVIDIA Turing (GTX 16 / RTX 20) or newer. On Turing it is incompatible with HDR and with custom resolutions (NVIDIA's own release notes and an NVIDIA employee, 2019). Whether that still applies on RTX 30/40/50 was not confirmed.
- DSC: NVIDIA's own image-processing features (DSR, DLDSR, NIS) work with DSC only when the mode fits one GPU display head. Whether a mode fits depends on the monitor's EDID timings, and NVIDIA gives no simple rule (TFT Central). Integer scaling on DSC was not documented either way.

**Evidence**

- Input lag of GPU vs display scaling: **no controlled measurement was found.** Blur Busters forum discussion says scaling can be done "in virtually real time (single scan-line buffered)". A frame-buffered monitor scaler can add up to one refresh (0-16.7 ms at 60 Hz), while recent GPU scalers add "0-to-few ms". One user's photodiode-style test found no difference across scalers. https://forums.blurbusters.com/viewtopic.php?p=3229, https://forums.blurbusters.com/viewtopic.php?p=2560
- Integer scaling, Turing only, issues with HDR: AnandTech on driver 436.02, https://www.anandtech.com/show/14765. Not fixable on Turing, also breaks custom resolutions: PCGH, https://www.pcgameshardware.de/Nvidia-Geforce-Grafikkarte-255598/News/Inkompatiblitaet-von-Integer-Scaling-mit-HDR-und-Custom-Aufloesungen-1333997/
- DSR/DLDSR/NIS with DSC (the single-head rule): TFT Central, https://tftcentral.co.uk/news/nvidia-dsr-and-dldsr-do-work-with-dsc-monitors-sometimes

**Confidence:** native = high (nothing to scale). GPU + Aspect for below-native = medium (the lag difference is unmeasured; the choice rests on consistency and on aspect being preserved). Integer limits on Turing = high; on newer cards = unknown.

---

## 2. Output color

### 2a. RGB vs YCbCr

**Recommendation:** **RGB (4:4:4) whenever the link carries it.** YCbCr 4:4:4 is lossless in chroma too, but drivers send YCbCr as Limited range. Avoid 4:2:2 and 4:2:0 on a PC: text on colored backgrounds fringes and blurs. Use them only when nothing else fits the link (a 4K 120 Hz TV on HDMI 2.0, for example), and then prefer 4:2:2 over 4:2:0.

**When YCbCr is forced:** HDMI 2.0 (18 Gbps) can't carry 4K 60 Hz RGB at 10-bit, or 4K 120 Hz at all, so the driver falls back to 4:2:2 or 4:2:0. DP 1.4 (HBR3, about 25.9 Gbps usable after 8b/10b) can't carry 4K 144 Hz 10-bit RGB without DSC. HDMI 2.1 FRL (48 Gbps raw, about 42.6 usable) and DP 2.1 UHBR carry far more.

**Evidence**

- RTINGS: chroma subsampling is "most noticeable with text atop a flat color"; PC use needs 4:4:4; 4:2:2 is better than 4:2:0 "but still fell short". https://www.rtings.com/tv/learn/chroma-subsampling. Test pattern: https://testufo.com/chroma
- Link rates and the 8b/10b overhead: Display Ninja, https://www.displayninja.com/which-cable-do-i-need-for-144hz/, https://www.displayninja.com/what-is-displayport-2-1/ (vendor and blog sources; the VESA and HDMI Forum specs agree on 32.4 Gbps HBR3 and 48 Gbps FRL)

**Confidence:** high.

### 2b. 8 vs 10 vs 12 bpc

**Recommendation:** **the highest depth the link carries without dropping to YCbCr, no deeper than the panel.** This is what `DisplayOutput.BestColor` already does.

- **Latency and performance:** no source shows a frame-rate or input-lag cost for 10 or 12 bpc. Output depth is set in the display engine at scan-out, not where frames are rendered. The real cost is **link bandwidth**: at the top refresh, 10-bit can be what pushes a monitor into DSC, or the driver into 4:2:2 (see 3).
- **SDR desktop:** Windows composes the SDR desktop in 8-bit, so 10-bit output adds little to plain SDR content. It does help where the GPU changes values after rendering: a gamma ramp, digital vibrance, ICC or ACM correction. A 10-bit output carries those changes without the 8-bit rounding that shows as banding.
- **HDR:** 10-bit is the target (HDR10). 8-bit HDR works with dithering but bands more easily.
- **12 bpc on a 10-bit panel:** no gain; the panel throws away the extra bits. HDMI 2.1 TVs often accept 12 bpc and handle it fine. Exo already caps depth at the EDID's panel depth.
- **DSC:** VESA calls it "visually lossless" and reviewers report not seeing it, but **no blind study was found**. Its documented side effects are real: DSR/DLDSR/NIS are unavailable when the mode needs two GPU heads, a second head can block a third or fourth monitor, and NVIDIA links with DSC show longer black screens on mode changes (alt-tab out of exclusive full screen, especially in Vulkan and OpenGL games).

**Evidence**

- DSC and DSR/DLDSR heads, multi-monitor limit: https://tftcentral.co.uk/news/nvidia-dsr-and-dldsr-do-work-with-dsc-monitors-sometimes, https://forums.blurbusters.com/viewtopic.php?p=92611
- DSC alt-tab blackouts (user reports, consistent across sources): https://www.guidingtech.com/fix-black-screen-when-alt-tab-on-windows/, https://forums.developer.nvidia.com/t/black-screen-and-system-freezes-when-i-enable-dsc-10-bits-hdr-and-vrr/307921?page=2
- DSC "visually lossless" (vendor claim, no study): https://www.displayninja.com/what-is-dsc/

**Confidence:** no latency cost = medium (from how it works, not measured). Bandwidth and DSC side effects = high for the head rule (NVIDIA via TFT Central), low-medium for the alt-tab blackouts (user reports only). 12-on-10 has no gain = high.

### 2c. Full vs Limited range

**Recommendation:** **RGB Full for PC monitors.** For TVs, either **RGB Full with the TV's HDMI black level set to Full/High (or Auto)**, or Limited at both ends. **The two ends must match**:

- PC sends Limited, screen expects Full: washed-out blacks and dull whites (the usual NVIDIA-on-HDMI complaint).
- PC sends Full, screen expects Limited: crushed blacks and clipped highlights, and shadow detail is lost.

**Does the driver pick correctly?** **Not reliably on HDMI.** NVIDIA's long-standing default is Limited for "Ultra HD, HD, SD" (CEA) timings, which covers 1080p 60 Hz over HDMI and some DP modes. Users report the choice reverting after driver updates and reboots. On DP monitors running PC timings the driver usually picks Full.

**Evidence**

- NVIDIA defaults to Limited on CEA timings, and Output dynamic range has been in the driver since 347.09: PC Monitors, https://pcmonitors.info/articles/correcting-hdmi-colour-on-nvidia-and-amd-gpus/. Also https://hwbusters.com/quick-tips/quick-tips-20-nvidia-gpu-and-hdmi-monitor/
- The setting reverting after updates or reboots: forum reports, https://forums.anandtech.com/threads/your-dynamic-range-setting-may-be-off-if-you-use-hdmi.2488083/post-38509373

**Confidence:** what a mismatch looks like = high. Driver default Limited on CEA HDMI timings = high (documented for years). That it still reverts = low-medium (anecdotal).

### 2d. "Use default color settings" (driver picks) vs manual

**Recommendation:** **Manual (policy USER) with RGB Full at the deepest depth that fits.** "Best for this screen" should write this explicitly, not "driver picks". The driver's BEST_QUALITY policy is still the right *fallback* for a user who wants hands-off behavior on one screen, and for some HDMI TVs where the user wants the TV's own default.

**Why:** the driver's default is what produces Limited range on HDMI CEA timings (2c), and on HDMI 2.0 TVs at 4K it may pick YCbCr 4:2:2/4:2:0 where 8-bit RGB would fit. Exo checks the supported combinations live (`IS_SUPPORTED_COLOR`) and can do better.

**Confidence:** medium. The Limited-range problem is high confidence. The claim that the driver's format pick is worse than Exo's on TVs rests on user reports, not on a test.

---

## 3. Refresh rate

**Recommendation:** **the highest refresh rate Windows offers at the native resolution**, unless reaching it costs RGB or puts the link onto DSC (see below). Exo's `BestFor` (`WebHostBridge.Display.cs`) already does the first part, from Windows' list less the rates the screen's EDID rules out (`DisplayOutput.OfferedRates`).

**Conditions and side effects**

- **Higher refresh lowers lag at any frame rate.** Each refresh is shorter, so frames reach the screen sooner even below the cap, and V-Sync or tearing artifacts shrink. This is the scan-out mechanism. RTINGS's per-refresh input-lag numbers follow it: one forum-quoted retest was 3.14 ms at 240 Hz, 5.27 at 120 Hz and 9.58 at 60 Hz. https://forums.blurbusters.com/viewtopic.php?p=71252
- **Overclocked modes** (165 Hz "OC" on a 144 Hz panel and similar): overdrive is often tuned for the base rate, so overshoot or ghosting rises and some panels skip frames. Reviews say to stay at the base rate when the OC mode misbehaves, and to test with TestUFO frame skipping. https://forums.anandtech.com/threads/acer-predator-xb271hu-bmiprz-wont-overclock-to-165-hz.2468465/post-38528406, https://forums.blurbusters.com/viewtopic.php?p=54062. Windows does list OC modes once they are on in the monitor's menu, so Exo can't tell them apart. Low confidence that they are worse on today's monitors; most newer "OC" modes are within spec.
- **10-bit at max refresh:** on DP 1.4 without DSC, many 1440p 240 Hz and 4K 144 Hz monitors only take 8-bit RGB (or YCbCr 4:2:2) at their top refresh. Pick the refresh first, then color, which Exo does: `SetDisplay` (`WebHostBridge.Display.cs`) sets the refresh, then picks the color again from what the driver takes at the new rate.
- **DSC at max refresh:** it can be the only way to reach the top rate. The cost (2b) is DSR/DLDSR and extra monitors, not image or lag. Exo can't see DSC state.

**Confidence:** highest refresh is best = high. OC-mode downsides = low (anecdotal, model-specific). Color at max refresh = high (bandwidth arithmetic).

---

## 4. VRR / G-SYNC / FreeSync

**Recommendation** (NVIDIA; Blur Busters G-SYNC 101 and its author)

| Setting | Value | Confidence |
| --- | --- | --- |
| G-SYNC | On, on every VRR-capable screen | High |
| G-SYNC mode | **Full screen** globally (see below for windowed) | Medium |
| V-Sync (driver) with G-SYNC | **On** | High |
| V-Sync (in game) with G-SYNC | Off | High |
| Frame cap with G-SYNC | In-game limiter first; otherwise driver Max Frame Rate at least 3 fps under refresh; Reflex caps itself when on | High |
| Low latency mode | On with a cap; Ultra only when there is no cap and no Reflex | High (Blur Busters) |

**Evidence**

- Optimal settings, Low Latency Mode and Reflex: Blur Busters G-SYNC 101, page 14. Reflex "will override [LLM], regardless of what LLM is set to". With Reflex on, its engine limiter caps at about 116 fps at 120 Hz and 138 at 144 Hz. Ultra auto-caps at the same values in supported games with G-SYNC and driver V-Sync on. Use "On" when a limiter is already in use. LLM is unsupported in Vulkan; DX12 support came in driver 551.23. https://www.blurbusters.com/gsync/gsync101-input-lag-tests-and-settings/14/
- Why V-Sync is on in the driver: G-SYNC alone still tears when frame times spike over the top of the range. Driver V-Sync catches those frames without V-Sync's usual lag as long as frames stay under the cap. Blur Busters G-SYNC 101 pages 5 and 14 (as cited in `gpu-profiles.md`, BB5 and BB14).
- Limiters: in-game limiters are "almost always free of additional latency". The driver's Max Frame Rate (since 441.87) "is a CPU-level FPS limiter ... comparable to the RTSS framerate limiter in both frametime performance and added delay", up to about one frame. https://www.blurbusters.com/gsync/gsync101-input-lag-tests-and-settings/11/. **No newer independent test of the driver limiter was found**, so whether later drivers lowered its delay is unknown.
- Full screen vs windowed: with G-SYNC on, borderless and windowed do not add DWM's one-frame delay over exclusive full screen. The G-SYNC 101 author advises keeping G-SYNC on "Enable for full screen mode" globally and switching windowed on per game when needed. DX12 "fullscreen" games and flip-model borderless games already count as full screen. DX9 games still need exclusive full screen. https://www.blurbusters.com/gsync/gsync101-input-lag-tests-and-settings/10/, https://forums.blurbusters.com/viewtopic.php?p=53964, https://forums.blurbusters.com/viewtopic.php?p=126472. Windows 11's "Optimizations for windowed games" moves DX10/11 borderless games to flip model and gives them VRR (see 6b), so more and more borderless games work with full-screen-only G-SYNC.
- Reflex cap formula (refresh - refresh² / 3600): the Blur Busters forum, as in `gpu-profiles.md` (BBF). The cap values Blur Busters quotes (116 at 120 Hz, 138 at 144 Hz) match the formula.

**G-SYNC Compatible monitors that flicker**

- VRR flicker is a gamma or brightness shift that follows the refresh rate. It shows most in dark scenes and loading screens, and when crossing the LFC boundary. TFT Central measured shifts of up to 11 RGB steps on current OLEDs in a worst-case 480-to-10 Hz sweep; real games swing less. VA panels flicker as well, sometimes more; IPS is close to immune; monitors without a G-SYNC module flicker more (Blur Busters forum moderator). https://tftcentral.co.uk/articles/exploring-and-testing-oled-vrr-flicker, https://forums.blurbusters.com/viewtopic.php?p=108344
- Mitigations, best first: steady frame rates (a cap); the monitor's anti-flicker mode; on the worst screens, VRR off **for that screen**. VESA Adaptive-Sync certification does not guarantee no flicker (RTINGS, as quoted on Guru3D, https://forums.guru3d.com/goto/post?id=6331256).
- So **VRR stays on by default.** Per-display G-SYNC (`NvAPI_DISP_SetAdaptiveSyncData`, `nvidia-control-panel.md` section 7) is the right tool for an OLED or VA screen that flickers, rather than turning G-SYNC off globally.

**AMD / Intel:** FreeSync or Adaptive-Sync on, a frame cap under refresh, Anti-Lag on (Anti-Lag 2 where a game ships it: AMD claims a 37% average latency cut, vendor numbers), Radeon Chill off for lowest lag. https://www.notebookcheck.net/AMD-Radeon-Anti-Lag-2-lands-with-even-more-latency-improvements-and-without-any-risk-of-false-positive-bans.840667.0.html, https://www.club386.com/?p=72043. Exo's Radeon profile (`GpuProfile.Radeon`) and the `GpuProfile.Amd`/`Intel` lists match this, except that Anti-Lag is on only while a game Exo launched runs: on globally, the driver also hooks Windows' own apps and costs CPU (RadeonTuner's driver notes). No independent Intel low-latency measurement was found.

**Against Exo's profile:**

- G-SYNC on, driver V-Sync on, LLM On, cap at Reflex's value and never closer than 3 under refresh: **all match the evidence.**
- **G-SYNC global and application mode Full screen** (`GsyncGlobalMode`, `GsyncAppMode` = 1 in `GpuProfile.cs`): matches. Exo used "Full screen and windowed" until this research. Blur Busters' author keeps it on Full screen globally, windowed mode globally lets desktop apps and browsers switch into VRR (stutter or flicker, worst on OLED and VA), and Windows 11's windowed-game optimizations make most borderless games count as full screen.
- **Driver cap vs in-game cap:** the driver limiter adds up to about a frame; an in-game limiter or Reflex doesn't. Exo can't set in-game limiters. Keeping the driver cap as the safety net is right, but the UI should say "a game's own limiter or Reflex is better when it has one". Medium confidence.

---

## 5. Low latency and driver performance settings (NVIDIA)

| Setting | Recommendation | Measured effect | Confidence |
| --- | --- | --- | --- |
| Low Latency Mode | **On** (Ultra only without a cap or Reflex) | NVIDIA: Ultra is "just in time" frame submission, up to 33% less latency, most effective "GPU-bound ... between 60 and 100 fps", DX9/DX11 only at launch (DX12 since 551.23). In CPU-bound games there is no queue to cut. A forum summary of Battle(non)sense says Ultra can cost a little throughput. https://www.nvidia.com/en-gb/geforce/news/gamescom-2019-game-ready-driver, https://www.pcworld.com/article/3432512/nvidia-gamescom-game-ready-driver-improves-performance-latency-and-sharpness.html, https://forums.blurbusters.com/viewtopic.php?p=47053 | High for On with a cap; medium for Ultra's cost (no primary measurement found) |
| Reflex (in game) | **On** wherever a game has it; it overrides LLM | LDAT tests: biggest gains when GPU-bound. Fortnite at 4K on an RTX 3090 went from about 104 to under 50 ms; 6-9 ms at 1080p/1440p; about nothing in Valorant at 600+ fps. https://www.techspot.com/article/2123-nvidia-reflex-rtx/, https://www.techpowerup.com/review/nvidia-reflex-review-test-ldat-v2/5.html | High |
| Max pre-rendered frames | 1 (part of LLM On) | Same mechanism as LLM | High |
| Power management | **Evidence does not support a latency gain.** Keep "Prefer maximum performance" only as a frame-time-steadiness choice | No controlled latency or frame-time test was found. Forum reports of steadier clocks and fewer DPC spikes; it keeps 3D clocks while a 3D app runs. https://ntlite.com/community/goto/post?id=32075, https://www.overtake.gg/threads/nvidia-powermode-microstutter-explained-are-you-running-3d-mode.155508/ | Low |
| Threaded optimization | **Auto** (driver default) | No measurement found | Low (keep the default) |
| Shader cache size | Larger than the default is harmless with disk space | No NVIDIA statement on the current default or on a gain from 100 GB was found. Forum reports disagree on older defaults (128 MB OpenGL, 1 GB Linux). The Borderlands 4 report in `gpu-profiles.md` is the only gain cited. https://forums.developer.nvidia.com/t/opengl-shader-disk-cache-max-size-garbage-collection/60056 | Low |
| Texture filtering quality | **Quality** (driver default) is the evidence-safe value | **No measurement on any card since about 2006 was found.** On GeForce 7800 High Quality cost under 5% (HEXUS); on G80 (2006) it was "in stride" (AnandTech). Performance modes add visible texture shimmer in motion (HEXUS). One anecdote on a GTX 1080 Ti. https://www.anandtech.com/show/2116/15, https://www.hexus.net/tech/reviews/graphics/1549-nvidia-forceware-70-75-mip-boundary-shimmering-geforce-7800-gt-gtx/?page=4 | Low for any gain, medium that it costs image quality |

**Against Exo's profile:**

- **Texture filtering High performance plus its three optimizations** (`GpuProfile.cs`, kept as the user's performance-first call): `gpu-profiles.md` already says "no measured gain found". Modern GPUs have spare texture-filtering throughput, and the old tests show the gap closing as far back as 2006. The image cost (shimmer in motion, bilinear between mip levels inside AF) is the documented part. The evidence favors the driver default (Quality, optimizations default); performance-first is a preference, not evidence.
- **Power management "Prefer maximum performance"** (`0x1057EB71 = 1` in `GpuProfile.cs`, commented "no clock ramp-up lag"): no latency measurement supports that wording. It is cheap and plausible for steady frame times, so keeping it is defensible, but it is unmeasured (low confidence).
- LLM On, max pre-rendered 1, threaded optimization default, shader cache, "compile while idle": nothing in the evidence contradicts them.

---

## 6. Windows

### 6a. Hardware-accelerated GPU scheduling (HAGS)

**Recommendation:** **On** (the Windows 11 default). It is **required** for DLSS Frame Generation on RTX 40/50.

**Evidence:** PCGH measured a 1-2 fps gain at 1440p on an RTX 2080 Ti (Windows 10 era). GamersNexus found little change, with the most effect on weak CPUs. https://gamersnexus.net/guides/3599-windows-10-hardware-accelerated-gpu-scheduling-benchmarks. Required for DLSS FG: Nixxes support, https://support.nixxes.com/hc/en-us/articles/48432027293971-How-to-use-DLSS-Frame-Generation. **No good 2024-2026 latency or frame-pacing study was found**; reports of VR, capture or stutter problems are anecdotal.

**Confidence:** high for "required by DLSS FG". Medium for "no meaningful cost".

### 6b. Optimizations for windowed games

**Recommendation:** **On.** It moves DX10/DX11 windowed and borderless games from blt to flip model: lower latency, plus VRR and Auto HDR in windowed games. Some games tear with it on (reported), and it can be turned off per game.

**Evidence:** Microsoft support, https://support.microsoft.com/en-us/topic/3f006843-2c7e-4ed0-9a5e-f9389e535952. Flip model gives lower latency: https://learn.microsoft.com/en-ie/Windows/Win32/direct3ddxgi/for-best-performance--use-dxgi-flip-model, https://devblogs.microsoft.com/directx/dxgi-flip-model/. Tearing reports: https://www.ghacks.net/?p=180430

**Confidence:** high.

### 6c. Flip model / MPO

**Recommendation:** **Leave MPO on (Windows default).** Disable it (NVIDIA's `mpo_disable.reg`, article 5157) only on a PC that shows desktop flicker or stutter with G-SYNC.

**Why:** MPO is what lets a borderless or windowed game reach "Hardware: Independent Flip" or "Hardware Composed: Independent Flip". That latency equals exclusive full screen; with ALLOW_TEARING it can be lower still, "even in a window on systems with multi-plane overlay support" (Microsoft). NVIDIA's MPO workaround dates from 2022 (flicker with G-SYNC), and later drivers reportedly fixed much of it.

**Evidence:** https://learn.microsoft.com/en-ie/Windows/Win32/direct3ddxgi/for-best-performance--use-dxgi-flip-model, NVIDIA's workaround via https://support.iracing.com/support/solutions/articles/31000134867-troubleshooting-nvidia-performance-issues, and the NVIDIA swap-chain guide https://developer.nvidia.com/blog/advanced-api-performance-swap-chains/

**Confidence:** medium. The latency benefit of independent flip is documented. The flicker reports are anecdotal, and NVIDIA's own article was not fetched.

### 6d. Full-screen optimizations (exclusive full screen)

**Recommendation:** **Leave on (default).** FSO turns exclusive full screen into flip-model independent flip, which Microsoft describes as having "the same efficiency as fullscreen exclusive". **No independent latency test was found** that measured a loss. Turn it off only per game when that game misbehaves.

**Confidence:** medium.

### 6e. Game Mode

**Recommendation:** **On (default).** Average fps barely moves, but lows improve when background work is running. TechRadar: dips of 10+ fps fell from 20 to 15 in five minutes. PCWorld: about +6 fps in Gears 4 with background load. A 2025 single-author test (iGeeksBlog) showed 1% lows going from 48 to 71 fps in Forza Horizon 5 with Edge and Discord open.

**Evidence:** https://www.techradar.com/news/windows-10-game-mode-gut-check-a-clear-benefit-youll-barely-notice, https://www.pcworld.com/article/406291/tested-windows-10s-game-mode-makes-unplayable-games-playable-sometimes.html, https://www.igeeksblog.com/turning-off-windows-game-mode-improved-gaming-performance/

**Confidence:** medium (old tests, plus one new anecdotal one).

### 6f. HDR, Auto HDR, SDR content brightness, HDR Calibration

**Recommendation**

- **HDR on the desktop: only on screens that do HDR well** (OLED, or mini-LED with many zones and about 600+ nits). On HDR400 edge-lit monitors, leave Windows HDR off and turn it on per game. Never make it part of "best". In HDR Windows maps SDR with the piecewise sRGB curve, not gamma 2.2, so SDR blacks look lifted. No Microsoft statement of a 2.2 option in 24H2 or 25H2 was found; the community fix is an ICC profile. https://github.com/ledoge/win11hdr-srgb-to-gamma2.2-icm
- **Auto HDR:** a preference, not a quality fix. Its cost is too small to measure in user tests (no controlled test was found). RTX HDR costs about 3-10% fps (anecdotal). https://www.patreon.com/posts/99110213. Turning Auto HDR on also turns on windowed-game optimizations (Microsoft).
- **SDR content brightness:** set it so SDR white matches what the user is used to in SDR mode. Microsoft gives no target value. https://support.microsoft.com/help/4040263
- **Windows HDR Calibration app:** **recommend running it once per HDR screen.** It measures the darkest and brightest visible detail and the peak brightness using HGIG test patterns, and stores them for games and Auto HDR to read. https://support.microsoft.com/en-us/windows/hardware/display-graphics/calibrate-your-hdr-display-using-the-windows-hdr-calibration-app

**Confidence:** the gamma mismatch = high (well documented). Auto HDR's cost = low. Calibration app = high (Microsoft).

---

## 7. Desktop color sliders (vibrance, brightness, contrast, gamma)

**Recommendation:** **"Best" never touches them.** They change the image to taste, not toward accuracy.

- **Digital vibrance:** a saturation boost applied in the display pipeline (CSC). No performance cost has been reported, and the mechanism gives no reason for one. Competitive players raise it for visibility, but it moves colors away from the source and clips saturated colors. Its HDR behavior is undocumented. The neutral value is the driver's default (50%). **No source supports any other value as "best".** Confidence: medium (no direct measurement; from the mechanism).
- **Brightness, contrast and gamma through the gamma ramp:** the ramp is a lookup table in the display engine, so it adds no frame latency. Applying it is slow, though: `SetDeviceGammaRamp` "may take as long as 200ms to return". Microsoft documents that it **"has undefined behavior in HDR modes"**. The ramp "is also reset on most display events (connecting/disconnecting a monitor, resolution changes, etc.)", any app can overwrite it (exclusive-full-screen games often set their own), and a ramp that fails the heuristics "fails silently". Microsoft recommends ICC profiles instead. https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-setdevicegammaramp. Confidence: high (Microsoft).
- So the ramp is **not dependable while Windows HDR is on.** Exo follows this: on an HDR screen the Display tab offers no brightness, contrast or gamma (`WebHostBridge.Display.cs`) and `NvidiaColor.Apply` writes the ramp only while HDR is off. It re-applies the ramp on start (`NvidiaColor.ApplySaved`), the right model for resets.
- **NVIDIA "Color accuracy mode"** (Reference / Accurate / Enhanced) only reports which color pipeline is in use; it is not an sRGB clamp. https://www.nvidia.com/content/Control-Panel-Help/vLatest/en-gb/mergedProjects/nvdspENG/CS_Adjust_Color_Settings_Advanced.htm. Leave it alone.

---

## 8. RTX Video Super Resolution and RTX Video HDR

**Recommendation:** **Off by default on gaming PCs. Never in "Best for this screen".** Offer Super Resolution **Auto** to users who watch a lot of low-resolution video, on RTX 40/50 especially and on any desktop RTX card. Offer RTX Video HDR only when Windows HDR is on and the screen handles HDR well.

**Evidence and conditions**

- **Power and GPU cost:** **no reviewer table was found.** Early user measurements (2023, the original model, quality 4, RTX 40-class) were about 70 W for 720p video, 130 W for 1080p and 240 W for 1440p, against about 28/40/50 W at quality 1. A later user saw about 40 W vs 20 W off for 1080p at level 1. https://techpowerup.com/forums/goto/post?id=4962317. NVIDIA's FAQ: every RTX GPU supports all scenarios at quality 1; xx70 and up at quality 4. https://forums.guru3d.com/goto/post?id=6107460
- **January 2025 model:** "up to 30% fewer GPU resources at its highest quality setting", plus an **Auto** quality that scales with the content and the GPU. https://wccftech.com/nvidia-rolls-out-enhanced-rtx-video-super-resolution/amp/, https://www.kitguru.net/components/graphic-cards/joao-silva/rtx-video-super-resolution-gets-ai-model-upgrade/
- **When it runs:** only when the video is smaller than its window on screen (upscaling), in supported players (Chrome, Edge, VLC and others). Not for DRM content. https://www.hwcooling.net/en/nvidia-boosts-rtx-video-super-resolution-performance-adds-hdr/
- **Why off for gamers:** a video on a second screen keeps the GPU's tensor cores and power busy while a game runs. At high quality the 2023 numbers are tens to hundreds of watts. **No published test of the fps cost in a game was found**, so the size of the hit is unknown (low confidence). Off costs nothing.
- **RTX Video HDR** turns SDR video into HDR10; it needs an HDR10 screen with Windows HDR on (driver 551.23+). https://www.hwcooling.net/en/nvidia-boosts-rtx-video-super-resolution-performance-adds-hdr/
- **Generation:** RTX 20 and 30 have no FP8 and fewer tensor cores; RTX 20 got VSR later than 30/40. Expect higher relative cost on RTX 20/30 at levels 3-4 (inferred from NVIDIA's "xx70 and up for level 4" rule).

**Confidence:** off for gaming PCs = medium (from cost and mechanism; no in-game fps test). Power numbers = low (user measurements of the 2023 model).

---

## 9. Monitor-side settings to tell the user about

Exo can't set these. Keep the advice to this short list; each item helps most screens:

1. **Overdrive: the middle setting.** The top setting overshoots (inverse ghosting), worst at low frame rates under VRR. On monitors without variable overdrive, if most games run at 60-80 fps, one step lower. Reviews: https://igorslab.de/en/the-asus-rog-strix-xg27aq-w-in-test-a-good-1440p-sdr-gaming-monitor/4, https://www.igorslab.de/en/ktc-m27p20-pro-hdr-gaming-monitor-review-a-real-clone-or-a-one-off/5/. Confidence: high.
2. **Backlight strobing (ELMB, ULMB, DyAc and similar): off with VRR**, except combined modes (ELMB Sync, ULMB 2) on screens that have them. Strobing is only clean when fps holds at refresh, and it costs brightness. Confidence: medium (well known; not re-sourced here).
3. **VRR anti-flicker mode on OLED** (where the monitor has one): the middle setting. TFT Central measured the swing dropping from 11 to 8.5 steps on the PG27AQDP. https://tftcentral.co.uk/articles/exploring-and-testing-oled-vrr-flicker. Confidence: high.
4. **HDR mode on the monitor:** on OLED, the mode with accurate tracking ("True Black 400" or equivalent) for the desktop, and peak-1000 modes for HDR games if ABL dimming doesn't bother you. On HDR400 edge-lit monitors, leave HDR off. Confidence: medium.
5. **sRGB mode on wide-gamut monitors in SDR**: removes oversaturation (NVIDIA's color accuracy mode doesn't clamp). It often locks brightness. Confidence: medium. https://www.displayninja.com/what-is-srgb-emulation-mode/
6. **TVs: Game mode (or ALLM), and HDMI black level set to match the PC** (Auto, or Full/High with RGB Full). This is the biggest single lag and image fix on a TV. Confidence: high. NVIDIA "Content type reported to the display" = Full-screen gaming may trigger game mode on TVs that read the AVI content-type field. **NVIDIA's documentation of this was not found** (low confidence).
7. **DSC toggle in the monitor menu** (where present): leave it on unless the user needs DSR/DLDSR or a third screen; turning it off usually drops refresh or chroma. Confidence: medium.
8. Avoid **dynamic contrast and black equalizer** for accurate images. They are preferences that crush or lift shadows. Confidence: medium.

---

## What Exo's "Best for this screen" should set

| Setting | Value | When | Confidence |
| --- | --- | --- | --- |
| Refresh rate | Highest Windows lists at the current (native) resolution | Always (done today) | High |
| Color format | RGB | Whenever the link takes RGB at that refresh (done today) | High |
| Range | Full | RGB on monitors (done today). On HDMI TVs: Full plus a note to set the TV's HDMI black level to Auto/Full | High (monitors), medium (TVs) |
| Depth | Deepest RGB depth the link takes, capped at the panel's EDID depth (done today) | Always. A 10-bit pick that needs DSC is acceptable; Exo can't see DSC | Medium |
| Fallback when RGB doesn't fit | YCbCr 4:4:4, then 4:2:2, then 4:2:0, at the deepest depth (done today), with the cable/port note | HDMI 2.0 TVs and monitors at 4K high refresh | High |
| Color policy | USER (explicit), not "driver picks" | Always when Exo applies a color (done today on NVIDIA) | Medium |
| Scaling | Leave as is at native (the normal case); GPU, Aspect ratio for games below native. Best today (NVIDIA) writes No scaling on the GPU and turns on Override, so nothing is stretched; a below-native game shows black borders, and a screen on another scaling blinks once | At native it changes nothing on screen | High (native), medium (below native) |
| G-SYNC for this screen | On | VRR-capable screen, unless the user reports flicker (OLED/VA) | High |
| Digital vibrance, hue, ramp (brightness/contrast/gamma) | **Not touched** | Never | High |
| RTX Video Super Resolution / HDR | **Not touched** (stays off) | Never in Best; a user opt-in elsewhere | Medium |
| Windows HDR | **Not touched** | Never in Best | High |

---

## Changes the evidence suggests to Exo's current GPU profile or Display behavior

1. **G-SYNC global and application mode: Full screen and windowed → Full screen.** Done: `GsyncGlobalMode` and `GsyncAppMode` are 1 with G-SYNC (`ExoLauncher/Helpers/GpuProfile.cs`).
2. **Texture filtering quality: High performance + three optimizations → driver default.** Kept as the user's performance-first call (`0x00CE2691 = 0x14`, `0x0084CD70 = 1`, `0x00E73211 = 1`; `0x002ECAF2 = 0` already matches the default). No measurement since about 2006 shows a gain; the image cost (shimmer, bilinear between mips inside AF) is documented. Low-medium confidence.
3. **Power management "Prefer maximum performance": relabel, don't necessarily change.** `0x1057EB71 = 1`. No latency measurement supports "no clock ramp-up lag"; only forum reports of steadier clocks and DPC times. The `gpu-profiles.md` row now says so; the code comment in `GpuProfile.cs` still says "no clock ramp-up lag". Low confidence.
4. **Ramp sliders under Windows HDR.** Done: no brightness, contrast or gamma on an HDR screen, and no ramp written while HDR is on (`NvidiaColor.Apply`).
5. **Range on HDMI TVs.** `DisplayOutput.BestColor` picks RGB Full for every screen. Right for monitors; on TVs whose black level is set to Limited it crushes blacks. Keep Full, and add one line to `why` when the output is HDMI and the EDID says TV (CEA extension with no PC timings, or HDMI TV video blocks): "Set the TV's HDMI black level to Auto or Full." Medium confidence.
6. **Driver-limiter wording.** Done in the `gpu-profiles.md` frame cap row; the UI text doesn't say it yet: a game's own limiter or Reflex adds less lag than the driver cap, which is the safety net. Blur Busters G-SYNC 101 p.11. The value itself stays. High confidence for the wording.
7. **Per-screen G-SYNC off for flicker.** `NvAPI_DISP_SetAdaptiveSyncData` was tried and left out: it answered invalid argument on the dev PC's fixed-refresh screens, so it couldn't be checked (`nvidia-control-panel.md`, last section). The evidence (TFT Central, Blur Busters) still makes it the right fix for VRR flicker, better than the profile's global No G-SYNC variant. Medium confidence.
8. **"Driver picks" is a choice, not what Best writes.** Done: "Use default color settings" (BEST_QUALITY) is offered, and Best writes USER, since NVIDIA's default picks Limited on HDMI CEA timings (2c/2d).
9. **Integer scaling note.** The scaling control offers Integer without saying it may block HDR (documented on Turing). `NvidiaScaling.cs`, `ui/src/components/gpu/DisplayTab.tsx`. High confidence for Turing, unknown on newer cards.

No change suggested: G-SYNC on with driver V-Sync, LLM On, max pre-rendered 1, the Reflex-formula cap with the 3-under floor, preferred refresh highest, VRR overlay off, the shader-cache and compile-while-idle values, refresh = highest in Best, RGB-first color with the panel-depth cap, and the AMD/Intel lists (Anti-Lag on, Chill off, FreeSync/Adaptive-Sync on with a cap).

## Gaps worth measuring on the dev PC

These are what the literature doesn't answer and Exo could measure itself with PresentMon (`MsBetweenDisplayChange`, `MsUntilDisplayed`) or NVIDIA FrameView:

- Driver Max Frame Rate vs in-game limiter latency on a current driver.
- Texture filtering High performance vs Quality fps on an RTX 30/40 in two or three games.
- Prefer maximum performance vs Normal: frame-time variance.
- RTX VSR at Auto and at 4 with a 1080p video on the second screen while a game runs: fps and watts.
