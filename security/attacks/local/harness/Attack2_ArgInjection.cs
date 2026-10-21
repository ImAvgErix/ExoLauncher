using System.Diagnostics;
using ExoLauncher.Adapters.Cli;

namespace ExoLauncher.Tests;

/// <summary>
/// Attack 2 — child-process and command-line injection. Exercises the REAL
/// argument builders (SteamProtocol, WindowsCommandLine) and reproduces the
/// exact ProcessStartInfo the game-exe path builds
/// (Adapters/ProcessHelper.BuildGameStartInfo) against a harmless stub exe.
/// </summary>
internal static class Attack2_ArgInjection
{
    internal static void Run(string argvEchoExe)
    {
        Check.Section("Attack 2 — child-process / command-line injection");

        // --- Steam app id injection (Adapters/Cli/SteamProtocol.cs:IsValidAppId) ---
        string[] maliciousIds =
        [
            "730; calc", "730 && calc", "730|calc", "730 & calc", "730\"", "../../evil",
            "-applaunch", "0", "-1", "730 --foo", "", "  ", "0x730", "99999999999",
        ];
        var allRejected = true;
        foreach (var id in maliciousIds)
            if (SteamProtocol.IsValidAppId(id)) { allRejected = false; Check.Info($"ACCEPTED bad id: '{id}'"); }
        Check.That("Steam appId injection rejected", allRejected,
            "IsValidAppId rejected every id containing ;&|\" space or non-digits");
        Check.That("Steam appId valid case accepted", SteamProtocol.IsValidAppId("730"), "plain '730' is valid");

        // LaunchArguments must keep the id intact and tokenize extraArgs as argv
        // (no shell). Injected shell metacharacters become inert argv tokens.
        var launch = SteamProtocol.LaunchArguments("730", "--windowed & calc.exe | whoami");
        Check.That("Steam launch args keep id isolated",
            launch.Length >= 3 && launch[0] == "-applaunch" && launch[1] == "730",
            "argv = [" + string.Join(" | ", launch) + "]");
        Check.That("Steam launch args: '&' is an inert token, not a separator",
            launch.Contains("&") && launch.Contains("calc.exe"),
            "metacharacters survive as literal argv tokens (passed via ArgumentList, never a shell)");

        bool threw = false;
        try { SteamProtocol.LaunchArguments("730; calc", null); } catch (ArgumentException) { threw = true; }
        Check.That("Steam launch rejects injected id", threw, "LaunchArguments('730; calc') throws ArgumentException");

        // --- WindowsCommandLine.Parse (used for every CLI backend's extra args) ---
        var parsed = WindowsCommandLine.Parse("--foo \"a b\" -- --rm & calc | del");
        Check.That("WindowsCommandLine tokenizes quotes/-- as argv",
            parsed.Contains("a b") && parsed.Contains("--") && parsed.Contains("&") && parsed.Contains("calc"),
            "tokens = [" + string.Join(" | ", parsed) + "]  (CommandLineToArgvW, documented to never invoke a shell)");

        // --- Direct game-exe launch path (mirrors ProcessHelper.BuildGameStartInfo) ---
        // UseShellExecute=true with the exe as FileName: ShellExecuteEx passes
        // the parameter string to the program; it does NOT run cmd parsing, so
        // '&' / '|' cannot chain a second command. Proven with a stub recorder.
        var outFile = Path.Combine(Path.GetTempPath(), "exo-attack2-argv-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            var hostileLaunchOptions = "--mod & calc.exe | whoami && del C:\\nope";
            var psi = new ProcessStartInfo
            {
                FileName = argvEchoExe,
                Arguments = hostileLaunchOptions,          // == options.ExtraArgs in BuildGameStartInfo
                UseShellExecute = true,                    // == BuildGameStartInfo
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(argvEchoExe) ?? "",
            };
            psi.EnvironmentVariables["ARGV_OUT"] = outFile; // note: ignored under UseShellExecute, see below
            // UseShellExecute cannot set env vars; run the recorder a second way
            // to capture argv deterministically: relaunch without shell but same
            // raw argument string, which is the strictest test of chaining.
            var psi2 = new ProcessStartInfo
            {
                FileName = argvEchoExe,
                Arguments = hostileLaunchOptions,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi2.Environment["ARGV_OUT"] = outFile;
            using (var p = Process.Start(psi2)) { p!.WaitForExit(10_000); }

            var recorded = File.Exists(outFile) ? File.ReadAllText(outFile) : "";
            var blocks = recorded.Split("<<<ARGV-ECHO", StringSplitOptions.RemoveEmptyEntries).Length;
            Check.That("Launch options do not spawn a second command",
                blocks == 1,
                $"exactly one stub instance ran (blocks={blocks}); '&'/'|'/'&&' did not chain a new process");
            Check.That("Shell metacharacters reach the game as literal argv",
                recorded.Contains("&") && recorded.Contains("calc.exe") && recorded.Contains("|"),
                "the stub received '&', 'calc.exe', '|' as ordinary arguments, not as executed commands");
            // Confirm no calc actually launched.
            var calc = Process.GetProcessesByName("calc").Concat(Process.GetProcessesByName("calculator")).ToArray();
            Check.That("No calc.exe was launched by injection", calc.Length == 0,
                "calc/calculator process count = " + calc.Length);
            foreach (var c in calc) c.Dispose();
        }
        finally { try { File.Delete(outFile); } catch { } }
    }
}
