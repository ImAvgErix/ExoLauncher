# NVIDIA driver install: latest driver, no bloat, tweaked

Research for a one-click driver installer in System > Graphics > Driver and profile, above the driver profile. Sources, read on this PC on October 17, 2026 (read only; nothing installed or changed):

- **Nexus 1.4** (`C:\Program Files\Nexus`, .NET 8 WPF). `Nexus.dll` is protected: method bodies are encrypted at rest (ILSpy 9 shows empty bodies, "Read out of bounds" on the main types), user strings are AES/base64, resources are encrypted, and `Nexus.Authentication.Security.AntiRuntime` kills debuggers. Its logic could not be read statically, and it was not run. What Nexus does was read instead from its type and member names, its own files (`%TEMP%\Nexus\...`), the NVIDIA installer log of the install it ran on October 12 (`%TEMP%\NvidiaLogging\Log.4472setup.exe.log`), and the registry keys it marked.
- **NVCleanstall**: not on this PC (Downloads, Program Files, `%LOCALAPPDATA%`, `%APPDATA%` searched). **Not reversed.** Its section uses TechPowerUp's product page and changelog (v1.0.0 to v1.19.0) only.
- **NVIDIA 617.42 Game Ready DCH** (`617.42-desktop-win10-win11-64bit-international-dch-whql.exe`, 990,853,168 bytes), downloaded by Nexus to `%TEMP%\Nexus\Drivers\Nvidia\nvidia-617.42.exe` and extracted by it to `%TEMP%\Nexus\Extracted\Nvidia\nvidia-617.42\`. `setup.cfg` and every `.nvi` were read from both the archive (7-Zip) and the extraction, and are byte-identical.
- NVIDIA's lookup APIs, queried live for this PC's RTX 3070 (`PCI\VEN_10DE&DEV_2484`).

Labels: **seen** = read in a file, a binary, a log, the registry or a live response. **inferred** = reasoned from evidence, not read. **public** = public documentation or common knowledge, not checked here. **unknown** = looked for, not found.

## Version check

**Seen (live, October 17, 2026).** Two NVIDIA endpoints, no key, plain GET:

1. Product IDs: `https://www.nvidia.com/Download/API/lookupValueSearch.aspx?TypeID=N` returns XML `<LookupValue ParentID=".."><Name>..</Name><Value>..</Value>`.
   - `TypeID=1`: product type (GeForce = 1).
   - `TypeID=2`: series (psid). `GeForce RTX 30 Series` = 120, `GeForce RTX 30 Series (Notebooks)` = 123, `GeForce RTX 50 Series` = 131, `(Notebooks)` = 133. ParentID = product type.
   - `TypeID=3`: product (pfid). `<LookupValue ParentID="120"><Name>GeForce RTX 3070</Name><Value>933</Value>`. ParentID = psid.
2. Driver list: `https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php?func=DriverManualLookup&psid=120&pfid=933&osID=57&languageCode=1033&isWHQL=1&dch=1&upCRD=0&sort1=0&numberOfResults=1`
   - Returns JSON `IDS[].downloadInfo` with `Version` ("617.42"), `Name` (URL-encoded "GeForce Game Ready Driver"), `IsDC`, `IsCRD`, `IsWHQL`, `IsBeta`, `ReleaseDateTime`, `DownloadURL`, `DownloadURLFileSize` ("990.85 MB"), `DetailsURL`, `ReleaseNotes`. `IsNewest` was "0" on the newest entry, so sort by version, do not trust that flag.
   - `osID=57` = Windows 10/11 64-bit. `languageCode=1033` = English (the package is international either way).
   - `dch=1` DCH; `dch=0` returns 472.12 (the last Standard driver, 2021). Standard is dead: always DCH.
   - `upCRD=0` Game Ready; `upCRD=1` Studio. The response's `spcall` echoes the stored procedure, with `upCRD` as its last argument. On this date `upCRD=1` returned `DriverDownloadIDNotFound` for the RTX 3070 (no Studio driver in that list): show Studio only when the call returns one.

**Cards Game Ready left (seen live, October 21, 2026).** NVIDIA's current branches (R615 here) support Turing and newer only; release 580 was the last for Maxwell, Pascal and Volta. For a GTX 1080 Ti (psid 101, pfid 845) and a GTX 970 (98, 756) the driver list returns `GeForce Security Update Driver` 582.78 (`Release` "580"), and Studio 581.57; an RTX 2080 (107, 879) and the RTX 3070 get Game Ready 617.42 (`Release` "615"). The forum hotfixes are on the current branch, so Exo offers one only to a card whose `MatchingDeviceId` device is 0x1E00 (Turing) or above; every Maxwell, Pascal and Volta device id is below it.

**PCI device ID to psid/pfid** (seen pieces; the join is inferred, NVIDIA publishes no direct map):

1. Read `PCI\VEN_10DE&DEV_xxxx` and the name from `Win32_VideoController` (Exo already has it in `GraphicsService`).
2. Name the product: the driver's own `ListDevices.txt` maps `DEV_2484` to "NVIDIA GeForce RTX 3070" (seen, 7,128 lines, per subsystem too). Before a driver is installed, Windows' device name works.
3. Strip "NVIDIA ", match `TypeID=3` `Name` exactly; its `ParentID` is the psid. Laptop GPUs ("Laptop GPU" in the name) live under the "(Notebooks)" series; prefer the psid whose name says Notebooks when the PC is a laptop.
4. Cache the two lookup lists (about 200 KB each) for a day; the driver list call is small.

Installed version: `Win32_VideoController.DriverVersion` `32.0.16.1742` = 617.42 (last five digits of the last two fields: `6.1742` → `617.42`; seen on this PC). Exo's `NvidiaProfiles.DriverVersion()` already reads 61742 through NvAPI.

**What Nexus does (seen names, inferred behaviour):** `NvidiaDriverService.GetAvailableDriversAsync` → `FetchDriversFromTechPowerUpAsync`, using HtmlAgilityPack: it scrapes TechPowerUp's driver page (`techpowerup.com/download/nvidia-geforce-graphics-drivers/`, which lists versions and NVIDIA's file names) for its version drop-down, rather than asking NVIDIA. Exo should ask NVIDIA directly (above): it is the source, it filters by GPU, and it gives Studio vs Game Ready.

## Download

**URL pattern (seen in the live API and on TechPowerUp):**

```
https://us.download.nvidia.com/Windows/{ver}/{ver}-desktop-win10-win11-64bit-international-dch-whql.exe
```

Use `DownloadURL` from the API response rather than building it. Notebook packages use `notebook` in place of `desktop`; Studio packages carry `-nsd-` (`{ver}-desktop-win10-win11-64bit-international-nsd-dch-whql.exe`). **Public, not checked here.**

Nexus: `NvidiaDriverService.DownloadDriverAsync(downloadUrl, destinationPath, progress, ct)` streams the response to `%TEMP%\Nexus\Drivers\Nvidia\nvidia-{ver}.exe` (seen: state machine fields `request`, `response`, `totalBytes`, `contentStream`, `fileStream`, `buffer`; the file on disk). The file is the same size as NVIDIA's (990,853,168 bytes = "990.85 MB") and carries NVIDIA's valid signature, so it came from NVIDIA's server or an exact mirror (inferred). There is no hashing in the download state machine (seen). The assembly references `X509Certificate.CreateFromSignedFile` and has a `SignerName` property (seen), so a signer-name check after download is likely (inferred, unconfirmed).

**Verification Exo should do (seen facts this rests on):** the self-extracting exe has a valid Authenticode signature, signer `CN=NVIDIA Corporation, OU=2008B9F, O=NVIDIA Corporation, L=Santa Clara, S=California, C=US`, timestamp by DigiCert (`Get-AuthenticodeSignature`: Valid). NVIDIA publishes no hash. So:

1. `WinVerifyTrust` (WINTRUST_ACTION_GENERIC_VERIFY_V2, revocation check on) must return 0, and the leaf certificate's subject O must be `NVIDIA Corporation`. Do not pin OU or thumbprint; they rotate (the inner `setup.exe` is signed `OU=1004yx4`).
2. File size equals the HTTP Content-Length; the version in the file name equals the version asked for.
3. After extraction, check `setup.exe` the same way, and `Display.Driver\NV_DISP.CAT` is the catalog Windows checks every driver file against at install (WHQL, signer "Microsoft Windows Hardware Compatibility Publisher", seen on `nvlddmkm.sys`). Windows enforces that itself; Exo only must not touch the files it covers.

Host to add to `NetworkDestinations.cs` and PRIVACY.md: `www.nvidia.com` and `gfwsl.geforce.com` (version check), `us.download.nvidia.com` (download). Build the client with `ExoHttp.Create`.

## Extraction

The exe is a PE with an appended 7z archive (seen, 7-Zip: `Type = 7z`, `Method = LZMA2:26 LZMA:20 BCJ2`, physical size 989,742,486). `7z x driver.exe -o<dir>` extracts it; 7-Zip reads the PE overlay. Nexus ships `Bin\7za.exe` for this (seen) and extracts to `%TEMP%\Nexus\Extracted\Nvidia\nvidia-{ver}\` (seen; `NvidiaDebloaterService.ExtractDriverAsync`, then `GpuDebloaterModalWindow.ExtractAndScanPackageAsync` builds the component list from the extracted `.nvi` files). Extraction is about 3.8 GB on disk; unpacking takes about 4 minutes on this PC (seen: ctime 16:53 → 16:57).

Layout of 617.42 (seen):

```
setup.exe  setup.cfg  EULA.txt  ListDevices.txt
Display.Driver\  (2.8 GB)   NVI2\  HDAudio\  PhysX\  PPC\  NVPCF\  NvVAD\  MSVCRT\
NvApp\  NvApp.MessageBus\  NvBackend\  NvContainer\  NvCpl\  NvDLISR\  NvTelemetry\  ShadowPlay\  FrameViewSDK\
```

7-Zip can extract only the folders wanted: `7z x driver.exe -o<dir> setup.exe setup.cfg EULA.txt ListDevices.txt Display.Driver NVI2 [HDAudio] [PhysX] -r`. Solid LZMA2 still decompresses the skipped blocks it passes, so time drops less than size (inferred).

## The package format

**`setup.cfg`** (seen): root `<setup title=".." version="617.42" installerMin="2.1002.158.1139">`.

- `<install><search dir="."><select name="**\*"/> ... <deselect .../>` makes NVI2 scan every subfolder for `*.nvi`. A component is in the package if and only if its folder (with its `.nvi`) is there. The setup log of Nexus's install confirms it: `FindSubPackageConfigFiles : Found 1 sub-package config files - 0 excluded - 1 selected unique` → `Display.Driver\DisplayDriver.nvi`.
- `<sub-package name=".." disposition=".." userSelectable/hidden>` overrides per component (Display.Driver is `critical`; Display.NvApp `default`; VirtualAudio `demand`, hidden).
- `<options>` are the command-line switches (seen, full list): `enableTelemetry`, `clean`, `prestage`, `validate`, `forcereboot`, `noreboot`, `k` (= forcereboot), `n` (= noreboot), `passive`, `noeula`, `nofinish`, `ignorepnp`, `progresswitheula`, `nosplash`, `nvappinitiated`, `custominvokerid`.
- `<manifest>` lists files setup expects, including `${{EulaHtmlFile}}` = `NvApp\Unified_EULA\EULA_DD_NvApp_${{LocaleName}}.html`, `${{FunctionalConsentFile}}` and `${{PrivacyPolicyFile}}` under `NvApp\`. These are not `core="true"`. Nexus removed `NvApp\` without editing setup.cfg and setup ran to final status 0 (seen in the log), so leaving those lines is fine on 617.42. Other tools delete the three lines anyway (public); Exo does not need to.
- `presentations.cfg` (in `NVI2\`) holds the UI; nothing to change.

**`.nvi`** (seen): one XML per component, root `<nvi name="Package.Id" version=".." disposition="default|demand" hidden=".." hasDriver="true">` with `<dependencies><package type="requires|after|installs|uninstalls" package=".."/>`, `<constraints>`, `<properties>`, `<manifest>` (every file with its size) and `<phases>` (what setup does: `<inf>`, `<exe>`, `<addRegistry>`, `<deleteDirectoryTree>`, services). `disposition="demand"` packages install only when something that requires them is installed.

**Dependencies that matter** (seen in the `.nvi` files):

- `Display.NvApp` requires `NvTelemetry`, installs `VirtualAudio.Driver`; NvBackend requires `NvContainer.User` + `NvTelemetry`; ShadowPlay requires `VirtualAudio.Driver`, `NvContainer.User`, `NvContainer.LocalSystem`; NvTelemetry requires `NvContainer.LocalSystem`. Remove NvApp and all of these become unneeded.
- `Display.Driver` requires nothing in the package. It already carries, in its own folder: the NVIDIA Display Container (`Display.NvContainer\NVDisplay.Container.exe`, service `NVDisplay.ContainerLocalSystem`), the Control Panel as an appx (`NVCPL\*.appx`, installed with `Dism.exe` in phase `cplAppxPackageInstall` when `!IsNvCplUIPresent && Feature.NvCpl`), Ansel (`NvCamera\`), NVWMI, and `NvTelemetry64.dll`. The log of the Nexus install confirms: `Found "Display.ControlPanel,NVDisplayContainer,Ansel,Display.NVWMI" selected package for Display.Driver`. These cannot be removed by deleting folders; they are inside the signed driver.
- `NVI2\` is the installer engine (`NVI2.dll`, `NVI2UI.dll`, `NvInstallerUtil.dll`, `NVPrxy*.dll` are `core="true"` in the manifest). Always keep.
- `Display.PhysX` installs only when the INF sets `NVSupportPhysx` (`Feature.Physx`, seen in the log).

## Components

| Component (package id) | Folder | What it is | Driver only, no bloat |
| --- | --- | --- | --- |
| Display.Driver | `Display.Driver\` | The graphics driver (nvlddmkm), Vulkan/OpenGL/OpenCL/CUDA/NVENC, Display Container, Control Panel appx, Ansel, NVWMI | **Keep** (required) |
| installer core | `NVI2\` + `setup.exe`, `setup.cfg` | NVIDIA's installer engine and UI | **Keep** (required) |
| HDAudio.Driver | `HDAudio\` | Audio over HDMI/DisplayPort | **Keep if** a monitor/TV/AVR plays sound over the GPU; otherwise strip. Default: keep (removing it silences HDMI/DP speakers) |
| Display.PhysX | `PhysX\` (203 MB) | PhysX System Software for old GPU-PhysX games | Optional. Default: keep (few games need it, it costs disk only) |
| MSVCRuntime2019 | `MSVCRT\` | VC++ runtimes for NVIDIA's apps | Strip (the driver alone did not need it on this PC) |
| Display.NvApp | `NvApp\` (467 MB) | NVIDIA App | Strip |
| Display.NvApp.MessageBus, .NvBackend, .NvCPL | `NvApp.MessageBus\`, `NvBackend\`, `NvCpl\` | NVIDIA App back end and its control panel | Strip |
| NvContainer (+ .LocalSystem, .Session, .User, .AIUser, NvPlugin.Watchdog) | `NvContainer\` | App-side containers (not the driver's Display Container) | Strip |
| NvTelemetry | `NvTelemetry\` | NVIDIA App telemetry | Strip |
| ShadowPlay | `ShadowPlay\` | Recording/overlay | Strip |
| VirtualAudio.Driver | `NvVAD\` | Virtual audio for ShadowPlay | Strip |
| FrameViewSdk | `FrameViewSDK\` | Performance overlay for NVIDIA App | Strip |
| NvDLISR | `NvDLISR\` | DL image super-resolution for the App's screenshots | Strip |
| USBC | `PPC\` | USB-C controller driver on old RTX 20 cards with VirtualLink | Strip (keep only if the card has a USB-C port and something uses it) |
| NVPCF | `NVPCF\` | Platform Controllers (Dynamic Boost, laptop power sharing) | Strip on desktops; **keep on laptops** |

Exo's default "driver only" set: `Display.Driver`, `NVI2`, `HDAudio`, `PhysX`, plus `NVPCF` on a laptop. That is close to what Nexus installed here (seen: only Display.Driver).

**How Nexus strips (seen):** it deletes component folders after extraction and does not edit `setup.cfg` or any `.nvi`. Evidence: at the install (16:54) setup found exactly one `.nvi` (Display.Driver); setup.cfg and every `.nvi` in its extraction are byte-identical to the archive; `Program Files\NVIDIA Corporation\Installer2` holds only `Display.Driver.*`, `installer.*`, `InstallerCore`, `CoreTemp.*`. Its modes (seen enum `GpuDebloatMode`): `DriverOnly`, `DriverAndCore`, `Stock`, `Custom`. What "Core" adds is **unknown** (strings encrypted); `PerformDriverAndCoreCleanup`, `ScanComponents`, `GetDisplayComponents` and `PatchSetupCfg` exist as names only. `PatchSetupCfg` exists, but it changed nothing in this run (seen). The extraction left on disk today is complete again (ctime 16:57, after the install): Nexus re-extracted on a later visit (inferred).

## Tweaks

Nexus applies GPU tweaks from Library-like pages, not inside the driver package: `GPUTweaksVM` has `NvidiaRecommendedTweaks`, `NvidiaPerfTweaks`, `NvidiaPowerTweaks`, `NvidiaLatencyTweaks`, `NvidiaAdvancedTweaks`, `NvidiaTelemetry`, `DisablePStates` (seen names). It marks keys it writes with a `Tweak` = `Nexus1` string value (seen in three keys). The values below are **seen on this PC**; "Nexus" in the source column means the key carries the `Nexus1` marker, "this PC" means the value is there but unmarked (Nexus is the likely writer: its latency and power pages, the same session; inferred).

| Tweak | Exact change | Effect | Risk | Kind | Source |
| --- | --- | --- | --- | --- | --- |
| MSI mode | `HKLM\SYSTEM\CurrentControlSet\Enum\<GPU instance>\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties` `MSISupported`=1, `MessageNumberLimit`=1 | Message-signaled interrupts instead of line-based | None | Latency | **The driver's own INF sets both** (seen in `nv_dispi.inf`: `HKR,"Interrupt Management\MessageSignaledInterruptProperties",MSISupported,0x00010001, 1`). Redundant on current drivers; Exo should only verify, never write |
| Disable HDCP | GPU class key `HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\00NN` `RMHdcpKeyglobZero`=1 (DWORD) | No HDCP handshake | Protected video (Netflix 4K, Blu-ray, some streaming) stops playing or drops to SD; some TVs/capture setups need it off | Neither (fixes some capture/monitor issues) | this PC |
| Disable MPO | `HKLM\SOFTWARE\Microsoft\Windows\Dwm` `OverlayTestMode`=5 | DWM stops using multiplane overlays | Fixes flicker/stutter on some setups; costs some power/latency in windowed flip-model games; Microsoft may ignore it on newer builds | Bug workaround | this PC |
| TDR off | `HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers` `TdrLevel`=0, `TdrDelay`=0, `TdrDdiDelay`=0, `TdrDebugMode`=0, `TdrLimitCount`=0, `TdrLimitTime`=0, `TdrTestMode`=0 | Windows no longer resets a hung GPU | **High**: a GPU hang freezes the PC instead of recovering. Do not ship | None | Nexus |
| HAGS on | same key `HwSchMode`=2 | Hardware-accelerated GPU scheduling | Low; needed for DLSS frame generation | Performance (FG) | Nexus key (may be the user's own setting) |
| Force max P-state | GPU class key `DisableDynamicPstate`=1 | GPU stays at full clocks | Higher idle power and heat | Latency (marginal) | this PC (`DisablePStates`) |
| "Latency" class-key set | GPU class key: `D3PCLatency`=1, `F1TransitionLatency`=1, `LOWLATENCY`=1, `Node3DLowLatency`=1, `PciLatencyTimerControl`=0x20, `RMDeepL1EntryLatencyUsec`=1, `RmGspcMaxFtuS`/`RmGspcMinFtuS`/`RmGspcPerioduS`=1, `RMLpwrEiIdleThresholdUs`/`RMLpwrGrIdleThresholdUs`/`RMLpwrGrRgIdleThresholdUs`/`RMLpwrMsIdleThresholdUs`=1, `VRDirectFlipDPCDelayUs`/`VRDirectFlipTimingMarginUs`/`VRDirectJITFlipMsHybridFlipDelayUs`=1, `vrrCursorMarginUs`/`vrrDeflickerMarginUs`/`vrrDeflickerMaxUs`=1, `PreferSystemMemoryContiguous`=1, `RmCacheLoc`=0, `RmDisableInst2Sys`=1, `RmFbsrPagedDMA`=1, `TrackResetEngine`=0, `RMCtxswLog`=3 | Undocumented resource-manager knobs; mostly shorten idle/power-gating timers | Unmeasured; power and stability effects unknown; VRR margins may cause flicker | Claimed latency, unproven | this PC |
| Feature switches | `HKLM\SYSTEM\CurrentControlSet\Services\nvlddmkm\Parameters\FTS`: 118 values `EnableGR*`, `EnableRID*`, `EnableDDC*`, `EnableFSN*`, `EnableDDW1000` (all 1 except `EnableDDC14`=0); `nvlddmkm\FTS` `EnableRID61684`=1, `NXPS`=1 | Driver feature-test overrides | **Unknown meaning**, undocumented; can change behaviour per driver | Unknown | `nvlddmkm\FTS`: Nexus; `Parameters\FTS`: this PC |
| Display power saving off | `HKLM\SYSTEM\CurrentControlSet\Services\nvlddmkm\Global\NVTweak` `DisplayPowerSaving`=0 | No panel power saving (laptops) | Battery life | Neither | Nexus |
| Driver telemetry | `nvlddmkm` service `Parameters\Global\Startup\SendTelemetryData`=0 | Driver telemetry off | None | Privacy | **The INF already sets 0** (seen: `HKR,"Parameters\Global\Startup\SendTelemetryData",,%REG_DWORD%,0x0`). Redundant |

Not present on this PC; **public, not checked**:

| Tweak | Change | Effect | Risk | Kind |
| --- | --- | --- | --- | --- |
| NVIDIA App/GFE telemetry | Strip `NvTelemetry`, `NvApp*`, `NvContainer` (component removal) | No telemetry service at all | None | Privacy |
| Disable Display Container | Service `NVDisplay.ContainerLocalSystem` Start=4 | One fewer process | Breaks the NVIDIA Control Panel (NVCleanstall's own warning). Do not ship | Bloat |
| Disable Ansel | `NvCameraEnable.exe off` from the driver store (`Display.Driver\NvCamera\`) | Ansel hooks off | Low | Bloat |
| Disable GPU activity/RAS plug-in | `HKLM\SYSTEM\CurrentControlSet\Services\NVDisplay.ContainerLocalSystem\LocalSystem\_DisplayDriverRAS` `DisableLoad`=1 (seen as NVIDIA's own phase `createDisableLoadRegForDCHRASPlugin` in `DisplayDriver.nvi`, under `DisableGPUActivation`) | Container skips the RAS plug-in | Low | Privacy/bloat |
| HD Audio sleep timer off | NVCleanstall "Disable NVIDIA HD Audio device sleep timer" | Fixes audio drop-outs on some VR headsets/AVRs | Low | Bug workaround |
| Power: prefer maximum performance | Exo's GPU profile already sets driver power mode through DRS (`GpuProfile.cs`) | | | Performance |

**Recommendation:** Exo's "tweaked" default should be removal of bloat components only, plus settings that are supported and reversible. Leave MSI and driver telemetry alone (the driver already sets them), offer HDCP off and MPO off only as named, reversible toggles with their trade-off stated, and never ship TDR off, the FTS switches or the class-key "latency" set (unmeasured, undocumented, risky).

## Install

**Command line** (switches seen in `setup.cfg` `<options>` and in `NVI2.dll` strings: `-s`, `-silent`, `-deselect`, `-uninstall`, `-preservecache`, `-prestage`, `-log`, `-loglevel`):

```
setup.exe -s -noreboot -noeula -nofinish -nosplash [-clean]
```

- `-s` silent; `-passive` shows only progress (a good fit if Exo wants NVIDIA's progress window).
- `-clean` = NVIDIA's "Perform a clean installation" (property `CleanInstall`): resets NVIDIA settings and profiles. Default to upgrade (no `-clean`); offer clean as an option. Exo re-applies its profile afterwards in both cases.
- `-noreboot` (`-n`): Exo decides about the reboot.
- `-noeula`: skips the EULA page. **The user still agrees to NVIDIA's license**: show NVIDIA's EULA link in Exo's consent step before the install.
- Needs admin: run `setup.exe` elevated (UAC) after Exo's own consent; it writes `Program Files`, the driver store and services.
- Logs: `%TEMP%\NvidiaLogging\Log.<pid>setup.exe.log` and `Log.<pid>RunDll32.EXE.log` (seen).
- **Exit code meaning: unknown.** `NVI2.dll` has "Exit code is considered a success" and "Exit code requires a reboot" (seen) but the numbers were not found. Nexus treats 0 as success (seen in its watchdog script). Exo should treat 0 as success and confirm by reading the driver version afterwards (`Win32_PnPSignedDriver`/NvAPI) and by the log's final line (`process final status 0`); treat any other code as failure and keep the files.
- Reboot: the log shows `Global:RebootRequired`; the Nexus install (an upgrade from 617.14) needed none and ended on `InstallFinishedNoSummary` in 2 min 16 s (seen). A clean install or a first install over Microsoft's basic driver may (inferred).

**How Nexus runs it (seen):** not silently. `%TEMP%\Nexus\Watchdog\gpu_install_watchdog.ps1 -SetupExe -ExtractedRoot -DownloadedInstaller -Vendor Nvidia -DeviceId <DEV id>` opens a console menu; option 1 runs `Start-Process -FilePath setup.exe -Wait` **with no arguments**, so NVIDIA's own wizard shows (log: `Installer not running in silent mode`, pages `!EULA`, `!InstallSelect`, `!PreInstallCheck`, `!InstallProgress`). It snapshots `Win32_PnPSignedDriver` for `*DEV_<id>*` before and after (informational), and on exit code 0 deletes the extraction and the download. The October 12 install was not a clean install (`Condition "Global:CleanInstall && !Feature.NvCpl" disabled phase`). `RunNvidiaDebloatPipeline(log, cleanInstall)` carries a clean-install flag (seen field) — how it is passed is unknown. AMD goes through `pnputil` (`InstallAmdDriverViaPnputilAsync`, seen); NVIDIA does not.

`pnputil /add-driver nv_dispi.inf /install` would install the bare INF without NVI2, but it skips the package's phases (Control Panel appx, Display Container setup, cleanup of old packages). Use `setup.exe`.

## Afterwards

1. Reboot only when the setup log says one is required; ask, never force.
2. Re-apply Exo's GPU profile: `GpuProfileService` already flags a driver change (`State.DriverUpdated` compares the backup's driver with `NvidiaProfiles.DriverVersion()`), and a new driver (or `-clean`) can reset DRS. After install, call `GpuProfileService.Apply(gsync)` with the stored variant, then re-read state.
3. Re-apply Exo's display settings from System > Display (`NvidiaScaling`, output color, RTX Video), which live in the driver's persistence and may be reset by `-clean`.
4. Exo Shaders: the engine already treats a new driver as a reason to compile again (its CHANGELOG: "compiles everything again after a driver update"). The driver installer deletes `%TEMP%\NVIDIA Corporation\NV_Cache` (seen phase `removeDXCacheWhileInstall`) and, on clean installs, NGX model caches. Tell the Shaders page the driver changed so its rebuild prompt shows.
5. Delete the extraction; keep the verified installer (see rollback).

## Recommendation for Exo's flow

System > Graphics > Driver and profile, above the driver profile, one row:

`Driver 617.14 · Update available: 617.42 (Game Ready, October 6)` → **Update**

1. Check (on opening GPU, at most once a day): lookup IDs (cached) → driver list → compare with the installed version. Game Ready by default; Studio only when the user picked Studio and NVIDIA lists one.
2. Click → consent sheet: what downloads (size), what is kept (driver, HDMI/DP audio, PhysX) and removed, upgrade or clean, NVIDIA's EULA link, "needs administrator rights", "closes running games: no, it waits".
3. Download to `%LOCALAPPDATA%\ExoLauncher\drivers\{ver}\` with progress; verify Authenticode (NVIDIA Corporation, valid chain, revocation) and size.
4. Extract with 7-Zip to a temp folder; check `setup.exe` signature; delete the component folders not kept (folders only).
5. Elevated `setup.exe -s -noreboot -noeula -nofinish -nosplash [-clean]`; show progress from the log or as indeterminate.
6. Verify the installed version; parse the log's final status and reboot flag.
7. Re-apply the GPU profile and display settings; signal Exo Shaders; ask for a reboot only if needed.

Safety rules:

- Never start while a game runs (Exo's run-state); never while Exo Shaders compiles. Block the button with the reason.
- Keep the previous verified installer (one version back) for rollback: "Roll back to 617.14" runs the same pipeline with it. Windows' own "Roll back driver" works only for the immediately previous driver-store package.
- Debloating only removes component folders. Never edit, patch or re-sign a file covered by NVIDIA's signature or the `NV_DISP.CAT` catalog (INF, .sys, .dll), never install an unsigned or test-signed driver, never enable test signing. NVCleanstall's INF-based tweaks need exactly that (it "rebuilds the driver's digital signature" with its own key and offers an "EasyAntiCheat-compatible" signature mode, per its changelog). Exo applies tweaks as registry values after a normal install instead, so the driver stays NVIDIA's signed driver and anti-cheat never sees a modified one.
- Never set `TdrLevel`=0, never disable the Display Container, never write undocumented FTS or class-key values.
- Back up every registry value Exo changes (same pattern as `gpu-profile.json`) and offer Undo.
- If the install fails: leave the old driver in place, keep the files, show one status line, write the log path to `app.log`.

## NVCleanstall (not reversed; TechPowerUp's description and changelog only)

- Two sources: it fetches NVIDIA's package itself, downloading only the selected components ("Skip download of unselected driver components"), or it customizes an exe already on disk. Since v1.13 downloads are "split into smaller chunks" and fetching the version list "consumes less bandwidth"; v1.19 adds a checkbox to bypass Cloudflare, so it serves (parts of) packages through TechPowerUp/Cloudflare rather than only NVIDIA's server (inferred from the changelog).
- Components: any package can be unselected, including ones NVIDIA's custom install hides ("Stereo 3D Glasses", "USB-C", "Notebook Optimizations", "Telemetry"), with dependency checking. On laptops "Recommended" also selects NV Platform Controllers.
- Tweaks named in the changelog: disable driver telemetry (experimental, v1.7), Message Signaled Interrupts with CPU selection and priority (v1.3/v1.10, only on `nvlddmkm` devices), disable HDCP (v1.10), disable NVIDIA Container ("breaks GFE and the NVIDIA Control Panel", v1.10), disable Ansel (v1.16), disable Multiplane Overlay (v1.15.1), remove the NVENC session limit (keylase/nvidia-patch, v1.15.1), DLSS indicator overlay (v1.11), HD Audio sleep timer off (v1.1), install the Control Panel app from the Store without an account (v1.3), mod the INF to add unsupported devices (v1.7), "Unattended Installation" with optional no reboot (v1.1), pre/post install hooks (v1.10), "Build Package" self-extracting exe (`-y` for unattended), block Windows Update driver installs while it runs (v1.8).
- Signature: tweaks that edit the INF invalidate NVIDIA's catalog, so it "rebuilds the driver's digital signature" with a generated key, which it then "throws away", and can auto-click Windows' unsigned-driver warning. That is the line Exo must not cross.
- Exact registry values, setup arguments and package edits: **unknown** (not reversed, not on this PC, and the tool was not downloaded).

## Licensing and attribution

- Nexus is proprietary and obfuscated; NVCleanstall is proprietary freeware. No code from either was read or may be copied. This document records behaviour, file formats, NVIDIA's public endpoints and registry locations, which are facts, not their code. Do not copy Nexus's watchdog script text either; Exo's flow is its own.
- NVIDIA's driver package is under NVIDIA's license (`EULA.txt` in the package). Exo must download it from NVIDIA per user, change it only locally on that user's PC (removing folders), and never host or redistribute a stripped package. Show NVIDIA's EULA before using `-noeula`.
- NVIDIA's lookup endpoints are undocumented public web APIs used by nvidia.com's own driver page; they may change. Fail closed: no update offer if the response does not parse.
- The registry values listed are observations; most are undocumented by NVIDIA. Cite nothing from third-party tweak packs as fact.

## Not determined

- Nexus's exact tweak lists per button, its `DriverAndCore` component set, what `PatchSetupCfg` changes, how `cleanInstall` reaches setup, and whether it checks the signer: its strings and method bodies are encrypted and it was not run.
- Which of the unmarked class-key and `Parameters\FTS` values Nexus wrote (likely, not proven), and what the FTS switches do.
- NVIDIA `setup.exe` exit-code numbers (success other than 0, reboot required).
- Studio driver availability per GPU through `upCRD=1` (none returned for the RTX 3070 today), and the notebook/Studio file-name patterns (public, not checked).
- Everything NVCleanstall does beyond its changelog.
