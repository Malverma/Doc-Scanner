using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using ReceiptScanner.App.ViewModels;
using ReceiptScanner.App.Views;
using ReceiptScanner.Core.Scanning;
using ReceiptScanner.Core.Storage;

namespace ReceiptScanner.Tests;

/// <summary>Writes screenshots when RS_SCREENSHOTS names a folder. Otherwise these do nothing.</summary>
public class ScreenshotTests
{
    private static readonly string? Output = Environment.GetEnvironmentVariable("RS_SCREENSHOTS");

    [AvaloniaFact]
    public async Task Capture_every_main_state()
    {
        if (Output is null)
        {
            return;
        }

        Directory.CreateDirectory(Output);
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Sub("data"));
        var root = dir.Sub("Receipts");
        foreach (var name in new[] { "Grocery", "Gas", "Meals", "Office supplies", "Travel", "Medical and prescriptions" })
        {
            Directory.CreateDirectory(Path.Combine(root, name));
        }

        Directory.CreateDirectory(Path.Combine(root, "Travel", "2026"));
        for (var i = 0; i < 12; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(root, "Gas", $"r{i}.pdf"), "");
        }

        var backend = new SimulatedBackend(SimulatedBehavior.Normal, 420, TimeSpan.Zero);
        var vm = new MainViewModel(backend, paths, new AppLog(paths.LogFolder), new AppSettings { BaseFolder = root }, new FakePlatform());
        await vm.ProbeScannerAsync();
        var window = new MainWindow { DataContext = vm };
        window.Show();

        void Shot(string name)
        {
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame()!.Save(Path.Combine(Output!, name + ".png"));
        }

        vm.TileClicked(vm.Tiles.First(t => t.Name == "Grocery"));
        Shot("1-idle");

        await vm.ScanAsync();
        Shot("2-saved");

        backend.Behavior = SimulatedBehavior.Jam;
        await vm.ScanAsync();
        Shot("3-jam");
        vm.DismissError();

        backend.Behavior = SimulatedBehavior.OverLength;
        await vm.ScanAsync();
        Shot("4-review");
        vm.Discard();

        backend.Behavior = SimulatedBehavior.Unconfirmed;
        await vm.ScanAsync();
        Shot("5-unconfirmed");
        vm.DismissError();

        backend.Behavior = SimulatedBehavior.Stall;
        var stalled = vm.ScanAsync();
        vm.ShowStopWaiting = true;
        Shot("6-scanning");
        vm.StopWaiting();
        await stalled;
        vm.DismissError();

        backend.Behavior = SimulatedBehavior.Normal;
        await vm.ScanAsync();
        vm.BeginMove();
        Shot("7-move");
        vm.CancelMove();

        vm.TileClicked(vm.Tiles.First(t => t.Name == "Grocery"));
        Shot("7b-receipt-list");

        vm.OpenSettings();
        Shot("8-settings");
        vm.CloseSettings();

        vm.BeginNewFolder();
        vm.NamePromptText = "a/b";
        vm.CommitNamePrompt();
        Shot("9-new-folder");
        vm.CancelNamePrompt();

        Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        Shot("10-dark");
        Avalonia.Application.Current.RequestedThemeVariant = ThemeVariant.Default;

        window.Width = 820;
        window.Height = 560;
        Shot("11-minimum-size");
        window.Close();
    }
}
