using ExoLauncher.Tests;

// Isolate all app state (settings, logs, secret store) into a throwaway dir so
// nothing touches the real user's %LOCALAPPDATA%\ExoLauncher (PathHelper reads
// EXO_LAUNCHER_DATA_DIR). Set before any product code runs.
var tempDataDir = Path.Combine(Path.GetTempPath(), "exo-attacks-data-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDataDir);
Environment.SetEnvironmentVariable("EXO_LAUNCHER_DATA_DIR", tempDataDir);

// Locate the harmless argv-echo stub (built from security/attacks/local/argv-echo).
string? FindArgvEcho()
{
    var roots = new[]
    {
        AppContext.BaseDirectory,
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."),
        Directory.GetCurrentDirectory(),
    };
    foreach (var r in roots)
    {
        var guess = Path.GetFullPath(Path.Combine(r, "security", "attacks", "local", "argv-echo",
            "bin", "Release", "net11.0", "win-x64", "argv-echo.exe"));
        if (File.Exists(guess)) return guess;
    }
    // Last resort: search upward for the repo root.
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        var guess = Path.Combine(dir.FullName, "security", "attacks", "local", "argv-echo");
        if (Directory.Exists(guess))
        {
            var exe = Directory.GetFiles(guess, "argv-echo.exe", SearchOption.AllDirectories);
            if (exe.Length > 0) return exe[0];
        }
        dir = dir.Parent;
    }
    return null;
}

Console.WriteLine("ExoLauncher security attack harness");
Console.WriteLine("data dir: " + tempDataDir);

var argvEcho = FindArgvEcho();
if (argvEcho is null)
{
    Console.WriteLine("SETUP ERROR: argv-echo.exe not found. Build it first:");
    Console.WriteLine("  dotnet build -c Release security/attacks/local/argv-echo/argv-echo.csproj");
    return 2;
}
Console.WriteLine("argv-echo: " + argvEcho);

try
{
    Attack2_ArgInjection.Run(argvEcho);
    Attack3_ProcessOwnership.Run(argvEcho);
    Attack5_DataAtRest.Run(tempDataDir);
}
catch (Exception ex)
{
    Console.WriteLine("HARNESS CRASH: " + ex);
    Check.Failures++;
}

Console.WriteLine();
Console.WriteLine($"==== {Check.Passed} passed, {Check.Failures} failed ====");

try { Directory.Delete(tempDataDir, true); } catch { }

return Check.Failures == 0 ? 0 : 1;
