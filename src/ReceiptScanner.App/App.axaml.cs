using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ReceiptScanner.App.ViewModels;
using ReceiptScanner.App.Views;
using ReceiptScanner.Core.Scanning;
using ReceiptScanner.Core.Storage;

namespace ReceiptScanner.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var paths = new AppPaths();
            paths.CleanTemp();
            var log = new AppLog(paths.LogFolder);
            var settings = AppSettings.Load(paths.SettingsFile, paths.DefaultBaseFolder, log);
            var backend = CreateBackend(desktop.Args ?? [], log);
            log.Info($"App started. Version {typeof(App).Assembly.GetName().Version}; OS {Environment.OSVersion}; simulated: {backend.IsSimulated}");

            var window = new MainWindow();
            var viewModel = new MainViewModel(backend, paths, log, settings, new DesktopActions(window));
            window.DataContext = viewModel;
            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, _) => paths.CleanTemp();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// The simulator runs only when asked for, or where real scanning is impossible. On Windows a missing
    /// worker shows an error instead, so simulated scans can't be mistaken for real ones.
    /// </summary>
    private static IScanBackend CreateBackend(string[] args, AppLog log)
    {
        var simulate = args.FirstOrDefault(a => a.StartsWith("--simulate", StringComparison.OrdinalIgnoreCase));
        if (simulate is null && OperatingSystem.IsWindows())
        {
            var worker = Path.Combine(AppContext.BaseDirectory, "worker", "ReceiptScanner.TwainWorker.exe");
            return new WorkerBackend(worker, log);
        }

        var behavior = SimulatedBehavior.Normal;
        if (simulate?.Split('=') is [_, var name])
        {
            Enum.TryParse(name, ignoreCase: true, out behavior);
        }

        return new SimulatedBackend(behavior);
    }
}
