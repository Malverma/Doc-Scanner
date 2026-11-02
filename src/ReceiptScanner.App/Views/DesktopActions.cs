using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ReceiptScanner.App.ViewModels;

namespace ReceiptScanner.App.Views;

public sealed class DesktopActions(Window window) : IPlatformActions
{
    public async Task<string?> PickFolderAsync(string? startFolder)
    {
        var options = new FolderPickerOpenOptions { Title = "Choose the base folder for receipts", AllowMultiple = false };
        if (startFolder is not null)
        {
            options.SuggestedStartLocation = await window.StorageProvider.TryGetFolderFromPathAsync(startFolder);
        }

        var folders = await window.StorageProvider.OpenFolderPickerAsync(options);
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public void OpenInFileManager(string folder)
    {
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true })?.Dispose();
    }
}
