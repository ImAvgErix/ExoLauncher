using System.Security.Cryptography;
using System.Text;

namespace ExoLauncher.Helpers;

internal static class WebAdBlockPolicy
{
    internal static string ProfileKey(string profilePath, bool inPrivate) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.GetFullPath(profilePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .ToUpperInvariant() + "|" + inPrivate)));

    internal static string InstallSignature(string version, string folder) => version + "|" + Path.GetFullPath(folder);
    internal static string ProfileSignature(string installSignature, string revision, string extensionId) =>
        installSignature + "|" + revision + "|" + extensionId;

    // A newer toggle may commit while WebView2 is applying the previous one.
    // Re-read after every async mutation so the last committed app setting wins.
    internal static async Task ReconcilePreferenceAsync(Func<bool> read, Func<bool, Task> apply)
    {
        bool desired;
        do { desired = read(); await apply(desired); }
        while (desired != read());
    }

    internal static async Task<TExtension?> MaintainExtensionAsync<TExtension>(TExtension? extension, bool needsInstall,
        Func<bool> read, Func<Task<TExtension>> install, Func<TExtension, bool, Task> apply) where TExtension : class
    {
        try { if (needsInstall && read()) extension = await install(); }
        finally
        {
            // Existing protection must still obey a disable committed during a
            // failed update, even when the bundled installation files are gone.
            if (extension is not null) await ReconcilePreferenceAsync(read, enabled => apply(extension, enabled));
        }
        return extension;
    }
}

/// <summary>All views sharing one real browser profile, without retaining their lifetime.</summary>
internal sealed class WebAdBlockTargets<TCore, TContext> where TCore : class
{
    private readonly object _gate = new();
    private readonly List<(WeakReference<TCore> Core, TContext Context)> _targets = [];

    internal void Add(TCore core, TContext context)
    {
        lock (_gate)
        {
            _targets.RemoveAll(target => !target.Core.TryGetTarget(out var existing) || ReferenceEquals(existing, core));
            _targets.Add((new(core), context));
        }
    }

    internal void Forget(TCore core)
    {
        lock (_gate)
            _targets.RemoveAll(target => !target.Core.TryGetTarget(out var existing) || ReferenceEquals(existing, core));
    }

    internal (TCore Core, TContext Context)[] Snapshot()
    {
        lock (_gate)
        {
            var result = new List<(TCore, TContext)>();
            _targets.RemoveAll(target => !target.Core.TryGetTarget(out _));
            foreach (var target in _targets)
                if (target.Core.TryGetTarget(out var core)) result.Add((core, target.Context));
            return result.ToArray();
        }
    }
}
