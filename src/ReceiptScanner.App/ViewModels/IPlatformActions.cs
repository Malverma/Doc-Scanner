namespace ReceiptScanner.App.ViewModels;

/// <summary>Things the view model needs from the desktop, kept behind an interface so tests can replace them.</summary>
public interface IPlatformActions
{
    Task<string?> PickFolderAsync(string? startFolder);

    void OpenInFileManager(string folder);

    /// <summary>Opens the folder holding a file, with the file selected where the platform can do that.</summary>
    void ShowFile(string path);

    void OpenFile(string path);
}
