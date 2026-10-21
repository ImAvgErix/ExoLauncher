using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace ExoLauncher.Tests;

/// <summary>
/// The P/Invoke half of the banned-API rule (BannedSymbols.txt covers .NET
/// symbols): Exo never captures the screen or synthesizes input. Steam and the
/// other stores are driven through ExoLauncher.SteamIpc, Legendary, gogdl and
/// their own URIs, never by reading pixels or sending clicks.
/// </summary>
public sealed partial class BannedNativeImportsTests
{
    private static readonly HashSet<string> Banned = new(StringComparer.OrdinalIgnoreCase)
    {
        // Screen capture.
        "PrintWindow", "BitBlt", "StretchBlt", "GetDIBits",
        // Synthetic input.
        "SendInput", "mouse_event", "keybd_event",
    };

    [Fact]
    public void HostDeclaresNoBannedImport()
    {
        var offenders = typeof(ExoHttp).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Select(method => (method, import: method.GetCustomAttribute<DllImportAttribute>()))
            .Where(entry => entry.import is not null)
            .Select(entry => (entry.method, entry: string.IsNullOrEmpty(entry.import!.EntryPoint) ? entry.method.Name : entry.import.EntryPoint))
            .Where(entry => Banned.Contains(entry.entry))
            .Select(entry => $"{entry.method.DeclaringType}.{entry.method.Name} -> {entry.entry}")
            .ToArray();
        Assert.True(offenders.Length == 0, "Banned native imports: " + string.Join(", ", offenders));
        // The scan must see the host's own imports, or it proves nothing.
        Assert.Contains(typeof(ExoHttp).Assembly.GetTypes()
                .SelectMany(type => type.GetMethods(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)),
            method => method.GetCustomAttribute<DllImportAttribute>() is not null);
    }

    /// <summary>Catches the names in sources the assembly scan cannot see: GetProcAddress strings, SteamIpc, LibraryImport.</summary>
    [Fact]
    public void HostSourceNamesNoBannedImport()
    {
        var offenders = Repo.HostSources()
            .SelectMany(path => File.ReadLines(path).Select((line, index) => (path, line, index)))
            .Where(entry => BannedName().Matches(entry.line).Any(match => Banned.Contains(match.Groups[1].Value)) &&
                            (entry.line.Contains("extern", StringComparison.Ordinal) ||
                             entry.line.Contains("Import", StringComparison.Ordinal) ||
                             entry.line.Contains('"')))
            .Select(entry => $"{Path.GetRelativePath(Repo.Root, entry.path)}:{entry.index + 1}")
            .ToArray();
        Assert.True(offenders.Length == 0, "Banned native API named in: " + string.Join(", ", offenders));
    }

    [GeneratedRegex(@"\b(PrintWindow|BitBlt|StretchBlt|GetDIBits|SendInput|mouse_event|keybd_event)\b")]
    private static partial Regex BannedName();
}
