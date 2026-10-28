using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;
using Microsoft.Win32;

namespace ExoLauncher.Services;

/// <summary>
/// A keyboard's own settings app, the keyboard page's one button, chosen in one table: by the keyboard's vendor id
/// (Wooting, Keychron, Razer, Logitech, Corsair, SteelSeries), else by what a QMK board says it speaks: Vial's serial
/// number magic, or VIA's raw HID channel (usage page 0xFF60, usage 0x61). Wootility, Keychron Launcher, VIA and Vial
/// run in the browser; a maker's app opens from its Start menu shortcut when installed, else its download page opens.
/// Exo only opens them: nothing is sent to the keyboard.
/// </summary>
internal static partial class Peripherals
{
    /// <summary>One app: its id on the wire, its name, the page it runs on (or comes from), and the Start menu names an
    /// installed copy goes by (null: it runs in the browser).</summary>
    internal sealed record KeyboardApp(string Id, string Name, string Url, string[]? Shortcuts = null);

    /// <summary>What the keyboard's card carries: its app's id and name, and where it is: web, installed or download.</summary>
    internal sealed record KeyboardAppCard(string Id, string Name, string Where);

    internal static readonly KeyboardApp Wootility = new("wootility", "Wootility", "https://wootility.io/");
    internal static readonly KeyboardApp KeychronLauncher = new("keychron", "Keychron Launcher", "https://launcher.keychron.com/");
    internal static readonly KeyboardApp Via = new("via", "VIA", "https://usevia.app/");
    internal static readonly KeyboardApp Vial = new("vial", "Vial", "https://vial.rocks/");
    internal static readonly KeyboardApp Synapse = new("synapse", "Razer Synapse", "https://www.razer.com/synapse-4", ["Razer Synapse"]);
    internal static readonly KeyboardApp GHub = new("ghub", "Logitech G HUB", "https://www.logitechg.com/innovation/g-hub.html", ["Logitech G HUB"]);
    internal static readonly KeyboardApp OptionsPlus = new("options", "Logi Options+", "https://www.logitech.com/software/logi-options-plus.html", ["Logi Options+", "Logitech Options+"]);
    internal static readonly KeyboardApp Icue = new("icue", "Corsair iCUE", "https://www.corsair.com/icue", ["iCUE", "Corsair iCUE"]);
    internal static readonly KeyboardApp SteelSeriesGg = new("gg", "SteelSeries GG", "https://steelseries.com/gg", ["SteelSeries GG"]);

    internal static readonly KeyboardApp[] KeyboardApps = [Wootility, KeychronLauncher, Via, Vial, Synapse, GHub, OptionsPlus, Icue, SteelSeriesGg];

    /// <summary>Vial's firmware puts this in its USB serial number, which Windows keeps in the device's instance id.</summary>
    private const string VialSerial = "vial:f64c2b3c";

    /// <summary>The keyboard's own app, or null for anything else and a keyboard none is known for.</summary>
    internal static KeyboardApp? AppOf(Peripheral card)
    {
        if (card.Kind != "keyboard") return null;
        var byVendor = card.VendorId switch
        {
            0x31E3 => Wootility,
            0x03EB when card.ProductId is 0xFF01 or 0xFF02 => Wootility, // Wooting One and Two, on Atmel's ids
            0x3434 => KeychronLauncher,
            0x1532 => Synapse,
            0x046D => LogiOptions().IsMatch(card.Name) ? OptionsPlus : GHub,
            0x1B1C => Icue,
            0x1038 => SteelSeriesGg,
            _ => null,
        };
        if (byVendor is not null) return byVendor;
        if (card.Source?.Contains(VialSerial, StringComparison.OrdinalIgnoreCase) == true) return Vial;
        return card.Collections.Any(c => c is { Page: 0xFF60, Usage: 0x61 }) ? Via : null;
    }

    /// <summary>Logitech's office keyboards (MX, Craft, POP, ERGO, Signature, the K series) are set in Options+; its G ones in G HUB.</summary>
    [GeneratedRegex(@"\b(MX|Craft|POP|ERGO|Signature|K\d{3})\b", RegexOptions.IgnoreCase)]
    private static partial Regex LogiOptions();

    /// <summary>A keyboard with its app on its card; a maker's app is looked for among the Start menu's shortcuts.</summary>
    private static Peripheral WithApp(Peripheral card) => AppOf(card) is { } app
        ? card with { App = new KeyboardAppCard(app.Id, app.Name, app.Shortcuts is null ? "web" : ShortcutOf(app) is null ? "download" : "installed") }
        : card;

    /// <summary>
    /// Opens a keyboard's app by its id: a maker's app from its shortcut when installed, else its page in the browser.
    /// A browser app needs WebHID, which Firefox and its kin lack: with one of them as the default browser it opens in
    /// an installed Chromium browser (Edge, Chrome, Brave) when there is one. Null once it is opening, else what went wrong.
    /// </summary>
    internal static string? OpenKeyboardApp(string? id)
    {
        if (KeyboardApps.FirstOrDefault(a => a.Id == id) is not { } app) return "Exo doesn't know that app.";
        if (app.Shortcuts is null && GeckoDefault() && ChromiumBrowser() is { } browser && Started(new ProcessStartInfo(browser, [app.Url]))) return null;
        var target = app.Shortcuts is null ? app.Url : ShortcutOf(app) ?? app.Url;
        return Started(new ProcessStartInfo(target) { UseShellExecute = true }) ? null : $"{app.Name} didn't open. Try again.";
    }

    private static bool Started(ProcessStartInfo start)
    {
        try
        {
            using var _ = Process.Start(start);
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            AppLog.Warn($"Devices: a keyboard's app didn't open: {ex.Message}");
            return false;
        }
    }

    /// <summary>An installed Chromium browser, by its App Paths entry (this user's, then the machine's).</summary>
    private static string? ChromiumBrowser()
    {
        foreach (var exe in new[] { "msedge.exe", "chrome.exe", "brave.exe" })
            foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                using var key = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\" + exe);
                if (key?.GetValue(null) is string path && path.Trim('"') is { } file && File.Exists(file)) return file;
            }
        return null;
    }

    /// <summary>An installed copy's Start menu shortcut, all users' or this user's.</summary>
    private static string? ShortcutOf(KeyboardApp app)
    {
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), Environment.GetFolderPath(Environment.SpecialFolder.Programs) })
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
            try
            {
                foreach (var link in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories).Take(3000))
                    if (ShortcutNamed(Path.GetFileNameWithoutExtension(link), app.Shortcuts ?? [])) return link;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    /// <summary>A shortcut named as the app, or as the app and its version ("Razer Synapse 3"); "iCUE Uninstall" isn't it.</summary>
    internal static bool ShortcutNamed(string link, IReadOnlyList<string> names) => names.Any(name =>
        link.Equals(name, StringComparison.OrdinalIgnoreCase)
        || link.Length > name.Length + 1 && link.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase)
            && link[(name.Length + 1)..].All(ch => char.IsAsciiDigit(ch) || ch == '.'));

    /// <summary>Windows' default browser for https is Firefox or one built on it (Windows 11 keeps the newer choice in UserChoiceLatest).</summary>
    private static bool GeckoDefault()
    {
        const string associations = @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\";
        try
        {
            foreach (var choice in new[] { "UserChoiceLatest", "UserChoice" })
            {
                using var key = Registry.CurrentUser.OpenSubKey(associations + choice);
                if (key?.GetValue("ProgId") is string progId) return GeckoProgId().IsMatch(progId);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return false;
    }

    [GeneratedRegex(@"^(Firefox|Waterfox|LibreWolf|Floorp|Zen|Mullvad|TorBrowser|PaleMoon|Basilisk)", RegexOptions.IgnoreCase)]
    private static partial Regex GeckoProgId();
}
