using ReceiptScanner.Core.Scanning;
using ReceiptScanner.Core.Storage;

namespace ReceiptScanner.Tests;

/// <summary>Runs the real process handling against shell scripts that behave like the worker.</summary>
public class WorkerBackendTests
{
    private const string LongPaperLine = """echo '{"Type":"longPaper","Method":"Test","Confirmed":true,"Evidence":"e","FrameHeightInches":72}'""";

    private static (WorkerBackend Backend, ScanRequest Request, TempDir Dir) Setup(string script)
    {
        var dir = new TempDir();
        var worker = FakeWorker.Create(dir.Path, script);
        var log = new AppLog(dir.Sub("logs"));
        var raw = Path.Combine(dir.Path, "scan.raw");
        return (new WorkerBackend(worker, log), new ScanRequest(300, ColorMode.Gray, raw, LongPaperStrategy.Auto, Path.Combine(dir.Path, "p.json"), false), dir);
    }

    private static readonly IProgress<ScanProgress> NoProgress = new Progress<ScanProgress>();

    [Fact]
    public async Task Complete_with_matching_file_succeeds()
    {
        var (backend, request, dir) = Setup($$"""
            {{LongPaperLine}}
            head -c 200 /dev/zero > "$6"
            echo '{"Type":"complete","Width":10,"Height":20,"BytesPerPixel":1,"Dpi":300}'
            """.Replace("$6", "\"$(echo \"$@\" | sed 's/.*--out \\([^ ]*\\).*/\\1/')\"", StringComparison.Ordinal));
        using (dir)
        {
            var outcome = await backend.ScanAsync(request, NoProgress, CancellationToken.None);

            var completed = Assert.IsType<ScanOutcome.Completed>(outcome);
            Assert.Equal((10, 20), (completed.Image.Width, completed.Image.Height));
            Assert.True(completed.Status!.Confirmed);
            Assert.Equal(72, completed.Status.FrameHeightInches);
        }
    }

    [Fact]
    public async Task Scan_size_and_paper_remaining_cross_the_process_boundary()
    {
        var (backend, request, dir) = Setup("""
            echo "$@" > "$(dirname "$0")/args"
            head -c 200 /dev/zero > "$(dirname "$0")/scan.raw"
            echo '{"Type":"complete","Width":10,"Height":20,"BytesPerPixel":1,"Dpi":300,"PaperRemaining":true}'
            """);
        using (dir)
        {
            var outcome = await backend.ScanAsync(request with { ExpectedWidthMm = 80.5, ExpectedLengthMm = 400 }, NoProgress, CancellationToken.None);

            Assert.True(Assert.IsType<ScanOutcome.Completed>(outcome).PaperRemaining);
            var args = await File.ReadAllTextAsync(Path.Combine(dir.Path, "args"));
            Assert.Contains("--width-mm 80.5 --length-mm 400", args, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task File_smaller_than_reported_is_a_failure_and_is_removed()
    {
        var (backend, request, dir) = Setup($$"""
            echo partial > "{{"$RAW"}}"
            echo '{"Type":"complete","Width":10,"Height":20,"BytesPerPixel":1,"Dpi":300}'
            """);
        using (dir)
        {
            var script = await File.ReadAllTextAsync(Path.Combine(dir.Path, "fake-worker.sh"));
            await File.WriteAllTextAsync(Path.Combine(dir.Path, "fake-worker.sh"), script.Replace("$RAW", request.RawOutputPath, StringComparison.Ordinal));

            var outcome = await backend.ScanAsync(request, NoProgress, CancellationToken.None);

            Assert.Equal(ScanErrorCode.IncompleteData, Assert.IsType<ScanOutcome.Failed>(outcome).Code);
            Assert.False(File.Exists(request.RawOutputPath));
        }
    }

    [Fact]
    public async Task Worker_dying_mid_scan_is_a_failure_even_with_data_on_disk()
    {
        var (backend, request, dir) = Setup("""
            echo '{"Type":"rows","RowCount":500}'
            kill -9 $$
            """);
        using (dir)
        {
            var outcome = await backend.ScanAsync(request, NoProgress, CancellationToken.None);

            Assert.Equal(ScanErrorCode.IncompleteData, Assert.IsType<ScanOutcome.Failed>(outcome).Code);
        }
    }

    [Fact]
    public async Task Complete_with_a_nonzero_exit_is_a_failure()
    {
        var (backend, request, dir) = Setup("""
            echo '{"Type":"complete","Width":1,"Height":1,"BytesPerPixel":1,"Dpi":300}'
            exit 3
            """);
        using (dir)
        {
            // The file is valid, so only the exit code can make this fail.
            await File.WriteAllBytesAsync(request.RawOutputPath, [0]);

            Assert.IsType<ScanOutcome.Failed>(await backend.ScanAsync(request, NoProgress, CancellationToken.None));
        }
    }

    [Theory]
    [InlineData("Jam")]
    [InlineData("NoPaper")]
    [InlineData("CoverOpen")]
    [InlineData("LongPaperUnconfirmed")]
    public async Task Worker_errors_are_reported_by_code(string code)
    {
        var (backend, request, dir) = Setup($$"""
            echo '{"Type":"error","Code":"{{code}}","Message":"m"}'
            exit 1
            """);
        using (dir)
        {
            var outcome = await backend.ScanAsync(request, NoProgress, CancellationToken.None);

            Assert.Equal(Enum.Parse<ScanErrorCode>(code), Assert.IsType<ScanOutcome.Failed>(outcome).Code);
        }
    }

    [Fact]
    public async Task Error_after_a_complete_message_still_fails()
    {
        var (backend, request, dir) = Setup("""
            echo '{"Type":"complete","Width":1,"Height":1,"BytesPerPixel":1,"Dpi":300}'
            echo '{"Type":"error","Code":"Jam","Message":"m"}'
            """);
        using (dir)
        {
            Assert.Equal(ScanErrorCode.Jam, Assert.IsType<ScanOutcome.Failed>(await backend.ScanAsync(request, NoProgress, CancellationToken.None)).Code);
        }
    }

    [Fact]
    public async Task Offline_before_data_retries_once_then_succeeds()
    {
        var (backend, request, dir) = Setup("""
            marker="$(dirname "$0")/tried"
            if [ ! -e "$marker" ]; then
              touch "$marker"
              echo '{"Type":"error","Code":"ScannerNotFound","Message":"m"}'
              exit 1
            fi
            echo '{"Type":"error","Code":"NoPaper","Message":"second attempt ran"}'
            exit 1
            """);
        using (dir)
        {
            var outcome = await backend.ScanAsync(request, NoProgress, CancellationToken.None);

            Assert.Equal(ScanErrorCode.NoPaper, Assert.IsType<ScanOutcome.Failed>(outcome).Code);
        }
    }

    [Fact]
    public async Task Disconnect_after_data_does_not_retry()
    {
        var (backend, request, dir) = Setup("""
            marker="$(dirname "$0")/count"
            echo x >> "$marker"
            echo '{"Type":"rows","RowCount":50}'
            echo '{"Type":"error","Code":"Disconnected","Message":"m"}'
            exit 1
            """);
        using (dir)
        {
            var outcome = await backend.ScanAsync(request, NoProgress, CancellationToken.None);

            Assert.Equal(ScanErrorCode.Disconnected, Assert.IsType<ScanOutcome.Failed>(outcome).Code);
            Assert.Single(await File.ReadAllLinesAsync(Path.Combine(dir.Path, "count")));
        }
    }

    [Fact]
    public async Task Stopping_ends_a_hung_worker()
    {
        var (backend, request, dir) = Setup("""
            echo '{"Type":"waiting"}'
            exec sleep 600
            """);
        using (dir)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

            var outcome = await backend.ScanAsync(request, NoProgress, cts.Token).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(ScanErrorCode.StoppedByUser, Assert.IsType<ScanOutcome.Failed>(outcome).Code);
        }
    }

    [Fact]
    public async Task Stray_driver_output_is_ignored()
    {
        var (backend, request, dir) = Setup("""
            echo 'some driver chatter'
            echo '{broken'
            echo '{"Type":"error","Code":"Jam"}'
            """);
        using (dir)
        {
            Assert.Equal(ScanErrorCode.Jam, Assert.IsType<ScanOutcome.Failed>(await backend.ScanAsync(request, NoProgress, CancellationToken.None)).Code);
        }
    }

    [Fact]
    public async Task Missing_worker_is_reported_rather_than_simulated()
    {
        using var dir = new TempDir();
        var backend = new WorkerBackend(Path.Combine(dir.Path, "missing.exe"), new AppLog(dir.Sub("logs")));
        var request = new ScanRequest(300, ColorMode.Gray, Path.Combine(dir.Path, "r.raw"), LongPaperStrategy.Auto, "p", false);

        var outcome = await backend.ScanAsync(request, NoProgress, CancellationToken.None);

        Assert.Equal(ScanErrorCode.DriverMissing, Assert.IsType<ScanOutcome.Failed>(outcome).Code);
        Assert.False((await backend.ProbeAsync()).Found);
        Assert.False(backend.IsSimulated);
    }

    [Fact]
    public async Task Probe_reports_the_found_source()
    {
        var (backend, _, dir) = Setup("""echo '{"Type":"source","Message":"TW-Brother DS-640, driver 1.0"}'""");
        using (dir)
        {
            var status = await backend.ProbeAsync();

            Assert.True(status.Found);
            Assert.Contains("DS-640", status.Detail);
        }
    }

    [Fact]
    public async Task Probe_recognizes_the_epson_and_passes_a_preference_to_the_worker()
    {
        var (backend, _, dir) = Setup("""
            echo "$@" > "$(dirname "$0")/args"
            echo '{"Type":"source","ScannerId":"epson-rr70w","Message":"EPSON RR-70W, driver 2.0"}'
            """);
        using (dir)
        {
            backend.PreferredScannerId = "epson-rr70w";
            var status = await backend.ProbeAsync();

            Assert.True(status.Found);
            Assert.Equal("RR-70W ready", status.Summary);
            Assert.Same(ScannerModels.EpsonRr70W, backend.Model);
            Assert.Contains("--scanner epson-rr70w", File.ReadAllText(Path.Combine(dir.Path, "args")));
        }
    }

    [Fact]
    public async Task Probe_without_a_scanner_id_recognizes_the_model_from_the_source_name()
    {
        var (backend, _, dir) = Setup("""echo '{"Type":"source","Message":"EPSON RR-70W, driver 2.0"}'""");
        using (dir)
        {
            var status = await backend.ProbeAsync();

            Assert.Same(ScannerModels.EpsonRr70W, status.Model);
        }
    }
}
