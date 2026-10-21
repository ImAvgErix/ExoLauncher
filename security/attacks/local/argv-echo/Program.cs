// Harmless argv recorder. Writes each received argument on its own line to the
// path in ARGV_OUT, plus a marker proving THIS process ran (so a test can tell
// whether a would-be injected second command spawned a second instance).
var outPath = Environment.GetEnvironmentVariable("ARGV_OUT");
if (!string.IsNullOrEmpty(outPath))
{
    var lines = new List<string> { "PID=" + Environment.ProcessId };
    lines.AddRange(args);
    // Append so a second spawned instance would be visible as extra blocks.
    File.AppendAllText(outPath, "<<<ARGV-ECHO " + Environment.ProcessId + ">>>\n" + string.Join("\n", lines) + "\n<<<END>>>\n");
    return 0;
}

// No ARGV_OUT: act as a long-lived target process for ownership/kill tests.
Console.WriteLine("argv-echo alive pid=" + Environment.ProcessId);
Thread.Sleep(Timeout.Infinite);
return 0;
