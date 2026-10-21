namespace ExoLauncher.Tests;

/// <summary>The checkout the tests run from: the folder holding ExoLauncher.sln.</summary>
internal static class Repo
{
    internal static string Root { get; } = Find();

    internal static string PathOf(params string[] parts) => Path.Combine([Root, .. parts]);

    private static string Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "ExoLauncher.sln"))) return dir.FullName;
        throw new InvalidOperationException("ExoLauncher.sln not found above " + AppContext.BaseDirectory);
    }

    /// <summary>The host's C# sources, without build output.</summary>
    internal static IEnumerable<string> HostSources() =>
        new[] { "ExoLauncher", "ExoLauncher.SteamIpc" }
            .SelectMany(project => Directory.EnumerateFiles(PathOf(project), "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) &&
                           !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar));
}
