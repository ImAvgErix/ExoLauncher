# Third-party notices

Exo Launcher's own code is proprietary (see [LICENSE](LICENSE)). The installer also carries the components below, each under its own license, and Exo downloads a few tools at run time. Nothing here changes those licenses; where a license needs its full text, the text ships next to the component or is linked.

## Separate programs shipped with Exo

These run as their own programs. Exo talks to them only through messages, and Exo Launcher is not covered by their licenses.

| Program | License | Source |
| --- | --- | --- |
| **Exo Mouse** (the Devices room), built on OpenMouse's protocol library `@openmouse/protocol` 0.48.1; [OpenMouse](https://github.com/OpenMouse-Project/openmouse) is made by [snekxs](https://github.com/snekxs) and its contributors, and is in Exo with snekxs's permission | AGPL-3.0-or-later | [github.com/UhhErix/exo-mouse](https://github.com/UhhErix/exo-mouse) |
| **Exo Shaders** (System > Shaders), an unofficial fork of [SCSKiller](https://github.com/BlueHeisenberg/SCSKiller) | GPL-3.0-or-later, with an additional permission under section 7 (`LICENSE-EXCEPTION.txt`) | [github.com/UhhErix/exo-shaders](https://github.com/UhhErix/exo-shaders). Its own third-party notices ship in `addons\shaders\THIRD-PARTY-NOTICES.md`. Its native helpers come unmodified from SCSKiller 1.3.0 (see `engine/prebuilt/SOURCE.md` in its repository). |
| **Exo Capture** (instant replay, recording and snips; `addons\capture`), Exo's own | Exo Capture usage terms (`addons\capture\LICENSE`); versions up to Exo Launcher 3.17.1 were GPL-3.0-or-later | Not published; up to 3.17.1 at [github.com/UhhErix/exo-capture](https://github.com/UhhErix/exo-capture) |
| **OBS Studio game capture files** 32.2.2 (`graphics-hook`, `inject-helper`, `get-graphics-offsets`; 32- and 64-bit), OBS Project and contributors, unmodified and signed by OBS Project, LLC, used by Exo Capture only for a game Windows capture gets no picture from | GPL-2.0-or-later | [github.com/obsproject/obs-studio/tree/32.2.2](https://github.com/obsproject/obs-studio/tree/32.2.2) (`plugins/win-capture`) |
| **Exo Shield** 2026.1026.1812.7226, Exo's blocker, made from [uBlock Origin Lite](https://github.com/uBlockOrigin/uBOL-home) 2026.1026.1812 (Raymond Hill and contributors) and [HaGeZi](https://github.com/hagezi/dns-blocklists)'s Multi PRO DNS blocklist, loaded into the embedded music browser | GPL-3.0 (full text in the extension's `LICENSE.txt`, its changes in `NOTICE.txt`) | [github.com/UhhErix/exo-shield](https://github.com/UhhErix/exo-shield) |
| **SponsorBlock** 6.1.7, Ajay Ramachandran and contributors, unmodified, loaded into the embedded music browser for YouTube Music | GPL-3.0 | [github.com/ajayyy/SponsorBlock/tree/6.1.7](https://github.com/ajayyy/SponsorBlock/tree/6.1.7) |

The source for every build of these programs that Exo ships is published at the links above, at the version in that release.

## Libraries and assets inside Exo

| Component | Version | License |
| --- | --- | --- |
| .NET runtime (self-contained) | 10 | MIT |
| Windows App SDK (WinUI) | 2.x | Microsoft Software License Terms |
| Microsoft Edge WebView2 SDK | 1.0.4314.35 | BSD-3-Clause |
| [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx) C API (`sherpa-onnx-c-api.dll`) with [ONNX Runtime](https://github.com/microsoft/onnxruntime) (`onnxruntime.dll`), from the org.k2fsa.sherpa.onnx.runtime.win-x64 package, for Only My Voice and dictation | 2.0.1 (ONNX Runtime 1.31.1, from the Microsoft.ML.OnnxRuntime package) | Apache-2.0 (sherpa-onnx), MIT (ONNX Runtime) |
| Microsoft.Data.Sqlite | 10.0.14 | MIT |
| SQLitePCLRaw | 3.0.5 | Apache-2.0 |
| SQLite | bundled with SQLitePCLRaw | Public domain |
| libdave (Discord, DAVE end-to-end encryption for calls; `dave.dll`), with Exo's one addition, an MLS exporter for the call relay (`voice/dave-exporter.patch`) | 1.2.2 (6396c90) | MIT |
| mlspp (Cisco, linked into `dave.dll`) | as pinned by libdave | BSD-2-Clause |
| OpenSSL (linked into `dave.dll`), built with vcpkg's openssl port (MIT, `voice/ports/openssl`) | 3.5.9 | Apache-2.0 |
| Opus (Xiph.Org, `opus.dll`), with Exo's one fix, an unclipped two-channel downmix (`voice/opus-downmix.patch`) | 1.6.1 | BSD-3-Clause |
| webrtc-audio-processing (freedesktop.org's copy of WebRTC's audio processing; its AEC3 echo canceller in calls, built into `exo_apm.dll`), with the code it carries: Ooura's and Mark Olesen's FFTs, pffft, rnnoise and spl_sqrt_floor, each under its own permissive license | 2.1 | BSD-3-Clause |
| Abseil C++ (Google, linked into `exo_apm.dll`) | 20240722.0 | Apache-2.0 |
| React, React DOM | 19.3.1 | MIT |
| clsx | 2.1.1 | MIT |
| qrcode-generator (Kazuhiko Arase), the Discord sign-in code | 2.0.4 | MIT |
| lottie-web (Airbnb), Discord's animated stickers | 5.13.0 | MIT |
| hls.js | 1.7.4 | Apache-2.0 |
| dash.js | 5.2.1 | BSD-3-Clause |
| Inter font (Rasmus Andersson and the Inter Project Authors), the official release's variable fonts: InterVariable.woff2 and InterVariable-Italic.woff2 in the UI (`ui/public/fonts`) and Exo Mouse, InterVariable.ttf in the app | 4.1 | SIL Open Font License 1.1 |
| Tabler Icons (Paweł Kuna), through Iconify's `@iconify-json/tabler`: every icon in the UI, Exo Mouse and setup | 3.50.0 | MIT |
| Simple Icons, the marks of a Discord profile's connected accounts (`ui/src/components/discord/connectionMarks.ts`; Skype, Xbox and Amazon from 8.15.0) | 16.36.0 | CC0 1.0 |
| Simple Icons, the YouTube Music, Apple Music, Steam, Epic Games and GOG.com marks and brand colors in the title bar and taskbar icon of Exo's sign-in and pop-out windows (`ExoLauncher/Helpers/ServiceMarks.cs`) | 16.36.0 | CC0 1.0 |
| emojibase-data (Miles Johnson), the Discord room's emoji names, categories and keywords (`ui/src/components/discord/emoji.json`, trimmed from it) | 18.0.0 | MIT |
| Glassy UI (Copyright (c) 2026 Glassy UI contributors), its Liquid Glass material: the rim, sheen, shadows and light and dark glass values in `ui/src/tokens.css` and `ui/src/styles/glass.css`, after its `styles/liquid.css` ([github.com/alexburlis/glassyui](https://github.com/alexburlis/glassyui)) | October 2026 | MIT |

The WebView2 Runtime itself is Microsoft's, installed and updated by Windows.

The Controller Lab's stick calibration follows the sequences of [dualshock-tools](https://github.com/dualshock-tools/dualshock-tools.github.io) (the_al, MIT), and its DualSense Edge profiles the open format and stick presets of [dualsense-tester](https://github.com/daidr/dualsense-tester) (daidr, MIT), checked against a real Edge's stored profile. Exo's code is its own; nothing comes from Sony's software.

Devices' USB port line follows the idea of [CPU Direct USB](https://github.com/MariusHeier/cpu-direct-usb) by Marius Heier (MIT): tell a device's USB controller by its PCI id and count the chips between it and the processor. Exo's reader is its own code; the controller ids it knows are facts from [The PCI ID Repository](https://pci-ids.ucw.cz/) and Linux's xHCI driver.

## Downloaded by Exo

Exo does not ship these in its installer file; it downloads a fixed release and checks its size and SHA-256 first (the frame generation mods: their newest release, checked against the SHA-256 GitHub publishes for it). Exo's setup fetches the store tools, DeepFilterNet and the two NVIDIA models while it installs; the upscaler files come only for a game that ships them, and the frame generation mods only for a game you turn frame generation on in. The store tools run as their own programs.

| Tool | Used for | License |
| --- | --- | --- |
| [Legendary](https://github.com/derrod/legendary) | Epic Games library, installs and launches | GPL-3.0 |
| [gogdl](https://github.com/Heroic-Games-Launcher/heroic-gogdl) | GOG library, installs and launches | GPL-3.0 |
| [Nile](https://github.com/imLinguin/nile) | Amazon Games library, installs and launches | GPL-3.0 |
| [DeepFilterNet](https://github.com/Rikorose/DeepFilterNet) 3 (Hendrik Schröter), 0.5.6: `exo-deepfilter.dll`, Exo's build of its C interface, libDF, with the low-latency model and the Rust crates it links (tract, ndarray and the rest, each MIT or Apache-2.0), from Exo's own GitHub release | Noise suppression in Discord calls and dictation | MIT or Apache-2.0 |
| [TitaNet-small](https://catalog.ngc.nvidia.com/orgs/nvidia/teams/nemo/models/titanet_small) (NVIDIA NeMo) as sherpa-onnx publishes it in ONNX: `titanet_small.onnx`, from [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx)'s release, checked against its size and SHA-256 | Only My Voice in Discord calls | CC-BY-4.0 |
| [Parakeet TDT 0.6B v3](https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3) (NVIDIA NeMo) as sherpa-onnx publishes it in ONNX with int8 weights: `encoder.int8.onnx`, `decoder.int8.onnx`, `joiner.int8.onnx` and `tokens.txt`, unpacked from [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx)'s asr-models release, each checked against its size and SHA-256 | Dictation, the mic in Exo's text boxes | CC-BY-4.0 |
| NVIDIA DLSS, AMD FidelityFX / FSR, Intel XeSS files, from each vendor's own GitHub release | Upscaler updates, only for files a game already ships | DLSS: [NVIDIA RTX SDKs License](https://github.com/NVIDIA/DLSS/blob/main/LICENSE.txt); FidelityFX / FSR: [AMD's FidelityFX SDK license](https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/blob/main/docs/license.md); XeSS: [Intel Simplified Software License](https://github.com/intel/xess/blob/main/LICENSE.txt) (October 2022), in full below |
| [MFG Unlocker](https://github.com/dashdogy/MFG-Unlocker) (dashdogy), its newest release: `MFGUnlocker.dll` | NVIDIA's own frame generation on GeForce RTX 20 and 30, and 3x and 4x on RTX 40, only in a game you turn it on for, placed in that game's folder | MIT; it embeds programs derived from Intel's XeSS SDK under Intel's terms, and Dear ImGui, MinHook and Ultimate ASI Loader under theirs |
| [OptiScaler](https://github.com/optiscaler/OptiScaler), its newest release: its loader, and from what that release carries, AMD's FidelityFX loader and frame generation, Nukem's [dlssg-to-fsr3](https://github.com/Nukem9/dlssg-to-fsr3) and, on a Radeon or Intel Arc, [fakenvapi](https://github.com/optiscaler/fakenvapi) and Intel XeSS, with the release's own license files | FSR frame generation where NVIDIA's can't run, only in a game you turn it on for, placed in that game's folder | OptiScaler: GPL-3.0; dlssg-to-fsr3: GPL-3.0 source, its build as Nukem publishes it; fakenvapi: MIT; FidelityFX: [AMD's FidelityFX SDK license](https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/blob/main/docs/license.md); XeSS: Intel Simplified Software License, in full below |

Upscaler builds that NVIDIA, AMD and Intel don't publish on GitHub come from [DLSS Swapper](https://github.com/beeradmoore/dlss-swapper)'s public archive; Exo uses its release list and download archive only, not its code.

## Intel XeSS license

The XeSS files Exo fetches for your games (`libxess.dll`, `libxess_fg.dll`, `libxell.dll`) are Intel's [XeSS SDK](https://github.com/intel/xess) binaries, unmodified, under these terms:

```text
Intel Simplified Software License (Version October 2022)

Intel(R) Xe Super Sampling (XeSS) SDK: Copyright (C) 2025 Intel Corporation

Use and Redistribution. You may use and redistribute the software, which is provided in binary form only, (the "Software"), without modification, provided the following conditions are met:

* Redistributions must reproduce the above copyright notice and these terms of use in the Software and in the documentation and/or other materials provided with the distribution.
* Neither the name of Intel nor the names of its suppliers may be used to endorse or promote products derived from this Software without specific prior written permission.
* No reverse engineering, decompilation, or disassembly of the Software is permitted, nor any modification or alteration of the Software or its operation at any time, including during execution.

No other licenses. Except as provided in the preceding section, Intel grants no licenses or other rights by implication, estoppel or otherwise to, patent, copyright, trademark, trade name, service mark or other intellectual property licenses or rights of Intel.

Third party software.  "Third Party Software" means the files (if any) listed in the "third-party-software.txt" or other similarly-named text file that may be included with the Software. Third Party Software, even if included with the distribution of the Software, may be governed by separate license terms, including without limitation, third party license terms, open source software notices and terms, and/or other Intel software license terms. These separate license terms solely govern Your use of the Third Party Software.

DISCLAIMER. THIS SOFTWARE IS PROVIDED "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE, AND NON-INFRINGEMENT ARE DISCLAIMED. THIS SOFTWARE IS NOT INTENDED FOR USE IN SYSTEMS OR APPLICATIONS WHERE FAILURE OF THE SOFTWARE MAY CAUSE PERSONAL INJURY OR DEATH AND YOU AGREE THAT YOU ARE FULLY RESPONSIBLE FOR ANY CLAIMS, COSTS, DAMAGES, EXPENSES, AND ATTORNEYS' FEES ARISING OUT OF ANY SUCH USE, EVEN IF ANY CLAIM ALLEGES THAT INTEL WAS NEGLIGENT REGARDING THE DESIGN OR MANUFACTURE OF THE SOFTWARE.

LIMITATION OF LIABILITY. IN NO EVENT WILL INTEL BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

No support.  Intel may make changes to the Software, at any time without notice, and is not obligated to support, update or provide training for the Software.

Termination. Your right to use the Software is terminated in the event of your breach of this license.

Feedback.  Should you provide Intel with comments, modifications, corrections, enhancements or other input (“Feedback”) related to the Software, Intel will be free to use, disclose, reproduce, license or otherwise distribute or exploit the Feedback in its sole discretion without any obligations or restrictions of any kind, including without limitation, intellectual property rights or licensing obligations.

Compliance with laws.  You agree to comply with all relevant laws and regulations governing your use, transfer, import or export (or prohibition thereof) of the Software.

Governing law.  All disputes will be governed by the laws of the United States of America and the State of Delaware without reference to conflict of law principles and subject to the exclusive jurisdiction of the state or federal courts sitting in the State of Delaware, and each party agrees that it submits to the personal jurisdiction and venue of those courts and waives any objections. THE UNITED NATIONS CONVENTION ON CONTRACTS FOR THE INTERNATIONAL SALE OF GOODS (1980) IS SPECIFICALLY EXCLUDED AND WILL NOT APPLY TO THE SOFTWARE.
```

## Trademarks

Steam, Valve, Epic Games, GOG, Riot Games, VALORANT, League of Legends, Xbox, Microsoft Store, EA, Ubisoft, Battle.net, Amazon Games, Discord, Spotify, Apple Music, YouTube Music, NVIDIA, DLSS, AMD, FSR, Intel, XeSS, Finalmouse and the other names, logos and game art shown in Exo or in these documents belong to their owners. Exo Launcher is an independent product and is not affiliated with, endorsed by or sponsored by any of them. Game covers, trailers and store data are shown as the stores and services provide them.
