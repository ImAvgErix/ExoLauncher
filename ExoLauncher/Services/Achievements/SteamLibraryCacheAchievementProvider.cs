using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using ExoLauncher.Adapters;
using ExoLauncher.Models;
using Microsoft.Win32;

namespace ExoLauncher.Services.Achievements;

/// <summary>
/// Read-only Steam provider. Steam's local library cache is the only source
/// used for account progress: the public Community XML is a catalog endpoint
/// and does not reliably carry the signed-in account's unlock state.
/// </summary>
public sealed class SteamLibraryCacheAchievementProvider : IAchievementProvider
{
    private const long MaxPayloadBytes = 8 * 1024 * 1024;
    private const long MaxStoreCatalogBytes = 2 * 1024 * 1024;
    private const int MaxAchievements = 10_000;
    private static readonly TimeSpan StoreCatalogTimeout = TimeSpan.FromSeconds(8);
    private static readonly HttpClient StoreCatalogClient = CreateStoreCatalogClient();
    private readonly Func<string?> _resolveSteamRoot;
    private readonly Func<string, string?> _resolveAccountId;
    private readonly Func<Uri, CancellationToken, Task<string?>> _fetchStoreAppDetails;

    public SteamLibraryCacheAchievementProvider()
        : this(ResolveSteamRoot, ResolveAccountId, FetchStoreAppDetailsAsync)
    {
    }

    internal SteamLibraryCacheAchievementProvider(
        Func<string?> resolveSteamRoot,
        Func<string, string?> resolveAccountId,
        Func<Uri, CancellationToken, Task<string?>>? fetchStoreAppDetails = null)
    {
        _resolveSteamRoot = resolveSteamRoot;
        _resolveAccountId = resolveAccountId;
        _fetchStoreAppDetails = fetchStoreAppDetails ?? FetchStoreAppDetailsAsync;
    }

    public string Id => "steam";
    public StoreKind Store => StoreKind.Steam;
    public AchievementProviderCapabilities Capabilities =>
        AchievementProviderCapabilities.Snapshot |
        AchievementProviderCapabilities.Progress;

    public bool Supports(GameEntry game) =>
        game.Store == StoreKind.Steam &&
        (IsSteamAppId(game.LaunchTarget?.Trim()) ||
         game.Id.StartsWith("steam:", StringComparison.OrdinalIgnoreCase));

    public async Task<AchievementSnapshot> GetSnapshotAsync(
        GameEntry game,
        CancellationToken cancellationToken = default)
    {
        var sourceGameId = SourceGameId(game);
        if (game.Store != StoreKind.Steam || sourceGameId is null)
            return Unavailable(sourceGameId ?? string.Empty, "This entry has no valid Steam app id.");

        var root = _resolveSteamRoot();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return Unavailable(sourceGameId, "Steam is not installed.");
        var accountId = _resolveAccountId(root);
        if (string.IsNullOrWhiteSpace(accountId))
            return Unavailable(sourceGameId, "No unambiguous active Steam account was found.");

        var coverageKey = AchievementCoverageKeys.FromAccount("steam", accountId);
        var cachePath = Path.Combine(
            root,
            "userdata",
            accountId,
            "config",
            "librarycache",
            sourceGameId + ".json");
        AchievementSnapshot local;
        try
        {
            var info = new FileInfo(cachePath);
            if (!info.Exists)
                return Unavailable(sourceGameId,
                    "Steam has not provided current local achievement progress for this game.", coverageKey);
            if (info.Length is <= 0 or > MaxPayloadBytes)
                return Unavailable(sourceGameId,
                    "Steam has not provided current local achievement progress for this game.", coverageKey);

            await using var stream = new FileStream(
                cachePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 64 * 1024,
                useAsync: true);
            using var reader = new StreamReader(stream);
            var json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            if (json.Length > MaxPayloadBytes)
                return Unavailable(sourceGameId,
                    "Steam has not provided current local achievement progress for this game.", coverageKey);
            // Steam can switch the active account while the cache is being
            // read. Do not show or persist an otherwise valid snapshot from
            // the account that was active at the start of this refresh.
            if (!IsCurrentAccount(root, accountId))
                return Unavailable(sourceGameId,
                    "Steam account changed during achievement refresh.");
            local = ParseSnapshotJson(json, sourceGameId, coverageKey, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Unavailable(sourceGameId,
                "Steam has not provided current local achievement progress for this game.", coverageKey);
        }

        if (!IsUncorroboratedLocalZero(local)) return local;

        var catalogStatus = await GetStoreCatalogStatusAsync(sourceGameId, cancellationToken)
            .ConfigureAwait(false);
        // The Store catalog request can take several seconds, so check again
        // before promoting a local 0 / 0 result to a real account summary.
        if (!IsCurrentAccount(root, accountId))
            return Unavailable(sourceGameId,
                "Steam account changed during achievement refresh.");
        return catalogStatus switch
        {
            SteamStoreAchievementCatalogStatus.ConfirmedZero => local with
            {
                Coverage = AchievementCoverageStatus.Complete,
                Capabilities = AchievementProviderCapabilities.Snapshot |
                               AchievementProviderCapabilities.CompleteCatalog,
                Message = "Steam's local cache and Store catalog report no achievements for this game.",
            },
            SteamStoreAchievementCatalogStatus.NonZero => Unavailable(
                sourceGameId,
                "Steam's local 0 / 0 cache conflicts with the Store achievement catalog.",
                coverageKey),
            _ => Unavailable(
                sourceGameId,
                "Steam could not corroborate the local 0 / 0 achievement cache.",
                coverageKey),
        };
    }

    internal static AchievementSnapshot ParseSnapshotJson(
        string? json,
        string sourceGameId,
        string coverageKey,
        DateTimeOffset observedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxPayloadBytes)
            return Unavailable(sourceGameId, "Steam returned no usable cached achievement data.", coverageKey, observedAtUtc);

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
            var root = document.RootElement;
            if (root.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                return Unavailable(sourceGameId, "Steam returned no usable cached achievement data.", coverageKey, observedAtUtc);

            var hasAchievementSection = TryGetSection(root, "achievements", out var achievementSection);
            if (!hasAchievementSection || achievementSection.ValueKind != JsonValueKind.Object)
                return Unavailable(sourceGameId, "Steam returned no usable local achievement progress.", coverageKey, observedAtUtc);

            // nTotal/nAchieved are Steam's account-scoped summary. Vectors are
            // frequently only highlights, so never infer the displayed count
            // from them and never substitute a public catalog's locked rows.
            var total = ReadInt(achievementSection, "nTotal");
            var unlocked = ReadInt(achievementSection, "nAchieved");
            if (total is null or < 0 || unlocked is null or < 0 || unlocked > total)
                return Unavailable(sourceGameId, "Steam returned no usable local achievement progress.", coverageKey, observedAtUtc);

            // Only Steam's account achievement vectors can establish unlock state.
            // `achievementmap` is a catalog/metadata blob and may contain entries
            // for other app ids, stale data, or a contradictory bAchieved field.
            // Never let it add or change account state.
            var entries = new Dictionary<string, AchievementEntry>(StringComparer.OrdinalIgnoreCase);
            if (!CollectAccountEntries(achievementSection, entries, sourceGameId, observedAtUtc))
                return Unavailable(sourceGameId, "Steam returned contradictory local achievement progress.", coverageKey, observedAtUtc);
            if (TryGetSection(root, "achievementmap", out var mapSection))
                EnrichAchievementMapMetadata(mapSection, entries);
            if (entries.Count > total.Value)
                return Unavailable(sourceGameId, "Steam returned inconsistent local achievement progress.", coverageKey, observedAtUtc);
            if (entries.Values.Count(row => row.State.Unlocked) > unlocked.Value)
                return Unavailable(sourceGameId, "Steam returned inconsistent local achievement progress.", coverageKey, observedAtUtc);

            if (total == 0)
            {
                if (unlocked != 0 || entries.Count != 0)
                    return Unavailable(sourceGameId, "Steam returned inconsistent local achievement progress.", coverageKey, observedAtUtc);
                return new AchievementSnapshot
                {
                    ProviderId = "steam",
                    SourceGameId = sourceGameId,
                    CoverageKey = coverageKey,
                    Coverage = AchievementCoverageStatus.Unavailable,
                    Capabilities = AchievementProviderCapabilities.Snapshot,
                    ReportedTotal = 0,
                    ReportedUnlocked = 0,
                    ObservedAtUtc = observedAtUtc,
                    Message = "Steam's local 0 / 0 cache requires separate catalog verification.",
                };
            }

            return new AchievementSnapshot
            {
                ProviderId = "steam",
                SourceGameId = sourceGameId,
                CoverageKey = coverageKey,
                Coverage = AchievementCoverageStatus.Partial,
                Capabilities = AchievementProviderCapabilities.Snapshot |
                               AchievementProviderCapabilities.Progress,
                ReportedTotal = total,
                ReportedUnlocked = unlocked,
                ObservedAtUtc = observedAtUtc,
                Entries = entries.Values.OrderBy(row => row.Definition.ExternalId, StringComparer.Ordinal).ToArray(),
                Message = "Steam local achievement progress.",
            };
        }
        catch (JsonException)
        {
            return Unavailable(sourceGameId, "Steam returned no usable cached achievement data.", coverageKey, observedAtUtc);
        }
    }

    private async Task<SteamStoreAchievementCatalogStatus> GetStoreCatalogStatusAsync(
        string sourceGameId,
        CancellationToken cancellationToken)
    {
        var uri = new Uri(
            $"https://store.steampowered.com/api/appdetails?appids={sourceGameId}&l=english",
            UriKind.Absolute);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(StoreCatalogTimeout);
        try
        {
            var json = await _fetchStoreAppDetails(uri, timeout.Token).ConfigureAwait(false);
            return ParseStoreAchievementCatalog(json, sourceGameId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return SteamStoreAchievementCatalogStatus.Unavailable;
        }
    }

    internal static SteamStoreAchievementCatalogStatus ParseStoreAchievementCatalog(
        string? json,
        string sourceGameId)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxStoreCatalogBytes ||
            string.IsNullOrWhiteSpace(sourceGameId))
            return SteamStoreAchievementCatalogStatus.Unavailable;

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty(sourceGameId, out var envelope) ||
                envelope.ValueKind != JsonValueKind.Object ||
                !envelope.TryGetProperty("success", out var success) ||
                success.ValueKind != JsonValueKind.True ||
                !envelope.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Object)
                return SteamStoreAchievementCatalogStatus.Unavailable;

            if (data.TryGetProperty("achievements", out var achievements))
            {
                if (achievements.ValueKind != JsonValueKind.Object)
                    return SteamStoreAchievementCatalogStatus.Unavailable;
                var total = ReadInt(achievements, "total");
                if (total is null or < 0)
                    return SteamStoreAchievementCatalogStatus.Unavailable;
                if (total > 0) return SteamStoreAchievementCatalogStatus.NonZero;
            }

            // Absence of the achievements object is only a positive zero signal
            // when Steam also supplied a well-formed categories array that does
            // not advertise achievement support.
            if (!data.TryGetProperty("categories", out var categories) ||
                categories.ValueKind != JsonValueKind.Array)
                return SteamStoreAchievementCatalogStatus.Unavailable;
            foreach (var category in categories.EnumerateArray())
            {
                if (category.ValueKind != JsonValueKind.Object)
                    return SteamStoreAchievementCatalogStatus.Unavailable;
                var id = ReadInt(category, "id");
                var description = ReadText(category, "description", 128);
                if (id is null or < 0 || string.IsNullOrWhiteSpace(description))
                    return SteamStoreAchievementCatalogStatus.Unavailable;
                if (id == 22 ||
                    string.Equals(description,
                        "Steam Achievements", StringComparison.OrdinalIgnoreCase))
                    return SteamStoreAchievementCatalogStatus.Unavailable;
            }
            return SteamStoreAchievementCatalogStatus.ConfirmedZero;
        }
        catch (JsonException)
        {
            return SteamStoreAchievementCatalogStatus.Unavailable;
        }
    }

    private static bool IsUncorroboratedLocalZero(AchievementSnapshot snapshot) =>
        snapshot.Coverage == AchievementCoverageStatus.Unavailable &&
        snapshot.ReportedTotal == 0 &&
        snapshot.ReportedUnlocked == 0 &&
        snapshot.Entries.Count == 0;

    private static HttpClient CreateStoreCatalogClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ExoLauncher/1.0");
        return client;
    }

    private static async Task<string?> FetchStoreAppDetailsAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await StoreCatalogClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK ||
            response.Content.Headers.ContentLength is > MaxStoreCatalogBytes)
            return null;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (memory.Length + read > MaxStoreCatalogBytes) return null;
            await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return Encoding.UTF8.GetString(memory.ToArray());
    }

    internal static AchievementSnapshot ParseCommunitySnapshotXml(
        string? xml,
        string sourceGameId,
        string coverageKey,
        DateTimeOffset observedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(xml) || xml.Length > MaxPayloadBytes)
            return Unavailable(sourceGameId, "Steam Community returned no usable achievement data.", coverageKey, observedAtUtc);

        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxPayloadBytes,
                MaxCharactersFromEntities = 0,
            };
            using var stringReader = new StringReader(xml);
            using var reader = XmlReader.Create(stringReader, settings);
            var document = XDocument.Load(reader, LoadOptions.None);
            var entries = new Dictionary<string, AchievementEntry>(StringComparer.Ordinal);
            foreach (var row in document.Descendants().Where(element =>
                         string.Equals(element.Name.LocalName, "achievement", StringComparison.OrdinalIgnoreCase)))
            {
                if (entries.Count >= MaxAchievements) break;
                var externalId = XmlValue(row, "apiname") ?? XmlValue(row, "id");
                if (string.IsNullOrWhiteSpace(externalId) || externalId.Length > 512) continue;
                // Parse catalog fields defensively for validation only. Neither
                // `closed` nor a timestamp is accepted as account-progress
                // evidence by the result returned from this method.
                var unlocked = XmlBool(row, "achieved") ?? XmlAttributeBool(row, "closed") ?? false;
                var hidden = XmlBool(row, "hidden") ?? false;
                var name = XmlValue(row, "name");
                var description = XmlValue(row, "description") ?? string.Empty;
                if (hidden && !unlocked)
                {
                    name = "Hidden achievement";
                    description = string.Empty;
                }

                DateTimeOffset? unlockedAtUtc = null;
                if (unlocked && long.TryParse(XmlValue(row, "unlockTimestamp"),
                        NumberStyles.Integer, CultureInfo.InvariantCulture, out var unlockUnix) && unlockUnix > 0)
                {
                    try { unlockedAtUtc = DateTimeOffset.FromUnixTimeSeconds(unlockUnix); }
                    catch { /* invalid provider timestamp */ }
                }

                entries[externalId] = new AchievementEntry
                {
                    Definition = new AchievementDefinition
                    {
                        ProviderId = "steam",
                        SourceGameId = sourceGameId,
                        ExternalId = externalId,
                        Name = string.IsNullOrWhiteSpace(name)
                            ? (hidden ? "Hidden achievement" : externalId)
                            : name,
                        Description = description.Length <= 4_096 ? description : string.Empty,
                        Hidden = hidden,
                        IconUnlockedUrl = XmlHttpsUrl(row, "iconOpen") ?? XmlHttpsUrl(row, "icon"),
                        IconLockedUrl = XmlHttpsUrl(row, "iconClosed"),
                        GlobalUnlockPercent = XmlDouble(row, "percent"),
                    },
                    State = new AchievementState
                    {
                        ExternalId = externalId,
                        Unlocked = unlocked,
                        UnlockedAtUtc = unlockedAtUtc,
                        ObservedAtUtc = observedAtUtc,
                    },
                };
            }

            if (entries.Count == 0)
                return Unavailable(sourceGameId, "Steam Community returned no usable achievement data.", coverageKey, observedAtUtc);

            // This endpoint exposes an achievement catalog, but its XML is not
            // a signed-in account-progress API. In particular, `closed` is not
            // stable account unlock evidence. Keep this parser fail-closed so
            // callers cannot ever turn the catalog into a believable 0 / N.
            return Unavailable(sourceGameId,
                "Steam's public achievement catalog cannot verify this account's progress.",
                coverageKey,
                observedAtUtc);
        }
        catch (XmlException)
        {
            return Unavailable(sourceGameId, "Steam Community returned no usable achievement data.", coverageKey, observedAtUtc);
        }
    }

    private static string? XmlValue(XElement row, string localName)
    {
        var value = row.Elements().FirstOrDefault(element =>
            string.Equals(element.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase))?.Value.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static bool? XmlBool(XElement row, string localName)
    {
        var value = XmlValue(row, localName);
        if (bool.TryParse(value, out var parsed)) return parsed;
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric)
            ? numeric != 0
            : null;
    }

    private static bool? XmlAttributeBool(XElement row, string localName)
    {
        var value = row.Attributes().FirstOrDefault(attribute =>
            string.Equals(attribute.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase))?.Value.Trim();
        if (bool.TryParse(value, out var parsed)) return parsed;
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric)
            ? numeric != 0
            : null;
    }

    private static double? XmlDouble(XElement row, string localName)
    {
        return double.TryParse(XmlValue(row, localName), NumberStyles.Float,
                   CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
            ? value
            : null;
    }

    private static string? XmlHttpsUrl(XElement row, string localName)
    {
        var text = XmlValue(row, localName);
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
               string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? uri.AbsoluteUri
            : null;
    }

    private static bool TryGetSection(
        JsonElement root,
        string sectionName,
        out JsonElement section)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty(sectionName, out var property))
        {
            section = UnwrapData(property);
            return true;
        }

        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var pair in root.EnumerateArray())
            {
                if (pair.ValueKind != JsonValueKind.Array) continue;
                var values = pair.EnumerateArray();
                if (!values.MoveNext() || values.Current.ValueKind != JsonValueKind.String ||
                    !string.Equals(values.Current.GetString(), sectionName, StringComparison.OrdinalIgnoreCase) ||
                    !values.MoveNext())
                    continue;
                section = UnwrapData(values.Current);
                return true;
            }
        }

        section = default;
        return false;
    }

    private static JsonElement UnwrapData(JsonElement section)
    {
        if (section.ValueKind == JsonValueKind.Object &&
            section.TryGetProperty("data", out var data))
            return data;
        return section;
    }

    private static bool CollectAccountEntries(
        JsonElement achievementSection,
        IDictionary<string, AchievementEntry> entries,
        string sourceGameId,
        DateTimeOffset observedAtUtc)
    {
        // Steam has used these vector names across library-cache formats. Do
        // not recursively scrape the enclosing object: nearby metadata can
        // resemble an achievement row without being account state.
        foreach (var vectorName in new[]
                 {
                     "vecHighlight",
                     "vecUnachieved",
                     "vecAchievedHidden",
                     "vecAchieved",
                 })
        {
            if (!achievementSection.TryGetProperty(vectorName, out var vector)) continue;
            if (vector.ValueKind != JsonValueKind.Array) return false;
            foreach (var row in vector.EnumerateArray())
            {
                if (entries.Count >= MaxAchievements) return false;
                var entry = MapEntry(row, sourceGameId, observedAtUtc);
                if (entry is null) continue;
                if (!TryAddAccountEntry(entries, entry)) return false;
            }
        }
        return true;
    }

    private static bool TryAddAccountEntry(
        IDictionary<string, AchievementEntry> entries,
        AchievementEntry incoming)
    {
        var externalId = incoming.Definition.ExternalId;
        if (!entries.TryGetValue(externalId, out var existing))
        {
            entries[externalId] = incoming;
            return true;
        }

        // The same account row cannot report both locked and unlocked. Treat
        // that as bad source data instead of choosing a value or emitting an
        // achievement notification from it.
        if (existing.State.Unlocked != incoming.State.Unlocked) return false;
        entries[existing.Definition.ExternalId] = MergeAccountDuplicate(existing, incoming);
        return true;
    }

    private static void EnrichAchievementMapMetadata(
        JsonElement section,
        IDictionary<string, AchievementEntry> entries)
    {
        if (section.ValueKind == JsonValueKind.String)
        {
            var nestedJson = section.GetString();
            if (string.IsNullOrWhiteSpace(nestedJson) || nestedJson.Length > MaxPayloadBytes) return;
            try
            {
                using var nested = JsonDocument.Parse(nestedJson, new JsonDocumentOptions { MaxDepth = 64 });
                EnrichAchievementMapMetadata(nested.RootElement, entries, depth: 0);
            }
            catch (JsonException)
            {
                // Metadata is optional; a corrupt map cannot invalidate the
                // independently verified account vectors.
            }
            return;
        }

        EnrichAchievementMapMetadata(section, entries, depth: 0);
    }

    private static void EnrichAchievementMapMetadata(
        JsonElement element,
        IDictionary<string, AchievementEntry> entries,
        int depth)
    {
        if (depth > 32) return;
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    EnrichAchievementMapMetadata(item, entries, depth + 1);
                break;
            case JsonValueKind.Object:
                var externalId = ReadText(element, "strID", 512);
                if (!string.IsNullOrWhiteSpace(externalId) && entries.TryGetValue(externalId, out var existing))
                    entries[existing.Definition.ExternalId] = MergeMapMetadata(existing, element);
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals("strDescription") || property.NameEquals("strName")) continue;
                    EnrichAchievementMapMetadata(property.Value, entries, depth + 1);
                }
                break;
        }
    }

    private static AchievementEntry? MapEntry(
        JsonElement row,
        string sourceGameId,
        DateTimeOffset observedAtUtc)
    {
        var externalId = ReadText(row, "strID", 512);
        if (string.IsNullOrWhiteSpace(externalId)) return null;

        var hidden = ReadBool(row, "bHidden") ?? false;
        var unlocked = ReadBool(row, "bAchieved") ?? false;
        var name = ReadText(row, "strName", 512);
        var description = ReadText(row, "strDescription", 4_096) ?? string.Empty;
        if (hidden && !unlocked)
        {
            name = "Hidden achievement";
            description = string.Empty;
        }

        var current = ReadDouble(row, "flCurrentProgress");
        var target = ReadDouble(row, "flMaxProgress");
        if (current is < 0) current = null;
        if (target is <= 0) target = null;
        if (current is not null && target is not null)
            current = Math.Min(current.Value, target.Value);

        DateTimeOffset? unlockedAtUtc = null;
        var unlockedUnix = ReadLong(row, "rtUnlocked");
        if (unlocked && unlockedUnix is > 0)
        {
            try { unlockedAtUtc = DateTimeOffset.FromUnixTimeSeconds(unlockedUnix.Value); }
            catch { /* invalid provider timestamp */ }
        }

        return new AchievementEntry
        {
            Definition = new AchievementDefinition
            {
                ProviderId = "steam",
                SourceGameId = sourceGameId,
                ExternalId = externalId,
                Name = string.IsNullOrWhiteSpace(name) ? (hidden ? "Hidden achievement" : externalId) : name,
                Description = description,
                Hidden = hidden,
                IconUnlockedUrl = ReadHttpsUrl(row, "strImage"),
            },
            State = new AchievementState
            {
                ExternalId = externalId,
                Unlocked = unlocked,
                UnlockedAtUtc = unlockedAtUtc,
                ProgressCurrent = current,
                ProgressTarget = target,
                ObservedAtUtc = observedAtUtc,
            },
        };
    }

    private static AchievementEntry MergeAccountDuplicate(AchievementEntry existing, AchievementEntry incoming)
    {
        // The caller has already verified the two account rows agree on
        // unlocked state. Preserve that state while retaining richer optional
        // metadata when Steam repeats the same row in multiple vectors.
        var hidden = existing.Definition.Hidden || incoming.Definition.Hidden;
        return existing with
        {
            Definition = existing.Definition with
            {
                Name = hidden && !existing.State.Unlocked
                    ? "Hidden achievement"
                    : PreferredText(existing.Definition.Name, incoming.Definition.Name),
                Description = hidden && !existing.State.Unlocked
                    ? string.Empty
                    : PreferredText(existing.Definition.Description, incoming.Definition.Description),
                Hidden = hidden,
                IconUnlockedUrl = existing.Definition.IconUnlockedUrl ?? incoming.Definition.IconUnlockedUrl,
            },
            State = existing.State with
            {
                UnlockedAtUtc = existing.State.UnlockedAtUtc ?? incoming.State.UnlockedAtUtc,
                ProgressCurrent = Max(existing.State.ProgressCurrent, incoming.State.ProgressCurrent),
                ProgressTarget = Max(existing.State.ProgressTarget, incoming.State.ProgressTarget),
            },
        };
    }

    private static AchievementEntry MergeMapMetadata(AchievementEntry accountEntry, JsonElement mapRow)
    {
        var mapHidden = ReadBool(mapRow, "bHidden") ?? false;
        var hidden = accountEntry.Definition.Hidden || mapHidden;
        var mapName = ReadText(mapRow, "strName", 512) ?? string.Empty;
        var mapDescription = ReadText(mapRow, "strDescription", 4_096) ?? string.Empty;
        return accountEntry with
        {
            Definition = accountEntry.Definition with
            {
                Name = hidden && !accountEntry.State.Unlocked
                    ? "Hidden achievement"
                    : PreferredMapName(accountEntry.Definition.Name, mapName,
                        accountEntry.Definition.ExternalId),
                Description = hidden && !accountEntry.State.Unlocked
                    ? string.Empty
                    : PreferredText(accountEntry.Definition.Description, mapDescription),
                Hidden = hidden,
                IconUnlockedUrl = accountEntry.Definition.IconUnlockedUrl ?? ReadHttpsUrl(mapRow, "strImage"),
            },
            // The map is never account authority. In particular, ignore its
            // bAchieved, timestamps, and progress fields.
            State = accountEntry.State,
        };
    }

    private static string PreferredText(string preferred, string fallback) =>
        string.IsNullOrWhiteSpace(preferred) || preferred == "Hidden achievement"
            ? fallback
            : preferred;

    private static string PreferredMapName(string existing, string mapped, string externalId) =>
        string.IsNullOrWhiteSpace(mapped)
            ? existing
            : string.IsNullOrWhiteSpace(existing) || existing == "Hidden achievement" ||
              string.Equals(existing, externalId, StringComparison.Ordinal)
                ? mapped
                : existing;

    private static string? SourceGameId(GameEntry game)
    {
        var target = game.LaunchTarget?.Trim();
        var hasSteamId = game.Id.StartsWith("steam:", StringComparison.OrdinalIgnoreCase);
        var canonicalId = hasSteamId ? game.Id[6..].Trim() : null;
        if (hasSteamId && !IsSteamAppId(canonicalId)) return null;
        if (!string.IsNullOrWhiteSpace(target) && !IsSteamAppId(target)) return null;
        if (canonicalId is not null && !string.IsNullOrWhiteSpace(target) &&
            !string.Equals(canonicalId, target, StringComparison.Ordinal))
            return null;
        return canonicalId ?? (IsSteamAppId(target) ? target : null);
    }

    private static bool IsSteamAppId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 20 && value.All(char.IsDigit);

    private static string? ResolveSteamRoot()
    {
        var candidates = new List<string?>();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            candidates.Add(key?.GetValue("SteamPath") as string);
            candidates.Add(key?.GetValue("InstallPath") as string);
        }
        catch { /* registry is best-effort */ }
        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"));
        return candidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!.Replace('/', Path.DirectorySeparatorChar).TrimEnd('\\', '/'))
            .FirstOrDefault(Directory.Exists);
    }

    private static string? ResolveAccountId(string steamRoot)
    {
        string? registryAccount = null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
            var value = key?.GetValue("ActiveUser");
            uint account = value switch
            {
                int signed => unchecked((uint)signed),
                uint unsigned => unsigned,
                long wide when wide is > 0 and <= uint.MaxValue => (uint)wide,
                string text when uint.TryParse(text, NumberStyles.None,
                    CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => 0,
            };
            if (account > 0) registryAccount = account.ToString(CultureInfo.InvariantCulture);
        }
        catch { /* ambiguous account will fail closed below */ }
        return SteamPlaytime.ResolveActiveAccountId(steamRoot, registryAccount);
    }

    private bool IsCurrentAccount(string steamRoot, string expectedAccountId)
    {
        try
        {
            return string.Equals(_resolveAccountId(steamRoot), expectedAccountId,
                StringComparison.Ordinal);
        }
        catch
        {
            // If active-account resolution becomes unavailable, treating the
            // just-read cache as current would be an unsafe assumption.
            return false;
        }
    }

    private AchievementSnapshot Unavailable(string sourceGameId, string message, string? coverageKey = null) =>
        Unavailable(sourceGameId, message, coverageKey ?? "steam:unavailable", DateTimeOffset.UtcNow);

    private static AchievementSnapshot Unavailable(
        string sourceGameId,
        string message,
        string coverageKey,
        DateTimeOffset observedAtUtc) => new()
    {
        ProviderId = "steam",
        SourceGameId = sourceGameId,
        CoverageKey = coverageKey,
        Coverage = AchievementCoverageStatus.Unavailable,
        Capabilities = AchievementProviderCapabilities.Snapshot |
                       AchievementProviderCapabilities.Progress,
        ObservedAtUtc = observedAtUtc,
        Message = message,
    };

    private static string? ReadText(JsonElement element, string property, int maxLength)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
            return null;
        var text = value.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(text) || text.Length > maxLength ? null : text;
    }

    private static int? ReadInt(JsonElement element, string property)
    {
        var value = ReadLong(element, property);
        return value is >= int.MinValue and <= int.MaxValue ? (int)value.Value : null;
    }

    private static long? ReadLong(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        return value.ValueKind == JsonValueKind.String &&
               long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
            ? number
            : null;
    }

    private static double? ReadDouble(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number))
            return number;
        return value.ValueKind == JsonValueKind.String &&
               double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) &&
               double.IsFinite(number)
            ? number
            : null;
    }

    private static bool? ReadBool(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.GetBoolean();
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number != 0;
        if (value.ValueKind == JsonValueKind.String)
        {
            if (bool.TryParse(value.GetString(), out var parsed)) return parsed;
            if (int.TryParse(value.GetString(), out number)) return number != 0;
        }
        return null;
    }

    private static string? ReadHttpsUrl(JsonElement element, string property)
    {
        var text = ReadText(element, property, 2_048);
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
               string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? uri.AbsoluteUri
            : null;
    }

    private static double? Max(double? left, double? right) =>
        left is null ? right : right is null ? left : Math.Max(left.Value, right.Value);
}

internal enum SteamStoreAchievementCatalogStatus
{
    Unavailable,
    ConfirmedZero,
    NonZero,
}
