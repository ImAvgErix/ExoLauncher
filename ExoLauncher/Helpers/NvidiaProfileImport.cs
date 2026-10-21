using System.Globalization;
using System.Xml;

namespace ExoLauncher.Helpers;

/// <summary>
/// NVIDIA Profile Inspector's export (.nip): .NET XmlSerializer output of
/// <c>ArrayOfProfile</c> / <c>Profile</c> (<c>ProfileName</c>,
/// <c>Executeables</c> with one <c>string</c> per program, <c>Settings</c> of
/// <c>ProfileSetting</c>: <c>SettingNameInfo</c>, <c>SettingID</c> and, for
/// Dword and Qword, <c>SettingValue</c> in decimal, <c>ValueType</c> one of
/// Dword, AnsiString, String, Binary (Base64), Qword). The declaration says
/// utf-16 but Profile Inspector saves it as UTF-8 with a BOM; both read.
///
/// Exo applies only Dword settings, to the global profile; text, binary and
/// 64-bit values are counted as left out.
/// </summary>
internal static class NvidiaProfileImport
{
    internal const int MaxBytes = 1024 * 1024;
    private const int MaxSettings = 4096;

    internal sealed record Setting(uint Id, string Type, string Value);
    internal sealed record Profile(string Name, IReadOnlyList<string> Executables, IReadOnlyList<Setting> Settings);

    /// <summary>One write in plain words; <see cref="Name"/> null for an id Exo has no name for.</summary>
    internal sealed record Change(uint Id, uint Value, string? Name, string Words);

    /// <summary>
    /// What applying a profile would write, how many of those Exo cannot name, how
    /// many are not Dword values, and how many this driver does not list (it
    /// refuses to set those).
    /// </summary>
    internal sealed record Plan(string ProfileName, IReadOnlyList<Change> Writes, int Unknown, int LeftOut, int NotInDriver);

    /// <summary>The profiles in a .nip file, or null when it is not one.</summary>
    internal static IReadOnlyList<Profile>? Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxBytes) return null;
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                MaxCharactersInDocument = MaxBytes,
            };
            // The declaration names utf-16 even when the text is not; parse the text as given.
            var body = text.TrimStart('﻿');
            if (body.StartsWith("<?xml", StringComparison.Ordinal))
            {
                var end = body.IndexOf("?>", StringComparison.Ordinal);
                if (end < 0) return null;
                body = body[(end + 2)..];
            }
            using var reader = XmlReader.Create(new StringReader(body), settings);
            var document = new XmlDocument { XmlResolver = null };
            document.Load(reader);
            if (document.DocumentElement is not { LocalName: "ArrayOfProfile" } root) return null;
            var profiles = new List<Profile>();
            var count = 0;
            foreach (var node in root.ChildNodes.OfType<XmlElement>().Where(element => element.LocalName == "Profile"))
            {
                var name = Child(node, "ProfileName")?.InnerText.Trim() ?? "";
                var programs = Child(node, "Executeables")?.ChildNodes.OfType<XmlElement>()
                    .Where(element => element.LocalName == "string")
                    .Select(element => element.InnerText.Trim())
                    .Where(program => program.Length > 0)
                    .ToList() ?? [];
                var list = new List<Setting>();
                foreach (var item in Child(node, "Settings")?.ChildNodes.OfType<XmlElement>().Where(element => element.LocalName == "ProfileSetting") ?? [])
                {
                    if (++count > MaxSettings) return null;
                    if (!uint.TryParse(Child(item, "SettingID")?.InnerText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var id)) continue;
                    list.Add(new Setting(id, Child(item, "ValueType")?.InnerText.Trim() ?? "Dword", Child(item, "SettingValue")?.InnerText ?? ""));
                }
                profiles.Add(new Profile(name, programs, list));
            }
            return profiles.Count > 0 ? profiles : null;
        }
        catch (XmlException) { return null; }
    }

    /// <summary>
    /// The profile to apply to the global profile: the one profile in the file
    /// that names no program (Profile Inspector's Base Profile), never a
    /// game's. Null with a reason otherwise.
    /// </summary>
    internal static Profile? PickGlobal(IReadOnlyList<Profile> profiles, out string? reason)
    {
        reason = null;
        var global = profiles.Where(profile => profile.Executables.Count == 0).ToList();
        if (global.Count == 1) return global[0];
        reason = global.Count == 0
            ? "This file is for a game, not the global profile. Export the Base Profile in Profile Inspector."
            : "This file holds more than one global profile.";
        return null;
    }

    /// <summary>The Dword settings to write, named where Exo knows them, in id order.</summary>
    internal static Plan PlanFor(Profile profile, IReadOnlySet<uint> driverIds)
    {
        var writes = new Dictionary<uint, Change>();
        var leftOut = 0;
        foreach (var setting in profile.Settings)
        {
            if (!string.Equals(setting.Type, "Dword", StringComparison.OrdinalIgnoreCase) ||
                !uint.TryParse(setting.Value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                leftOut++;
                continue;
            }
            // A repeated id: the last one wins, as it would when written in order.
            writes[setting.Id] = new Change(setting.Id, value, GraphicsCatalog.NameOf(setting.Id), GraphicsCatalog.ValueOf(setting.Id, value));
        }
        var list = writes.Values.Where(change => driverIds.Contains(change.Id)).OrderBy(change => change.Id).ToList();
        return new Plan(profile.Name, list, list.Count(change => change.Name is null), leftOut, writes.Count - list.Count);
    }

    /// <summary>The writes that put back what the driver held before (null = driver default), for Undo.</summary>
    internal static List<KeyValuePair<uint, uint?>> UndoWrites(IReadOnlyDictionary<uint, uint?> before, IEnumerable<uint> written) =>
        written.Distinct().Order().Select(id => new KeyValuePair<uint, uint?>(id, before.TryGetValue(id, out var value) ? value : null)).ToList();

    private static XmlElement? Child(XmlElement parent, string name) =>
        parent.ChildNodes.OfType<XmlElement>().FirstOrDefault(element => element.LocalName == name);
}
