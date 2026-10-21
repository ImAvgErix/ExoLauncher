using System.Text.Json;
using ExoLauncher.Models;

namespace ExoLauncher.Adapters.Cli;

/// <summary>Confirms Nile's installed record and manifest after a download operation.</summary>
internal sealed record NileJobEvidence(string ProductId, string ConfigRoot, string InstallPath,
    string Version, DateTime ManifestWriteUtc, long ManifestLength, bool HasGameFiles)
{
    internal static NileJobEvidence? Read(string productId, IEnumerable<string> configRoots)
    {
        if (string.IsNullOrWhiteSpace(productId) || productId.IndexOfAny(['/', '\\', ':']) >= 0)
            return null;
        // Only the signed-in folder, the same one ReadCachedLibrary reads.
        var signedIn = NileCli.SessionRoot(configRoots, File.Exists, path =>
        {
            try { return File.ReadAllText(path); }
            catch { return null; }
        });
        foreach (var root in signedIn is null ? [] : new[] { signedIn })
        {
            try
            {
                var libraryPath = Path.Combine(root, "library.json");
                if (!File.Exists(libraryPath) || new FileInfo(libraryPath).Length > 16 * 1024 * 1024)
                    continue;
                var owned = NileCli.ParseLibraryJson(File.ReadAllText(libraryPath));
                if (owned.Count == 0) continue;
                if (!owned.Any(row => row.ProductId == productId)) return null;
                var installedPath = Path.Combine(root, "installed.json");
                if (!File.Exists(installedPath) || new FileInfo(installedPath).Length > 4 * 1024 * 1024)
                    return null;
                using var document = JsonDocument.Parse(File.ReadAllText(installedPath));
                if (document.RootElement.ValueKind != JsonValueKind.Array) return null;
                foreach (var row in document.RootElement.EnumerateArray())
                {
                    if (row.ValueKind != JsonValueKind.Object || ReadString(row, "id") != productId) continue;
                    var path = ReadString(row, "path");
                    var version = ReadString(row, "version");
                    var manifest = new FileInfo(Path.Combine(root, "manifests", productId + ".raw"));
                    if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
                        string.IsNullOrWhiteSpace(version) || !manifest.Exists || manifest.Length == 0)
                        return null;
                    var hasFiles = Directory.Exists(path) &&
                        BoundedFileTree.EnumerateFiles(path, BoundedFileTree.CatalogEntryLimit, 12).Any();
                    return new NileJobEvidence(productId, root, path, version,
                        manifest.LastWriteTimeUtc, manifest.Length, hasFiles);
                }
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                return null;
            }
        }
        return null;
    }

    internal static bool IsFailureLine(string line) =>
        line.TrimStart().StartsWith("ERROR", StringComparison.OrdinalIgnoreCase) ||
        line.TrimStart().StartsWith("CRITICAL", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Unable to load manifest", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Not enough space available", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Traceback (most recent call last)", StringComparison.Ordinal);

    internal static InstallResult Evaluate(int exitCode, string productId, string? diagnosticFailure,
        bool reportedUpToDate, NileJobEvidence? before, NileJobEvidence? after, string successMessage)
    {
        if (exitCode != 0 || !string.IsNullOrWhiteSpace(diagnosticFailure))
            return new InstallResult { Ok = false, Message = diagnosticFailure ?? "Amazon Games could not finish. Try again." };
        if (after is null || after.ProductId != productId || !after.HasGameFiles)
            return new InstallResult { Ok = false, Message = "Exo could not confirm the game files are on this PC. Refresh the library and try again." };
        var refreshed = before is null || before.ConfigRoot != after.ConfigRoot ||
                        before.Version != after.Version || before.ManifestWriteUtc != after.ManifestWriteUtc ||
                        before.ManifestLength != after.ManifestLength;
        if (!refreshed && !reportedUpToDate)
            return new InstallResult { Ok = false, Message = "Exo could not confirm that this finished. Refresh the library and try again." };
        return new InstallResult { Ok = true, Message = successMessage, Path = after.InstallPath };
    }

    private static string? ReadString(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
