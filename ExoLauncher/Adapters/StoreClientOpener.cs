using System.Diagnostics;
using ExoLauncher.Adapters.Cli;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Adapters;

internal sealed record StoreClientOpenResult(bool Ok, string Message);

internal interface IStoreClientOpenRuntime
{
    bool Exists(StoreClientLaunchCommand command);
    void AllowUserOpen(StoreKind store);
    void Dispatch(StoreKind store, StoreClientLaunchCommand command);
    bool RevealAndCheck(StoreClientLaunchCommand command, IReadOnlyList<string> processNames);
    Task Delay(CancellationToken ct);
}

/// <summary>Explicit client opening succeeds only after its window is available.</summary>
internal static class StoreClientOpener
{
    internal static async Task<InstallResult> UninstallHandoffAsync(StoreKind store, string label,
        string executable, IReadOnlyList<string> processNames, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var opened = await OpenAsync(store, label, new StoreClientLaunchCommand(executable), processNames, ct)
            .ConfigureAwait(false);
        return new InstallResult
        {
            Ok = opened.Ok,
            HandoffOnly = opened.Ok,
            Message = opened.Ok
                ? $"Confirm removal of the requested game in {label}. Exo has not confirmed that it was removed."
                : $"Removal was not confirmed. {opened.Message}",
        };
    }

    internal static Task<StoreClientOpenResult> OpenAsync(StoreKind store, string label,
        StoreClientLaunchCommand? command, IReadOnlyList<string> processNames,
        CancellationToken ct = default) =>
        OpenAsync(store, label, command, processNames, new WindowsClientOpenRuntime(), ct);

    internal static async Task<StoreClientOpenResult> OpenAsync(StoreKind store, string label,
        StoreClientLaunchCommand? command, IReadOnlyList<string> processNames,
        IStoreClientOpenRuntime runtime, CancellationToken ct = default, int attempts = 30)
    {
        if (command is null || !runtime.Exists(command)) return new(false, label + " is not installed.");
        try
        {
            ct.ThrowIfCancellationRequested();
            runtime.AllowUserOpen(store);
            // A single-instance shell activation need not return a process ID.
            runtime.Dispatch(store, command);
            for (var attempt = 0; attempt < attempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                if (runtime.RevealAndCheck(command, processNames)) return new(true, "Opened " + label + ".");
                if (attempt + 1 < attempts) await runtime.Delay(ct).ConfigureAwait(false);
            }
            return new(false, label + " accepted the request, but its window did not appear. Try opening it again.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return new(false, "Opening canceled."); }
        catch (Exception ex)
        {
            AppLog.Warn($"Open {store}: {ex.GetType().Name} (0x{ex.HResult:X8}): {ex.Message}");
            return new(false, label + " could not be opened.");
        }
    }

    internal static async Task RevealPendingAsync(StoreKind store, StoreClientLaunchCommand command,
        IReadOnlyList<string> processNames)
    {
        var runtime = new WindowsClientOpenRuntime();
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                runtime.AllowUserOpen(store);
                if (runtime.RevealAndCheck(command, processNames)) return;
                await runtime.Delay(CancellationToken.None).ConfigureAwait(false);
            }
            catch { return; }
        }
    }

    private sealed class WindowsClientOpenRuntime : IStoreClientOpenRuntime
    {
        public bool Exists(StoreClientLaunchCommand command) => command.IsAppx ||
            !string.IsNullOrWhiteSpace(command.FileName) && File.Exists(command.FileName);
        public void AllowUserOpen(StoreKind store) => HiddenStoreRuntime.AllowUserOpen(store);
        public void Dispatch(StoreKind store, StoreClientLaunchCommand command)
        {
            using var started = Process.Start(new ProcessStartInfo
            {
                FileName = command.FileName,
                Arguments = store == StoreKind.Steam ? SteamProtocol.OpenMainUri() : command.Arguments,
                UseShellExecute = true,
                WorkingDirectory = command.IsAppx ? "" : Path.GetDirectoryName(command.FileName) ?? "",
            });
        }
        public bool RevealAndCheck(StoreClientLaunchCommand command, IReadOnlyList<string> processNames) =>
            StoreWindowHider.RevealClientWindows(command, processNames);
        public Task Delay(CancellationToken ct) => Task.Delay(500, ct);
    }
}
