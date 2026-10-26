using System.Text.Json;
using ExoLauncher.Services.Hid;

namespace ExoLauncher.Services;

/// <summary>
/// HID for the Devices room's mouse module: hid.list, hid.open, hid.close,
/// hid.sendReport, hid.sendFeatureReport, hid.receiveFeatureReport,
/// hid.receiveInputReport; hid.input and hid.changed. Report bytes travel as
/// base64. Devices are named by an opaque key, never by a path.
/// </summary>
public sealed partial class WebHostBridge
{
    // HID calls run on the thread pool, so the first two may race to wire the service.
    private int _hidWired;

    private HidService Hid()
    {
        var hid = _services.Hid;
        if (Interlocked.Exchange(ref _hidWired, 1) == 1) return hid;
        hid.Input += OnHidInput;
        hid.Changed += OnHidChanged;
        hid.Start();
        return hid;
    }

    private object HidList(JsonElement p, bool hasParams)
    {
        int[]? vendors = null;
        if (hasParams && p.ValueKind == JsonValueKind.Object && p.TryGetProperty("vendorIds", out var v) && v.ValueKind == JsonValueKind.Array)
            vendors = v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out _)).Select(x => x.GetInt32()).Take(256).ToArray();
        // An empty list means every maker: Exo Mouse asks that way and picks its own mice from the answer.
        return new
        {
            ok = true,
            devices = Hid().List(vendors).Select(d => new
            {
                key = d.Key, vendorId = d.VendorId, productId = d.ProductId, productName = d.ProductName,
                collections = d.Collections.Select(c => new
                {
                    usagePage = c.UsagePage, usage = c.Usage,
                    inputIds = c.InputIds, outputIds = c.OutputIds, featureIds = c.FeatureIds,
                    inputLength = c.InputLength, outputLength = c.OutputLength, featureLength = c.FeatureLength,
                }),
            }),
        };
    }

    private async Task<object> HidCallAsync(string method, JsonElement p, bool hasParams, CancellationToken ct)
    {
        if (ReadString(p, hasParams, "device") is not { Length: > 0 } key) return new { ok = false, message = "No device." };
        var reportId = (int)(ReadDouble(p, hasParams, "reportId") ?? 0);
        if (reportId is < 0 or > 255) return new { ok = false, message = "No such report." };
        try
        {
            var hid = Hid();
            switch (method)
            {
                case "hid.open": hid.Open(key); return new { ok = true };
                case "hid.close": hid.Close(key); return new { ok = true };
                case "hid.sendReport": await hid.SendReportAsync(key, reportId, Bytes(p, hasParams), ct).ConfigureAwait(false); return new { ok = true };
                case "hid.sendFeatureReport": await hid.SendFeatureReportAsync(key, reportId, Bytes(p, hasParams), ct).ConfigureAwait(false); return new { ok = true };
                case "hid.receiveFeatureReport":
                    return new { ok = true, data = Convert.ToBase64String(await hid.ReceiveFeatureReportAsync(key, reportId, ct).ConfigureAwait(false)) };
                case "hid.receiveInputReport":
                    return new { ok = true, data = Convert.ToBase64String(await hid.ReceiveInputReportAsync(key, reportId, ct).ConfigureAwait(false)) };
                default: return new { ok = false, message = "Unknown HID call." };
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException or FormatException)
        {
            return new { ok = false, message = ex.Message };
        }
    }

    // ------------------------------------------------------------------ mouse profiles

    private const int MaxMouseProfilesBytes = 512 * 1024;

    private static string MouseProfilesPath => Path.Combine(ExoLauncher.Helpers.PathHelper.AppDataDir, "mouse", "profiles.json");

    /// <summary>The mouse module's saved profiles (an opaque JSON object it owns), from Exo's data folder.</summary>
    private static object MouseProfilesGet()
    {
        try
        {
            var path = MouseProfilesPath;
            if (!File.Exists(path)) return new { ok = true, data = (object?)null };
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.ValueKind == JsonValueKind.Object
                ? new { ok = true, data = (object?)doc.RootElement.Clone() }
                : new { ok = true, data = (object?)null };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new { ok = false, message = "Exo could not read your mouse profiles." };
        }
    }

    /// <summary>Saves the module's profiles: a JSON object of at most 512 KB, written whole or not at all.</summary>
    private static object MouseProfilesSet(JsonElement p, bool hasParams)
    {
        if (!hasParams || p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return new { ok = false, message = "No profiles to save." };
        var json = data.GetRawText();
        if (json.Length > MaxMouseProfilesBytes) return new { ok = false, message = "Too many profiles to save." };
        try
        {
            var path = MouseProfilesPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
            return new { ok = true };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new { ok = false, message = "Exo could not save your mouse profiles." };
        }
    }

    private static byte[] Bytes(JsonElement p, bool hasParams) =>
        ReadString(p, hasParams, "data") is { Length: > 0 } data ? Convert.FromBase64String(data) : [];

    private void OnHidInput(string key, int reportId, byte[] data) =>
        PostEvent("hid.input", new { device = key, reportId, data = Convert.ToBase64String(data) });

    private void OnHidChanged() => PostEvent("hid.changed", new { });
}
