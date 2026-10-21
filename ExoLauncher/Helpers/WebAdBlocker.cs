using Microsoft.UI.Dispatching;
using Microsoft.Web.WebView2.Core;

namespace ExoLauncher.Helpers;

internal static class WebAdBlocker
{
    internal const string Version = "1.74.0";
    private sealed record SurfaceContext(nint Owner, DispatcherQueue Dispatcher);
    private sealed class Registration(string key, string name)
    {
        internal readonly string Key = key;
        internal readonly string Name = name;
        internal readonly WebAdBlockTargets<CoreWebView2, SurfaceContext> Targets = new();
        internal readonly SemaphoreSlim Operation = new(1, 1);
        internal string? ExtensionId, InstallSignature, ProfileSignature, ConfiguredSignature;
        internal bool NeedsConfiguration, Configuring, Retrying;
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Registration> Profiles = new(StringComparer.Ordinal);

    internal static async Task AttachAsync(CoreWebView2 core, string name, nint owner = 0)
    {
        try
        {
            var key = WebAdBlockPolicy.ProfileKey(core.Profile.ProfilePath, core.Profile.IsInPrivateModeEnabled);
            var context = new SurfaceContext(owner, DispatcherQueue.GetForCurrentThread()
                ?? throw new InvalidOperationException("The browser UI queue is unavailable."));
            Registration registration;
            lock (Gate)
            {
                if (!Profiles.TryGetValue(key, out registration!)) Profiles[key] = registration = new(key, name);
                registration.Targets.Add(core, context);
            }
            await EnsureProfileAsync(registration);
        }
        catch (Exception error) { AppLog.Warn("uBlock Origin could not attach to " + name + ": " + error.Message); }
    }

    internal static void Detach(CoreWebView2? core)
    {
        if (core is null) return;
        lock (Gate)
            foreach (var registration in Profiles.Values) registration.Targets.Forget(core);
    }

    private static string Marker(Registration registration, string suffix) =>
        Path.Combine(PathHelper.AppDataDir, "adblock", registration.Key + suffix);

    private static string? ReadMarker(string path)
    {
        try { return File.ReadAllText(path); }
        catch { return null; }
    }

    private static void WriteMarker(string path, string value)
    {
        // Markers avoid repeat work; extension storage remains authoritative.
        // A read-only cache must never stop the user's global on/off setting.
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, value);
        }
        catch (Exception error) { AppLog.Warn("uBlock cache marker could not be saved: " + error.Message); }
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
                    // A closed view can retain its managed wrapper. Use a surviving
                    // popup in that same profile when its parent has closed.
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

    private static async Task<bool> EnsureProfileAsync(Registration registration, bool retry = true)
    {
        var succeeded = false;
        await registration.Operation.WaitAsync();
        try
        {
            var signature = WebAdBlockPolicy.InstallSignature(Version,
                Path.Combine(PathHelper.AppDirectory, "extensions", "ublock-origin"));
            var extensionId = ReadMarker(Marker(registration, ".extid"));
            var profileSignature = extensionId is null ? null
                : WebAdBlockPolicy.ProfileSignature(signature, WebAdBlockProfile.Revision, extensionId);
            // Markers prove the bundled extension is installed and configured
            // for this profile. A warm attach then costs two file reads instead
            // of a browser extension round-trip.
            var warm = extensionId is not null &&
                string.Equals(ReadMarker(Marker(registration, ".version")), signature, StringComparison.Ordinal) &&
                string.Equals(ReadMarker(Marker(registration, ".profile")), profileSignature, StringComparison.Ordinal);
            if (warm)
            {
                lock (Gate)
                {
                    registration.ExtensionId = extensionId;
                    registration.InstallSignature = signature;
                    registration.ProfileSignature = profileSignature;
                    registration.ConfiguredSignature = profileSignature;
                    registration.NeedsConfiguration = false;
                }
                succeeded = true;
            }
            else
            {
                succeeded = await WithLiveCoreAsync(registration, async (core, _) =>
                {
                var folder = Path.Combine(PathHelper.AppDirectory, "extensions", "ublock-origin");
                var installed = await core.Profile.GetBrowserExtensionsAsync();
                var extension = installed.FirstOrDefault(item => item.Name == "uBlock Origin");
                var needsInstall = extension is null || (registration.InstallSignature != signature &&
                    ReadMarker(Marker(registration, ".version")) != signature);
                var reinstalled = false;
                lock (Gate) registration.ExtensionId = extension?.Id;
                extension = await WebAdBlockPolicy.MaintainExtensionAsync(extension, needsInstall,
                    () => true,
                    async () =>
                    {
                        if (!File.Exists(Path.Combine(folder, "manifest.json")))
                            throw new FileNotFoundException("The bundled uBlock Origin files are missing.");
                        var added = await core.Profile.AddBrowserExtensionAsync(folder);
                        reinstalled = true;
                        // Reinstallation can reset storage without changing the ID.
                        WriteMarker(Marker(registration, ".profile"), "");
                        WriteMarker(Marker(registration, ".version"), signature);
                        return added;
                    },
                    async (current, enabled) => { if (current.IsEnabled != enabled) await current.EnableAsync(enabled); });
                if (extension is null)
                {
                    lock (Gate) registration.NeedsConfiguration = true;
                    return;
                }
                WriteMarker(Marker(registration, ".extid"), extension.Id);
                var newProfileSignature = WebAdBlockPolicy.ProfileSignature(signature, WebAdBlockProfile.Revision, extension.Id);
                lock (Gate)
                {
                    registration.ExtensionId = extension.Id;
                    if (reinstalled || !needsInstall) registration.InstallSignature = signature;
                    if (reinstalled) registration.ConfiguredSignature = null;
                    registration.ProfileSignature = newProfileSignature;
                    registration.NeedsConfiguration = reinstalled || (registration.ConfiguredSignature != newProfileSignature &&
                        ReadMarker(Marker(registration, ".profile")) != newProfileSignature);
                }
            });
            }
        }
        catch (Exception error)
        {
            AppLog.Warn("uBlock Origin unavailable for " + registration.Name + ": " + error.Message);
        }
        finally { registration.Operation.Release(); }

        if (succeeded) StartConfiguration(registration);
        else if (retry && registration.Targets.Snapshot().Length > 0) ScheduleRetry(registration);
        return succeeded;
    }

    private static void StartConfiguration(Registration registration)
    {
        lock (Gate)
        {
            if (!registration.NeedsConfiguration || registration.Configuring ||
                !registration.Targets.Snapshot().Any(target => target.Context.Owner != 0)) return;
            registration.Configuring = true;
        }
        _ = ConfigureProfileAsync(registration);
    }

    private static async Task ConfigureProfileAsync(Registration registration)
    {
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (attempt > 0) await Task.Delay(TimeSpan.FromSeconds(attempt * 2));
                string? extensionId, signature;
                lock (Gate) { extensionId = registration.ExtensionId; signature = registration.ProfileSignature; }
                if (extensionId is null || signature is null) return;
                var applied = false;
                try
                {
                    if (!await WithLiveCoreAsync(registration, async (core, context) =>
                        { applied = await WebAdBlockProfile.ApplyAsync(core, extensionId, context.Owner); }, requireOwner: true)) return;
                    lock (Gate)
                    {
                        if (applied &&
                            registration.ExtensionId == extensionId && registration.ProfileSignature == signature)
                        {
                            WriteMarker(Marker(registration, ".profile"), signature);
                            registration.ConfiguredSignature = signature;
                            registration.NeedsConfiguration = false;
                            AppLog.Info("Automatic uBlock protection configured for " + registration.Name + ".");
                            return;
                        }
                    }
                }
                catch (Exception error) { AppLog.Warn("uBlock configuration failed for " + registration.Name + ": " + error.Message); }
            }
        }
        finally { lock (Gate) registration.Configuring = false; }
    }

    private static void ScheduleRetry(Registration registration)
    {
        lock (Gate) { if (registration.Retrying) return; registration.Retrying = true; }
        _ = RetryAsync();
        async Task RetryAsync()
        {
            try
            {
                for (var attempt = 1; attempt <= 2; attempt++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(attempt * 2));
                    if (registration.Targets.Snapshot().Length == 0 || await EnsureProfileAsync(registration, retry: false)) return;
                }
            }
            catch (Exception error) { AppLog.Warn("uBlock retry failed: " + error.Message); }
            finally { lock (Gate) registration.Retrying = false; }
        }
    }

}
