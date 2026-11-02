using Avalonia;
using Avalonia.Headless;
using ReceiptScanner.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace ReceiptScanner.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>()
            .UseSkia()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>Loads the real theme without the real app's startup work.</summary>
public sealed class TestApp : ReceiptScanner.App.App
{
    public override void OnFrameworkInitializationCompleted()
    {
    }
}
