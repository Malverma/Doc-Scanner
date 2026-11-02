using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ReceiptScanner.App.ViewModels;

namespace ReceiptScanner.App.Views;

public partial class MainWindow : Window
{
    private readonly CancellationTokenSource _closing = new();

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
