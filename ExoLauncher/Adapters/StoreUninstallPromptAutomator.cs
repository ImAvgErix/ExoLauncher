using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Adapters;

/// <summary>
/// Confirms native store uninstall prompts after the user has explicitly clicked
/// Remove in Exo. Uses UI Automation only: no foreground activation or cursor input.
/// </summary>
internal static class StoreUninstallPromptAutomator
{
    public static void Arm(string gameTitle, TimeSpan duration, params string[] processNames)
    {
        if (processNames.Length == 0) return;
        var targets = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(processNames)));
        var title = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(gameTitle ?? "")));
        var seconds = Math.Clamp((int)Math.Ceiling(duration.TotalSeconds), 10, 180);
        var script = Script
            .Replace("__TARGETS__", targets, StringComparison.Ordinal)
            .Replace("__TITLE__", title, StringComparison.Ordinal)
            .Replace("__SECONDS__", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

        var path = Path.Combine(Path.GetTempPath(), "exo-uninstall-" + Guid.NewGuid().ToString("N") + ".ps1");
        try
        {
            File.WriteAllText(path, script, Encoding.UTF8);
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -STA -ExecutionPolicy Bypass -File \"{path}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null)
            {
                File.Delete(path);
                return;
            }

            _ = Task.Run(() =>
            {
                using (process)
                {
                    try
                    {
                        process.WaitForExit((seconds + 8) * 1000);
                        var output = process.StandardOutput.ReadToEnd().Trim();
                        if (output.Contains("clicked", StringComparison.OrdinalIgnoreCase))
                            AppLog.Info($"Silently confirmed the {gameTitle} store uninstall prompt.");
                    }
                    catch (Exception ex) { AppLog.Debug("Uninstall prompt automation: " + ex.Message); }
                    finally
                    {
                        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                        try { File.Delete(path); } catch { }
                    }
                }
            });
        }
        catch (Exception ex)
        {
            AppLog.Debug("Uninstall prompt automation start: " + ex.Message);
            try { File.Delete(path); } catch { }
        }
    }

    private const string Script =
        """
        Add-Type -AssemblyName UIAutomationClient
        Add-Type -AssemblyName UIAutomationTypes
        $ErrorActionPreference = 'SilentlyContinue'
        $targetsJson = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('__TARGETS__'))
        $titleJson = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('__TITLE__'))
        $targets = @($targetsJson | ConvertFrom-Json)
        $gameTitle = [string]($titleJson | ConvertFrom-Json)
        $deadline = [DateTime]::UtcNow.AddSeconds(__SECONDS__)
        $clicked = $false
        $actions = @('uninstall', 'uninstall now', 'remove', 'remove now', 'yes', 'confirm', 'continue', 'ok')
        $walker = [System.Windows.Automation.TreeWalker]::RawViewWalker
        function Normalize-UiText([string]$value) {
          if ([string]::IsNullOrWhiteSpace($value)) { return '' }
          return ([regex]::Replace($value.ToLowerInvariant(), '[^\p{L}\p{Nd}]', ''))
        }
        $normalizedTitle = Normalize-UiText $gameTitle
        if ([string]::IsNullOrWhiteSpace($normalizedTitle)) {
          Write-Output 'invalid-game-title'
          exit 2
        }

        while ([DateTime]::UtcNow -lt $deadline) {
          $root = [System.Windows.Automation.AutomationElement]::RootElement
          $wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
          foreach ($win in @($wins)) {
            try {
              $ownerPid = [int]$win.Current.ProcessId
              $procName = [Diagnostics.Process]::GetProcessById($ownerPid).ProcessName
              if ($targets -notcontains $procName) { continue }

              $elements = New-Object System.Collections.Generic.List[object]
              $names = New-Object System.Collections.Generic.List[string]
              $stack = New-Object System.Collections.Generic.Stack[object]
              $stack.Push(@($win, 0))
              while ($stack.Count -gt 0) {
                $pair = $stack.Pop(); $el = $pair[0]; $depth = [int]$pair[1]
                if ($depth -gt 45) { continue }
                try {
                  $elements.Add($el)
                  $name = [string]$el.Current.Name
                  if ($name) { $names.Add($name) }
                  $child = $walker.GetFirstChild($el)
                  while ($null -ne $child) {
                    $stack.Push(@($child, $depth + 1))
                    $child = $walker.GetNextSibling($child)
                  }
                } catch { }
              }

              $context = [string]::Join(' ', $names)
              if ($context -notmatch '(?i)uninstall|remove') { continue }
              # Never accept a generic prompt from the store process. The
              # accessible dialog text must identify the exact requested game.
              $normalizedContext = Normalize-UiText $context
              if (-not $normalizedContext.Contains($normalizedTitle)) { continue }
              foreach ($el in $elements) {
                try {
                  $name = ([string]$el.Current.Name).Trim().ToLowerInvariant()
                  if ($actions -notcontains $name) { continue }
                  $pattern = $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
                  if ($null -eq $pattern) { continue }
                  ([System.Windows.Automation.InvokePattern]$pattern).Invoke()
                  Write-Output ('clicked:' + $name)
                  $clicked = $true
                  Start-Sleep -Milliseconds 850
                  break
                } catch { }
              }
            } catch { }
          }
          Start-Sleep -Milliseconds 350
        }
        if ($clicked) { exit 0 }
        Write-Output 'no-uninstall-prompt'
        exit 1
        """;
}
