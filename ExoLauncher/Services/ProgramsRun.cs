namespace ExoLauncher.Services;

/// <summary>
/// The programs Windows saw this user run, read with the deep clean's own readers (<see cref="ReadSources"/>): the name
/// cache, compatibility and recent-apps records, UserAssist and Game Bar's list. Library finds portable games by them
/// (<see cref="Adapters.RecompInstallations"/>): no installer records those.
/// </summary>
internal sealed partial class LeftoverScan
{
    /// <summary>Each record's text (a value's name and data, a decoded UserAssist name, Game Bar's path): a program's path is somewhere in it.</summary>
    internal List<string> ProgramsRun()
    {
        var values = new List<(string Key, string Name, string Text, bool Machine)>();
        foreach (var path in UserValues) ReadValues(path, values, machine: false);
        var texts = values.SelectMany(value => new[] { value.Name, value.Text }).ToList();
        ReadSubKeys(UserAssist, (_, key) =>
        {
            using var count = key.OpenSubKey("Count");
            foreach (var name in count?.GetValueNames() ?? []) texts.Add(KnownFolder(Rot13(name)));
        });
        ReadSubKeys(GameConfig, (_, key) => { if (key.GetValue("MatchedExeFullPath") is string exe) texts.Add(exe); });
        return texts;
    }
}
