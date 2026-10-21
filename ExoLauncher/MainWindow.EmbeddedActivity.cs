using System.Runtime.CompilerServices;
using Microsoft.Web.WebView2.Core;

namespace ExoLauncher;

public sealed partial class MainWindow
{
    private sealed class EmbeddedActivityState
    {
        internal bool? Active;
        internal long Version;
        internal CoreWebView2MemoryUsageTargetLevel? MemoryTarget;
    }
    private readonly ConditionalWeakTable<CoreWebView2, EmbeddedActivityState> _embeddedActivity = new();

    private void SetWebViewMemoryTarget(CoreWebView2 core, CoreWebView2MemoryUsageTargetLevel target)
    {
        // Event handlers can outlive their view; a closed WebView2 reports a
        // null core and ConditionalWeakTable rejects null keys.
        if (core is null) return;
        var state = _embeddedActivity.GetOrCreateValue(core);
        if (state.MemoryTarget == target) return;
        core.MemoryUsageTargetLevel = target;
        state.MemoryTarget = target;
    }

    private async void SetEmbeddedActivity(CoreWebView2 core, bool active, bool force = false)
    {
        if (core is null) return;
        var state = _embeddedActivity.GetOrCreateValue(core);
        if (!force && state.Active == active) return;
        state.Active = active;
        var version = ++state.Version;
        try
        {
            var result = await core.ExecuteScriptAsync($"window.__exoClientPresentation?.setActive({(active ? "true" : "false")}); !!window.__exoClientPresentation")
                .AsTask().WaitAsync(TimeSpan.FromSeconds(2), _lifetimeCts.Token);
            if (result != "true" && version == state.Version) state.Active = null;
        }
        catch { if (version == state.Version) state.Active = null; }
    }
}
