using Microsoft.Win32;

namespace ExoLauncher.Helpers;

internal readonly record struct TrayIconRegistration(Guid? Guid, uint? Uid);

/// Windows records every notification-area icon an executable registers under
/// HKCU\Control Panel\NotifyIconSettings. GUID-identified icons (Discord)
/// carry IconGuid; plain hwnd+uID icons (Spotify) carry UID.
internal static class TrayIcons
{
    internal static IReadOnlyList<TrayIconRegistration> Select(
        IEnumerable<(string? Executable, string? Guid, int? Uid)> entries, string executable)
    {
        string target;
        try { target = Path.GetFullPath(executable); }
        catch { return []; }

        var icons = new List<TrayIconRegistration>();
        var seen = new HashSet<TrayIconRegistration>();
        foreach (var (candidate, guidText, uid) in entries)
        {
            if (candidate is null) continue;
            string full;
            try { full = Path.GetFullPath(candidate); }
            catch { continue; }
            if (!string.Equals(full, target, StringComparison.OrdinalIgnoreCase)) continue;
            TrayIconRegistration icon;
            if (Guid.TryParse(guidText, out var guid)) icon = new TrayIconRegistration(guid, null);
            else if (uid is >= 0) icon = new TrayIconRegistration(null, (uint)uid.Value);
            else continue;
            if (seen.Add(icon)) icons.Add(icon);
        }
        return icons;
    }

    internal static IReadOnlyList<TrayIconRegistration> Read(string executable)
    {
        // Runs on the UI thread every 30 s per hosted client: the other two
        // values are read only for this executable's own icons.
        string target;
        try { target = Path.GetFullPath(executable); }
        catch { return []; }
        var entries = new List<(string? Executable, string? Guid, int? Uid)>();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
            if (key is null) return [];
            foreach (var name in key.GetSubKeyNames())
            {
                using var sub = key.OpenSubKey(name);
                if (sub?.GetValue("ExecutablePath") is not string path) continue;
                try { if (!string.Equals(Path.GetFullPath(path), target, StringComparison.OrdinalIgnoreCase)) continue; }
                catch { continue; }
                entries.Add((path, sub.GetValue("IconGuid") as string, sub.GetValue("UID") as int?));
            }
        }
        catch { return []; }
        return Select(entries, executable);
    }
}
