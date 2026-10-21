namespace ExoLauncher.Helpers;

internal enum EmbeddedRendererKind { Shell, Embedded, Unknown }

/// <summary>
/// Classifies a WebView2 renderer by the frame sources it hosts. A frame on the
/// Exo app origin marks the shell renderer; every other renderer is embedded
/// (music, pop-outs, sign-ins, anything future). Pure — no WebView2.
/// </summary>
internal static class EmbeddedRendererClass
{
    internal static EmbeddedRendererKind From(IEnumerable<string> frameSources)
    {
        var any = false;
        foreach (var source in frameSources)
        {
            if (string.IsNullOrWhiteSpace(source)) continue;
            any = true;
            if (WebViewTrustPolicy.IsTrustedAppUri(source)) return EmbeddedRendererKind.Shell;
        }
        return any ? EmbeddedRendererKind.Embedded : EmbeddedRendererKind.Unknown;
    }

    internal static bool HasMusicHost(IEnumerable<string> frameSources, string? service)
        => service is not null && frameSources.Any(source => MusicWebPolicy.IsServiceDocument(service, source));

    /// <summary>
    /// Foreground trimming only touches renderers whose surface is hidden. The
    /// Music surface shares the embedded renderers, including login and identity
    /// pages in their processes, so a visible Music room or an open music
    /// pop-out exempts them all. A hidden YouTube document that is still
    /// playing keeps its decoder resident so skip and the mini player stay live.
    /// Unknown renderers are never trimmed while Exo is in front.
    /// </summary>
    internal static bool ShouldTrimInForeground(EmbeddedRendererKind kind, bool embeddedSurfaceVisible,
        bool musicPlaying = false) =>
        kind == EmbeddedRendererKind.Embedded && !embeddedSurfaceVisible && !musicPlaying;
}
