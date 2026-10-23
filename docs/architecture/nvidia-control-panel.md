# NVIDIA Control Panel: how each setting is read and written

Research map for System > Display. It covers what every NVIDIA Control Panel (NVCP) page calls, so Exo can offer the same settings itself. Every live value below came from a getter or a registry read on the dev PC (RTX 3070, driver 617.42, two 1080p DisplayPort screens, `\\.\DISPLAY1` = displayId `0x80061080`, `\\.\DISPLAY2` = `0x80061081`). Exo wrote to the driver only where it says so: the scaling override and the last section.

## Sources and method

| Binary | Version | Role |
| --- | --- | --- |
| `nvcplui.exe` (Store package `NVIDIACorp.NVIDIAControlPanel_8.1.969.0`) | 8.1.969.0 | Shell: tree, Desktop menu, tray and context-menu registry |
| `nvdisps.dll` (+`nvdispsr.dll`) | driver store | Display pages (UI and English strings) |
| `nvvitvs.dll` (+`r`) | driver store | Video pages |
| `nvgames.dll` (+`r`) | driver store | 3D, PhysX, image-with-preview, G-SYNC pages |
| `nvdevtools.dll` | driver store | Developer > performance counters |
| `nvcpl.dll` | 8.17.16.1742 | "Shim": `DesktopColors.*` properties, `NvCplApi*` exports, setting-key → DRS-id table |
| `nvxdapix.dll` | driver store | "ApiX" engine (`Nvidia::UXDriver::ApiX::Features::*`). This is where every page's settings turn into NvAPI calls |

The driver store folder is `nv_dispi.inf_amd64_feedb8c0271ca811` (617.42). Its `nvapi64.dll` is 32.0.16.1742, the same as `C:\Windows\System32\nvapi64.dll`. The other two `nv_dispi` folders hold 32.0.16.1714. The PDB paths say `r615\r617_07`.

How the IDs were found, using dumpbin `/disasm` and a string dump, with no REA:

- `nvxdapix.dll` and `nvcpl.dll` statically link NVIDIA's `nvapi64.lib`. Each NvAPI function is a stub of the form `mov ecx,<id>; call [nvapi_QueryInterface]`, and the linker lays the stubs out **in ASCII name order**. nvxdapix has 302 stubs. 144 of them are named in NVIDIA's public `nvapi_interface.h`, `falahati/NvAPIWrapper` or `arcnmx/nvapi-rs`. The unknown stubs sit between named neighbors, which limits what their names can be.
- Every NvAPI call in nvxdapix goes through a logging wrapper. The wrapper loads the function's name as a string (for example `"NvAPI_DISP_SetScalingCapsOverride"`), and its caller passes the stub address. Pairing the two gives a name for each stub ID: **216 pairs, all consistent with the alphabetical order, with one known alias** (`GetUsages` = `GPU_GetDynamicPstatesInfo`). Every private ID below marked "confirmed" was found this way.
- Each NvAPI name string is grouped with the `*.cpp` name it sits next to in `.rdata` (`DesktopColor.cpp`, `UnderscanWrapper.cpp`, `VideoSettings.cpp`, ...). That tells which feature owns each call.

Calling convention for every function: `fn = nvapi_QueryInterface(id)`, cdecl, returns `NvAPI_Status` (0 = OK, -104 = NOT_SUPPORTED, -160 = setting not found). `version = sizeof(struct) | (ver << 16)`.

Key: **C** = confirmed (read in the binary, or a live value read back). **I** = inferred. **?** = unknown.

---

## 1. Display > Change resolution, step 3 "Apply the following settings"

| Setting | Choices | Mechanism | Admin / restart / scope | Confirmed? |
| --- | --- | --- | --- | --- |
| Use default color settings / Use NVIDIA color settings | radio | `NvAPI_Disp_ColorControl` 0x92F9D80D, `NV_COLOR_DATA_V5` (24 B, ver 0x50018). Default = `cmd SET(2)`, `colorSelectionPolicy = BEST_QUALITY(1)`, other fields AUTO (dynamicRange 0xFF). NVIDIA = `colorSelectionPolicy = USER(0)` plus the explicit format/bpc/range | no admin, no restart, per display (displayId) | **C**: `SetDesktopColorSettingsInternal` logs `"Setting colorSelectionPolicy = "`. The SET stores `cmd=2` at +6, `0xFF` at +9 and the policy bool at +0x10 (nvxdapix 0x6023D2E0..D3C5). Live: both screens `policy=0` (NVIDIA), GET_DEFAULT returns `policy=1` |
| Desktop color depth | Highest (32-bit), SDR (30-bit color), HDR (64-bit); Medium (16-bit) / Lowest (8-bit) exist only in the legacy mode list | `NV_COLOR_DATA_V5.depth` (`NV_DESKTOP_COLOR_DEPTH`: 1 = 8 bpc "32-bit", 2 = 10 bpc "30-bit", 5 = FP16 HDR "64-bit"; 0 = keep) through the same ColorControl SET. The 16/8-bit entries are GDI `dmBitsPerPel` (ChangeDisplaySettingsExW, imported by nvxdapix) | per display; changing depth is a mode set (screen blinks) | **C** for the field: log `"NV_DESKTOP_COLOR_DEPTH(desktop color depth): "` in DesktopColor.cpp, and labels `Highest (%1!d!-bit)`, `SDR (30-bit color)`, `HDR (64-bit)` in nvdispsr. **I** for the label → enum mapping. Live: `depth=0` (keep), default `depth=1`, source depth 32 |
| Output color format | RGB, YCbCr422, YCbCr444, YCbCr420 | ColorControl `colorFormat` (0 RGB, 1 422, 2 444, 3 420). The list comes from ColorControl `cmd IS_SUPPORTED_COLOR(3)` plus `NvAPI_GetHDMISupportInfo` 0x6AE16EC3 (HDMI) / `NvAPI_GetDisplayPortInfo` 0xC64FF367 (DP) | per display | **C**: `GetPossibleColorSettingsForHdmi/ForDP` log `info.isYCbCr4xxSupportedOnCurrentMode`. Exo already does this |
| Output color depth | 6/8/10/12/16 bpc | ColorControl `bpc` (1..5). Also `NvAPI_Disp_GetColorSupportedCombinations` **0x037FB940** (private, `DesktopColorSettings::FillColorCombinationPossibleValues`) | per display | **C** (ID from the call site). Live: DISPLAY1 8 bpc, DISPLAY2 12 bpc |
| Output dynamic range | Full, Limited | ColorControl `dynamicRange` (0 VESA = Full, 1 CEA = Limited) | per display | **C**. Live: Full on both |
| Content type reported to the display | Auto-select, Desktop programs, Full-screen videos, Full-screen gaming | `NvAPI_Disp_InfoFrameControl` 0x6067AF3F (public `NV_INFOFRAME_DATA`, AVI infoframe content type). Shim property `DesktopColors.Itc` (`GetItc/SetItc/GetItcValues`) | per display, HDMI only | **C** for the function (DesktopColor.cpp group and Shim names). **I** for the field |
| Resolution / refresh / custom resolution | list | CCD `SetDisplayConfig` / `ChangeDisplaySettingsExW` (imported). Custom: `NvAPI_DISP_TryCustomDisplay` 0x1F7DB630, `DISP_SaveCustomDisplay` 0x49882876, `DISP_DeleteCustomDisplay` 0x552E5B9B, `RevertCustomDisplayTrial` 0x854BA405 | per display | **C** (CustomModeWrapper.cpp / HideModesWrapper.cpp) |

ColorControl only accepts format, bpc and range while the policy is USER. The "default" radio is the policy flag and nothing else: the driver then picks its own best format.

## 2. Display > Adjust desktop size and position

| Setting | Choices | Mechanism | Admin / restart / scope | Confirmed? |
| --- | --- | --- | --- | --- |
| Scaling mode + Perform scaling on | Aspect ratio / Full-screen / No scaling / Integer scaling × GPU / Display | `NvAPI_DISP_SetDisplayConfig` 0x5D8CF8DE (public). Each target's `NV_DISPLAYCONFIG_PATH_ADVANCED_TARGET_INFO_V1` (128 B, ver 0x10080) has `scaling` at +8. GPU: 2 Full-screen, 3 No scaling, 5 Aspect, 8 Integer. Display: 1 Full-screen, 7 No scaling, 6 Aspect (the "Balanced" values). Integer is GPU only | per display; a mode set (blink), no admin | **C** for the API and field. Live via `NvAPI_DISP_GetDisplayConfig` 0x11ABCCF8: both targets `scaling=3` (No scaling on GPU). **I** for the label ↔ value table, taken from `nvapi.h` comments ("Force GPU" / "Balanced") |
| Override the scaling mode set by games and programs | checkbox | `NvAPI_DISP_SetScalingCapsOverride` **0xC89D5384**, read with `NvAPI_DISP_GetScalingCapsOverride` **0x0BB06808**. Caps: `NvAPI_DISP_GetScalingCaps` **0xF17985ED**. Re-applied at logon by the startup task `ApplyScalingOverride` | per display; no restart | **C** for the IDs (`SetDisplayConfigWrapper::EnableOverrideScaling`, ScalingSupportWrapper.cpp). Layout decoded by writing each shape on one screen and reading it back (`Helpers/NvidiaScaling.cs`): 32 bytes, version 32 \| 2 << 16, the switch at +16, the scaling it holds at +28; the SET takes a third argument 0, and off also sets the flags at +4, +8, +12 (1) and +20 (7) |
| Size tab: Enable desktop resizing / Resize | width/height underscan | `NvAPI_QueryUnderscanCap` 0x61D7B624, `EnumUnderscanConfig` 0x4144111A, `SetUnderscanConfig` 0x3EFADA1D, `DeleteUnderscanConfig` 0xF98854C8, plus private `NvAPI_DISP_EnableUnderscanConfig` **0xF8A3A0A1** / `NvAPI_DISP_DisableUnderscanConfig` **0x1123ACB1** | per display and per timing; mode set | **C** (UnderscanWrapper.cpp, call sites at 0x602291D8 / 0x602292AB). Structs not decoded |
| Position tab (analog/TV) | x/y offset | `NvAPI_GetDisplayPosition` 0x6BB1EE5D / `SetDisplayPosition` 0x57D9060F (AnalogOffset.cpp) | per display, analog only | **C** |
| Pan & scan (TV) | — | `NvAPI_DISP_SetPanScanInfo` **0xCBC7C82A**, `DISP_Get/SetViewPortInfo` **0x69CCCD08 / 0xF183FFEA** | TV only | **C** |

## 3. Display > Adjust desktop color settings

| Setting | Choices | Mechanism | Admin / restart / scope | Confirmed? |
| --- | --- | --- | --- | --- |
| Use default color settings / Use NVIDIA settings | radio | Per-monitor registry flag `NvCplUseColorCorrection` (REG_DWORD 0/1) in `HKCU\Software\NVIDIA Corporation\Global\NVTweak\Devices\<monitor key>\Color`. The same key holds `NvCplGammaSet` and `NvCplICCProfile`. Shim: `DesktopColors.Enabled`. "Default" resets the ramp (`ResetOSGamma`), DVC and hue | HKCU, no admin; per monitor | **C**: names in `GetNvColorsInternal` and `SessionEventFeatures::SetLoginColors`. Live: `Devices\509901423-0\Color\NvCplUseColorCorrection = 0`; `1089131374-0` and `3997754613-0` have empty `Color` keys. **?**: how the `<number>-<n>` monitor key is derived |
| Channel: All / Red / Green / Blue | combo | Selects which channel the three sliders edit. It is UI state only (Shim `DesktopColors.Parametric` Get/Set/Min/Max per channel) | — | **C** (Shim names) |
| Brightness, Contrast, Gamma | sliders (OEM limits from `HKLM\SOFTWARE\NVIDIA Corporation\Global\NVTweak\Defaults`: `BrightnessMin/Max/Default`, `ContrastMin/Max/Default`, `GammaMin/Max/Default`; key absent here) | **A gamma ramp.** `DesktopColorSettings::SetGammaRamp` → `SetGammaRampInternal` → GDI `CreateDCW("DISPLAY")` + `SetDeviceGammaRamp` (IAT call at 0x60240728). For targets flagged by property 0x360001 it uses `SetGammaRampEx` → private `NvAPI_DISP_SetTargetGammaCorrection` **0x7082A053** `(displayId, ramp*)` (call at 0x60240249). Logs `[ GammaRed=, GammaGreen=, GammaBlue=`. `nvcpl.dll` also exports `NvColorSetGammaRamp(Ex)`. Startup tasks `ResetOSGamma` and `LoginColors`/`IDMColors` put the ramp back at logon | no admin, per display; lost when a game or Windows resets the ramp, so NVIDIA re-applies it at logon | **C** for both paths. **?** for the ramp layout `SetTargetGammaCorrection` takes and for what property 0x360001 means |
| Digital vibrance | 0–100 % | `NvAPI_GetDVCInfoEx` 0x0E45002D / `NvAPI_SetDVCLevelEx` 0x4A82C2B1 `(NvDisplayHandle, outputId 0, struct{ver 0x10014, cur, min, max, def})` | per display | **C** (call sites in `SetDVCValueInternal`). Live: 50 (0–100, default 50) on both. Matches Exo |
| Hue | 0–359° | `NvAPI_GetHUEInfo` 0x95B64341 / `NvAPI_SetHUEAngle` 0xF5A0F22C | per display | **C**. Live: 0, default 0. Matches Exo |
| Image sharpening (when shown) | slider | `NvAPI_GetImageSharpeningInfo` 0x9FB063DF / `NvAPI_SetImageSharpeningLevel` 0x3FC9A59C (`DesktopColors.ImageSharpening`) | per display | **C** |
| Color accuracy mode (step 2 on some setups) | Accurate / Enhanced / Reference | Private `NvAPI_DISP_ColorAccuracyMode` **0xB5E8E855** `(displayId, struct{u32 ver = 0x1000C, u32 cmd (1 GET, 2 SET), u32 mode})`. Persisted, then re-applied by the startup task `RestoreColorAccuracyModePersistence` | per display | **C** for the ID and layout (SET site 0x60233036 writes `cmd=2`; the getter reads +8 back). Live GET: `mode=3` on both. **?** for which number is which label |

Exo already has vibrance, hue and a ramp (`NvidiaDisplay.cs`, `NvidiaColor.cs`). NVCP's brightness, contrast and gamma are the same thing as Exo's: a ramp, not a driver CSC.

## 4. Video > Adjust video color settings

All video settings go through **`NvAPI_GetVideoStateEx` 0x0B6EF8B9 / `NvAPI_SetVideoStateEx` 0x9321CA5B** (private), called as `fn(NvDisplayHandle, struct*)`. Both are confirmed from `VideoSettings::GetVideoSetting/SetVideoSetting` (call sites 0x60202425 / 0x6020298D).

- GET struct `_NVAPI_GET_VIDEO_STATE_COMPONENT_EX`: 0x80 B, ver **0x10080**. +4 componentId, +8 device index (forced 0 for components 29/30), +0xC flags (bit0 bIsSupported, bit1 bIsHigherPriorityThanApp), +0x18 min, +0x1C max, +0x20 steps, +0x24 defaultValue, +0x28 current, +0x30 defaultAlgo (u64), +0x50 lastAlgoSet (u64), +0x58 lastValueSet. **C** from the log lines; min/max/steps/current were read live.
- SET struct `_NVAPI_SET_VIDEO_STATE_COMPONENT_EX`: 0x40 B, ver **0x10040**. +4 componentId, +8 device index, +0xC bit0 bIsTopPriority, +0x10 enable, +0x14 setToValue, +0x18 setToAlgo (u64), +0x20 status out (non-zero → NOT_SUPPORTED). **C**.
- The driver stores each display's values in the GPU class key `HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0000` as `_User_SUB0_DFP<n>_VAL_<Name>` (value), `_XEN_<Name>` (enable, seen as 0x80000001) and `_XALG_<Name>` (8-byte algorithm), with `_User_Video_Active_Device`. No panel binary contains these names, so the driver writes them itself in response to SetVideoStateEx (**I**). Writing them directly would need admin; going through NvAPI does not.

Component IDs, read live on DISPLAY1/2 and matched against the registry names:

| Setting | Choices | Component | Live range (min..max, default) | Confirmed? |
| --- | --- | --- | --- | --- |
| How to make color adjustments: video player / NVIDIA settings | radio | SET `enable` + `bIsTopPriority` per component | — | **I** (field names C, semantics I) |
| Color tab: Brightness | slider | 0 | -101..101, 0 (registry `VAL_Brightness = 0`) | **C** |
| Contrast | slider | 1 | 40..160, 100 | **C** |
| Hue | slider | 2 | -180..180, 0 | **C** |
| Saturation | slider | 3 | 13..187, 100 | **C** |
| Gamma tab: R / G / B | sliders | 6 / 7 / 8 (5 = Y gamma, not supported here) | 30..300, 100 (= 1.00) | **C** |
| Advanced tab: Dynamic range | Full (0-255), Limited (16-235) | 10 (`Color_Range`), chosen by algorithm | supported, algo values at +0x34/+0x54 | **C** for the component, **I** for algo ↔ label |

## 5. Video > Adjust video image settings

Same functions and structs as section 4.

| Setting | Choices | Component | Live | Confirmed? |
| --- | --- | --- | --- | --- |
| Edge enhancement | player / NVIDIA + 0–100 % | 16 (`Edge_Enhance`) | 0..99, 100 steps, now 0 | **C** (registry name + range). **I** that 16 is edge and 15 is noise (old public enum order, both same range) |
| Noise reduction | player / NVIDIA + 0–100 % | 15 (`Noise_Reduce`) | 0..99 | as above |
| Deinterlacing: Use inverse telecine | checkbox | 14 (`Cadence`, nvxdapix calls it with `mov r9d,0Eh`; class `VideoSettingInverseTelecine`) | flags 3, enable 1, algo 4 (registry `XALG_Cadence = 04…`) | **C** |
| RTX video enhancement: Super resolution | on/off + Quality Auto/1–4 | 29 (0x1D, class `VideoSettingSuperResolution`; status from `NvAPI_Video_GetVideoStateInfo` **0xC9116276**, "IsSuperResolutionActive") | 0..5, 6 steps, now 0 | **C** for component and API. **I** for 0 = off, 1–4 quality, 5 = Auto |
| RTX video enhancement: High dynamic range (RTX Video HDR) | on/off | 30 (0x1E, `VideoSettingTrueHDR`, "IsTrueHDRActive") | 0..7, now 0 | **C** for component. **?** for the meaning of 1..7 |
| (not in NVCP) | — | 31–36: 0..200 def 100 (×2), 10..100 def 50, 400..2000 def 650, 0..65535, 0..2 | — | **I**: RTX Video HDR tuning as the NVIDIA app shows it (contrast, saturation, middle gray, peak nits) |

Components 29/30 ignore the device index, so they are global. The others are per display handle.

## 6. 3D > Adjust image settings with preview

| Setting | Choices | Mechanism | Confirmed? |
| --- | --- | --- | --- |
| Let the 3D application decide | radio | Puts the AA/AF settings back to application-controlled in the global DRS profile (`AA_MODE_SELECTOR` 0x107EFC5B, `ANISO_MODE_SELECTOR` 0x10D2BB16, ...) | **I** |
| Use the advanced 3D image settings | radio | No write; it links to Manage 3D settings and leaves the global profile as set there | **I** |
| Use my preference emphasizing: Performance … Balanced … Quality | slider | Global DRS profile `QUALITY_ENHANCEMENTS_ID` **0x00CE2691** "Texture filtering - Quality" (High performance 0x14, Performance 0x0A, Quality 0, High quality 0xFFFFFFF6). The page's help text says the panel "will make all of the appropriate texture filtering adjustments based on your preference" | **I** for the slider → setting. **C** for the setting: nvcpl's setting table maps internal key 0x202 to 0x00CE2691 (r14 = 0x277, `lea edx,[r14-75h]`), and the global profile reads 0x14 now (Exo's own value) |

The pages use NvCplApi keys (0x200 AA method, 0x201 aniso level, 0x202 texture quality, 0x209 AA selector, 0x20D aniso selector, ...). nvcpl turns them into DRS writes through its table at 0x640116A0–0x64015000. Exo already writes these IDs in `GpuProfile.cs`, so this page adds nothing.

## 7. Everything else in the tree

Page list from nvgames' telemetry names (`*_Page`). Mechanisms are from nvxdapix unless noted.

| Page / item | Mechanism | Admin / restart / scope | Confirmed? |
| --- | --- | --- | --- |
| Set up G-SYNC: Enable G-SYNC, full screen / windowed and full screen | Global DRS `VRR_MODE_ID` 0x1194F158 (0 off, 1 full screen, 2 windowed + full screen), `VRR_FEATURE_INDICATOR` 0x1094F157, `VRRREQUESTSTATE` 0x1094F1F7, `VSYNCVRRCONTROL` 0x10A879CE. Shim `ResolutionSettings::Get/SetVRRModeValue`, `ApplicationProfiles::SetToggleGloablVRR` | DRS, no admin, global | **C**. Live: all 0. Exo writes these already |
| Set up G-SYNC: "Enable settings for the selected display model" | `NvAPI_DISP_SetAdaptiveSyncData` 0x3EEBBA1D / `GetAdaptiveSyncData` 0xB73D1EE9 (public, `bDisableAdaptiveSync`); info `NvAPI_DISP_GetGSyncInfo` **0x212F551F** | per display | **C** (DisplaySettings.cpp). Live: adaptive sync not disabled on either screen |
| G-SYNC / VRR indicator overlay | DRS 0x1095F16F (overlay), 0x1094F16F (PhysX indicator) | global | **C** |
| Set up digital audio | `NvAPI_GPU_GetAudioStreams` **0xE4E75871** / `SetAudioStreams` **0x7CA86A93**, `GetMaxAudioStreamCount` **0x05EE23B6**, `GetInfoFrameStatePvt` 0x7FC17574 / `SetInfoFrameState` 0x67EFD887 | per connector | **C** for the IDs, structs **?** |
| Set PhysX configuration (processor) | `NvAPI_CUDA_PhysxSetState` **0xD875B6C5**, `CUDA_EnumComputeCapableByTopology` **0x36E39E6B**. Auto-select = REG_DWORD `NvCplPhysxAuto` in `HKLM\SYSTEM\CurrentControlSet\Services\nvlddmkm\Parameters\Global\NVTweak` (`PhysXSettings::WritePhysXAutoSelectRegistryValue`) | HKLM → admin | **C**. Live: `NvCplPhysxAuto = 0` |
| Configure Surround / Mosaic | public `NvAPI_Mosaic_*` (SetGridTopology 0x3F113C77, ValidateDisplayGridsWithSLI 0x1ECFD263, SetDisplayGridsWithSLI **0x2053F234**, ...) | mode set | **C** |
| Rotate display | `NV_DISPLAYCONFIG_PATH_ADVANCED_TARGET_INFO.rotation` via DISP_SetDisplayConfig, or CCD `SetDisplayConfig` | per display | **C**. Live: rotation 0 |
| Set up multiple displays | CCD `SetDisplayConfig` + `NvAPI_DISP_SetDisplayConfig` | — | **C** |
| View HDCP status | `NvAPI_GetVideoLinkParams` **0x76E48826** (`bHDCPOn`, `bRepeaterPresent`) | read only | **C** |
| Manage 3D settings > DSR factors / smoothness | not DRS: `NvAPI_DISP_GetHyperSamplingSettings(Ex)` **0x461F84C1 / 0xBB8052C3**, `Set...` **0x655C5FF5 / 0x0B396D98** ("Smooth scaling", "Upscaling") | global | **C** for the IDs |
| Manage 3D settings > Image Scaling (NIS) | `NvAPI_Set_NIS2_Sharpness` **0x28EE366C**; Shim `ShimSystem::Set/GetIsNIS2Enabled`, `SetIsUpscalingFactorsEnabled` | global | **C** for the ID |
| Developer > Manage GPU performance counters | REG_DWORD `RmProfilingAdminOnly` (1 = admins only, the default; 0 = all users). Global key `HKLM\SYSTEM\CurrentControlSet\Services\nvlddmkm\Global\NVTweak`, copied to each GPU (`SetPerfCountRestrictionAllGpu`, `EnsurePerfCountSync`, `NvAPI_GPU_GetPerGpuRegistryPath` **0x77C71D7C**), then **`NvAPI_RestartDisplayDriver` 0xB4B26B65** | **admin + display driver restart** | **C** for the value, key and restart (call at 0x6027E094). **I** for the 0/1 meaning. Live: value absent (driver default) |
| Desktop > Enable developer settings | `NvDevToolsVisible` (`SystemSettings::Set/GetIsDevToolsVisible`) | — | **C** name, key location **?** |
| Desktop > Add desktop context menu | REG_DWORD `ContextUIPolicy`. Read from `HKCU\SOFTWARE\NVIDIA Corporation\Global\NvCplApi\Policies`, falling back to `HKLM\SYSTEM\CurrentControlSet\Services\nvlddmkm\Global\NvCplApi\Policies`; written to HKCU. Shown when the value is 2 or missing, hidden for anything else (nvcplui 0x140071042–0x140071054) | HKCU, no admin | **C**. Live: `HKLM\...\nvlddmkm\Parameters\Global\NvCplApi\Policies\ContextUIPolicy = 2` |
| Desktop > Show notification tray icon | REG_DWORD `StartOnLogin` in `SOFTWARE\NVIDIA Corporation\NvTray`. The menu item appears only when `ShowInSedona` is set under `nvlddmkm\(Parameters\)NvTray` or `SOFTWARE\NVIDIA Corporation\NvTray` | HKCU (I) | **C** names. Live: no NvTray key, so the item is hidden on this PC |
| Desktop > Display GPU activity icon (Optimus) / Run with GPU context menu | `ShowTrayIcon` and `ShowContextMenu` in `HKCU\SOFTWARE\NVIDIA Corporation\Global\CoProcManager` | HKCU | **C**. Live: `ShowContextMenu = 1` |
| Manage display mode (Advanced Optimus / MUX laptops) | `NvAPI_DISP_SetDisplayMux` **0x73BE7F64**, `DISP_QueryMuxInfo` **0x2CCC37A0**, `DISP_MuxTransition` **0x469921DF**, `DISP_GetDisplayMuxCaps` **0x6050BB05** | laptop only; reboot or mux switch | **C** IDs |
| Change ECC state, Manage license, SDI output, Synchronize displays (Quadro Sync), Mosaic, 3D Vision | `GPU_SetECCConfiguration`, `GPU_EnableLicensedFeature` **0xD2294406**, `NvAPI_VIO_*`, `NvAPI_GSync_*`, `NvAPI_3DVP_*` | workstation | **C** IDs |

## Private NvAPI IDs decoded here (driver 617.42)

Confirmed by call site, except the `I` rows, which come from alphabetical order alone:

| ID | Function | ID | Function |
| --- | --- | --- | --- |
| 0xB5E8E855 | DISP_ColorAccuracyMode | 0x037FB940 | Disp_GetColorSupportedCombinations |
| 0x7082A053 | DISP_SetTargetGammaCorrection | 0xF17985ED | DISP_GetScalingCaps (I) |
| 0x0BB06808 | DISP_GetScalingCapsOverride | 0xC89D5384 | DISP_SetScalingCapsOverride |
| 0xF8A3A0A1 | DISP_EnableUnderscanConfig | 0x1123ACB1 | DISP_DisableUnderscanConfig |
| 0xCBC7C82A | DISP_SetPanScanInfo | 0x69CCCD08 / 0xF183FFEA | DISP_Get/SetViewPortInfo |
| 0x0B6EF8B9 | GetVideoStateEx | 0x9321CA5B | SetVideoStateEx |
| 0xC9116276 | Video_GetVideoStateInfo | 0x1003328A | GetVideoDeviceInfo |
| 0x461F84C1 / 0xBB8052C3 | DISP_GetHyperSamplingSettings(Ex) | 0x655C5FF5 / 0x0B396D98 | DISP_SetHyperSamplingSettings(Ex) (I) |
| 0x28EE366C | Set_NIS2_Sharpness | 0x7F5CF74D | DISP_SetFeatureConfig |
| 0x212F551F | DISP_GetGSyncInfo | 0xAB3B7419 | DISP_GetTimingInfo |
| 0xD875B6C5 | CUDA_PhysxSetState | 0xE4E75871 / 0x7CA86A93 | GPU_Get/SetAudioStreams |
| 0x76E48826 | GetVideoLinkParams | 0xEA99498D / 0x8A2CF5F5 | DRS_GetSettingEx / DRS_SetSettingEx |

The full list (216 pairs) can be regenerated in a few minutes: dump nvxdapix with `dumpbin /disasm:nobytes`, find the `mov ecx,ID / call rax` stubs, and pair them with the `lea rax,["NvAPI_X"]` wrapper callers.

## What Exo already does vs what Exo could add

| Exo already does | Exo could add |
| --- | --- |
| Output color format / bpc / range with policy USER, or "Use default color settings" (policy BEST_QUALITY) (`NvidiaDisplay.cs`) | Video color/image (components 0–16): niche, see below |
| Digital vibrance, hue | |
| Brightness / contrast / gamma ramp, re-applied on start, none while HDR is on (`NvidiaColor.cs`, `WindowsHdr.cs`) | |
| Scaling: mode, GPU/Display, integer, and the "override games" switch (`NvidiaScaling.cs`) | |
| Refresh rate (`DisplayOutput.cs`) | |
| HDR on/off (`WindowsHdr.cs`) | |
| RTX Video Super Resolution / RTX Video HDR (components 29/30, `NvidiaVideo.cs`) | |
| G-SYNC global mode, indicators, texture quality, AA/AF defaults (`GpuProfile.cs`) | |

Desktop color depth 30-bit, content type for TVs and per-display G-SYNC were tried and left out (last section).

## Recommendation for System > Display

Goal: the best image and feel by default, and only controls the screen or card can actually use.

1. **Output (built):** format/bpc/range, plus "Use default color settings" (policy BEST_QUALITY) as a choice. Best for this screen writes USER, not the driver's pick: NVIDIA's default picks Limited on HDMI CEA timings (`display-best-settings.md` 2c/2d). Formats and depths come from `IS_SUPPORTED_COLOR`, less what the screen's EDID rules out.
2. **Desktop color depth:** tried, not supported on GeForce (last section). Leave the 16/8-bit GDI modes out.
3. **Scaling (built):** one control with Aspect ratio / Full-screen / No scaling / Integer, Perform scaling on GPU or Display, and the override switch. Best for this screen writes No scaling on the GPU with the override on. Not done: Integer only on Turing+, and "Display" only when the target reports monitor scaling.
4. **G-SYNC per screen:** tried, unverifiable here (last section); the global mode stays in the profile.
5. **Content type:** tried, did not work (last section).
6. **RTX Video (built):** Super Resolution (with quality) and RTX Video HDR, shown only where the card has them.
7. **Color (built):** vibrance / hue / ramp. NVCP itself re-applies the ramp at logon, so Exo's re-apply on start is the right model.

## What to leave out

- Video color and video image sliders (components 0–16: brightness, contrast, saturation, gamma, edge enhancement, noise reduction). Modern players and browsers ignore or override them, and they only change DXVA overlay video.
- Inverse telecine, the analog/TV position, pan & scan, overscan and TV border: legacy TV-out.
- Size / underscan resizing: only for TVs with forced overscan. Point users to the TV's "Just scan" setting instead.
- Color accuracy mode: its labels aren't mapped to numbers yet, and it matters only for calibration workflows.
- Adjust image settings with preview: already covered by Exo's profile, and the slider would fight `GpuProfile`.
- Performance counters: needs admin and restarts the display driver, so it's for developers only.
- Digital audio, PhysX processor, Surround/Mosaic, HDCP, ECC, SDI, Sync, licensing, MUX: either hardware-specific, or correct by default.
- Tray icon, context menu, developer menu: NVCP chrome. Exo replaces the panel rather than tuning it.

## Open questions

- Struct layouts for `DISP_SetTargetGammaCorrection` (ramp format), underscan configs, audio streams, and the hyper-sampling (DSR) settings.
- `NV_DISP_COLOR_ACCURACY_MODE` value ↔ label (live value is 3).
- The "player vs NVIDIA" bit semantics for components 0-16 (`enable` vs `bIsTopPriority` vs registry `XEN` 0x80000001). Settled for 29/30 (2026-10-17, set in the panel and read back): Super resolution 0 off, 1-4 quality, 5 Auto; RTX Video HDR 0 off, 5 on; the panel writes off as value 0 with enable set (`Helpers/NvidiaVideo.cs`).
- How the `NVTweak\Devices\<n>-<m>` monitor key is computed.
- What the image-preview slider writes besides texture quality, and the exact value for each slider stop.

## Tried and left out (October 2026, RTX 3070, driver 617.42, two DisplayPort screens)

- **30-bit desktop** (`NvAPI_Disp_ColorControl` SET with `depth` = `NV_DESKTOP_COLOR_DEPTH_10BPC`, other fields as read): `NVAPI_NOT_SUPPORTED` (-104) on a 12 bpc screen. GeForce drivers keep the desktop at 8 bpc; 10 bpc reaches the screen only from full-screen games and HDR.
- **Content type "Game" for TVs** (`NvAPI_Disp_InfoFrameControl` 0x6067AF3F, AVI type 2, `NV_INFOFRAME_DATA` 32 bytes, `NV_INFOFRAME_VIDEO` as six NvU32 units: `itContent` bits 29-30 of unit 0, `contentTypes` bits 0-2 of unit 1): `SET_OVERRIDE` is stored and read back as written, but the live infoframe (`GET`) kept content type Graphics and its YCC quantization moved to a reserved value. Override back to all-auto restored it. Not shipped: it does not do what it says, and a wrong infoframe can upset a TV.
- **G-SYNC per screen** (`NvAPI_DISP_SetAdaptiveSyncData` 0x3EEBBA1D, 40 bytes, v1 or v2, `bDisableAdaptiveSync` bit 0 at offset 8): `NVAPI_INVALID_ARGUMENT` (-5) on both screens, which have no variable refresh, so it cannot be checked here. G-SYNC stays a driver profile setting (full screen only), which every screen follows.
