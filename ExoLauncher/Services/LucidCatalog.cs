using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;

namespace ExoLauncher.Services;

/// <summary>
/// Lucid's headphone list, read from the user's own Lucid at run time (Exo ships none of it). Lucid looks for
/// <c>Data\headphone-corrections.json</c> beside itself, else uses the copy built into Lucid.dll; Lucid.exe is a .NET
/// single-file bundle, so Lucid.dll is read out of the bundle (inflated when the bundle is compressed) and the list out
/// of its manifest resources. Only the headphones Lucid's picker offers come back: enabled, calibrated, with an EQ.
/// </summary>
internal static class LucidCatalog
{
    internal sealed record Headphone(string Id, string Name, string Model, string Brand, string Category, string Grade, string Keywords);

    private static readonly Lock Gate = new();
    private static (string Path, long Length, DateTime Written, IReadOnlyList<Headphone> List)? _cache;

    /// <summary>Lucid IEMs first, as in Lucid's picker; null when this Lucid's list can't be read.</summary>
    internal static IReadOnlyList<Headphone>? Read(string exe)
    {
        try
        {
            var info = new FileInfo(exe);
            lock (Gate)
                if (_cache is { } hit && hit.Path == exe && hit.Length == info.Length && hit.Written == info.LastWriteTimeUtc) return hit.List;
            if (CatalogJson(exe) is not { } json) return null;
            var list = Parse(json);
            lock (Gate) _cache = (exe, info.Length, info.LastWriteTimeUtc, list);
            return list;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or BadImageFormatException or InvalidDataException)
        {
            Helpers.AppLog.Warn("Lucid: its headphone list couldn't be read: " + ex.GetType().Name);
            return null;
        }
    }

    private static string? CatalogJson(string exe)
    {
        var dir = Path.GetDirectoryName(exe) ?? "";
        var disk = Path.Combine(dir, "Data", "headphone-corrections.json");
        if (File.Exists(disk)) return File.ReadAllText(disk);
        var assembly = Path.Combine(dir, "Lucid.dll");
        byte[]? dll;
        using (var stream = File.OpenRead(exe)) dll = BundleFile(stream, "Lucid.dll");
        dll ??= File.Exists(assembly) ? File.ReadAllBytes(assembly) : null;
        return dll is not null && Resource(dll, "headphone-corrections.json") is { } bytes ? Encoding.UTF8.GetString(bytes) : null;
    }

    // The single-file host's marker: the bundle header's offset sits in the 8 bytes before it.
    private static readonly byte[] BundleSignature =
    [
        0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38, 0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
        0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18, 0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae,
    ];

    /// <summary>One file out of a .NET single-file bundle; null when this isn't a bundle or the file isn't in it.</summary>
    internal static byte[]? BundleFile(Stream exe, string name)
    {
        if (FindSignature(exe) is not { } at || at < 8) return null;
        using var reader = new BinaryReader(exe, Encoding.UTF8, leaveOpen: true);
        exe.Position = at - 8;
        var header = reader.ReadInt64();
        if (header <= 0 || header >= exe.Length) return null;
        exe.Position = header;
        var major = reader.ReadUInt32();
        reader.ReadUInt32();
        var count = reader.ReadInt32();
        if (count is < 0 or > 100_000) return null;
        reader.ReadString(); // bundle id
        if (major >= 2) exe.Position += 8 * 4 + 8; // deps.json, runtimeconfig.json, flags
        for (var i = 0; i < count; i++)
        {
            var offset = reader.ReadInt64();
            var size = reader.ReadInt64();
            var compressed = major >= 6 ? reader.ReadInt64() : 0;
            reader.ReadByte(); // type
            var path = reader.ReadString();
            if (!string.Equals(path, name, StringComparison.OrdinalIgnoreCase)) continue;
            if (size is < 0 or > 512L * 1024 * 1024 || offset < 0 || offset + Math.Max(size, compressed) > exe.Length) return null;
            exe.Position = offset;
            if (compressed == 0) return reader.ReadBytes((int)size);
            using var inflate = new DeflateStream(new MemoryStream(reader.ReadBytes((int)compressed)), CompressionMode.Decompress);
            var bytes = new byte[size];
            inflate.ReadExactly(bytes);
            return bytes;
        }
        return null;
    }

    private static long? FindSignature(Stream exe)
    {
        var buffer = new byte[1 << 20];
        long start = 0;
        // The marker lives in the host, before the bundled files; 64 MB is far past any host.
        while (start < Math.Min(exe.Length, 64L << 20))
        {
            exe.Position = start;
            var read = exe.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            var hit = buffer.AsSpan(0, read).IndexOf(BundleSignature);
            if (hit >= 0) return start + hit;
            if (read < buffer.Length) return null;
            start += read - BundleSignature.Length;
        }
        return null;
    }

    /// <summary>An embedded manifest resource whose name ends with <paramref name="suffix"/>.</summary>
    internal static byte[]? Resource(byte[] assembly, string suffix)
    {
        using var pe = new PEReader(new MemoryStream(assembly));
        if (!pe.HasMetadata || pe.PEHeaders.CorHeader is not { } cor) return null;
        var metadata = pe.GetMetadataReader();
        foreach (var handle in metadata.ManifestResources)
        {
            var resource = metadata.GetManifestResource(handle);
            if (!resource.Implementation.IsNil || !metadata.GetString(resource.Name).EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            var blob = pe.GetSectionData(cor.ResourcesDirectory.RelativeVirtualAddress + (int)resource.Offset).GetReader();
            var length = blob.ReadInt32();
            return length >= 0 && length <= blob.RemainingBytes ? blob.ReadBytes(length) : null;
        }
        return null;
    }

    /// <summary>Lucid's catalog file (schema 2: devices), as its HeadphoneCorrectionManager reads it.</summary>
    internal static IReadOnlyList<Headphone> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true });
        var devices = Property(doc.RootElement, "devices") is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray().ToArray() : [];
        var byId = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in devices)
            if (Str(device, "id") is { Length: > 0 } id && Str(device, "name") is { Length: > 0 }) byId[id] = device;
        var list = new List<Headphone>();
        foreach (var (id, device) in byId)
        {
            // An entry can borrow another's calibration; Lucid drops it when that source isn't usable itself.
            var source = device;
            if (Str(device, "correctionSourceId") is { Length: > 0 } from && !string.Equals(from, id, StringComparison.OrdinalIgnoreCase))
            {
                if (!byId.TryGetValue(from, out source) || Str(source, "correctionSourceId") is { Length: > 0 } || !Selectable(source)) continue;
            }
            if (!Flag(device, "enabled") || !Selectable(source)) continue;
            var name = Str(device, "name")!;
            var brand = Str(device, "brand") ?? "";
            var cost = Property(source, "correctionCost") is { ValueKind: JsonValueKind.Number } number ? number.GetDouble() : 0;
            list.Add(new Headphone(id, name, Model(name, brand), brand,
                Str(device, "category") is { Length: > 0 } category ? category : "Gaming Headsets",
                cost <= 1.5 ? "Excellent" : cost <= 2.5 ? "Good" : cost <= 3.5 ? "Compatible" : "Not recommended",
                string.Join(' ', Words(device, "aliases").Concat(Words(device, "alsoKnownAs"))).ToLowerInvariant()));
        }
        return [new Headphone(LucidSettings.LucidIems, "Lucid IEMs", "Lucid IEMs", "tekkusai", "In-Ear Monitors", "Excellent", "lucid iems"),
            .. list.OrderBy(h => CategoryOrder(h.Category)).ThenBy(h => h.Category, StringComparer.OrdinalIgnoreCase).ThenBy(h => h.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private static bool Selectable(JsonElement device) =>
        Flag(device, "enabled") && Flag(device, "isCalibrated") && !string.IsNullOrWhiteSpace(Str(device, "eqConfig"));

    private static int CategoryOrder(string category) => category switch
    {
        "Gaming Headsets" => 0, "Headphones" => 1, "In-Ear Monitors" => 2, "Earbuds" => 3, _ => 4,
    };

    /// <summary>Lucid's GetDisplayNameWithoutBrand: "Astro A50 X" under Astro is "A50 X".</summary>
    private static string Model(string name, string brand) =>
        brand.Length > 0 && name.StartsWith(brand + " ", StringComparison.OrdinalIgnoreCase) ? name[brand.Length..].Trim() : name;

    private static JsonElement? Property(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in element.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return null;
    }

    private static string? Str(JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString()?.Trim() : null;

    // Lucid's defaults: enabled and calibrated unless the entry says otherwise.
    private static bool Flag(JsonElement element, string name) => Property(element, name) is not { ValueKind: JsonValueKind.False };

    private static IEnumerable<string> Words(JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.Array } array
            ? array.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!.Trim())
            : [];
}
