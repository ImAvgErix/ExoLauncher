using System.Security.Cryptography;
using System.Text;

namespace ExoLauncher.Helpers;

/// <summary>Deterministic content hash over a vendored extension tree — the
/// pin a bundled extension is verified against before WebView2 installs it.</summary>
internal static class WebExtensionManifest
{
    // sha256 over "relpath\nfilehash\n" lines, ordinal-sorted, with Exo-added
    // documentation files excluded. The rest of the tree must match the
    // recorded download exactly.
    private static readonly HashSet<string> HashExcluded = new(StringComparer.Ordinal)
        { "SOURCE.txt", "LICENSE.txt" }; // Exo's notes and the license text, not the extension's code

    internal static string ComputeManifestHash(string folder)
    {
        var builder = new StringBuilder();
        var baseLength = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length + 1;
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                     .Select(path => path[baseLength..].Replace(Path.DirectorySeparatorChar, '/'))
                     .Where(relative => !HashExcluded.Contains(relative))
                     .OrderBy(relative => relative, StringComparer.Ordinal))
        {
            builder.Append(file).Append('\n')
                .Append(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(folder, file.Replace('/', Path.DirectorySeparatorChar))))).ToLowerInvariant())
                .Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }
}
