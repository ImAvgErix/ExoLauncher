using ExoLauncher.Helpers;

namespace ExoLauncher;

/// Managed process groups for the background trim loop and its five-minute
/// memory line in the log: Exo's own tree, the hosted clients and the store
/// clients' UI helpers. Settings no longer reads any of this.
public sealed partial class MainWindow
{
    private readonly ManagedProcessIdentity _hostResourceIdentity = ManagedProcessResources.Identify(Environment.ProcessId)!.Value;
    private volatile ManagedProcessIdentity[] _webViewResourceIdentities = [];

    private void RefreshRuntimeResourceIdentities()
    {
        if (WebHost.CoreWebView2 is not { } core) return;
        _webViewResourceIdentities = core.Environment.GetProcessInfos()
            .Select(info => ManagedProcessResources.Identify(info.ProcessId) ?? new ManagedProcessIdentity(info.ProcessId, 0))
            .ToArray();
    }

    private IReadOnlyList<ManagedProcessGroup> ManagedResourceGroups()
    {
        var groups = new List<ManagedProcessGroup>
        {
            new("exo", [_hostResourceIdentity]),
            new("webview", _webViewResourceIdentities),
        };
        if (_musicApp is { } music) groups.Add(music.ResourceGroup);
        groups.AddRange(Adapters.StoreClientHelpers.Groups());
        return ManagedProcessResources.Normalize(groups);
    }
}
