using ExoLauncher.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ExoLauncher;

public sealed partial class MainWindow
{
    /// <summary>
    /// A question Exo's own window asks before something that costs money or can't be taken back, outside the page: a page
    /// that is no longer trustworthy can't answer it. False when declined, closed over, or the window can't show it.
    /// </summary>
    internal async Task<bool> ConfirmNativeAsync(string title, string body, string action)
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () => answer.TrySetResult(await ShowConfirmAsync(title, body, action))))
            return false;
        return await answer.Task.ConfigureAwait(false);
    }

    private async Task<bool> ShowConfirmAsync(string title, string body, string action)
    {
        try
        {
            if (_closingWindow || WebHost.XamlRoot is not { } root) return false;
            await _connectedDialogGate.WaitAsync(_lifetimeCts.Token);
            try
            {
                if (_closingWindow || WebHost.XamlRoot is null) return false;
                var dialog = new ContentDialog
                {
                    XamlRoot = root, Title = title,
                    Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
                    PrimaryButtonText = action, CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close,
                };
                ExoConnectedTheme.Dialog(dialog);
                return await dialog.ShowAsync() == ContentDialogResult.Primary;
            }
            finally { _connectedDialogGate.Release(); }
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or System.Runtime.InteropServices.COMException) { return false; }
    }
}
