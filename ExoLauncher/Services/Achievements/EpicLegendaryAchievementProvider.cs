using System.Globalization;
using System.Text.Json;
using ExoLauncher.Adapters.Cli;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services.Achievements;

/// <summary>
/// Reads the signed-in Epic account through Legendary's supported achievements
/// command. The provider never receives or persists the OAuth token.
/// </summary>
public sealed class EpicLegendaryAchievementProvider : IAchievementProvider
{
    private const int MaxPayloadChars = 8 * 1024 * 1024;
    private const int MaxAchievements = 10_000;
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(20);

    private readonly Func<string?> _resolveExecutable;
    private readonly Func<string, IReadOnlyList<string>, CancellationToken,
        Task<(int ExitCode, string StdOut, string StdErr)>> _run;
    private readonly Func<string?> _resolveCoverageKey;

    public EpicLegendaryAchievementProvider()
        : this(
            ResolveLegendaryReadOnly,
            static (executable, args, ct) =>
                CliRunner.RunAsync(executable, args, null, null, ct),
            ResolveCoverageKey)
    {
    }

    internal EpicLegendaryAchievementProvider(
        Func<string?> resolveExecutable,
        Func<string, IReadOnlyList<string>, CancellationToken,
            Task<(int ExitCode, string StdOut, string StdErr)>> run,
        Func<string?> resolveCoverageKey)
    {
        _resolveExecutable = resolveExecutable;
        _run = run;
        _resolveCoverageKey = resolveCoverageKey;
    }

    public string Id => "epic";
    public StoreKind Store => StoreKind.Epic;
    public AchievementProviderCapabilities Capabilities =>
        AchievementProviderCapabilities.Snapshot |
        AchievementProviderCapabilities.Progress |
        AchievementProviderCapabilities.Rarity |
        AchievementProviderCapabilities.CompleteCatalog;

    public bool Supports(GameEntry game) =>
        game.Store == StoreKind.Epic &&
        (game.Id.StartsWith("epic:", StringComparison.OrdinalIgnoreCase) ||
         !string.IsNullOrWhiteSpace(game.LaunchTarget));

    public async Task<AchievementSnapshot> GetSnapshotAsync(
        GameEntry game,
        CancellationToken cancellationToken = default)
    {
        var sourceGameId = SourceGameId(game);
        if (game.Store != StoreKind.Epic)
            return Unavailable(sourceGameId ?? string.Empty, "This entry has no Epic artifact id.");
        if (string.IsNullOrWhiteSpace(sourceGameId))
            return Unavailable(string.Empty,
                HasArtifactTargetMismatch(game)
                    ? "Epic library id and launch target disagree."
                    : "This entry has no valid Epic artifact id.");

        var executable = _resolveExecutable();
        if (string.IsNullOrWhiteSpace(executable))
            return Unavailable(sourceGameId, "Legendary is not available.");

        var coverageKey = _resolveCoverageKey();
        if (string.IsNullOrWhiteSpace(coverageKey))
            return Unavailable(sourceGameId, "Legendary is not signed in to an Epic account.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(QueryTimeout);
        try
        {
            var args = new[] { "achievements", sourceGameId, "--hidden", "--json" };
            var (exitCode, stdout, _) = await _run(executable, args, timeout.Token)
                .ConfigureAwait(false);
            if (exitCode != 0)
                return Unavailable(sourceGameId, "Epic achievements are temporarily unavailable.", coverageKey);
            if (stdout.Length > MaxPayloadChars)
                return Unavailable(sourceGameId, "Epic returned an unexpectedly large achievement payload.", coverageKey);

            // Legendary's command may span an account switch. The account
            // provenance used for the CLI read must still be current before
            // its result can reach UI or durable notification state.
            if (!IsCurrentCoverageKey(coverageKey))
                return Unavailable(sourceGameId, "Epic account changed during achievement refresh.");

            return ParseSnapshotJson(stdout, sourceGameId, coverageKey, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Unavailable(sourceGameId, "Epic achievement sync timed out.", coverageKey);
        }
        catch
        {
            return Unavailable(sourceGameId, "Epic achievements are temporarily unavailable.", coverageKey);
        }
    }

    internal static AchievementSnapshot ParseSnapshotJson(
        string? json,
        string sourceGameId,
        string coverageKey,
        DateTimeOffset observedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxPayloadChars)
            return Unavailable(sourceGameId, "Epic returned no usable achievement data.", coverageKey, observedAtUtc);

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Unavailable(sourceGameId, "Epic returned no usable achievement data.", coverageKey, observedAtUtc);

            // Legendary may expose an achievement more than once across its
            // category vectors. Treat any duplicate (including case-only
            // differences) as ambiguous account data instead of silently
            // picking whichever row happened to be parsed last.
            var entries = new Dictionary<string, AchievementEntry>(StringComparer.OrdinalIgnoreCase);
            var categoriesSeen = false;
            foreach (var category in new[] { "completed", "in_progress", "uninitiated", "hidden" })
            {
                if (!root.TryGetProperty(category, out var rows) || rows.ValueKind != JsonValueKind.Array)
                    continue;
                categoriesSeen = true;
                foreach (var row in rows.EnumerateArray())
                {
                    if (entries.Count >= MaxAchievements) break;
                    var entry = MapEntry(row, sourceGameId, observedAtUtc,
                        category == "completed");
                    if (entry is not null)
                    {
                        if (!entries.TryAdd(entry.Definition.ExternalId, entry))
                            return Unavailable(sourceGameId,
                                "Epic returned duplicate achievement identities.", coverageKey, observedAtUtc);
                    }
                }
            }

            if (!categoriesSeen)
                return Unavailable(sourceGameId, "Epic returned no usable achievement data.", coverageKey, observedAtUtc);

            var hasReportedTotal = root.TryGetProperty("total_achievements", out _);
            var reportedTotal = ReadInt(root, "total_achievements");
            var hasReportedUnlocked = root.TryGetProperty("user_unlocked", out _);
            var reportedUnlocked = ReadInt(root, "user_unlocked");
            // Counts are account data, not catalog data. Do not infer either
            // one from the subset of rows Legendary returned: a partial page
            // could otherwise look like a precise achievement count.
            if (!hasReportedTotal || !hasReportedUnlocked)
                return Unavailable(sourceGameId,
                    "Epic did not report complete achievement totals.", coverageKey, observedAtUtc);
            if (reportedTotal is null || reportedUnlocked is null)
                return Unavailable(sourceGameId, "Epic returned contradictory achievement totals.", coverageKey, observedAtUtc);

            var total = reportedTotal.Value;
            var entryUnlocked = entries.Values.Count(row => row.State.Unlocked);
            var unlocked = reportedUnlocked.Value;
            if (total < 0 || total < entries.Count || unlocked < entryUnlocked || unlocked < 0 || unlocked > total)
                return Unavailable(sourceGameId, "Epic returned contradictory achievement totals.", coverageKey, observedAtUtc);
            var complete = total == 0 || entries.Count >= total;
            if (complete && unlocked != entryUnlocked)
                return Unavailable(sourceGameId, "Epic returned contradictory achievement totals.", coverageKey, observedAtUtc);
            var capabilities = AchievementProviderCapabilities.Snapshot |
                               AchievementProviderCapabilities.Progress |
                               AchievementProviderCapabilities.Rarity;
            if (complete) capabilities |= AchievementProviderCapabilities.CompleteCatalog;

            return new AchievementSnapshot
            {
                ProviderId = "epic",
                SourceGameId = sourceGameId,
                CoverageKey = coverageKey,
                Coverage = complete ? AchievementCoverageStatus.Complete : AchievementCoverageStatus.Partial,
                Capabilities = capabilities,
                ReportedTotal = total,
                ReportedUnlocked = unlocked,
                ObservedAtUtc = observedAtUtc,
                Entries = entries.Values.OrderBy(row => row.Definition.ExternalId, StringComparer.Ordinal).ToArray(),
                Message = complete ? null : "Epic returned only part of the achievement catalog.",
            };
        }
        catch (JsonException)
        {
            return Unavailable(sourceGameId, "Epic returned no usable achievement data.", coverageKey, observedAtUtc);
        }
    }

    private static AchievementEntry? MapEntry(
        JsonElement row,
        string sourceGameId,
        DateTimeOffset observedAtUtc,
        bool completedCategory)
    {
        if (row.ValueKind != JsonValueKind.Object) return null;
        var externalId = ReadText(row, "name", 512);
        if (string.IsNullOrWhiteSpace(externalId)) return null;

        var hidden = ReadBool(row, "hidden") ?? false;
        var unlocked = ReadBool(row, "unlocked") ?? completedCategory;
        var displayName = ReadText(row, "display_name", 512);
        var description = ReadText(row, "description", 4_096) ?? string.Empty;
        if (hidden && !unlocked)
        {
            displayName = "Hidden achievement";
            description = string.Empty;
        }

        var progress = ReadDouble(row, "progress");
        if (progress is not null) progress = Math.Clamp(progress.Value, 0, 100);
        var rarity = ReadDouble(row, "rarity");
        if (rarity is < 0 or > 100) rarity = null;
        var points = ReadInt(row, "xp");
        if (points is < 0) points = null;

        return new AchievementEntry
        {
            Definition = new AchievementDefinition
            {
                ProviderId = "epic",
                SourceGameId = sourceGameId,
                ExternalId = externalId,
                Name = string.IsNullOrWhiteSpace(displayName) ? externalId : displayName,
                Description = description,
                Hidden = hidden,
                IconUnlockedUrl = ReadHttpsUrl(row, "icon_link"),
                GlobalUnlockPercent = rarity,
                Points = points,
                Tier = ReadText(row, "tier", 64),
            },
            State = new AchievementState
            {
                ExternalId = externalId,
                Unlocked = unlocked,
                UnlockedAtUtc = unlocked ? ReadTimestamp(row, "unlock_date") : null,
                ProgressCurrent = progress,
                ProgressTarget = progress is null ? null : 100,
                ObservedAtUtc = observedAtUtc,
            },
        };
    }

    private AchievementSnapshot Unavailable(string sourceGameId, string message, string? coverageKey = null) =>
        Unavailable(sourceGameId, message, coverageKey ?? "epic:unavailable", DateTimeOffset.UtcNow);

    private static AchievementSnapshot Unavailable(
        string sourceGameId,
        string message,
        string coverageKey,
        DateTimeOffset observedAtUtc) => new()
    {
        ProviderId = "epic",
        SourceGameId = sourceGameId,
        CoverageKey = coverageKey,
        Coverage = AchievementCoverageStatus.Unavailable,
        Capabilities = AchievementProviderCapabilities.Snapshot |
                       AchievementProviderCapabilities.Progress |
                       AchievementProviderCapabilities.Rarity |
                       AchievementProviderCapabilities.CompleteCatalog,
        ObservedAtUtc = observedAtUtc,
        Message = message,
    };

    private static string? SourceGameId(GameEntry game)
    {
        if (!game.Id.StartsWith("epic:", StringComparison.OrdinalIgnoreCase)) return null;
        var artifact = game.Id[5..].Trim();
        if (string.IsNullOrWhiteSpace(artifact) || artifact.Length > 256 ||
            artifact.ContainsAny('\r', '\n', '\0'))
            return null;

        var target = game.LaunchTarget?.Trim();
        return !string.IsNullOrWhiteSpace(target) &&
               !string.Equals(target, artifact, StringComparison.Ordinal)
            ? null
            : artifact;
    }

    private static bool HasArtifactTargetMismatch(GameEntry game)
    {
        if (!game.Id.StartsWith("epic:", StringComparison.OrdinalIgnoreCase)) return false;
        var artifact = game.Id[5..].Trim();
        var target = game.LaunchTarget?.Trim();
        return !string.IsNullOrWhiteSpace(artifact) && !string.IsNullOrWhiteSpace(target) &&
               !string.Equals(target, artifact, StringComparison.Ordinal);
    }

    private static string? ResolveCoverageKey()
    {
        var path = ResolveLegendaryUserPath();
        if (path is null) return null;
        try
        {
            var info = new FileInfo(path);
            if (info.Length is <= 0 or > 1024 * 1024) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 16 });
            var accountId = ReadText(document.RootElement, "account_id", 128);
            return string.IsNullOrWhiteSpace(accountId)
                ? null
                : AchievementCoverageKeys.FromAccount("epic", accountId);
        }
        catch
        {
            return null;
        }
    }

    private bool IsCurrentCoverageKey(string expectedCoverageKey)
    {
        try
        {
            return string.Equals(_resolveCoverageKey(), expectedCoverageKey,
                StringComparison.Ordinal);
        }
        catch
        {
            // A failed post-read check cannot safely certify the account that
            // produced the CLI response.
            return false;
        }
    }

    /// <summary>Resolution for enrichment must be read-only: never download or delete a tool.</summary>
    private static string? ResolveLegendaryReadOnly()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var candidate in new[]
                 {
                     Path.Combine(PathHelper.AppDataDir, "tools", "legendary.exe"),
                     Path.Combine(PathHelper.AppDirectory, "tools", "legendary.exe"),
                     CliRunner.ResolveOnPath("legendary.exe"),
                     CliRunner.ResolveOnPath("legendary"),
                     Path.Combine(local, "legendary", "legendary.exe"),
                 })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && IsAmd64Pe(candidate)) return candidate;
        }
        return null;
    }

    private static bool IsAmd64Pe(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is < 512 or > 512L * 1024 * 1024) return false;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            using var reader = new BinaryReader(stream);
            if (reader.ReadUInt16() != 0x5A4D) return false; // MZ
            stream.Position = 0x3C;
            var peOffset = reader.ReadInt32();
            if (peOffset is < 64 || peOffset > info.Length - 6) return false;
            stream.Position = peOffset;
            return reader.ReadUInt32() == 0x00004550 && reader.ReadUInt16() == 0x8664;
        }
        catch
        {
            return false;
        }
    }

    private static string? ResolveLegendaryUserPath()
    {
        var custom = Environment.GetEnvironmentVariable("LEGENDARY_CONFIG_PATH");
        if (!string.IsNullOrWhiteSpace(custom))
        {
            var candidate = Path.Combine(custom, "user.json");
            if (File.Exists(candidate)) return candidate;
        }
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrWhiteSpace(xdg))
        {
            var candidate = Path.Combine(xdg, "legendary", "user.json");
            if (File.Exists(candidate)) return candidate;
        }
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(profile)) return null;
        var fallback = Path.Combine(profile, ".config", "legendary", "user.json");
        return File.Exists(fallback) ? fallback : null;
    }

    private static string? ReadText(JsonElement element, string property, int maxLength)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.String)
            return null;
        var text = value.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(text) || text.Length > maxLength ? null : text;
    }

    private static int? ReadInt(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        return value.ValueKind == JsonValueKind.String &&
               int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
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
        if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed)) return parsed;
        return null;
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
            return timestamp;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var unix) && unix > 0)
        {
            try { return DateTimeOffset.FromUnixTimeSeconds(unix); }
            catch { return null; }
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
}
