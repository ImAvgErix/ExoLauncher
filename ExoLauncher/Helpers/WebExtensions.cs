using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.Web.WebView2.Core;

namespace ExoLauncher.Helpers;

/// <summary>Pinned, hash-verified provider extensions installed once into the
/// shared embedded profile (the YouTube Music and Apple Music players) — never into
/// sign-in or shell profiles. uBlock Origin Lite blocks YouTube Music's ads.</summary>
internal static class WebExtensions
{
    internal sealed record Entry(string Key, string Title, string Version, string Folder, string ManifestSha256,
        string DisplayName);

    internal static readonly Entry[] Entries =
    [
        new("sponsorblock", "SponsorBlock", "6.1.7", "sponsorblock",
            "47e23e0e1fdf19eec2c55d4ccefa5ae563c132181ad72d8fe40701617cc4d2f7", "SponsorBlock"),
        new("better-lyrics", "Better Lyrics", "2.3.3", "better-lyrics",
            "454a2ae1ec285033dbed8f736d4a2834b9bffccd413bb0100320a3ca972ddf34", "Better Lyrics"),
        new("ublock-origin-lite", "uBlock Origin Lite", "2026.1018.2229", "ublock-origin-lite",
            "036e7b044dc7b4f8c7cf158271c977c1fd3438c4f4c4f2531354db597b116ac1", "uBlock Origin Lite"),
    ];

    private sealed record SurfaceContext(nint Owner, DispatcherQueue Dispatcher);
    private sealed class Registration(string key)
    {
        internal readonly string Key = key;
        internal readonly Targets Targets = new();
        internal readonly SemaphoreSlim Operation = new(1, 1);
        internal readonly HashSet<string> Configured = new(StringComparer.Ordinal);
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Registration> Profiles = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, bool> VerifiedHashes = new(StringComparer.Ordinal);

    internal static async Task AttachAsync(CoreWebView2 core, string name, nint owner = 0)
    {
        try
        {
            var profile = core.Profile;
            if (profile.ProfileName != WebViewHostProfile.EmbeddedProfileName || profile.IsInPrivateModeEnabled) return;
            var key = ProfileKey(profile.ProfilePath);
            var context = new SurfaceContext(owner, DispatcherQueue.GetForCurrentThread()
                ?? throw new InvalidOperationException("The browser UI queue is unavailable."));
            Registration registration;
            lock (Gate)
            {
                if (!Profiles.TryGetValue(key, out registration!)) Profiles[key] = registration = new(key);
                registration.Targets.Add(core, context);
            }
            await PruneAsync(core, "embedded");
            await EnsureProfileAsync(registration);
        }
        catch (Exception error) { AppLog.Warn("Provider extensions could not attach to " + name + ": " + error.Message); }
    }

    // Extensions Exo shipped once and no longer does; their install markers
    // may still hold the id they were given.
    private static readonly string[] Retired = ["equicord"];
    // uBlock Origin (Manifest V2, which Edge's engine is retiring) went with 3.14, for uBlock Origin Lite. It kept
    // its own markers under adblock; by name it is exactly "uBlock Origin" (Lite's name goes on).
    private const string RetiredUBlock = "uBlock Origin";

    /// Installed extensions persist in a profile and each runs its own
    /// renderer whenever the profile loads. The shell must carry none (older
    /// builds attached uBlock there). The embedded profile loses only what
    /// Exo retired: a shipped extension's reported name can be its raw
    /// manifest key (SponsorBlock's __MSG_fullName__) or the localized one,
    /// so it is never judged by name. Runs on the UI thread that owns the core.
    internal static async Task PruneAsync(CoreWebView2 core, string surface)
    {
        try
        {
            var embedded = core.Profile.ProfileName == WebViewHostProfile.EmbeddedProfileName;
            var key = ProfileKey(core.Profile.ProfilePath);
            var retiredIds = Retired.Select(retired => ReadMarker(Marker(key, retired, ".extid"))).OfType<string>().ToHashSet();
            foreach (var extension in await core.Profile.GetBrowserExtensionsAsync())
            {
                var name = extension.Name ?? "";
                if (name.StartsWith("Microsoft", StringComparison.Ordinal)) continue;
                if (embedded && !name.StartsWith("Equicord", StringComparison.OrdinalIgnoreCase) &&
                    !retiredIds.Contains(extension.Id) && name != RetiredUBlock) continue;
                // A shipped extension removed here reinstalls on the next
                // attach only once its warm markers are gone.
                var shipped = Entries.Where(entry => Matches(entry, extension,
                    ReadMarker(Marker(key, entry.Key, ".extid")))).ToArray();
                await extension.RemoveAsync();
                AppLog.Info("Removed stale extension '" + (name.Length == 0 ? extension.Id : name) + "' from the " + surface + " profile.");
                foreach (var entry in shipped) ForgetInstall(key, entry.Key);
                if (retiredIds.Contains(extension.Id))
                    foreach (var retired in Retired) ForgetInstall(key, retired);
                if (name == RetiredUBlock)
                    try { Directory.Delete(Path.Combine(PathHelper.AppDataDir, "adblock"), true); } catch { }
            }
        }
        catch (Exception error) { AppLog.Warn("Stale extensions could not be removed from the " + surface + " profile: " + error.Message); }
    }

    internal static void Detach(CoreWebView2? core)
    {
        if (core is null) return;
        lock (Gate)
            foreach (var registration in Profiles.Values) registration.Targets.Forget(core);
    }

    // A profile's folder as a key for its marker files (the formula older builds used, so their markers still match).
    private static string ProfileKey(string profilePath) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.GetFullPath(profilePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .ToUpperInvariant() + "|False")));

    private static string Marker(Registration registration, Entry entry, string suffix) =>
        Marker(registration.Key, entry.Key, suffix);

    private static string Marker(string profileKey, string entryKey, string suffix) =>
        Path.Combine(PathHelper.AppDataDir, "extensions", profileKey + "." + entryKey + suffix);

    private static void ForgetInstall(string profileKey, string entryKey)
    {
        foreach (var suffix in new[] { ".extid", ".version", ".config" })
            DeleteMarker(Marker(profileKey, entryKey, suffix));
    }

    private static string? ReadMarker(string path)
    {
        try { return File.ReadAllText(path); }
        catch { return null; }
    }

    private static void WriteMarker(string path, string value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, value);
        }
        catch (Exception error) { AppLog.Warn("Extension cache marker could not be saved: " + error.Message); }
    }

    private static void DeleteMarker(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private static string Folder(Entry entry) =>
        Path.Combine(PathHelper.AppDirectory, "Assets", "Extensions", entry.Folder);

    private static bool Verified(Entry entry)
    {
        lock (Gate)
        {
            if (VerifiedHashes.TryGetValue(entry.Key, out var result)) return result;
            bool ok;
            try { ok = WebExtensionManifest.ComputeManifestHash(Folder(entry)) == entry.ManifestSha256; }
            catch { ok = false; }
            // Only a pass is remembered: files repaired on disk verify on the next attach.
            if (!ok) AppLog.Warn(entry.Title + " extension files failed hash verification; not installing.");
            else VerifiedHashes[entry.Key] = true;
            return ok;
        }
    }

    private static bool Matches(Entry entry, CoreWebView2BrowserExtension extension, string? storedId) =>
        storedId is not null && extension.Id == storedId ||
        extension.Name.StartsWith(entry.DisplayName, StringComparison.Ordinal);

    private static string Signature(Entry entry) => entry.Version + "|" + entry.ManifestSha256;

    private static async Task EnsureProfileAsync(Registration registration)
    {
        await registration.Operation.WaitAsync();
        try
        {
            foreach (var entry in Entries)
            {
                var storedId = ReadMarker(Marker(registration, entry, ".extid"));
                var signature = Signature(entry);
                var warm = storedId is not null &&
                    string.Equals(ReadMarker(Marker(registration, entry, ".version")), signature, StringComparison.Ordinal);
                if (warm) { _ = StartConfiguration(registration, entry, storedId!); continue; }
                await WithLiveCoreAsync(registration, async (core, context) =>
                {
                    var installed = (await core.Profile.GetBrowserExtensionsAsync())
                        .FirstOrDefault(item => Matches(entry, item, storedId));
                    if (!Verified(entry)) return;
                    var folder = Folder(entry);
                    if (!File.Exists(Path.Combine(folder, "manifest.json")))
                    {
                        AppLog.Warn(entry.Title + " extension files are missing; not installing.");
                        return;
                    }
                    if (installed is not null && ReadMarker(Marker(registration, entry, ".version")) != signature)
                    {
                        try { await installed.RemoveAsync(); } catch { }
                        installed = null;
                        ForgetInstall(registration.Key, entry.Key);
                    }
                    if (installed is null)
                    {
                        try
                        {
                            installed = await core.Profile.AddBrowserExtensionAsync(folder);
                            AppLog.Info(entry.Title + " extension installed on the embedded profile.");
                        }
                        catch (Exception error)
                        {
                            AppLog.Warn(entry.Title + " extension could not load in WebView2: " + error.Message);
                            return;
                        }
                    }
                    if (!installed.IsEnabled) await installed.EnableAsync(true);
                    WriteMarker(Marker(registration, entry, ".extid"), installed.Id);
                    WriteMarker(Marker(registration, entry, ".version"), signature);
                    _ = StartConfiguration(registration, entry, installed.Id);
                });
            }
        }
        catch (Exception error) { AppLog.Warn("Provider extensions unavailable: " + error.Message); }
        finally { registration.Operation.Release(); }
    }

    private static async Task<bool> WithLiveCoreAsync(Registration registration, Func<CoreWebView2, SurfaceContext, Task> action,
        bool requireOwner = false)
    {
        foreach (var (core, context) in registration.Targets.Snapshot().Reverse())
        {
            if (requireOwner && context.Owner == 0) continue;
            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            async void Run()
            {
                try
                {
                    try { _ = core.Profile.ProfileName; }
                    catch { registration.Targets.Forget(core); completed.TrySetResult(false); return; }
                    await action(core, context);
                    completed.TrySetResult(true);
                }
                catch (Exception error) { completed.TrySetException(error); }
            }
            if (context.Dispatcher.HasThreadAccess) Run();
            else if (!context.Dispatcher.TryEnqueue(Run)) { registration.Targets.Forget(core); continue; }
            if (await completed.Task) return true;
        }
        return false;
    }

    private static async Task StartConfiguration(Registration registration, Entry entry, string extensionId)
    {
        var signature = Signature(entry) + "|" + WebExtensionProfile.Revision + "|" + extensionId;
        lock (Gate)
        {
            if (registration.Configured.Contains(entry.Key) ||
                string.Equals(ReadMarker(Marker(registration, entry, ".config")), signature, StringComparison.Ordinal))
                return;
            registration.Configured.Add(entry.Key);
        }
        try
        {
            var applied = false;
            if (!await WithLiveCoreAsync(registration, async (core, context) =>
                { applied = await WebExtensionProfile.ApplyAsync(core, entry, extensionId, context.Owner); },
                requireOwner: true)) return;
            if (applied)
            {
                WriteMarker(Marker(registration, entry, ".config"), signature);
                AppLog.Info(entry.Title + " extension configured for the embedded profile.");
            }
            else lock (Gate) registration.Configured.Remove(entry.Key);
        }
        catch { lock (Gate) registration.Configured.Remove(entry.Key); }
    }

    /// <summary>All views sharing one real browser profile, without retaining their lifetime.</summary>
    private sealed class Targets
    {
        private readonly object _gate = new();
        private readonly List<(WeakReference<CoreWebView2> Core, SurfaceContext Context)> _targets = [];

        internal void Add(CoreWebView2 core, SurfaceContext context)
        {
            lock (_gate)
            {
                _targets.RemoveAll(target => !target.Core.TryGetTarget(out var existing) || ReferenceEquals(existing, core));
                _targets.Add((new(core), context));
            }
        }

        internal void Forget(CoreWebView2 core)
        {
            lock (_gate)
                _targets.RemoveAll(target => !target.Core.TryGetTarget(out var existing) || ReferenceEquals(existing, core));
        }

        internal (CoreWebView2 Core, SurfaceContext Context)[] Snapshot()
        {
            lock (_gate)
            {
                var result = new List<(CoreWebView2, SurfaceContext)>();
                _targets.RemoveAll(target => !target.Core.TryGetTarget(out _));
                foreach (var target in _targets)
                    if (target.Core.TryGetTarget(out var core)) result.Add((core, target.Context));
                return result.ToArray();
            }
        }
    }
}
