using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Adapters;

/// <summary>Handles the requested game's Steam EULA and default launch choice during its handoff.</summary>
internal sealed class SteamGameEulaPromptAutomator : IDisposable
{
    private readonly CancellationTokenSource _lifetime;

    internal SteamGameEulaPromptAutomator(Func<CancellationToken, Task> run, CancellationToken cancellationToken)
    {
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _lifetime.Token;
        _ = Task.Run(async () =>
        {
            try { token.ThrowIfCancellationRequested(); await run(token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error) { AppLog.Debug("Steam game EULA watcher: " + error.Message); }
            finally { _lifetime.Dispose(); }
        });
    }

    internal static SteamGameEulaPromptAutomator? Arm(string steamExe, string gameTitle, TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameTitle) || gameTitle.Length > 300 ||
            !Path.IsPathFullyQualified(steamExe) ||
            !Path.GetFileName(steamExe).Equals("steam.exe", StringComparison.OrdinalIgnoreCase)) return null;
        var seconds = Math.Clamp((int)Math.Ceiling(duration.TotalSeconds), 5, 720);
        return new SteamGameEulaPromptAutomator(token => RunAsync(BuildScript(steamExe, gameTitle, seconds), seconds, token), cancellationToken);
    }

    public void Dispose()
    {
        try { _lifetime.Cancel(); } catch (ObjectDisposedException) { }
    }

    internal static string BuildScript(string steamExe, string gameTitle, int seconds)
    {
        using var parent = Process.GetCurrentProcess();
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            steamExe = Path.GetFullPath(steamExe), gameTitle,
            seconds = Math.Clamp(seconds, 5, 720), parentId = Environment.ProcessId,
            parentStarted = parent.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
        })));
        return "& {\n" + SelectionScript + "\n" + RuntimeScript.Replace("__PAYLOAD__", payload, StringComparison.Ordinal) + "\n}\n";
    }

    private static async Task RunAsync(string script, int seconds, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
                Arguments = "-NoLogo -NoProfile -STA -NonInteractive -Command \"& ([scriptblock]::Create([Console]::In.ReadToEnd()))\"",
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            },
        };
        if (!process.Start()) return;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds + 3));
        void StopOwnedWatcher() { try { if (!process.HasExited) ProcessTermination.Kill(process); } catch { } }
        using var stop = timeout.Token.Register(StopOwnedWatcher);
        try
        {
            await process.StandardInput.WriteAsync(script.AsMemory(), timeout.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        finally
        {
            StopOwnedWatcher();
            // Drain both pipes, including cancellation/startup failures. A bad
            // accessibility provider cannot keep the launch handoff waiting.
            try { await Task.WhenAll(output, errors).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        }
        if (output.IsCompletedSuccessfully && output.Result.Contains("accepted-game-eula", StringComparison.Ordinal))
            AppLog.Info("Accepted the requested game's Steam EULA.");
        if (output.IsCompletedSuccessfully && output.Result.Contains("accepted-game-launch", StringComparison.Ordinal))
            AppLog.Info("Continued the requested game's Steam launch choice.");
    }

    // Pure selection functions are shared verbatim with fixture tests. They do
    // not access processes, windows, accessibility APIs, or agreement state.
    internal const string SelectionScript = """
        function Normalize-EulaText([string]$value) {
          if ([string]::IsNullOrWhiteSpace($value)) { return '' }
          return [regex]::Replace($value, '\s+', ' ').Trim()
        }
        function Test-SteamEulaOwnerPath([string]$steamExe, [string]$ownerPath) {
          try {
            if (-not [IO.Path]::IsPathRooted($steamExe) -or -not [IO.Path]::IsPathRooted($ownerPath)) { return $false }
            $expected = [IO.Path]::GetFullPath($steamExe)
            $actual = [IO.Path]::GetFullPath($ownerPath)
            if ([IO.Path]::GetFileName($expected) -ine 'steam.exe') { return $false }
            if ([string]::Equals($expected, $actual, [StringComparison]::OrdinalIgnoreCase)) { return $true }
            $cefRoot = [IO.Path]::Combine([IO.Path]::GetDirectoryName($expected), 'bin', 'cef') + [IO.Path]::DirectorySeparatorChar
            return [IO.Path]::GetFileName($actual) -ieq 'steamwebhelper.exe' -and $actual.StartsWith($cefRoot, [StringComparison]::OrdinalIgnoreCase)
          } catch { return $false }
        }
        function Select-SteamGameEulaAccept([string]$steamExe, [string]$gameTitle, $dialog) {
          $title = Normalize-EulaText $gameTitle
          if (-not $title -or $title.Length -gt 300 -or $dialog.IsDialog -ne $true -or $dialog.Complete -ne $true) { return -1 }
          if (-not (Test-SteamEulaOwnerPath $steamExe ([string]$dialog.OwnerPath))) { return -1 }
          $intro = 'Please read this agreement in its entirety. You must agree to the terms of the EULA to play ' + $title
          $hasHeader = $false; $hasIntro = $false
          $excluded = @('Steam Subscriber Agreement','Subscriber Agreement','Account Agreement','Create Account','Create an Account','Purchase','Confirm Purchase','Checkout','Payment','Terms of Service')
          foreach ($text in @($dialog.Names)) {
            $name = Normalize-EulaText ([string]$text)
            if ($excluded -contains $name) { return -1 }
            if ([string]::Equals($name, 'EULA', [StringComparison]::OrdinalIgnoreCase)) { $hasHeader = $true }
            if ([string]::Equals($name, $intro, [StringComparison]::OrdinalIgnoreCase)) { $hasIntro = $true }
          }
          if (-not $hasHeader -or -not $hasIntro) { return -1 }
          $selected = -1
          for ($index = 0; $index -lt @($dialog.Buttons).Count; $index++) {
            $button = $dialog.Buttons[$index]
            if (-not [string]::Equals((Normalize-EulaText ([string]$button.Name)), 'Accept', [StringComparison]::OrdinalIgnoreCase)) { continue }
            if ($button.Enabled -ne $true -or $button.Invokable -ne $true -or
                -not (Test-SteamEulaOwnerPath $steamExe ([string]$button.OwnerPath))) { return -1 }
            if ($selected -ge 0) { return -1 }
            $selected = $index
          }
          return $selected
        }
        function Test-SteamEulaDocumentReady([string]$signature, $acceptedDocuments, [int]$attempts, [DateTime]$nextAcceptAt, [DateTime]$now) {
          return $signature -and $acceptedDocuments.Count -lt 8 -and $attempts -lt 24 -and
                 $now -ge $nextAcceptAt -and -not $acceptedDocuments.Contains($signature)
        }
        function Select-SteamGameLaunch([string]$steamExe, [string]$gameTitle, $dialog) {
          $title = Normalize-EulaText ($gameTitle -replace '[\u00AE\u2122]', '')
          if (-not $title -or $dialog.IsDialog -ne $true -or $dialog.Complete -ne $true -or
              -not (Test-SteamEulaOwnerPath $steamExe ([string]$dialog.OwnerPath))) { return -1 }
          $hasGame = $false; $hasChoice = $false
          foreach ($text in @($dialog.Names)) {
            $name = Normalize-EulaText (([string]$text) -replace '[\u00AE\u2122]', '')
            if ($name -match '(?i)purchase|checkout|payment|account|password|sign in|log in|cloud conflict|out of sync|upload|download.*cloud|delete|overwrite|security|allow changes') { return -1 }
            if ($name -ieq $title -or $name -ieq ('Play ' + $title) -or $name -ieq ('Launch ' + $title)) { $hasGame = $true }
            if ($name -match '^(?i:launch options?|select (?:a )?launch option|choose (?:a )?launch option|select an option|play)\b') { $hasChoice = $true }
          }
          if (-not $hasGame -or -not $hasChoice) { return -1 }
          $selected = -1
          for ($index = 0; $index -lt @($dialog.Buttons).Count; $index++) {
            $button = $dialog.Buttons[$index]
            if ((Normalize-EulaText ([string]$button.Name)) -notmatch '^(?i:Play|Launch|Start Game)$') { continue }
            if ($button.Enabled -ne $true -or $button.Invokable -ne $true -or
                -not (Test-SteamEulaOwnerPath $steamExe ([string]$button.OwnerPath)) -or $selected -ge 0) { return -1 }
            $selected = $index
          }
          return $selected
        }
        """;

    private const string RuntimeScript = """
        $ErrorActionPreference = 'Stop'
        $config = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('__PAYLOAD__')) | ConvertFrom-Json
        Add-Type -AssemblyName UIAutomationClient
        Add-Type -AssemblyName UIAutomationTypes
        $deadline = [DateTime]::UtcNow.AddSeconds([int]$config.seconds)
        $acceptedDocuments = New-Object 'System.Collections.Generic.HashSet[string]'
        $attempts = 0; $nextAcceptAt = [DateTime]::MinValue
        $walker = [System.Windows.Automation.TreeWalker]::RawViewWalker
        function Test-EulaParentAlive {
          $owner = $null
          try {
            $owner = [Diagnostics.Process]::GetProcessById([int]$config.parentId)
            return -not $owner.HasExited -and $owner.StartTime.ToUniversalTime().Ticks.ToString() -eq [string]$config.parentStarted
          } catch { return $false } finally { if ($owner) { $owner.Dispose() } }
        }
        function Get-EulaOwnerPath($element) {
          $owner = $null
          try {
            $owner = [Diagnostics.Process]::GetProcessById([int]$element.Current.ProcessId)
            if ($owner.HasExited -or @('steam','steamwebhelper') -notcontains $owner.ProcessName) { return $null }
            $path = $owner.MainModule.FileName
            if (Test-SteamEulaOwnerPath ([string]$config.steamExe) $path) { return $path }
          } catch { } finally { if ($owner) { $owner.Dispose() } }
          return $null
        }
        function Read-EulaTree($element, [int]$limit, [int]$maxDepth) {
          $queue = New-Object 'System.Collections.Generic.Queue[object]'
          $nodes = New-Object 'System.Collections.Generic.List[object]'
          $queue.Enqueue(@($element, 0)); $complete = $true; $scheduled = 1
          while ($queue.Count -gt 0 -and $nodes.Count -lt $limit -and [DateTime]::UtcNow -lt $deadline) {
            $pair = $queue.Dequeue(); $current = $pair[0]; $depth = [int]$pair[1]
            $nodes.Add($current)
            try {
              $child = $walker.GetFirstChild($current)
              if ($child -and $depth -ge $maxDepth) { $complete = $false; continue }
              while ($child) {
                if ($scheduled -ge $limit -or [DateTime]::UtcNow -ge $deadline) { $complete = $false; break }
                $queue.Enqueue(@($child, $depth + 1)); $scheduled++
                $child = $walker.GetNextSibling($child)
              }
            } catch { $complete = $false }
          }
          if ($queue.Count -gt 0 -or [DateTime]::UtcNow -ge $deadline) { $complete = $false }
          return [pscustomobject]@{ Nodes = $nodes.ToArray(); Complete = $complete }
        }
        function Test-EulaDialog($element) {
          try {
            if ($element.Current.LocalizedControlType -ieq 'dialog') { return $true }
            if ($element.Current.ControlType -eq [System.Windows.Automation.ControlType]::Window) {
              $name = Normalize-EulaText (([string]$element.Current.Name) -replace '[\u00AE\u2122]', '')
              $game = Normalize-EulaText (([string]$config.gameTitle) -replace '[\u00AE\u2122]', '')
              if ($name -ieq 'EULA' -or $name -ieq $game -or $name -ieq ('Play ' + $game)) { return $true }
            }
            return $false
          } catch { return $false }
        }
        function Read-EulaDialog($element) {
          $tree = Read-EulaTree $element 512 20
          $names = New-Object 'System.Collections.Generic.List[string]'
          $buttons = New-Object 'System.Collections.Generic.List[object]'
          foreach ($node in $tree.Nodes) {
            try {
              $name = [string]$node.Current.Name
              if ($name.Length -gt 4096) { $name = $name.Substring(0,4096) }
              $names.Add($name)
              if ($node.Current.ControlType -ne [System.Windows.Automation.ControlType]::Button) { continue }
              $pattern = $null
              $invokable = $node.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)
              $buttons.Add([pscustomobject]@{ Name = $name; Enabled = $node.Current.IsEnabled; Invokable = $invokable; OwnerPath = (Get-EulaOwnerPath $node); Element = $node })
            } catch { $tree.Complete = $false }
          }
          $text = [string]::Join('|', $names.ToArray())
          if ($text.Length -gt 32768) { $text = $text.Substring(0,32768) }
          $text += '|' + [string]::Join(',', $element.GetRuntimeId())
          $hash = [Security.Cryptography.SHA256]::Create()
          try { $signature = [Convert]::ToBase64String($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($text))) } finally { $hash.Dispose() }
          return [pscustomobject]@{ IsDialog = (Test-EulaDialog $element); Complete = $tree.Complete; OwnerPath = (Get-EulaOwnerPath $element); Names = $names.ToArray(); Buttons = $buttons.ToArray(); Signature = $signature }
        }
        for ($poll = 0; $poll -lt 720 -and [DateTime]::UtcNow -lt $deadline -and $acceptedDocuments.Count -lt 8 -and $attempts -lt 24; $poll++) {
          if (-not (Test-EulaParentAlive)) { exit 0 }
          $searchBudget = 384; $invoked = $false
          try {
            $window = $walker.GetFirstChild([System.Windows.Automation.AutomationElement]::RootElement)
            for ($windowCount = 0; $window -and $windowCount -lt 80 -and [DateTime]::UtcNow -lt $deadline; $windowCount++) {
              if (Get-EulaOwnerPath $window) {
                # A dedicated EULA window needs no library traversal. Generic
                # Steam windows share a small discovery budget for this poll.
                if (Test-EulaDialog $window) { $scopes = @($window) }
                elseif ($searchBudget -gt 0) {
                  $tree = Read-EulaTree $window $searchBudget 18
                  $searchBudget -= $tree.Nodes.Count
                  $scopes = @($tree.Nodes | Where-Object { Test-EulaDialog $_ })
                } else { $scopes = @() }
                foreach ($element in $scopes) {
                  $dialog = Read-EulaDialog $element
                  $index = Select-SteamGameEulaAccept ([string]$config.steamExe) ([string]$config.gameTitle) $dialog
                  if ($index -lt 0) { $index = Select-SteamGameLaunch ([string]$config.steamExe) ([string]$config.gameTitle) $dialog }
                  if ($index -lt 0) { continue }
                  if (-not (Test-SteamEulaDocumentReady $dialog.Signature $acceptedDocuments $attempts $nextAcceptAt ([DateTime]::UtcNow))) { continue }
                  # Re-read the complete scope immediately before invoking. A
                  # replaced, disabled, ambiguous, or unrelated dialog is rejected.
                  $dialog = Read-EulaDialog $element
                  $index = Select-SteamGameEulaAccept ([string]$config.steamExe) ([string]$config.gameTitle) $dialog
                  $kind = 'accepted-game-eula'
                  if ($index -lt 0) { $index = Select-SteamGameLaunch ([string]$config.steamExe) ([string]$config.gameTitle) $dialog; $kind = 'accepted-game-launch' }
                  if ($index -lt 0 -or [DateTime]::UtcNow -ge $deadline -or -not (Test-EulaParentAlive)) { continue }
                  if (-not (Test-SteamEulaDocumentReady $dialog.Signature $acceptedDocuments $attempts $nextAcceptAt ([DateTime]::UtcNow))) { continue }
                  try {
                    $attempts++; $nextAcceptAt = [DateTime]::UtcNow.AddSeconds(1)
                    $pattern = $dialog.Buttons[$index].Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
                    ([System.Windows.Automation.InvokePattern]$pattern).Invoke()
                    $null = $acceptedDocuments.Add($dialog.Signature)
                    Write-Output $kind
                    $invoked = $true
                    break
                  } catch { }
                }
              }
              if ($invoked) { break }
              $window = $walker.GetNextSibling($window)
            }
          } catch { }
          Start-Sleep -Milliseconds 1000
        }
        Write-Output 'no-recognized-game-eula'
        """;
}
