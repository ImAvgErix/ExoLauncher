# Third-party notices

Exo Launcher's own code is proprietary (see [LICENSE](LICENSE)). The installer also carries the components below, each under its own license, and Exo downloads a few tools at run time. Nothing here changes those licenses; where a license needs its full text, the text ships next to the component or is linked.

## Separate programs shipped with Exo

These run as their own programs. Exo talks to them only through messages, and Exo Launcher is not covered by their licenses.

| Program | License | Source |
| --- | --- | --- |
| **Exo Mouse** (the Devices room), built on OpenMouse's protocol library `@openmouse/protocol` 0.45.2; [OpenMouse](https://github.com/OpenMouse-Project/openmouse) is made by [snekxs](https://github.com/snekxs) and its contributors, and is in Exo with snekxs's permission | AGPL-3.0-or-later | [github.com/ImAvgErix/exo-mouse](https://github.com/ImAvgErix/exo-mouse) |
| **Exo Shaders** (System > Shaders), an unofficial fork of [SCSKiller](https://github.com/BlueHeisenberg/SCSKiller) | GPL-3.0-or-later, with an additional permission under section 7 (`LICENSE-EXCEPTION.txt`) | [github.com/ImAvgErix/exo-shaders](https://github.com/ImAvgErix/exo-shaders). Its own third-party notices ship in `addons\shaders\THIRD-PARTY-NOTICES.md`. Its native helpers come unmodified from SCSKiller 1.3.0 (see `engine/prebuilt/SOURCE.md` in its repository). |
| **Exo Capture** (instant replay, recording and snips; `addons\capture`) | GPL-3.0-or-later | [github.com/ImAvgErix/exo-capture](https://github.com/ImAvgErix/exo-capture) |
| **OBS Studio game capture files** 32.2.2 (`graphics-hook`, `inject-helper`, `get-graphics-offsets`; 32- and 64-bit), OBS Project and contributors, unmodified and signed by OBS Project, LLC, used by Exo Capture only for a game Windows capture gets no picture from | GPL-2.0-or-later | [github.com/obsproject/obs-studio/tree/32.2.2](https://github.com/obsproject/obs-studio/tree/32.2.2) (`plugins/win-capture`) |
| **uBlock Origin Lite** 2026.1018.2229, Raymond Hill and contributors, unmodified, loaded into the embedded music browser | GPL-3.0 (full text in the extension's `LICENSE.txt`) | [github.com/uBlockOrigin/uBOL-home/releases/tag/2026.1018.2229](https://github.com/uBlockOrigin/uBOL-home/releases/tag/2026.1018.2229) |
| **SponsorBlock** 6.1.7, Ajay Ramachandran and contributors, unmodified, loaded into the embedded music browser for YouTube Music | GPL-3.0 | [github.com/ajayyy/SponsorBlock/tree/6.1.7](https://github.com/ajayyy/SponsorBlock/tree/6.1.7) |
| **Better Lyrics** 2.3.3, unmodified, loaded into the embedded music browser for YouTube Music | GPL-3.0 | [github.com/better-lyrics/better-lyrics/tree/v2.3.3](https://github.com/better-lyrics/better-lyrics/tree/v2.3.3) |

The source for every build of these programs that Exo ships is published at the links above, at the version in that release.

## Libraries and assets inside Exo

| Component | Version | License |
| --- | --- | --- |
| .NET runtime (self-contained) | 11 | MIT |
| Windows App SDK (WinUI) | 2.x | Microsoft Software License Terms |
| Microsoft Edge WebView2 SDK | 1.0.4191.47 | BSD-3-Clause |
| [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx) C API (`sherpa-onnx-c-api.dll`) with [ONNX Runtime](https://github.com/microsoft/onnxruntime) (`onnxruntime.dll`), from the org.k2fsa.sherpa.onnx.runtime.win-x64 package, for Only My Voice | 1.13.8 | Apache-2.0 (sherpa-onnx), MIT (ONNX Runtime) |
| Microsoft.Data.Sqlite | 10.0.12 | MIT |
| SQLitePCLRaw | 3.0.5 | Apache-2.0 |
| SQLite | bundled with SQLitePCLRaw | Public domain |
| libdave (Discord, DAVE end-to-end encryption for calls; `dave.dll`) | 8de72b1 | MIT |
| mlspp (Cisco, linked into `dave.dll`) | as pinned by libdave | BSD-2-Clause |
| OpenSSL 3 (linked into `dave.dll`) | as pinned by libdave | Apache-2.0 |
| Opus (Xiph.Org, `opus.dll`) | 1.5 | BSD-3-Clause |
| webrtc-audio-processing (freedesktop.org's copy of WebRTC's audio processing; its AEC3 echo canceller in calls, built into `exo_apm.dll`), with the code it carries: Ooura's and Mark Olesen's FFTs, pffft, rnnoise and spl_sqrt_floor, each under its own permissive license | 2.1 | BSD-3-Clause |
| Abseil C++ (Google, linked into `exo_apm.dll`) | 20240722.0 | Apache-2.0 |
| React, React DOM | 19.3.0 | MIT |
| clsx | 2.1.1 | MIT |
| qrcode-generator (Kazuhiko Arase), the Discord sign-in code | 2.0.4 | MIT |
| lottie-web (Airbnb), Discord's animated stickers | 5.13.0 | MIT |
| hls.js | 1.7.3 | Apache-2.0 |
| dash.js | 4.7.4 | BSD-3-Clause |
| Inter font (Rasmus Andersson and the Inter Project Authors), the official release's variable fonts: InterVariable.woff2 and InterVariable-Italic.woff2 in the UI (`ui/public/fonts`) and Exo Mouse, InterVariable.ttf in the app | 4.1 | SIL Open Font License 1.1 |
| Tabler Icons (Paweł Kuna), through Iconify's `@iconify-json/tabler`: every icon in the UI, Exo Mouse and setup | 3.48.0 | MIT |
| Simple Icons, the marks of a Discord profile's connected accounts (`ui/src/components/discord/connectionMarks.ts`; Skype, Xbox and Amazon from 8.15.0) | 15.22.0 | CC0 1.0 |
| emojibase-data (Miles Johnson), the Discord room's emoji names, categories and keywords (`ui/src/components/discord/emoji.json`, trimmed from it) | 17.0.0 | MIT |
| Glassy UI (Copyright (c) 2026 Glassy UI contributors), its Liquid Glass material: the rim, sheen, shadows and light and dark glass values in `ui/src/tokens.css` and `ui/src/styles/glass.css`, after its `styles/liquid.css` ([github.com/alexburlis/glassyui](https://github.com/alexburlis/glassyui)) | October 2026 | MIT |

The WebView2 Runtime itself is Microsoft's, installed and updated by Windows.

## Downloaded when a feature needs it

Exo does not ship these in its installer; it downloads a fixed release and checks its size and SHA-256 first. The store tools run as their own programs.

| Tool | Used for | License |
| --- | --- | --- |
| [Legendary](https://github.com/derrod/legendary) | Epic Games library, installs and launches | GPL-3.0 |
| [gogdl](https://github.com/Heroic-Games-Launcher/heroic-gogdl) | GOG library, installs and launches | GPL-3.0 |
| [Nile](https://github.com/imLinguin/nile) | Amazon Games library, installs and launches | GPL-3.0 |
| [DeepFilterNet](https://github.com/Rikorose/DeepFilterNet) 3 (Hendrik Schröter), 0.5.6: `exo-deepfilter.dll`, Exo's build of its C interface, libDF, with the low-latency model and the Rust crates it links (tract, ndarray and the rest, each MIT or Apache-2.0), from Exo's own GitHub release | Noise suppression in Discord calls, fetched when Exo's Discord client first needs it | MIT or Apache-2.0 |
| [TitaNet-small](https://catalog.ngc.nvidia.com/orgs/nvidia/teams/nemo/models/titanet_small) (NVIDIA NeMo) as sherpa-onnx publishes it in ONNX: `titanet_small.onnx`, from [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx)'s release, checked against its size and SHA-256 | Only My Voice in Discord calls, fetched the first time you choose Use as My Voice | CC-BY-4.0 |
| [NVIDIA Maxine](https://developer.nvidia.com/maxine) Audio Effects SDK 1.6.1.2 redistributable (NVIDIA Corporation): `NVAudioEffects.dll`, its CUDA, TensorRT and OpenSSL libraries and the `dereverb_denoiser_48k` model, from NVIDIA's own server for your card's generation, checked against its size and NVIDIA's signature | RTX Noise Removal in Discord calls, only when you choose Download | NVIDIA Software License Agreement and Product-Specific Terms for NVIDIA AI Products |
| NVIDIA DLSS, AMD FidelityFX / FSR, Intel XeSS files, from each vendor's own GitHub release | Upscaler updates, only for files a game already ships | DLSS: [NVIDIA RTX SDKs License](https://github.com/NVIDIA/DLSS/blob/main/LICENSE.txt); FidelityFX / FSR: [AMD's FidelityFX SDK license](https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/blob/main/docs/license.md); XeSS: [Intel Simplified Software License](https://github.com/intel/xess/blob/main/LICENSE.txt) (October 2022), in full below |

Upscaler builds that NVIDIA, AMD and Intel don't publish on GitHub come from [DLSS Swapper](https://github.com/beeradmoore/dlss-swapper)'s public archive; Exo uses its release list and download archive only, not its code.

## Intel XeSS license

The XeSS files Exo fetches for your games (`libxess.dll`, `libxess_fg.dll`, `libxell.dll`) are Intel's [XeSS SDK](https://github.com/intel/xess) binaries, unmodified, under these terms:

```text
Intel Simplified Software License (Version October 2022)

Intel(R) Xe Super Sampling (XeSS) SDK: Copyright (C) 2025 Intel Corporation

Use and Redistribution. You may use and redistribute the software, which is provided in binary form only, (the "Software"), without modification, provided the following conditions are met:

* 	Redistributions must reproduce the above copyright notice and these terms of use in the Software and in the documentation and/or other materials provided with the distribution.
* 	Neither the name of Intel nor the names of its suppliers may be used to endorse or promote products derived from this Software without specific prior written permission.
* 	No reverse engineering, decompilation, or disassembly of the Software is permitted, nor any modification or alteration of the Software or its operation at any time, including during execution.

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
