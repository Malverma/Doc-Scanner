using CommunityToolkit.Mvvm.ComponentModel;
using ReceiptScanner.Core.Storage;

namespace ReceiptScanner.App.ViewModels;

public sealed partial class FolderTileViewModel(FolderEntry entry) : ObservableObject
{
    public FolderEntry Entry { get; } = entry;

    public string Name { get; } = entry.Name;

    public string Path { get; } = entry.Path;

    public bool HasSubfolders { get; } = entry.HasSubfolders;

    public string CountText { get; } = entry.ReceiptCount == 1 ? "1 receipt" : $"{entry.ReceiptCount} receipts";

    [ObservableProperty]
    private bool _isTarget;
}

public sealed class ReceiptFileViewModel(ReceiptEntry entry)
{
    public ReceiptEntry Entry { get; } = entry;

    public string Name { get; } = entry.BaseName;

    public IReadOnlyList<string> Files { get; } = entry.Files;

    public string Details { get; } =
        $"{string.Join(" + ", entry.Files.Select(f => System.IO.Path.GetExtension(f).TrimStart('.').ToUpperInvariant()))}, {entry.Modified:g}, {FormatSize(entry.SizeBytes)}";

    private static string FormatSize(long bytes) =>
        bytes < 1024 * 1024 ? $"{Math.Max(1, bytes / 1024)} KB" : $"{bytes / 1048576.0:0.0} MB";
}

public sealed record BreadcrumbViewModel(string Name, string Path, bool IsLast);

public sealed record Choice<T>(string Label, T Value)
{
    public override string ToString() => Label;
}
