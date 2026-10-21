using System.Text.Json;
using System.Text.Json.Nodes;
using static ExoLauncher.Services.DiscordClient.DiscordProto;

namespace ExoLauncher.Services.DiscordClient;

/// <summary>
/// The account's favorite GIFs, as Discord keeps them: field 2 (favorite_gifs) of the
/// FrecencyUserSettings protobuf at users/@me/settings-proto/2. favorite_gifs is a map from the
/// GIF's page URL to { format (1 image, 2 video), src, width, height, order }, newest with the
/// highest order. Discord's client saves by sending a FrecencyUserSettings holding only the whole
/// favorite_gifs field (the server replaces that field), so Exo reads, changes and sends it the
/// same way and never touches the other fields. Seen in Discord's web client
/// (discord_protos.discord_users.v1.FrecencyUserSettings / FavoriteGIFs / FavoriteGIF).
/// </summary>
internal static class DiscordFavoriteGifs
{
    /// <summary>Discord refuses a favorites field larger than this (its client checks the same).</summary>
    private const int MaxBytes = 762_880;

    internal sealed record Gif(string Url, int Format, string Src, uint Width, uint Height, uint Order);

    /// <summary>The favorites, newest first; null when Discord can't be read.</summary>
    internal static async Task<List<Gif>?> ListAsync(DiscordRest rest, CancellationToken ct)
    {
        var read = await ReadAsync(rest, ct).ConfigureAwait(false);
        return read?.Gifs.OrderByDescending(g => g.Order).ToList();
    }

    // Each change reads, edits and sends the whole list: two at once would both start from the same list and one would be lost.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Adds (as the newest) or removes one favorite and saves; the new list, or null on failure.</summary>
    internal static async Task<List<Gif>?> ChangeAsync(DiscordRest rest, Gif? add, string? remove, CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await ChangeLockedAsync(rest, add, remove, ct).ConfigureAwait(false); }
        finally { Gate.Release(); }
    }

    private static async Task<List<Gif>?> ChangeLockedAsync(DiscordRest rest, Gif? add, string? remove, CancellationToken ct)
    {
        var read = await ReadAsync(rest, ct).ConfigureAwait(false);
        if (read is null) return null;
        var gifs = read.Gifs;
        if (remove is not null) gifs.RemoveAll(g => g.Url == remove);
        if (add is not null)
        {
            gifs.RemoveAll(g => g.Url == add.Url);
            var top = gifs.Count == 0 ? 0u : gifs.Max(g => g.Order);
            gifs.Add(add with { Order = top + 1 });
        }
        var field = EncodeFavorites(gifs, read.HideTooltip || gifs.Count > 2, read.Unknown);
        if (field.Length > MaxBytes) return null;
        var settings = new List<byte>();
        WriteBytes(settings, 2, field);
        var reply = await rest.SaveFrecencyAsync(Convert.ToBase64String(settings.ToArray()), ct).ConfigureAwait(false);
        return reply.Error is null ? gifs.OrderByDescending(g => g.Order).ToList() : null;
    }

    private sealed record Read(List<Gif> Gifs, bool HideTooltip, byte[] Unknown);

    private static async Task<Read?> ReadAsync(DiscordRest rest, CancellationToken ct)
    {
        if (await rest.FrecencyProtoAsync(ct).ConfigureAwait(false) is not { } proto) return null;
        try
        {
            foreach (var (number, wire, value) in Fields(proto))
                if (number == 2 && wire == 2) return DecodeFavorites(value);
            return new Read([], false, []);
        }
        catch (InvalidDataException) { return null; }
    }

    private static Read DecodeFavorites(byte[] data)
    {
        var gifs = new List<Gif>();
        var hide = false;
        var unknown = new List<byte>();
        foreach (var (number, wire, value) in Fields(data))
        {
            if (number == 1 && wire == 2)
            {
                string? key = null;
                Gif? gif = null;
                foreach (var (n, w, v) in Fields(value))
                {
                    if (n == 1 && w == 2) key = System.Text.Encoding.UTF8.GetString(v);
                    else if (n == 2 && w == 2) gif = DecodeGif(v);
                }
                if (key is not null && gif is not null) gifs.Add(gif with { Url = key });
            }
            else if (number == 2 && wire == 0) hide = Varint(value) != 0;
            else Raw(unknown, number, wire, value);
        }
        return new Read(gifs, hide, unknown.ToArray());
    }

    private static Gif DecodeGif(byte[] data)
    {
        int format = 0; string src = ""; uint width = 0, height = 0, order = 0;
        foreach (var (n, w, v) in Fields(data))
        {
            switch (n)
            {
                case 1 when w == 0: format = (int)Varint(v); break;
                case 2 when w == 2: src = System.Text.Encoding.UTF8.GetString(v); break;
                case 3 when w == 0: width = (uint)Varint(v); break;
                case 4 when w == 0: height = (uint)Varint(v); break;
                case 5 when w == 0: order = (uint)Varint(v); break;
            }
        }
        return new Gif("", format, src, width, height, order);
    }

    private static byte[] EncodeFavorites(IEnumerable<Gif> gifs, bool hideTooltip, byte[] unknown)
    {
        var output = new List<byte>();
        foreach (var gif in gifs)
        {
            var value = new List<byte>();
            if (gif.Format != 0) { WriteTag(value, 1, 0); WriteVarint(value, (ulong)gif.Format); }
            WriteString(value, 2, gif.Src);
            if (gif.Width != 0) { WriteTag(value, 3, 0); WriteVarint(value, gif.Width); }
            if (gif.Height != 0) { WriteTag(value, 4, 0); WriteVarint(value, gif.Height); }
            if (gif.Order != 0) { WriteTag(value, 5, 0); WriteVarint(value, gif.Order); }
            var entry = new List<byte>();
            WriteString(entry, 1, gif.Url);
            WriteBytes(entry, 2, value);
            WriteBytes(output, 1, entry);
        }
        if (hideTooltip) { WriteTag(output, 2, 0); WriteVarint(output, 1); }
        output.AddRange(unknown);
        return output.ToArray();
    }

    /// <summary>The favorites as the page reads them.</summary>
    internal static JsonArray ToJson(IEnumerable<Gif> gifs) =>
        [.. gifs.Select(g => (JsonNode)new JsonObject { ["url"] = g.Url, ["src"] = g.Src, ["format"] = g.Format, ["width"] = g.Width, ["height"] = g.Height })];
}
