using ReceiptScanner.Core.Scanning;
using ReceiptScanner.Core.Storage;

namespace ReceiptScanner.Tests;

/// <summary>Runs the Linux backend against shell scripts that behave like scanimage.</summary>
public class SaneBackendTests
{
    private const string EpsonDevice = "epsonscan2:Epson RR-70W:X8XP041349:esci2:usb:ES0230:395";

    // Real "scanimage -L" output from an RR-70W, plus the network devices SANE also lists.
    private const string ListOutput = $"""
        device `v4l:/dev/video0' is a Noname Integrated Camera: Integrated C virtual device
        device `airscan:e0:Epson RR-70W' is a eSCL Epson RR-70W ip=192.168.1.50
        device `{EpsonDevice}' is a EPSON Epson RR-70W:X8XP041349 flatbed scanner
        device `escl:https://192.168.1.83:443' is a HP Smart Tank 5100 series [909866] platen scanner
        """;

    private static readonly IProgress<ScanProgress> NoProgress = new Progress<ScanProgress>();

    /// <summary>The script answers -L with the device list and runs <paramref name="scan"/> for anything else.</summary>
    private static (SaneBackend Backend, ScanRequest Request, TempDir Dir) Setup(string scan, string list = ListOutput)
    {
        var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "list.txt"), list);
        var script = $$"""
            here="$(dirname "$0")"
            if [ "$1" = "-L" ]; then cat "$here/list.txt"; exit 0; fi
            echo "$@" > "$here/args"
            out=""; prev=""
            for a in "$@"; do [ "$prev" = "-o" ] && out="$a"; prev="$a"; done
            {{scan}}
            """;
        var backend = new SaneBackend(new AppLog(dir.Sub("logs")), FakeWorker.Create(dir.Path, script));
        var request = new ScanRequest(300, ColorMode.Gray, Path.Combine(dir.Path, "scan.raw"), LongPaperStrategy.Auto, Path.Combine(dir.Path, "p.json"), false);
        return (backend, request, dir);
    }

    [Fact]
    public async Task Probe_finds_the_epson_through_its_own_driver_not_the_network()
    {
        var (backend, _, dir) = Setup("exit 1");
        using (dir)
        {
            var status = await backend.ProbeAsync();

            Assert.True(status.Found);
            Assert.Equal("RR-70W ready", status.Summary);
            Assert.Same(ScannerModels.EpsonRr70W, status.Model);
            Assert.Equal(EpsonDevice, backend.Device);
        }
    }

    [Fact]
    public async Task Probe_without_a_supported_scanner_is_not_found()
    {
        var (backend, _, dir) = Setup("exit 1", "device `escl:https://192.168.1.83:443' is a HP Smart Tank 5100 series platen scanner\n");
        using (dir)
        {
            var status = await backend.ProbeAsync();

            Assert.False(status.Found);
            Assert.Equal("Scanner not found", status.Summary);
        }
    }

    [Fact]
    public async Task Scan_turns_on_long_paper_and_converts_pnm_to_raw()
    {
        var (backend, request, dir) = Setup("""
            printf 'P5\n# made by a test\n4 3\n255\n' > "$out"
            printf 'abcdefghijkl' >> "$out"
            """);
        using (dir)
        {
            var outcome = await backend.ScanAsync(request, NoProgress, CancellationToken.None);

            var completed = Assert.IsType<ScanOutcome.Completed>(outcome);
            Assert.Equal((4, 3, 1, 300), (completed.Image.Width, completed.Image.Height, completed.Image.BytesPerPixel, completed.Image.Dpi));
            Assert.Equal("abcdefghijkl", File.ReadAllText(request.RawOutputPath));
            Assert.True(completed.Status!.Confirmed);
            Assert.Null(completed.Status.FrameHeightInches);
            var args = File.ReadAllText(Path.Combine(dir.Path, "args"));
            Assert.Contains($"-d {EpsonDevice}", args, StringComparison.Ordinal);
            Assert.Contains("--mode Grayscale --resolution 300", args, StringComparison.Ordinal);
            Assert.Contains("--long-paper-mode=yes", args, StringComparison.Ordinal);
            Assert.False(File.Exists(request.RawOutputPath + ".pnm"));
        }
    }

    [Fact]
    public async Task Color_scan_reads_p6()
    {
        var (backend, request, dir) = Setup("""printf 'P6 2 1 255\nRGBrgb' > "$out" """);
        using (dir)
        {
            var outcome = await backend.ScanAsync(request with { Color = ColorMode.Color }, NoProgress, CancellationToken.None);

            var image = Assert.IsType<ScanOutcome.Completed>(outcome).Image;
            Assert.Equal((2, 1, 3), (image.Width, image.Height, image.BytesPerPixel));
            Assert.Contains("--mode Color", File.ReadAllText(Path.Combine(dir.Path, "args")), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("scanimage: sane_start: Document feeder out of documents", ScanErrorCode.NoPaper)]
    [InlineData("scanimage: sane_read: Document feeder jammed", ScanErrorCode.Jam)]
    [InlineData("scanimage: sane_start: Scanner cover is open", ScanErrorCode.CoverOpen)]
    [InlineData("scanimage: sane_read: Error during device I/O", ScanErrorCode.Disconnected)]
    [InlineData("scanimage: open of device epsonscan2:x failed: Invalid argument", ScanErrorCode.ScannerNotFound)]
    public async Task Driver_errors_map_to_app_errors_and_save_nothing(string message, ScanErrorCode code)
    {
        var (backend, request, dir) = Setup($"""
            printf 'P5 1 1 255\nx' > "$out"
            echo "{message}" >&2
            exit 7
            """);
        using (dir)
        {
            var outcome = await backend.ScanAsync(request, NoProgress, CancellationToken.None);

            Assert.Equal(code, Assert.IsType<ScanOutcome.Failed>(outcome).Code);
            Assert.False(File.Exists(request.RawOutputPath));
            Assert.False(File.Exists(request.RawOutputPath + ".pnm"));
        }
    }

    [Fact]
    public async Task Truncated_output_is_incomplete_not_saved()
    {
        var (backend, request, dir) = Setup("""printf 'P5 4 3 255\nabc' > "$out" """);
        using (dir)
        {
            var outcome = await backend.ScanAsync(request, NoProgress, CancellationToken.None);

            Assert.Equal(ScanErrorCode.IncompleteData, Assert.IsType<ScanOutcome.Failed>(outcome).Code);
        }
    }

    [Fact]
    public async Task Stopping_ends_scanimage()
    {
        var (backend, request, dir) = Setup("sleep 30");
        using (dir)
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            var outcome = await backend.ScanAsync(request, NoProgress, stop.Token);

            Assert.Equal(ScanErrorCode.StoppedByUser, Assert.IsType<ScanOutcome.Failed>(outcome).Code);
        }
    }

    [Fact]
    public async Task Missing_scanimage_says_so()
    {
        using var dir = new TempDir();
        var backend = new SaneBackend(new AppLog(dir.Sub("logs")), Path.Combine(dir.Path, "no-such-scanimage"));

        var status = await backend.ProbeAsync();

        Assert.False(status.Found);
        Assert.Contains("sane", status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Width_setting_is_not_claimed()
    {
        using var dir = new TempDir();

        Assert.False(((IScanBackend)new SaneBackend(new AppLog(dir.Sub("logs")))).AppliesScanWidth);
    }
}
