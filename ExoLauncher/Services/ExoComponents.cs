using System.Collections.Concurrent;
using ExoLauncher.Adapters;
using ExoLauncher.Helpers;
using ExoLauncher.Services.DiscordClient.Voice;

namespace ExoLauncher.Services;

/// <summary>A component's fetch moving on: bytes done of its download, or of what it unpacks to.</summary>
internal delegate void ComponentProgress(long done, bool unpacking);

/// <summary>
/// One part Exo fetches beside itself: a fixed version, pinned by size and SHA-256 where its code lives.
/// </summary>
/// <param name="Name">What it is, as setup shows it ("Parakeet speech model").</param>
/// <param name="Feature">What waits on it, for the notice when it can't be fetched ("Dictation").</param>
/// <param name="Download">Its download, in bytes.</param>
/// <param name="Unpack">What its download unpacks to, in bytes; 0 when it is used as it comes.</param>
/// <param name="Ready">Whether it is here and whole (verified against its pins).</param>
/// <param name="Fetch">Fetches it, verified before it is kept.</param>
/// <param name="Location">The file or folder it is kept in.</param>
internal sealed record ExoComponent(string Name, string Feature, long Download, long Unpack, Func<bool> Ready,
    Func<ComponentProgress, CancellationToken, Task> Fetch, Func<string> Location)
{
    /// <summary>A single pinned release file used as it comes (a store tool, a model), kept at <paramref name="path"/>.</summary>
    internal static ExoComponent Pinned(string name, string feature, GitHubReleaseAsset asset, Func<string> path, Func<string, bool> validate) =>
        new(name, feature, asset.ExpectedSize, 0,
            () => PinnedToolCache.IsPinnedAsset(asset, path(), validate),
            (progress, ct) => VerifiedGitHubReleaseDownloader.Shared.DownloadPinnedAsync(asset, path(), validate, ct, done => progress(done, unpacking: false)),
            path);
}

/// <summary>
/// Everything Exo fetches beside itself, in one list: the store tools (Legendary, gogdl, Nile), Exo's noise suppression
/// (DeepFilterNet), Only My Voice's voice model (TitaNet) and dictation's speech model (Parakeet). Each is pinned where its
/// code lives; this list only names them. Exo's setup fetches them all while it installs (ExoLauncher.exe
/// --fetch-components, which setup/src/main.cpp runs and shows as a measured step), so every feature is ready the first
/// time it's used. What didn't come (offline, a failed step, an update whose pin changed, a build run from source) is
/// fetched quietly after Exo starts, never while downloads are held (outside the download hours, or a game playing with
/// Pause downloads while playing on), and only what is missing or no longer matches its pin: an update fetches nothing
/// that is here and whole. A part that fails twice is said once ("Dictation couldn't be set up"); Exo keeps trying.
/// A feature that needs its part sooner still fetches it itself, silently. Uninstalling Exo removes them all.
/// </summary>
internal static class ExoComponents
{
    internal static IReadOnlyList<ExoComponent> All { get; } =
    [
        EpicAdapter.Component,
        GogAdapter.Component,
        AmazonAdapter.Component,
        NoiseSuppressorDownload.Component,
        SpeakerModel.Component,
        DictationModel.Component,
    ];

    /// <summary>A component's fetch moved on (bytes done, whether unpacking), from whoever started it.</summary>
    internal static event Action<ExoComponent, long, bool>? Progress;

    /// <summary>A component was fetched and verified: its feature is ready.</summary>
    internal static event Action<ExoComponent>? Fetched;

    /// <summary>A component failed to fetch twice in a row: its feature, said once in the notice stack.</summary>
    internal static event Action<string>? FailedTwice;

    private static readonly ConcurrentDictionary<ExoComponent, Lazy<Task>> Fetching = new();

    /// <summary>
    /// Fetches the component unless it's here and whole; one fetch at a time per component, which every caller shares
    /// (setup's, the quiet one after start, a feature's own). Throws when it didn't come.
    /// </summary>
    internal static Task FetchAsync(ExoComponent component, CancellationToken ct)
    {
        Lazy<Task>? mine = null;
        mine = new Lazy<Task>(() => Task.Run(async () =>
        {
            try
            {
                if (component.Ready()) return;
                await component.Fetch((done, unpacking) => Progress?.Invoke(component, done, unpacking), CancellationToken.None).ConfigureAwait(false);
                if (!component.Ready()) throw new InvalidDataException(component.Name + " didn't verify after it was fetched.");
                AppLog.Info($"Components: {component.Name} fetched.");
                Fetched?.Invoke(component);
            }
            finally { Fetching.TryRemove(new KeyValuePair<ExoComponent, Lazy<Task>>(component, mine!)); }
        }));
        return Fetching.GetOrAdd(component, mine).Value.WaitAsync(ct);
    }

    /// <summary>Megabytes as setup words them (1 MB = 1,048,576 bytes, as Windows counts).</summary>
    internal static string Megabytes(long bytes) => Math.Round(bytes / 1048576.0).ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>What setup says while a component comes: "Parakeet speech model · 210 of 465 MB", "Unpacking Parakeet speech model · …".</summary>
    internal static string Words(ExoComponent component, long done, bool unpacking) => unpacking
        ? $"Unpacking {component.Name} · {Megabytes(done)} of {Megabytes(component.Unpack)} MB"
        : $"{component.Name} · {Megabytes(done)} of {Megabytes(component.Download)} MB";

    /// <summary>
    /// ExoLauncher.exe --fetch-components [--progress=file]: what setup runs once Exo is installed. Fetches each component
    /// that isn't here and whole, and appends "done&lt;tab&gt;total&lt;tab&gt;words" lines to the progress file (bytes over every
    /// fetch's download and unpacking together), which setup's window shows as its measured bar. 0 when all are ready;
    /// 1 when one didn't come (setup finishes anyway: Exo fetches it after it starts).
    /// </summary>
    internal static int RunFromSetup(string[] args)
    {
        var file = args.FirstOrDefault(a => a.StartsWith("--progress=", StringComparison.OrdinalIgnoreCase))?["--progress=".Length..].Trim('"');
        var missing = All.Where(c => !c.Ready()).ToList();
        var total = missing.Sum(c => c.Download + c.Unpack);
        long before = 0;
        var shown = 0L;
        void Write(long done, string words)
        {
            if (string.IsNullOrEmpty(file)) return;
            try { File.AppendAllText(file, $"{done}\t{total}\t{words}\r\n"); }
            catch (IOException) { }
        }
        void OnProgress(ExoComponent c, long done, bool unpacking)
        {
            var at = before + (unpacking ? c.Download : 0) + done;
            // About 200 lines for the whole step: setup reads only the last one.
            if (at - shown < total / 200 && done != (unpacking ? c.Unpack : c.Download)) return;
            shown = at;
            Write(at, Words(c, done, unpacking));
        }
        Progress += OnProgress;
        var failed = 0;
        try
        {
            foreach (var component in missing)
            {
                Write(before, Words(component, 0, false));
                try { FetchAsync(component, CancellationToken.None).GetAwaiter().GetResult(); }
                catch (Exception ex)
                {
                    failed++;
                    AppLog.Info($"Components: setup couldn't fetch {component.Name} ({ex.GetType().Name}); Exo fetches it after it starts.");
                }
                before += component.Download + component.Unpack;
            }
        }
        finally { Progress -= OnProgress; }
        Write(total, failed == 0 ? "Ready" : "Exo finishes the rest after it starts");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// After Exo starts: fetches what is missing or no longer matches its pin, quietly, once downloads aren't held
    /// (<paramref name="held"/>: the download queue's hold reason). A component that fails is tried again in 2 minutes, then
    /// every 30; its second failure in a row raises <see cref="FailedTwice"/> once.
    /// </summary>
    internal static void FillInBackground(Func<string?> held, CancellationToken lifetime) => _ = Task.Run(async () =>
    {
        var failures = new Dictionary<ExoComponent, int>();
        try
        {
            // Out of the way of Exo's own start: the library, the rooms and their pictures come first.
            await Task.Delay(TimeSpan.FromSeconds(20), lifetime).ConfigureAwait(false);
            while (true)
            {
                var missing = All.Where(c => !c.Ready()).ToList();
                if (missing.Count == 0) return;
                if (held() is null)
                {
                    foreach (var component in missing)
                    {
                        try
                        {
                            await FetchAsync(component, lifetime).ConfigureAwait(false);
                            failures.Remove(component);
                        }
                        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
                        catch (Exception ex)
                        {
                            var count = failures[component] = failures.GetValueOrDefault(component) + 1;
                            AppLog.Info($"Components: {component.Name} wasn't fetched ({ex.GetType().Name}: {ex.Message}); try {count}.");
                            if (count == 2) FailedTwice?.Invoke(component.Feature);
                        }
                    }
                }
                var wait = held() is not null ? TimeSpan.FromMinutes(1)
                    : failures.Count == 0 ? TimeSpan.FromSeconds(5)
                    : failures.Values.Max() == 1 ? TimeSpan.FromMinutes(2) : TimeSpan.FromMinutes(30);
                await Task.Delay(wait, lifetime).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.Warn("Components: the background fetch stopped: " + ex.Message); }
    }, lifetime);

    /// <summary>Exo's uninstaller: every component goes with Exo (a reinstall fetches them again).</summary>
    internal static void RemoveAll()
    {
        foreach (var component in All)
        {
            var location = component.Location();
            try
            {
                if (Directory.Exists(location)) Directory.Delete(location, recursive: true);
                else if (File.Exists(location)) File.Delete(location);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Info($"Uninstall: {component.Name} wasn't removed ({ex.GetType().Name}).");
            }
        }
    }
}
