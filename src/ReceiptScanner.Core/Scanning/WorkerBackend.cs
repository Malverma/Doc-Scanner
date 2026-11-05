using System.Diagnostics;
using ReceiptScanner.Core.Storage;

namespace ReceiptScanner.Core.Scanning;

/// <summary>Runs the 32-bit TWAIN worker. One process per operation, so a hung driver call can always be ended.</summary>
public sealed class WorkerBackend(string workerPath, AppLog log) : IScanBackend
{
    private static readonly TimeSpan OfflineRetryDelay = TimeSpan.FromSeconds(2);

    public bool IsSimulated => false;

    public string? PreferredScannerId { get; set; }

    public ScannerModel? Model { get; private set; }

    public async Task<ScannerStatus> ProbeAsync(CancellationToken cancellationToken = default)
    {
        ScannerStatus status = new(false, ScanErrorText.Describe(ScanErrorCode.ScannerNotFound, ScannerModels.FromId(PreferredScannerId)).Title);
        var run = await RunAsync(["probe"], cancellationToken, e =>
        {
            if (e.Type == WorkerEvent.Source)
            {
                status = new ScannerStatus(true, $"{Model?.Name ?? "Scanner"} ready", e.Message, Model);
            }
            else if (e.Type == WorkerEvent.Error)
            {
                // With no preference, a scanner that can't be found may not be the one seen last.
                if (e.Code == ScanErrorCode.ScannerNotFound)
                {
                    Model = null;
                }

                status = new ScannerStatus(false, Describe(e).Title, e.Message);
            }
        });

        if (run.StartFailure is not null)
        {
            return new ScannerStatus(false, "Scanner component is missing", run.StartFailure);
        }

        return status;
    }

    public async Task<ScanOutcome> ScanAsync(ScanRequest request, IProgress<ScanProgress> progress, CancellationToken cancellationToken)
    {
        var outcome = await ScanOnceAsync(request, progress, cancellationToken);

        // The scanner can drop off USB after being idle, then return when opened again (spec A14).
        if (outcome is ScanOutcome.Failed { Code: ScanErrorCode.ScannerNotFound or ScanErrorCode.Disconnected } failed
            && failed.Detail == OfflineBeforeData && !cancellationToken.IsCancellationRequested)
        {
            log.Info("Scanner was offline before any data arrived. Retrying once.");
            try
            {
                await Task.Delay(OfflineRetryDelay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return outcome;
            }

            outcome = await ScanOnceAsync(request, progress, cancellationToken);
        }

        return outcome;
    }

    public async Task<LongPaperSetupResult> SetupLongPaperAsync(string profilePath, CancellationToken cancellationToken = default)
    {
        LongPaperSetupResult result = new(false, "Long Paper setup didn't finish.");
        var run = await RunAsync(["setup", "--profile", profilePath], cancellationToken, e =>
        {
            if (e.Type == WorkerEvent.Result)
            {
                result = new LongPaperSetupResult(e.Success == true, e.Message ?? "");
            }
            else if (e.Type == WorkerEvent.Error)
            {
                result = new LongPaperSetupResult(false, Describe(e).Title);
            }
        });

        return run.StartFailure is null ? result : new LongPaperSetupResult(false, run.StartFailure);
    }

    public async Task<DriverCheckResult> RunDriverCheckAsync(string reportPath, CancellationToken cancellationToken = default)
    {
        DriverCheckResult result = new(false, "The driver check didn't finish.", null);
        var run = await RunAsync(["discover", "--out", reportPath], cancellationToken, e =>
        {
            if (e.Type == WorkerEvent.Result)
            {
                result = new DriverCheckResult(e.Success == true, e.Message ?? "", reportPath);
            }
            else if (e.Type == WorkerEvent.Error)
            {
                result = new DriverCheckResult(false, Describe(e).Title, null);
            }
        });

        return run.StartFailure is null ? result : new DriverCheckResult(false, run.StartFailure, null);
    }

    private ErrorDescription Describe(WorkerEvent e) =>
        ScanErrorText.Describe(e.Code ?? ScanErrorCode.Unknown, ScannerModels.FromId(PreferredScannerId) ?? Model);

    private const string OfflineBeforeData = "offline-before-data";

    private async Task<ScanOutcome> ScanOnceAsync(ScanRequest request, IProgress<ScanProgress> progress, CancellationToken cancellationToken)
    {
        LongPaperStatus? longPaper = null;
        WorkerEvent? complete = null;
        WorkerEvent? error = null;
        long rows = 0;

        var arguments = new List<string>
        {
            "scan",
            "--dpi", request.Dpi.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--color", request.Color.ToString(),
            "--out", request.RawOutputPath,
            "--strategy", request.Strategy.ToString(),
            "--profile", request.ProfilePath,
        };
        if (request.AllowUnconfirmedLongPaper)
        {
            arguments.Add("--allow-unconfirmed");
        }

        if (request.ExpectedWidthMm is { } width)
        {
            arguments.AddRange(["--width-mm", width.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        }

        if (request.ExpectedLengthMm is { } length)
        {
            arguments.AddRange(["--length-mm", length.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        }

        var run = await RunAsync(arguments, cancellationToken, e =>
        {
            switch (e.Type)
            {
                case WorkerEvent.LongPaper:
                    longPaper = new LongPaperStatus(e.Method ?? "?", e.Confirmed == true, e.Evidence ?? "", e.FrameHeightInches);
                    break;
                case WorkerEvent.Waiting:
                    progress.Report(new ScanProgress(0, request.Dpi, false, e.Message));
                    break;
                case WorkerEvent.Rows:
                    rows = e.RowCount ?? rows;
                    progress.Report(new ScanProgress(rows, request.Dpi, true));
                    break;
                case WorkerEvent.Complete:
                    complete = e;
                    break;
                case WorkerEvent.Error:
                    error = e;
                    break;
            }
        });

        ScanOutcome.Failed Fail(ScanErrorCode code, string? detail)
        {
            TryDelete(request.RawOutputPath);
            log.Error($"Scan failed: {code} {detail}");
            return new ScanOutcome.Failed(code, detail, longPaper);
        }

        if (run.StartFailure is not null)
        {
            return Fail(ScanErrorCode.DriverMissing, run.StartFailure);
        }

        if (run.Killed)
        {
            return Fail(ScanErrorCode.StoppedByUser, null);
        }

        if (error is not null)
        {
            var code = error.Code ?? ScanErrorCode.Unknown;
            var offline = code is ScanErrorCode.ScannerNotFound or ScanErrorCode.Disconnected && rows == 0;
            return Fail(code, offline ? OfflineBeforeData : error.Message);
        }

        // A worker that dies without saying the scan completed counts as failed, whatever is on disk.
        if (complete is null || run.ExitCode != 0)
        {
            return Fail(rows > 0 ? ScanErrorCode.IncompleteData : ScanErrorCode.Unknown, $"Worker exit code {run.ExitCode}");
        }

        var image = new RawImage(
            request.RawOutputPath,
            complete.Width ?? 0,
            complete.Height ?? 0,
            complete.BytesPerPixel ?? 0,
            complete.Dpi ?? request.Dpi);

        if (!image.IsIntact())
        {
            return Fail(ScanErrorCode.IncompleteData, "Scan file doesn't match its reported size");
        }

        log.Info($"Scan complete: {image.Width}x{image.Height}, {image.BytesPerPixel} byte(s) per pixel, {image.Dpi} dpi, {image.HeightMillimeters:0.0} mm, paper remaining: {complete.PaperRemaining == true}");
        return new ScanOutcome.Completed(image, longPaper, complete.PaperRemaining == true);
    }

    private async Task<WorkerRun> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken, Action<WorkerEvent> onEvent)
    {
        if (!File.Exists(workerPath))
        {
            return new WorkerRun(-1, false, $"Worker not found at {workerPath}");
        }

        var info = new ProcessStartInfo(workerPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        if (ScannerModels.FromId(PreferredScannerId) is { } preferred)
        {
            info.ArgumentList.Add("--scanner");
            info.ArgumentList.Add(preferred.Id);
        }

        using var process = new Process { StartInfo = info };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new WorkerRun(-1, false, ex.Message);
        }

        var killed = false;
        await using var registration = cancellationToken.Register(() =>
        {
            killed = true;
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }
        });

        var stderr = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync(CancellationToken.None) is { } line)
            {
                // Drivers can write anything here, so only its size is logged (spec F10).
                log.Error($"Worker stderr: {line.Length} characters of driver output ignored");
            }
        }, CancellationToken.None);

        while (await process.StandardOutput.ReadLineAsync(CancellationToken.None) is { } line)
        {
            var parsed = WorkerProtocol.TryParse(line);
            if (parsed is null)
            {
                log.Info($"Worker stdout: {line.Length} characters of non-protocol output ignored");
                continue;
            }

            if (parsed.Type == WorkerEvent.Log)
            {
                log.Info($"Worker: {parsed.Message}");
            }
            else if (parsed.Type == WorkerEvent.Source)
            {
                Model = ScannerModels.FromId(parsed.ScannerId) ?? ScannerModels.FromSourceName(parsed.Message) ?? Model;
            }

            onEvent(parsed);
        }

        await process.WaitForExitAsync(CancellationToken.None);
        await stderr;
        return new WorkerRun(process.ExitCode, killed, null);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // It's outside the output folder, and the next launch cleans it up.
        }
    }

    private sealed record WorkerRun(int ExitCode, bool Killed, string? StartFailure);
}
