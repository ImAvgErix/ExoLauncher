using ExoLauncher.Services.DiscordClient.Voice;
using Microsoft.Win32;

namespace ExoLauncher.Services;

/// <summary>
/// Where Lucid's EQ plays: the outputs its block in Equalizer APO's config.txt names, and whether Windows' default
/// output is one of them with Equalizer APO installed on it. A headset or a controller's speaker Windows switched to
/// plays without the EQ, and nothing said so. Read only: Lucid stays the one writer of config.txt.
/// </summary>
internal static class LucidOutput
{
    /// <summary>The output Lucid's EQ is on (null: none yet), the one Windows plays on, and whether that one has it.</summary>
    internal sealed record Coverage(string? On, string? Playing, bool Covered);

    private const string RenderKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\";
    // Windows builds an output's name ("Headphones (High Definition Audio Device)") from these two; it stores no whole one.
    private const string Description = "{a45c254e-df1c-4efd-8020-67d146a850e0},2"; // PKEY_Device_DeviceDesc
    private const string Adapter = "{b3f8fa53-0004-438e-9003-51a46e139bfc},6";    // PKEY_DeviceInterface_FriendlyName

    /// <summary>Null without Equalizer APO's config.</summary>
    internal static Coverage? Read()
    {
        try
        {
            string folder;
            using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\EqualizerAPO"))
                folder = key?.GetValue("ConfigPath") as string
                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "EqualizerAPO", "config");
            var config = Path.Combine(folder, "config.txt");
            if (!File.Exists(config)) return null;
            var devices = LucidDevices(File.ReadAllText(config));
            var playing = Peripherals.DefaultEndpoints().FirstOrDefault(e => !e.Capture) is { } output ? GuidOf(output.Id) : null;
            var on = devices switch
            {
                null => null,
                { All: true } => "every output with Equalizer APO",
                { Guids: [var first, ..] } => NameOf(first) ?? "an output that isn't connected",
                _ => null,
            };
            var covered = devices is { } d && playing is not null && (d.All || d.Guids.Contains(playing, StringComparer.OrdinalIgnoreCase)) && HasApo(playing);
            return new Coverage(on, playing is null ? null : NameOf(playing), covered);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>
    /// The outputs Lucid's block ("# Lucid EQ begin" to "# Lucid EQ end") applies its EQ to: endpoint ids from its Device
    /// lines, or all; null when there is no block or it names none before its Include.
    /// </summary>
    internal static (bool All, string[] Guids)? LucidDevices(string config)
    {
        var inside = false;
        var guids = new List<string>();
        foreach (var raw in config.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("# Lucid EQ begin", StringComparison.OrdinalIgnoreCase)) { inside = true; continue; }
            if (line.StartsWith("# Lucid EQ end", StringComparison.OrdinalIgnoreCase)) break;
            if (!inside || line.StartsWith('#')) continue;
            if (line.StartsWith("Include:", StringComparison.OrdinalIgnoreCase)) return guids.Count == 0 ? null : (false, [.. guids]);
            if (!line.StartsWith("Device:", StringComparison.OrdinalIgnoreCase)) continue;
            var value = line["Device:".Length..].Trim();
            if (value.Equals("all", StringComparison.OrdinalIgnoreCase)) return (true, []);
            // A Device line lists ids or names separated by semicolons; Lucid writes endpoint ids.
            guids.Clear();
            guids.AddRange(value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Where(one => one.StartsWith('{')));
        }
        return null;
    }

    /// <summary>"{0.0.0.00000000}.{9709e4ab-…}" to its endpoint id, the part Equalizer APO's Device lines name.</summary>
    private static string? GuidOf(string endpointId) =>
        endpointId[(endpointId.LastIndexOf('.') + 1)..] is { Length: 38 } guid && guid.StartsWith('{') ? guid : null;

    private static string? NameOf(string guid)
    {
        using var properties = Registry.LocalMachine.OpenSubKey(RenderKey + guid + @"\Properties");
        if (properties?.GetValue(Description) is not string { Length: > 0 } name) return null;
        return properties.GetValue(Adapter) is string { Length: > 0 } adapter ? $"{name} ({adapter})" : name;
    }

    /// <summary>Equalizer APO installed on this output (its device picker puts its effect in the endpoint's FxProperties).</summary>
    private static bool HasApo(string guid)
    {
        using var fx = Registry.LocalMachine.OpenSubKey(RenderKey + guid + @"\FxProperties");
        return fx is not null && fx.GetValueNames().Any(name => VoiceTune.NamesEqualizerApo(fx.GetValue(name)));
    }
}
