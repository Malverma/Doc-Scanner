using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ReceiptScanner.App.ViewModels;

namespace ReceiptScanner.App.Views;

public partial class MainWindow : Window
{
    private const double DragThreshold = 6;

    private readonly CancellationTokenSource _closing = new();
    private PointerPressedEventArgs? _press;
    private Point _pressPoint;
    private ReceiptFileViewModel? _dragged;

    public MainWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

        Opened += (_, _) =>
        {
            if (DataContext is MainViewModel viewModel)
            {
                _ = viewModel.MonitorScannerAsync(_closing.Token);
            }
        };
        Activated += (_, _) => ViewModel?.Refresh();
        Closed += (_, _) => _closing.Cancel();
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    /// <summary>Space scans from anywhere except a text box or a dialog, so batches need no mouse.</summary>
    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } viewModel || e.Key != Key.Space || viewModel.IsNamePromptOpen || viewModel.IsSettingsOpen)
        {
            return;
        }

        if (viewModel.CanScan)
        {
            viewModel.ScanCommand.Execute(null);
        }

        e.Handled = true;
    }

    private void OnTileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Button { DataContext: FolderTileViewModel tile })
        {
            ViewModel?.OpenTileCommand.Execute(tile);
        }
    }

    private void OnReceiptPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control control && e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
        {
            _press = e;
            _pressPoint = e.GetPosition(this);
        }
    }

    private void OnReceiptPointerReleased(object? sender, PointerReleasedEventArgs e) => _press = null;

    private void OnReceiptDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: ReceiptFileViewModel receipt })
        {
            ViewModel?.OpenReceiptCommand.Execute(receipt);
        }
    }

    /// <summary>The drag waits for real movement, because starting one on press would swallow double-clicks.</summary>
    private async void OnReceiptPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_press is not { } press || sender is not Control { DataContext: ReceiptFileViewModel receipt })
        {
            return;
        }

        var moved = e.GetPosition(this) - _pressPoint;
        if (Math.Abs(moved.X) < DragThreshold && Math.Abs(moved.Y) < DragThreshold)
        {
            return;
        }

        _press = null;
        _dragged = receipt;
        try
        {
            // Copy only: Explorer treats a same-drive move as the default, which would take the receipt
            // out of its folder. Dropping on a folder tile in this app is handled as a move below.
            var data = new DataTransfer();
            foreach (var path in receipt.Files)
            {
                if (await StorageProvider.TryGetFileFromPathAsync(path) is { } file)
                {
                    data.Add(DataTransferItem.CreateFile(file));
                }
            }

            await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Copy);
        }
        catch (Exception ex)
        {
            // An async void handler has nowhere to send this, and it would otherwise end the app.
            ViewModel?.ReportDragFailure(ex);
        }
        finally
        {
            _dragged = null;
        }
    }

    private static string? FolderOf(object? target) => (target as Control)?.DataContext switch
    {
        FolderTileViewModel tile => tile.Path,
        BreadcrumbViewModel crumb => crumb.Path,
        _ => null,
    };

    private void OnFolderDragEnter(object? sender, DragEventArgs e)
    {
        if (_dragged is not null && sender is Button button)
        {
            button.Classes.Set("dropTarget", true);
        }

        OnFolderDragOver(sender, e);
    }

    /// <summary>Only receipts dragged from this window can land here, since this moves files.</summary>
    private void OnFolderDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = _dragged is not null && FolderOf(sender) is not null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnFolderDragLeave(object? sender, DragEventArgs e)
    {
        if (sender is Button button)
        {
            button.Classes.Set("dropTarget", false);
        }
    }

    private void OnFolderDrop(object? sender, DragEventArgs e)
    {
        OnFolderDragLeave(sender, e);
        if (_dragged is { } receipt && FolderOf(sender) is { } folder)
        {
            ViewModel?.MoveReceiptTo(receipt, folder);
        }

        e.Handled = true;
    }

    private void OnNameBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ViewModel?.CommitNamePromptCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            ViewModel?.CancelNamePromptCommand.Execute(null);
            e.Handled = true;
        }
    }

    protected override void OnPropertyChanged(Avalonia.AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DataContextProperty && change.NewValue is MainViewModel viewModel)
        {
            viewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MainViewModel.IsNamePromptOpen) && viewModel.IsNamePromptOpen)
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        NameBox.Focus();
                        NameBox.SelectAll();
                    });
                }
            };
        }
    }
}
