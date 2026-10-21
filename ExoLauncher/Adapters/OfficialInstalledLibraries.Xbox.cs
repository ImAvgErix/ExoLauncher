using System.Diagnostics;
using System.Xml.Linq;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Adapters;

internal static partial class OfficialInstalledLibraries
{
    internal sealed record XboxPackageTarget(string Identity, string Root, string AppId);
    internal sealed record XboxLaunchPlan(string Kind, string? Target);

    internal static XboxLaunchPlan PlanXboxLaunch(string root, string? executable,
        string? configXml, IEnumerable<XboxPackageTarget> packages, Func<string, bool> fileExists)
    {
        string? identity = null;
        string? applicationId = null;
        if (!string.IsNullOrWhiteSpace(configXml))
        {
            try
            {
                var xml = XDocument.Parse(configXml);
                identity = xml.Descendants().FirstOrDefault(e => e.Name.LocalName == "Identity")?.Attribute("Name")?.Value;
                applicationId = xml.Descendants().FirstOrDefault(e => e.Name.LocalName == "Executable")?.Attribute("Id")?.Value;
            }
            catch { return new("unavailable", null); }
        }
        if (!string.IsNullOrWhiteSpace(identity))
        {
            var matching = packages.Where(package => package.Identity == identity && SameXboxRoot(package.Root, root) &&
                    (string.IsNullOrWhiteSpace(applicationId) || package.AppId.EndsWith("!" + applicationId, StringComparison.Ordinal)))
                .Select(package => package.AppId).Distinct(StringComparer.Ordinal).ToArray();
            if (matching.Length == 1) return new("package", matching[0]);
            if (matching.Length > 1) return new("unavailable", null);
        }
        var helper = Path.Combine(root, "gamelaunchhelper.exe");
        if (fileExists(helper)) return new("helper", helper);
        // A packaged game's raw executable bypasses activation. Only a
        // standalone title without package identity may use direct launch.
        if (string.IsNullOrWhiteSpace(identity) && !string.IsNullOrWhiteSpace(executable) &&
            Path.IsPathFullyQualified(executable) && fileExists(executable) &&
            !ProcessHelper.IsNonGameProcessName(Path.GetFileNameWithoutExtension(executable)))
            return new("direct", executable);
        return new("unavailable", null);
    }

    private static bool SameXboxRoot(string a, string b)
    {
        try { return Path.GetFullPath(a).TrimEnd('\\', '/').Equals(Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static async Task<XboxLaunchPlan> ResolveXboxLaunchAsync(GameEntry game, CancellationToken ct)
    {
        var root = game.Path ?? Path.GetDirectoryName(game.LaunchTarget) ?? "";
        var configPath = Path.Combine(root, "MicrosoftGame.config");
        string? xml = null;
        if (File.Exists(configPath))
        {
            if (new FileInfo(configPath).Length > 1024 * 1024) return new("unavailable", null);
            try { xml = await File.ReadAllTextAsync(configPath, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { return new("unavailable", null); }
        }
        var targets = new List<XboxPackageTarget>();
        if (xml is not null)
        {
            try
            {
                // Only inspect the current user's package registrations, and
                // only read app entries for the exact installation folder.
                foreach (var package in new Windows.Management.Deployment.PackageManager().FindPackagesForUser(""))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var paths = new[] { package.EffectivePath, package.EffectiveExternalPath };
                        var matchingRoot = paths.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && SameXboxRoot(path, root));
                        if (matchingRoot is null) continue;
                        LogXboxPackageHealth(package, game.Title);
                        var entries = await package.GetAppListEntriesAsync().AsTask(ct).WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                        targets.AddRange(entries.Select(entry => new XboxPackageTarget(package.Id.Name, matchingRoot, entry.AppUserModelId)));
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { /* inaccessible or unrelated registration */ }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* use the installed gaming helper if present */ }
        }
        return PlanXboxLaunch(root, game.LaunchTarget, xml, targets, File.Exists);
    }

    // Windows flags a damaged or edited package; the Xbox app repairs it. Exo
    // only records it so a failed activation has a cause in app.log.
    private static void LogXboxPackageHealth(Windows.ApplicationModel.Package package, string title)
    {
        try
        {
            var status = package.Status;
            if (status.NeedsRemediation || status.Modified || status.Tampered)
                AppLog.Warn($"Xbox package for '{title}' reports needsRemediation={status.NeedsRemediation} " +
                            $"modified={status.Modified} tampered={status.Tampered}; repair it in the Xbox app if it will not start.");
        }
        catch { /* status unavailable for this registration */ }
    }

    private static LaunchResult LaunchXboxActivation(XboxLaunchPlan plan)
    {
        try
        {
            using var started = Process.Start(plan.Kind == "package"
                ? new ProcessStartInfo("explorer.exe") { Arguments = "shell:AppsFolder\\" + plan.Target, UseShellExecute = true }
                : new ProcessStartInfo(plan.Target!) { WorkingDirectory = Path.GetDirectoryName(plan.Target), UseShellExecute = true });
            return ClientHandoffOutcome("Requested Xbox game activation.");
        }
        catch (Exception ex) { return new LaunchResult { Ok = false, Message = Cli.CliFailure.Launch(null, ex) }; }
    }
}
