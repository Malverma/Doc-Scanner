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

    public void ShowFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // Explorer needs the quotes inside this one argument, so ArgumentList can't be used.
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false })?.Dispose();
            return;
        }

        OpenInFileManager(Path.GetDirectoryName(path)!);
    }

    public void OpenFile(string path)
    {
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
    }
}
