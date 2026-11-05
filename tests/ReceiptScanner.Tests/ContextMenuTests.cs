using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ReceiptScanner.App.ViewModels;
using ReceiptScanner.App.Views;
using ReceiptScanner.Core.Scanning;
using ReceiptScanner.Core.Storage;

namespace ReceiptScanner.Tests;

/// <summary>Opens the real right-click menus, which live in a popup outside the window's visual tree.</summary>
public class ContextMenuTests
{
    private static (MainWindow Window, MainViewModel Vm, FakePlatform Platform, TempDir Dir) Open()
    {
        var dir = new TempDir();
        var paths = new AppPaths(dir.Sub("data"));
        var root = dir.Sub("Receipts");
        Directory.CreateDirectory(Path.Combine(root, "Gas"));
        File.WriteAllText(Path.Combine(root, "Gas", "receipt_2026-11-05_10-00-00.pdf"), "");
        var platform = new FakePlatform();
        var vm = new MainViewModel(new SimulatedBackend(), paths, new AppLog(paths.LogFolder), new AppSettings { BaseFolder = root }, platform);
        vm.TileClicked(vm.Tiles.First(t => t.Name == "Gas"));
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, vm, platform, dir);
    }

    private static List<MenuItem> OpenMenuOn<T>(MainWindow window, Func<T, bool> match) where T : Control
    {
        var owner = window.GetVisualDescendants().OfType<T>().First(c => c.ContextMenu is not null && match(c));
        owner.ContextMenu!.Open(owner);
        Dispatcher.UIThread.RunJobs();
        return owner.ContextMenu.GetLogicalDescendants().OfType<MenuItem>().ToList();
    }

    [AvaloniaFact]
    public void Receipt_menu_items_are_enabled_and_run()
    {
        var (window, vm, platform, dir) = Open();
        using (dir)
        {
            var items = OpenMenuOn<Border>(window, b => b.DataContext is ReceiptFileViewModel);

            Assert.Equal(["Open", "Show in File Explorer", "Delete"], items.Select(i => i.Header as string));
            Assert.All(items, i => Assert.True(i.IsEffectivelyEnabled, $"{i.Header} is disabled"));

            items[1].Command!.Execute(items[1].CommandParameter);
            Assert.EndsWith(".pdf", platform.Opened);
            Assert.Single(vm.Receipts);
        }
    }

    [AvaloniaFact]
    public void Folder_tile_menu_items_are_enabled()
    {
        var (window, _, _, dir) = Open();
        using (dir)
        {
            var items = OpenMenuOn<Button>(window, b => b.DataContext is FolderTileViewModel);

            Assert.Equal(4, items.Count);
            Assert.All(items, i => Assert.True(i.IsEffectivelyEnabled, $"{i.Header} is disabled"));
        }
    }
}
