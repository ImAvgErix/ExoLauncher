using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;
using Microsoft.Win32;

namespace ExoLauncher.Services;

/// <summary>
/// The game runtimes Exo checks and installs: every Visual C++ the VisualCppRedist AIO repack carries (its list and
/// versions are the reference for "latest"), DirectX's legacy runtime and GameInput, .NET, XNA, OpenAL and PhysX, each
/// from its maker's own server, pinned to one file by its SHA-256 and checked against the maker's signature before it
/// runs. What a game can't install (Direct3D 12 is Windows itself; DirectStorage and the Agility SDK ship inside the games
/// that use them; Vulkan comes with the graphics driver) is listed as what this PC has. Detection reads uninstall
/// entries, a few registry values and file versions only: a snapshot in well under 300 ms, kept briefly.
/// </summary>
public sealed partial class DependencyService
{
    internal enum Group { VisualCpp, DirectX, DotNet, Other }

    internal enum Kind
    {
        /// <summary>Its own installer, run with Args.</summary>
        Exe,
        /// <summary>A Windows Installer package: msiexec /i.</summary>
        Msi,
        /// <summary>A zip whose Entry is the installer (an .exe or .msi).</summary>
        Zip,
        /// <summary>DirectX's June 2010 package: unpacked, then DXSETUP.</summary>
        DirectX,
        /// <summary>A Windows feature turned on through DISM (.NET Framework 3.5).</summary>
        Feature,
        /// <summary>What this PC has; nothing to install.</summary>
        Info,
    }

    internal enum State { Current, Outdated, Missing, Info }

    /// <summary>What detection found: the version shown, and the one compared (null: present, nothing to compare).</summary>
    internal sealed record Found(string Shown, Version? Comparable);

    /// <param name="Latest">The newest version, as shown.</param>
    /// <param name="LatestComparable">The same in the scale <see cref="Found.Comparable"/> uses (an MSI's own, for 2005).</param>
    /// <param name="Publishers">Who must have signed it ("Microsoft" for Microsoft itself, see <see cref="AuthenticodeVerifier.IsMicrosoft"/>).</param>
    /// <param name="Order">Where it runs in one install: frameworks first, oldest Visual C++ first, then what builds on them.</param>
    internal sealed record Runtime(
        string Id, string Name, Group Group, Kind Kind, string Latest, Version? LatestComparable,
        string? Url, string? Sha256, long Size, string Args, string? RepairArgs, string[] Publishers, int Order,
        Func<Snapshot, Found?> Find, Func<Snapshot, bool>? Applies = null, string? Entry = null, DateTime? EndOfSupport = null)
    {
        internal bool Installable => Kind != Kind.Info;
        internal bool IsVisualCpp => Group == Group.VisualCpp;
    }

    internal sealed record UninstallEntry(string Key, string Name, string? Version);

    /// <summary>
    /// Everything detection reads, gathered once (<see cref="ReadSnapshot"/>) so the rules (<see cref="Evaluate"/>) are pure.
    /// Files: "sys:" (System32) or "wow:" (SysWOW64) and the file name, to its version ("" when it has none).
    /// Values: "vc14:x64" and friends (the v14 runtime's own key), "ndp4" (.NET Framework's Release), "ndp35" (Install).
    /// </summary>
    internal sealed record Snapshot(
        IReadOnlyList<UninstallEntry> Uninstall,
        IReadOnlyDictionary<string, string> Files,
        IReadOnlyDictionary<string, string> Values,
        IReadOnlyList<string> DesktopX64,
        IReadOnlyList<string> DesktopX86,
        bool Nvidia,
        bool Arm64,
        string? Graphics);

    internal sealed record RuntimeStatus(Runtime Runtime, State State, Found? Found, string Latest, bool EndOfSupport, string? Detail);

    private const string Microsoft = "Microsoft";
    private static readonly string[] MicrosoftOnly = [Microsoft];

    // DirectX's June 2010 runtime: one file of each of its parts, in both System32 and SysWOW64 (every one ships in both).
    internal static readonly string[] DirectXFiles =
    [
        .. Enumerable.Range(24, 20).Select(n => $"d3dx9_{n}.dll"),
        "d3dx10.dll", .. Enumerable.Range(33, 11).Select(n => $"d3dx10_{n}.dll"),
        "d3dx11_42.dll", "d3dx11_43.dll", "D3DCompiler_42.dll", "D3DCompiler_43.dll", "d3dcsx_42.dll", "d3dcsx_43.dll",
        "xinput1_1.dll", "xinput1_2.dll", "xinput1_3.dll",
        "XAudio2_7.dll", "X3DAudio1_7.dll", "XAPOFX1_5.dll", "xactengine3_7.dll", "xactengine2_10.dll",
    ];

    // Visual C++ 2005's MSI versions, shown as its files' versions (SP1, ATL update, MFC update, the June 2011 update).
    private static readonly Dictionary<string, string> Vc2005Shown = new(StringComparer.Ordinal)
    {
        ["8.0.56336"] = "8.0.50727.762", ["8.0.59192"] = "8.0.50727.4053", ["8.0.59193"] = "8.0.50727.4053",
        ["8.0.61000"] = "8.0.50727.6195", ["8.0.61001"] = "8.0.50727.6195", ["8.0.61186"] = "8.0.50727.6229", ["8.0.61187"] = "8.0.50727.6229",
    };

    /// <summary>.NET's Desktop Runtime channels Exo offers: the latest patch comes from Microsoft's release list (DependencyService.Dotnet.cs); these are its fallback.</summary>
    internal static readonly (int Major, string Latest, DateTime EndOfSupport)[] DotnetChannels =
    [
        (10, "10.0.14", new DateTime(2028, 11, 14)),
        (9, "9.0.22", new DateTime(2026, 11, 10)),
        (8, "8.0.33", new DateTime(2026, 11, 10)),
        (6, "6.0.36", new DateTime(2024, 11, 12)),
    ];

    internal static readonly IReadOnlyList<Runtime> Catalog = BuildCatalog();

    private static IReadOnlyList<Runtime> BuildCatalog()
    {
        var list = new List<Runtime>
        {
            // Visual C++, oldest first. URLs and SHA-256s: Microsoft's own downloads, as Microsoft Learn's "Latest supported
            // Visual C++ Redistributable downloads" lists them (the same files winget and Winetricks pin).
            new("vc2005-x86", "Visual C++ 2005 (x86)", Group.VisualCpp, Kind.Exe, "8.0.50727.6195", new Version(8, 0, 61001),
                "https://download.microsoft.com/download/8/B/4/8B42259F-5D70-43F4-AC2E-4B208FD8D66A/vcredist_x86.EXE",
                "8648C5FC29C44B9112FE52F9A33F80E7FC42D10F3B5B42B2121542A13E44ADFD", 2710520,
                "/q:a /c:\"msiexec /i vcredist.msi /qn /norestart\"", "/q:a /c:\"msiexec /i vcredist.msi /qn /norestart REINSTALL=ALL REINSTALLMODE=vomus\"",
                MicrosoftOnly, 30, s => Vc2005(s, x64: false)),
            new("vc2005-x64", "Visual C++ 2005 (x64)", Group.VisualCpp, Kind.Exe, "8.0.50727.6195", new Version(8, 0, 61000),
                "https://download.microsoft.com/download/8/B/4/8B42259F-5D70-43F4-AC2E-4B208FD8D66A/vcredist_x64.EXE",
                "4487570BD86E2E1AAC29DB2A1D0A91EB63361FCAAC570808EB327CD4E0E2240D", 3179000,
                "/q:a /c:\"msiexec /i vcredist.msi /qn /norestart\"", "/q:a /c:\"msiexec /i vcredist.msi /qn /norestart REINSTALL=ALL REINSTALLMODE=vomus\"",
                MicrosoftOnly, 31, s => Vc2005(s, x64: true)),
            // 2008's wrapper takes Windows Installer's UI switches only, so its repair is the same run (Windows Installer keeps a newer one).
            new("vc2008-x86", "Visual C++ 2008 (x86)", Group.VisualCpp, Kind.Exe, "9.0.30729.6161", new Version(9, 0, 30729, 6161),
                "https://download.microsoft.com/download/5/D/8/5D8C65CB-C849-4025-8E95-C3966CAFD8AE/vcredist_x86.exe",
                "8742BCBF24EF328A72D2A27B693CC7071E38D3BB4B9B44DEC42AA3D2C8D61D92", 4483040, "/qn /norestart", null,
                MicrosoftOnly, 32, s => Uninstall(s, @"^Microsoft Visual C\+\+ 2008 Redistributable( - | \()x86\b")),
            new("vc2008-x64", "Visual C++ 2008 (x64)", Group.VisualCpp, Kind.Exe, "9.0.30729.6161", new Version(9, 0, 30729, 6161),
                "https://download.microsoft.com/download/5/D/8/5D8C65CB-C849-4025-8E95-C3966CAFD8AE/vcredist_x64.exe",
                "C5E273A4A16AB4D5471E91C7477719A2F45DDADB76C7F98A38FA5074A6838654", 5211080, "/qn /norestart", null,
                MicrosoftOnly, 33, s => Uninstall(s, @"^Microsoft Visual C\+\+ 2008 Redistributable( - | \()x64\b")),
            new("vc2010-x86", "Visual C++ 2010 (x86)", Group.VisualCpp, Kind.Exe, "10.0.40219.325", new Version(10, 0, 40219, 325),
                "https://download.microsoft.com/download/1/6/5/165255E7-1014-4D0A-B094-B6A430A6BFFC/vcredist_x86.exe",
                "99DCE3C841CC6028560830F7866C9CE2928C98CF3256892EF8E6CF755147B0D8", 8993744, "/q /norestart", "/repair /q /norestart",
                MicrosoftOnly, 34, s => FileVersion(s, "wow:msvcr100.dll")),
            new("vc2010-x64", "Visual C++ 2010 (x64)", Group.VisualCpp, Kind.Exe, "10.0.40219.325", new Version(10, 0, 40219, 325),
                "https://download.microsoft.com/download/1/6/5/165255E7-1014-4D0A-B094-B6A430A6BFFC/vcredist_x64.exe",
                "F3B7A76D84D23F91957AA18456A14B4E90609E4CE8194C5653384ED38DADA6F3", 10277328, "/q /norestart", "/repair /q /norestart",
                MicrosoftOnly, 35, s => FileVersion(s, "sys:msvcr100.dll")),
            new("vc2012-x86", "Visual C++ 2012 (x86)", Group.VisualCpp, Kind.Exe, "11.0.61030", new Version(11, 0, 61030),
                "https://download.microsoft.com/download/1/6/B/16B06F60-3B20-4FF2-B699-5E9B7962F9AE/VSU_4/vcredist_x86.exe",
                "B924AD8062EAF4E70437C8BE50FA612162795FF0839479546CE907FFA8D6E386", 6554576, "/install /quiet /norestart", "/repair /quiet /norestart",
                MicrosoftOnly, 36, s => Uninstall(s, @"^Microsoft Visual C\+\+ 2012 x86 Minimum Runtime")),
            new("vc2012-x64", "Visual C++ 2012 (x64)", Group.VisualCpp, Kind.Exe, "11.0.61030", new Version(11, 0, 61030),
                "https://download.microsoft.com/download/1/6/B/16B06F60-3B20-4FF2-B699-5E9B7962F9AE/VSU_4/vcredist_x64.exe",
                "681BE3E5BA9FD3DA02C09D7E565ADFA078640ED66A0D58583EFAD2C1E3CC4064", 7186992, "/install /quiet /norestart", "/repair /quiet /norestart",
                MicrosoftOnly, 37, s => Uninstall(s, @"^Microsoft Visual C\+\+ 2012 x64 Minimum Runtime")),
            new("vc2013-x86", "Visual C++ 2013 (x86)", Group.VisualCpp, Kind.Exe, "12.0.40664", new Version(12, 0, 40664),
                "https://download.visualstudio.microsoft.com/download/pr/10912113/5da66ddebb0ad32ebd4b922fd82e8e25/vcredist_x86.exe",
                "53B605D1100AB0A88B867447BBF9274B5938125024BA01F5105A9E178A3DCDBD", 6510136, "/install /quiet /norestart", "/repair /quiet /norestart",
                MicrosoftOnly, 38, s => Uninstall(s, @"^Microsoft Visual C\+\+ 2013 x86 Minimum Runtime")),
            new("vc2013-x64", "Visual C++ 2013 (x64)", Group.VisualCpp, Kind.Exe, "12.0.40664", new Version(12, 0, 40664),
                "https://download.visualstudio.microsoft.com/download/pr/10912041/cee5d6bca2ddbcd039da727bf4acb48a/vcredist_x64.exe",
                "A4BBA7701E355AE29C403431F871A537897C363E215CAFE706615E270984F17C", 7200744, "/install /quiet /norestart", "/repair /quiet /norestart",
                MicrosoftOnly, 39, s => Uninstall(s, @"^Microsoft Visual C\+\+ 2013 x64 Minimum Runtime")),
            // v14 covers every program built with Visual Studio 2015 to 2026: the build aka.ms/vc14 (Microsoft's "latest supported") serves.
            new("vc14-x86", "Visual C++ 2015–2026 (x86)", Group.VisualCpp, Kind.Exe, "14.51.36247.0", new Version(14, 51, 36247, 0),
                "https://download.visualstudio.microsoft.com/download/pr/57eef8ae-a341-46c3-b0bc-c041027b54cd/F0BAB33A302B3CDB2E11113760D016F54FD3D2632C65BA7834FAC4F0ABD7F1A3/VC_redist.x86.exe",
                "F0BAB33A302B3CDB2E11113760D016F54FD3D2632C65BA7834FAC4F0ABD7F1A3", 6941536, "/install /quiet /norestart", "/repair /quiet /norestart",
                MicrosoftOnly, 40, s => Vc14(s, "x86", "wow:vcruntime140.dll")),
            new("vc14-x64", "Visual C++ 2015–2026 (x64)", Group.VisualCpp, Kind.Exe, "14.51.36247.0", new Version(14, 51, 36247, 0),
                "https://download.visualstudio.microsoft.com/download/pr/ebdab8e5-1d7b-4d9f-a11b-cbb1720c3b12/843068991DAAA1F73AD9F6239BCE4D0F6A07A51F18C37EA2A867E9BECA71295C/VC_redist.x64.exe",
                "843068991DAAA1F73AD9F6239BCE4D0F6A07A51F18C37EA2A867E9BECA71295C", 18731856, "/install /quiet /norestart", "/repair /quiet /norestart",
                MicrosoftOnly, 41, s => Vc14(s, "x64", s.Arm64 ? null : "sys:vcruntime140.dll")),
            new("vc14-arm64", "Visual C++ 2015–2026 (Arm64)", Group.VisualCpp, Kind.Exe, "14.51.36247.0", new Version(14, 51, 36247, 0),
                "https://download.visualstudio.microsoft.com/download/pr/57eef8ae-a341-46c3-b0bc-c041027b54cd/B70EF586669A620A0A30A1156969C05C6A3831DC8F8BC992DA75779D2A92F944/VC_redist.arm64.exe",
                "B70EF586669A620A0A30A1156969C05C6A3831DC8F8BC992DA75779D2A92F944", 11870816, "/install /quiet /norestart", "/repair /quiet /norestart",
                MicrosoftOnly, 42, s => Vc14(s, "arm64", null), s => s.Arm64),

            // DirectX. The June 2010 package is what every DirectX 9, 10 and 11 game's own installer runs (D3DX, XInput 1.3,
            // XAudio 2.7, XACT, D3DCompiler_43); Direct3D 12 itself is part of Windows.
            new("directx", "DirectX (June 2010)", Group.DirectX, Kind.DirectX, "June 2010", JuneTwentyTen,
                "https://download.microsoft.com/download/8/4/A/84A35BF1-DAFE-4AE8-82AF-AD2AE20B6B14/directx_Jun2010_redist.exe",
                "053F76DCBB28802E23341B6A787E3B0791C0FA5C8D4D011B1044172DBF89C73B", 100275120, "/silent", null,
                MicrosoftOnly, 60, DirectXLegacy),
            new("gameinput", "GameInput", Group.DirectX, Kind.Zip, "4.0.302", new Version(4, 0, 302),
                "https://globalcdn.nuget.org/packages/microsoft.gameinput.4.0.302.nupkg",
                "A4F000C3A41385819B51E5CDD7DAE66D7DDE85C6DE0BFD823CFE617D4BAAAC33", 4827523, "/qn /norestart", null,
                MicrosoftOnly, 120, s => Uninstall(s, @"^Microsoft GameInput$"), Entry: "redist/GameInputRedist.msi"),
            new("d3d12", "DirectX 12", Group.DirectX, Kind.Info, "Built into Windows", null, null, null, 0, "", null, [], 0, _ => null),
            new("directstorage", "DirectStorage", Group.DirectX, Kind.Info, "Inside the games that use it", null, null, null, 0, "", null, [], 0, _ => null),
            new("agility", "Direct3D 12 Agility SDK", Group.DirectX, Kind.Info, "Inside the games that use it", null, null, null, 0, "", null, [], 0, _ => null),

            // .NET Framework: 4.8.1 is part of Windows 11 22H2 and later; 3.5 is a Windows feature Windows Update fills in.
            new("netfx481", ".NET Framework 4.8.1", Group.DotNet, Kind.Exe, "4.8.1", new Version(4, 8, 1),
                "https://download.microsoft.com/download/4/b/2/cd00d4ed-ebdd-49ee-8a33-eabc3d1030e3/NDP481-x86-x64-AllOS-ENU.exe",
                "C0CA2E0C9CD18A24A0A77369A13FAE2C2C4E8BC83355DD24E5DDC00F9D791FE3", 77688504, "/q /norestart", null,
                MicrosoftOnly, 20, NetFramework4),
            new("netfx35", ".NET Framework 3.5", Group.DotNet, Kind.Feature, "3.5 SP1", null, null, null, 0,
                "/Online /Enable-Feature /FeatureName:NetFx3 /All /NoRestart /Quiet", null, [], 10,
                s => s.Values.GetValueOrDefault("ndp35") == "1" ? new Found("3.5 SP1", null) : null),
        };

        // .NET's Desktop Runtime (it carries the .NET Runtime too), x64 and x86: URLs and SHA-512s from Microsoft's release list.
        var order = 80;
        foreach (var (major, latest, end) in DotnetChannels)
            foreach (var arch in new[] { "x64", "x86" })
            {
                var x64 = arch == "x64";
                list.Add(new($"dotnet{major}-{arch}", $".NET Desktop Runtime {major} ({arch})", Group.DotNet, Kind.Exe, latest, Version.Parse(latest),
                    null, null, 0, "/install /quiet /norestart", "/repair /quiet /norestart", MicrosoftOnly, order++,
                    s => Desktop(x64 ? s.DesktopX64 : s.DesktopX86, major), EndOfSupport: end));
            }

        list.AddRange(
        [
            new("xna40", "XNA Framework 4.0", Group.Other, Kind.Msi, "4.0.30901.0", new Version(4, 0, 30901, 0),
                "https://download.microsoft.com/download/5/3/A/53A804C8-EC78-43CD-A0F0-2FB4D45603D3/xnafx40_redist.msi",
                "2B03C130C4BB5F106B9619BEA8150201F7E982709CFE9A0CC8BDE75FF0A83B27", 7059456, "/qn /norestart", null,
                MicrosoftOnly, 90, s => Uninstall(s, @"^Microsoft XNA Framework Redistributable 4\.0")),
            // Creative's OpenAL installer, from openal.org (Creative's site for it): signed by Creative Labs.
            new("openal", "OpenAL", Group.Other, Kind.Zip, "2.0.7.0", null,
                "https://openal.org/downloads/oalinst.zip",
                "D165BCB7628FD950D14847585468CC11943B2A1DA92A59A839D397C68F9D4B06", 590434, "/s", null,
                ["Creative Labs, Inc.", "Creative Labs Inc", "Creative Labs Inc.", "Creative Technology Ltd", "Creative Technology Ltd."], 100,
                s => s.Uninstall.Any(e => e.Key.Equals("OpenAL", StringComparison.OrdinalIgnoreCase) || e.Name.Equals("OpenAL", StringComparison.OrdinalIgnoreCase))
                     || s.Files.ContainsKey("wow:OpenAL32.dll") ? new Found("Installed", null) : null, Entry: "oalinst.exe"),
            // NVIDIA's PhysX for games that run it on the processor or a GeForce: the current System Software, and the
            // Legacy package for games built on PhysX 2.7 and older. Listed on PCs with an NVIDIA card.
            new("physx", "PhysX System Software", Group.Other, Kind.Exe, "9.26.0703", new Version(9, 26, 703),
                "https://us.download.nvidia.com/Windows/9.26.0703/PhysX_9.26.0703_SystemSoftware.exe",
                "84B606D047B350B050B55E10993039C9FB0D3F8A2D01D3519872E80C1DA109F8", 34979112, "/s", null,
                ["NVIDIA Corporation"], 110, s => Uninstall(s, @"^NVIDIA PhysX System Software \d", exclude: PhysXLegacyCode), s => s.Nvidia),
            new("physx-legacy", "PhysX Legacy", Group.Other, Kind.Msi, "9.13.0604", new Version(9, 13, 604),
                "https://us.download.nvidia.com/Windows/9.13.0604/PhysX-9.13.0604-SystemSoftware-Legacy.msi",
                "2B78DF4A9004F5C4220D62C363709E5AC5E2E58DF9F2284DF0254CBFA4970D91", 20154368, "/qn /norestart", null,
                ["NVIDIA Corporation"], 111, s => s.Uninstall.FirstOrDefault(e => e.Key.Equals(PhysXLegacyCode, StringComparison.OrdinalIgnoreCase)) is { } legacy
                    ? new Found(legacy.Version ?? "9.13.0604", Parse(legacy.Version)) : null, s => s.Nvidia),
            // WebView2 keeps itself current through Edge Update: its bootstrapper, checked against Microsoft's signature.
            new("webview2", "Edge WebView2", Group.Other, Kind.Exe, "Kept current by Windows", null,
                "https://go.microsoft.com/fwlink/p/?LinkId=2124703", null, 0, "/silent /install", null,
                MicrosoftOnly, 130, s => s.Values.GetValueOrDefault("webview2") is { Length: > 0 } version ? new Found(version, null) : null),
            new("vulkan", "Vulkan", Group.Other, Kind.Info, "With your graphics driver", null, null, null, 0, "", null, [], 0,
                s => s.Files.GetValueOrDefault("sys:vulkan-1.dll") is { Length: > 0 } version ? new Found(version, null) : null),
        ]);
        return list;
    }

    // The June 2010 package's own version (9.29.1974): whole, it is the newest there is; partly there, older.
    private static Version JuneTwentyTen => new(9, 29, 1974, 0); // a property: the catalog is built before later fields are set

    private const string PhysXLegacyCode = "{FAAC26AD-73BA-40CE-86AA-C9213F9E064A}";

    internal static Runtime? Find(string id) => Catalog.FirstOrDefault(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>A version as Windows writes it ("v14.51.36247.00", "9.26.0703", "4.0.302.0"), its four parts filled in.</summary>
    internal static Version? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = Regex.Match(text, @"\d+(\.\d+){1,3}");
        if (!match.Success || !Version.TryParse(match.Value, out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
    }

    private static Found? Uninstall(Snapshot s, string pattern, string? exclude = null)
    {
        var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var best = s.Uninstall.Where(e => regex.IsMatch(e.Name) && !e.Key.Equals(exclude, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => Parse(e.Version)).FirstOrDefault();
        return best is null ? null : new Found(best.Version ?? "Installed", Parse(best.Version));
    }

    private static Found? Vc2005(Snapshot s, bool x64)
    {
        var found = Uninstall(s, x64 ? @"^Microsoft Visual C\+\+ 2005 Redistributable \(x64\)$" : @"^Microsoft Visual C\+\+ 2005 Redistributable( \(x86\))?$");
        if (found?.Comparable is not { } msi) return found;
        var key = $"{msi.Major}.{msi.Minor}.{msi.Build}";
        return found with { Shown = Vc2005Shown.GetValueOrDefault(key, found.Shown) };
    }

    private static Found? FileVersion(Snapshot s, string file) =>
        s.Files.GetValueOrDefault(file) is { Length: > 0 } version ? new Found(version, Parse(version)) : null;

    /// <summary>v14's own record (its Installed flag and version), and its runtime file where Windows keeps it.</summary>
    private static Found? Vc14(Snapshot s, string arch, string? file)
    {
        if (s.Values.GetValueOrDefault("vc14:" + arch) is not { Length: > 0 } version) return null;
        if (file is not null && !s.Files.ContainsKey(file)) return null; // its record outlived its files: install it again
        var parsed = Parse(version);
        return new Found(parsed?.ToString() ?? version, parsed);
    }

    private static Found? Desktop(IReadOnlyList<string> versions, int major)
    {
        var newest = versions.Select(v => (Text: v, Version: Parse(v))).Where(v => v.Version?.Major == major && !v.Text.Contains('-'))
            .OrderByDescending(v => v.Version).FirstOrDefault();
        return newest.Version is null ? null : new Found(newest.Text, newest.Version);
    }

    private static Found? NetFramework4(Snapshot s)
    {
        if (!int.TryParse(s.Values.GetValueOrDefault("ndp4"), out var release)) return null;
        var version = release >= 533320 ? "4.8.1" : release >= 528040 ? "4.8" : release >= 461808 ? "4.7.2" : release >= 461308 ? "4.7.1"
            : release >= 460798 ? "4.7" : release >= 394802 ? "4.6.2" : release >= 394254 ? "4.6.1" : release >= 393295 ? "4.6" : "4.5";
        return new Found(version, Version.Parse(version));
    }

    private static Found? DirectXLegacy(Snapshot s)
    {
        var folders = new[] { "sys:", "wow:" };
        var total = DirectXFiles.Length * folders.Length;
        var present = folders.Sum(folder => DirectXFiles.Count(file => s.Files.ContainsKey(folder + file)));
        if (present == 0) return null;
        // Some of its files: what a game's own setup left (xinput1_3 alone, say). The package fills in the rest.
        return present == total ? new Found("June 2010", JuneTwentyTen) : new Found($"{present} of {total} files", new Version(0, 0));
    }

    /// <summary>Each runtime that applies to this PC, as detection finds it. Pure: everything it reads is in the snapshot.</summary>
    internal static IReadOnlyList<RuntimeStatus> Evaluate(Snapshot s, IReadOnlyDictionary<string, DotnetRelease>? dotnet = null, DateTime? now = null)
    {
        var today = now ?? DateTime.UtcNow;
        var result = new List<RuntimeStatus>();
        foreach (var r in Catalog)
        {
            if (r.Applies is { } applies && !applies(s)) continue;
            var found = r.Find(s);
            var latest = r.Latest;
            var latestComparable = r.LatestComparable;
            var ended = r.EndOfSupport is { } end && today >= end;
            if (r.Id.StartsWith("dotnet", StringComparison.Ordinal) && dotnet?.GetValueOrDefault(DotnetKey(r.Id)) is { } release)
            {
                latest = release.Version;
                latestComparable = Parse(release.Version);
                ended = release.EndOfSupport;
            }
            var detail = r.Id switch
            {
                "d3d12" => s.Graphics,
                "directstorage" => "Games that load with it bring their own copy. Nothing to install.",
                "agility" => "Games bring newer Direct3D 12 features with them. Nothing to install.",
                "vulkan" when found is null => "Your graphics driver installs it.",
                _ => null,
            };
            var state = !r.Installable ? State.Info
                : found is null ? State.Missing
                : found.Comparable is { } have && latestComparable is { } want && have < want ? State.Outdated
                : State.Current;
            result.Add(new RuntimeStatus(r, state, found, latest, ended, detail));
        }
        return result;
    }

    /// <summary>"dotnet8-x64" → "8-x64": the release list's key.</summary>
    private static string DotnetKey(string id) => id["dotnet".Length..];

    private static readonly object SnapshotGate = new();
    private static (Snapshot Value, long At)? _snapshot;

    /// <summary>The snapshot, read again when older than 15 seconds or after an install (<see cref="ForgetSnapshot"/>).</summary>
    internal static Snapshot CurrentSnapshot()
    {
        lock (SnapshotGate)
        {
            if (_snapshot is { } cached && Environment.TickCount64 - cached.At < 15_000) return cached.Value;
            var read = ReadSnapshot();
            _snapshot = (read, Environment.TickCount64);
            return read;
        }
    }

    internal static void ForgetSnapshot()
    {
        lock (SnapshotGate) _snapshot = null;
    }

    private static readonly string[] UninstallNames = ["Microsoft Visual C++ 20", "Microsoft GameInput", "Microsoft XNA Framework", "NVIDIA PhysX", "OpenAL", "PhysX"];

    internal static Snapshot ReadSnapshot()
    {
        var started = Stopwatch.GetTimestamp();
        var uninstall = new List<UninstallEntry>();
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (key is null) continue;
                foreach (var name in key.GetSubKeyNames())
                {
                    try
                    {
                        using var entry = key.OpenSubKey(name);
                        if (entry?.GetValue("DisplayName") is not string display) continue;
                        if (!UninstallNames.Any(prefix => display.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) &&
                            !name.Equals(PhysXLegacyCode, StringComparison.OrdinalIgnoreCase)) continue;
                        uninstall.Add(new UninstallEntry(name, display.Trim(), (entry.GetValue("DisplayVersion") as string)?.Trim()));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { /* next */ }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { /* other view */ }
        }

        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var system = Environment.SystemDirectory;
        var wow = Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);
        void Read(string prefix, string folder, string file, bool version)
        {
            var path = Path.Combine(folder, file);
            if (!File.Exists(path)) return;
            var shown = "";
            if (version)
            {
                try
                {
                    var info = FileVersionInfo.GetVersionInfo(path);
                    shown = $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}.{info.FilePrivatePart}";
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            files[prefix + file] = shown;
        }
        foreach (var (prefix, folder) in new[] { ("sys:", system), ("wow:", wow) })
        {
            if (string.IsNullOrEmpty(folder)) continue;
            foreach (var file in new[] { "msvcr100.dll", "vcruntime140.dll", "OpenAL32.dll" }) Read(prefix, folder, file, version: true);
            foreach (var file in DirectXFiles) Read(prefix, folder, file, version: false);
        }
        Read("sys:", system, "vulkan-1.dll", version: true);

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (arch, view) in new[] { ("x64", RegistryView.Registry64), ("x86", RegistryView.Registry32), ("arm64", RegistryView.Registry64) })
        {
            var installed = ReadValue(view, $@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\{arch}", "Installed");
            if (installed is int one && one == 1 && ReadValue(view, $@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\{arch}", "Version") is string version)
                values["vc14:" + arch] = version;
        }
        if (ReadValue(RegistryView.Registry64, @"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full", "Release") is int release) values["ndp4"] = release.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (ReadValue(RegistryView.Registry64, @"SOFTWARE\Microsoft\NET Framework Setup\NDP\v3.5", "Install") is int netfx35) values["ndp35"] = netfx35.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (WebViewRuntimeLocator.IsRuntimeExe(WebViewRuntimeLocator.RegisteredEvergreenExe()) || WebViewRuntimeLocator.IsRuntimeFolder(WebViewRuntimeLocator.DiscoverInstalledRuntimeFolder()))
            values["webview2"] = WebViewRuntimeLocator.RegisteredVersion() ?? "Installed";

        var programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programsX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var arm64 = RuntimeInformation.OSArchitecture == Architecture.Arm64;
        // On an Arm64 PC the x64 runtime sits under dotnet\x64 (Arm64's own is dotnet itself).
        var desktopX64 = Versions(arm64 ? Path.Combine(programs, "dotnet", "x64") : Path.Combine(programs, "dotnet"));
        var desktopX86 = Versions(Path.Combine(programsX86, "dotnet"));
        var adapters = GpuCapability.Adapters();
        var nvidia = adapters.Any(name => name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase));
        // Direct3D 12's answer is for the page only (ListAsync): never a graphics device made on the way to Play.
        var snapshot = new Snapshot(uninstall, files, values, desktopX64, desktopX86, nvidia, arm64, null);
        AppLog.Debug($"Runtimes: read this PC in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms.");
        return snapshot;

        static IReadOnlyList<string> Versions(string dotnet)
        {
            try
            {
                var folder = Path.Combine(dotnet, "shared", "Microsoft.WindowsDesktop.App");
                return Directory.Exists(folder) ? Directory.GetDirectories(folder).Select(Path.GetFileName).OfType<string>().ToArray() : [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
        }
    }

    private static object? ReadValue(RegistryView view, string path, string name)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = root.OpenSubKey(path);
            return key?.GetValue(name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }

    /// <summary>
    /// What Direct3D 12 can do on the graphics card Windows picks for games: its feature level (12_2 is DirectX 12
    /// Ultimate) and shader model, asked of the driver once (it changes only with the card or its driver).
    /// </summary>
    internal static readonly Lazy<string?> Graphics = new(ReadGraphics, LazyThreadSafetyMode.ExecutionAndPublication);

    private static unsafe string? ReadGraphics()
    {
        try
        {
            var factoryIid = new Guid("c1b6694f-ff09-44a9-b03c-77900a0a1d17"); // IDXGIFactory6
            if (CreateDXGIFactory2(0, in factoryIid, out var factory) < 0) return null;
            try
            {
                var adapterIid = new Guid("29038f61-3839-4626-91fd-086879011a05"); // IDXGIAdapter1
                var byPreference = (delegate* unmanaged[Stdcall]<IntPtr, uint, int, Guid*, IntPtr*, int>)(*(IntPtr**)factory)[29]; // EnumAdapterByGpuPreference
                for (uint i = 0; i < 8; i++)
                {
                    IntPtr adapter;
                    if (byPreference(factory, i, 2 /* DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE */, &adapterIid, &adapter) < 0) return null;
                    try
                    {
                        AdapterDesc1 desc;
                        var getDesc1 = (delegate* unmanaged[Stdcall]<IntPtr, AdapterDesc1*, int>)(*(IntPtr**)adapter)[10];
                        if (getDesc1(adapter, &desc) < 0 || (desc.Flags & 2) != 0) continue; // DXGI_ADAPTER_FLAG_SOFTWARE
                        var name = new string(desc.Description).TrimEnd('\0').Trim();
                        var deviceIid = new Guid("189819f1-1db6-4b57-be54-1821339b85f7"); // ID3D12Device
                        if (D3D12CreateDevice(adapter, 0xb000, in deviceIid, out var device) < 0) return null;
                        try
                        {
                            var check = (delegate* unmanaged[Stdcall]<IntPtr, int, void*, uint, int>)(*(IntPtr**)device)[13]; // CheckFeatureSupport
                            var requested = stackalloc int[] { 0xb000, 0xb100, 0xc000, 0xc100, 0xc200 };
                            var levels = new FeatureLevels { Count = 5, Requested = requested };
                            var level = check(device, 2 /* D3D12_FEATURE_FEATURE_LEVELS */, &levels, (uint)sizeof(FeatureLevels)) >= 0 ? levels.Max : 0xb000;
                            var shader = 0;
                            // The highest shader model the runtime knows: ask from 6.9 down until it answers.
                            for (var model = 0x69; model >= 0x60 && shader == 0; model--)
                            {
                                var asked = model;
                                if (check(device, 7 /* D3D12_FEATURE_SHADER_MODEL */, &asked, 4) >= 0) shader = asked;
                            }
                            var levelText = $"{level >> 12}_{(level >> 8) & 0xF}";
                            var shaderText = shader == 0 ? "" : $" · shader model {shader >> 4}.{shader & 0xF}";
                            var ultimate = level >= 0xc200 ? "DirectX 12 Ultimate · " : "";
                            return $"{ultimate}feature level {levelText}{shaderText} on {name}";
                        }
                        finally { Marshal.Release(device); }
                    }
                    finally { Marshal.Release(adapter); }
                }
                return null;
            }
            finally { Marshal.Release(factory); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            AppLog.Debug("Runtimes: Direct3D 12 couldn't be asked: " + ex.Message);
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct FeatureLevels
    {
        public uint Count;
        public int* Requested;
        public int Max;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct AdapterDesc1
    {
        public fixed char Description[128];
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory2(uint flags, in Guid riid, out IntPtr factory);

    [DllImport("d3d12.dll")]
    private static extern int D3D12CreateDevice(IntPtr adapter, int minimumFeatureLevel, in Guid riid, out IntPtr device);
}
