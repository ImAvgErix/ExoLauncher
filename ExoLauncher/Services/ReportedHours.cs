using System.Text.Json;
using ExoLauncher.Adapters;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Hours services report over the network, kept on disk so the library can
/// show them offline: Xbox's MinutesPlayed per title (OpenXBL). Only titles
/// and minutes are written, never a key or gamertag. Each refresh replaces the
/// list whole, so a game the service no longer reports drops out. Older files'
/// tracker and trackerReadAt keys are ignored and dropped on the next save.
/// </summary>
internal static class ReportedHours
{
    internal sealed record Document(
        DateTimeOffset? XboxAt,
        IReadOnlyList<XboxPlaytime.TitleHours> Xbox);

    private static readonly object Gate = new();
    private static Document? _cached;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private static string FilePath => Path.Combine(PathHelper.AppDataDir, "reported-hours.json");

    internal static Document Empty { get; } = new(null, []);

    internal static Document Load()
    {
        lock (Gate) return _cached ??= Read();
    }

    internal static void SaveXbox(IReadOnlyList<XboxPlaytime.TitleHours> titles) =>
        Update(current => current with { XboxAt = DateTimeOffset.UtcNow, Xbox = titles });

    internal static void ClearXbox() => Update(current => current with { XboxAt = null, Xbox = [] });

    private static void Update(Func<Document, Document> change)
    {
        lock (Gate)
        {
            var next = change(_cached ??= Read());
            _cached = next;
            var tmp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(PathHelper.AppDataDir);
                File.WriteAllText(tmp, JsonSerializer.Serialize(next, Json));
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch (Exception ex)
            {
                AppLog.Debug("Reported hours save failed: " + ex.GetType().Name);
            }
            finally
            {
                try { File.Delete(tmp); } catch { /* only the unique temp */ }
            }
        }
    }

    private static Document Read()
    {
        try
        {
            if (!File.Exists(FilePath) || new FileInfo(FilePath).Length > 8 * 1024 * 1024) return Empty;
            var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(FilePath), Json);
            if (document is null) return Empty;
            return new Document(
                document.XboxAt,
                (document.Xbox ?? []).Where(title => title is { Minutes: > 0 } && !string.IsNullOrWhiteSpace(title.Name)).ToList());
        }
        catch (Exception ex)
        {
            AppLog.Debug("Reported hours unreadable: " + ex.GetType().Name);
            return Empty;
        }
    }
}
