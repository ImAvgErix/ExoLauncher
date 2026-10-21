namespace ExoLauncher.Tests;

/// <summary>Minimal assert harness (no NuGet). Records results and evidence.</summary>
internal static class Check
{
    internal static int Failures;
    internal static int Passed;

    internal static void That(string name, bool condition, string evidence)
    {
        if (condition) { Passed++; Console.WriteLine($"  PASS  {name}  ::  {evidence}"); }
        else { Failures++; Console.WriteLine($"  FAIL  {name}  ::  {evidence}"); }
    }

    internal static void Info(string line) => Console.WriteLine("        " + line);

    internal static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine("== " + title + " ==");
    }
}
