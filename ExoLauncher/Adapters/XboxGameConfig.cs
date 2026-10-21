using System.Xml;
using System.Xml.Linq;

namespace ExoLauncher.Adapters;

/// <summary>
/// The fields Exo reads from a packaged PC game's MicrosoftGame.config:
/// package identity, Store product id, display name and main executable.
/// Malformed, oversized or DTD-bearing files read as null.
/// </summary>
internal sealed record XboxGameConfig(string? IdentityName, string? StoreId, string? DisplayName, string? ExecutableName)
{
    internal const string FileName = "MicrosoftGame.config";
    private const long MaxBytes = 1024 * 1024;

    /// <summary>
    /// DefaultDisplayName unless it is an ms-resource: reference, a localised
    /// string Exo cannot resolve outside the package.
    /// </summary>
    internal string? ReadableName =>
        DisplayName is not null && !DisplayName.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)
            ? DisplayName
            : null;

    internal static XboxGameConfig? Read(string? folder, Func<string, bool>? fileExists = null)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        var path = Path.Combine(folder, FileName);
        try
        {
            if (!(fileExists ?? File.Exists)(path)) return null;
            if (new FileInfo(path).Length > MaxBytes) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Parse(stream);
        }
        catch { return null; }
    }

    internal static XboxGameConfig? Parse(string xml)
    {
        using var reader = new StringReader(xml);
        return Parse(reader);
    }

    private static XboxGameConfig? Parse(Stream stream)
    {
        using var reader = new StreamReader(stream);
        return Parse(reader);
    }

    private static XboxGameConfig? Parse(TextReader text)
    {
        try
        {
            using var reader = XmlReader.Create(text, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxBytes,
            });
            var doc = XDocument.Load(reader);
            var identity = Clean(First(doc, "Identity")?.Attribute("Name")?.Value);
            var storeId = Clean(First(doc, "StoreId")?.Value);
            var display = Clean(First(doc, "ShellVisuals")?.Attribute("DefaultDisplayName")?.Value);
            var executable = First(doc, "Executable");
            var exeName = Clean(executable?.Attribute("Name")?.Value ?? executable?.Attribute("ImageName")?.Value);
            if (identity is null && storeId is null && display is null && exeName is null) return null;
            return new XboxGameConfig(identity, storeId, display, exeName);
        }
        catch { return null; }
    }

    private static XElement? First(XDocument doc, string localName) =>
        doc.Descendants().FirstOrDefault(e => e.Name.LocalName == localName);

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
