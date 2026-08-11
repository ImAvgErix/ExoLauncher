using System.Runtime.InteropServices;
using ExoLauncher.Models;
using Microsoft.Win32;

namespace ExoLauncher.Services;

/// <summary>
/// Detect common runtimes. Offer official installers with consent — never silent-force.
/// </summary>
public sealed class DependencyService
{
    public IReadOnlyList<DependencyInfo> DetectAll()
    {
        return new[]
        {
            DetectVcRedist(),
            DetectDirectX(),
            DetectDotNetDesktop(),
            DetectWebView2(),
        };
    }

    public IReadOnlyList<DependencyInfo> GetMissingRequired(GameEntry game)
    {
        _ = game;
        // Only definite Missing — never Unknown (that spam-prompted VC++ on every machine).
        return DetectAll().Where(d => d.Status == "Missing").ToList();
    }

    /// <summary>
    /// Opens the official download page. Never runs an installer silently.
    /// </summary>
    public object OfferInstall(string dependencyId)
    {
        var dep = DetectAll().FirstOrDefault(d =>
            string.Equals(d.Id, dependencyId, StringComparison.OrdinalIgnoreCase));
        if (dep is null)
            return new { ok = false, message = "Unknown dependency." };
        if (string.IsNullOrWhiteSpace(dep.OfficialUrl))
            return new { ok = false, message = "No official URL mapped." };

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dep.OfficialUrl)
            {
                UseShellExecute = true,
            });
            return new
            {
                ok = true,
                message = "Opened official installer page. Confirm the vendor download yourself.",
                url = dep.OfficialUrl,
            };
        }
        catch (Exception ex)
        {
            return new { ok = false, message = ex.Message };
        }
    }

    private static DependencyInfo DetectVcRedist()
    {
        // Presence of recent VC++ runtime keys is a practical signal, not a full matrix.
        var present = false;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X64");
            present = key?.GetValue("Installed") is int i && i == 1;
        }
        catch { }

        return new DependencyInfo
        {
            Id = "vcredist",
            Name = "Visual C++ Redistributable",
            Status = present ? "Present" : "Missing",
            Detail = present
                ? "VC++ 2015–2022 x64 runtime key found."
                : "VC++ 2015–2022 x64 runtime not found.",
            CanOfferInstall = true,
            OfficialUrl = "https://aka.ms/vs/17/release/vc_redist.x64.exe",
        };
    }

    private static DependencyInfo DetectDirectX()
    {
        var sys = Environment.SystemDirectory;
        var d3d = File.Exists(Path.Combine(sys, "d3d11.dll"));
        return new DependencyInfo
        {
            Id = "directx",
            Name = "DirectX",
            Status = d3d ? "Present" : "Missing",
            Detail = d3d
                ? "d3d11.dll present (OS component)."
                : "DirectX runtime files missing — unusual on Windows 11.",
            CanOfferInstall = true,
            OfficialUrl = "https://www.microsoft.com/download/details.aspx?id=35",
        };
    }

    private static DependencyInfo DetectDotNetDesktop()
    {
        // We're running on .NET 10 — desktop runtime is present for this process.
        // Still report for user visibility.
        var ver = RuntimeInformation.FrameworkDescription;
        return new DependencyInfo
        {
            Id = "dotnet",
            Name = ".NET Desktop Runtime",
            Status = "Present",
            Detail = ver,
            CanOfferInstall = true,
            OfficialUrl = "https://dotnet.microsoft.com/download/dotnet",
        };
    }

    private static DependencyInfo DetectWebView2()
    {
        var present = false;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}")
                ?? Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}");
            present = key?.GetValue("pv") is string s && !string.IsNullOrWhiteSpace(s);
        }
        catch { }

        return new DependencyInfo
        {
            Id = "webview2",
            Name = "WebView2 Runtime",
            Status = present ? "Present" : "Missing",
            Detail = present
                ? "Evergreen WebView2 runtime registered."
                : "Required for the Exo Launcher UI shell.",
            CanOfferInstall = true,
            OfficialUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703",
        };
    }
}
