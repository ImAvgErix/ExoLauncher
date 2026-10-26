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

    /// <summary>
    /// A native part of Discord voice that voice\build.ps1 builds into voice\out (dave.dll, opus.dll, exo_apm.dll), beside
    /// the tests: CI builds and tests without them (they take vcpkg, OpenSSL and a long native build), so a test that
    /// needs one skips there and says why; on a PC that ran voice\build.ps1 it runs. Build-Release.ps1 refuses a release
    /// without them.
    /// </summary>
    internal static void RequireVoice(string dll) =>
        Assert.SkipUnless(File.Exists(Path.Combine(AppContext.BaseDirectory, dll)), $"{dll} isn't built here (voice\\build.ps1)");

    /// <summary>The host's C# sources, without build output.</summary>
    internal static IEnumerable<string> HostSources() =>
        new[] { "ExoLauncher", "ExoLauncher.SteamIpc" }
            .SelectMany(project => Directory.EnumerateFiles(PathOf(project), "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) &&
                           !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar));
}
