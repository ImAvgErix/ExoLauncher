using System.Text.Json;

namespace ExoLauncher.Services;

/// <summary>
/// devices.image: a device's official product picture, fitted to the Devices room's frame and kept on this PC
/// (DeviceImages), from what the page knows of it: kind, model, name, maker, color, layout. url is null when Exo's
/// list has no picture for it or the picture couldn't be fetched.
/// </summary>
public sealed partial class WebHostBridge
{
    private static async Task<object> DevicesImageAsync(JsonElement p, bool hasParams, CancellationToken cancellationToken)
    {
        string? Field(string name) => ReadString(p, hasParams, name)?.Trim() is { Length: > 0 and <= 200 } value ? value : null;
        var url = await DeviceImages.UrlAsync(
            new DeviceImages.Request(Field("kind"), Field("model"), Field("name"), Field("maker"), Field("color"), Field("layout")),
            cancellationToken).ConfigureAwait(false);
        return new { ok = url is not null, url };
    }
}
