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

public sealed record BreadcrumbViewModel(string Name, string Path, bool IsLast);

public sealed record Choice<T>(string Label, T Value)
{
    public override string ToString() => Label;
}
