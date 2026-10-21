namespace ExoLauncher;

/// <summary>
/// The one place Exo's native code constructs an <see cref="HttpClient"/>.
/// Every other <c>new HttpClient(...)</c> is a build error (RS0030, see
/// BannedSymbols.txt), so finding every client means finding the callers of
/// this method. Callers keep their own handler, timeout and headers. Every
/// host a client contacts is listed in <see cref="NetworkDestinations"/>.
/// </summary>
internal static class ExoHttp
{
    /// <param name="handler">Null uses the default handler, exactly as <c>new HttpClient()</c> does.</param>
    internal static HttpClient Create(TimeSpan timeout, HttpMessageHandler? handler = null, bool disposeHandler = true)
    {
#pragma warning disable RS0030 // The sanctioned construction site: this factory is what the ban points to.
        var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler);
#pragma warning restore RS0030
        http.Timeout = timeout;
        return http;
    }
}
