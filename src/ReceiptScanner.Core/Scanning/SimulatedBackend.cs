namespace ReceiptScanner.Core.Scanning;

public enum SimulatedBehavior
{
    Normal,
    Jam,
    NoPaper,
    Disconnect,
    OverLength,
    Unconfirmed,
    Stall,
}

/// <summary>
/// Stands in for the scanner so the app can be developed and tested without hardware. Its images are
/// obvious test patterns, and the UI shows a permanent banner whenever this backend is active.
/// </summary>
public sealed class SimulatedBackend(SimulatedBehavior behavior = SimulatedBehavior.Normal, double lengthMm = 420, TimeSpan? rowDelay = null) : IScanBackend
{
    private readonly TimeSpan _delay = rowDelay ?? TimeSpan.FromMilliseconds(25);

    public SimulatedBehavior Behavior { get; set; } = behavior;

    public double LengthMm { get; set; } = lengthMm;

    public bool IsSimulated => true;

    public Task<ScannerStatus> ProbeAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new ScannerStatus(true, "Simulated DS-640"));

    public async Task<ScanOutcome> ScanAsync(ScanRequest request, IProgress<ScanProgress> progress, CancellationToken cancellationToken)
    {
        var confirmed = Behavior != SimulatedBehavior.Unconfirmed;
        var status = new LongPaperStatus("Simulated", confirmed, confirmed ? "Simulator" : "Simulator: unconfirmed", 72);
        if (!confirmed && !request.AllowUnconfirmedLongPaper)
        {
            return new ScanOutcome.Failed(ScanErrorCode.LongPaperUnconfirmed, null, status);
        }

        switch (Behavior)
        {
            case SimulatedBehavior.NoPaper:
                return new ScanOutcome.Failed(ScanErrorCode.NoPaper, null, status);
            case SimulatedBehavior.Stall:
                try
                {
                    progress.Report(new ScanProgress(0, request.Dpi, false));
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return new ScanOutcome.Failed(ScanErrorCode.StoppedByUser, null, status);
                }

                break;
        }

        var lengthMm = Behavior == SimulatedBehavior.OverLength ? 1828.8 : LengthMm;
        var width = (int)Math.Round(216 * request.Dpi / 25.4);
        var height = (int)Math.Round(lengthMm * request.Dpi / 25.4);
        var channels = request.Color == ColorMode.Gray ? 1 : 3;
        var failAt = Behavior is SimulatedBehavior.Jam or SimulatedBehavior.Disconnect ? height / 3 : int.MaxValue;
        var row = new byte[width * channels];

        await using (var output = new FileStream(request.RawOutputPath, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20))
        {
            for (var y = 0; y < height; y++)
            {
                if (y >= failAt)
                {
                    output.Close();
                    File.Delete(request.RawOutputPath);
                    return new ScanOutcome.Failed(
                        Behavior == SimulatedBehavior.Jam ? ScanErrorCode.Jam : ScanErrorCode.Disconnected, null, status);
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    output.Close();
                    File.Delete(request.RawOutputPath);
                    return new ScanOutcome.Failed(ScanErrorCode.StoppedByUser, null, status);
                }

                FillRow(row, y, width, height, request.Dpi, channels, runsOffEnd: Behavior == SimulatedBehavior.OverLength);
                output.Write(row);
                if (y % Math.Max(1, height / 60) == 0)
                {
                    progress.Report(new ScanProgress(y, request.Dpi, true));
                    if (_delay > TimeSpan.Zero)
                    {
                        await Task.Delay(_delay, CancellationToken.None);
                    }
                }
            }
        }

        progress.Report(new ScanProgress(height, request.Dpi, true));
        return new ScanOutcome.Completed(new RawImage(request.RawOutputPath, width, height, channels, request.Dpi), status);
    }

    public Task<LongPaperSetupResult> SetupLongPaperAsync(string profilePath, CancellationToken cancellationToken = default)
    {
        Behavior = Behavior == SimulatedBehavior.Unconfirmed ? SimulatedBehavior.Normal : Behavior;
        return Task.FromResult(new LongPaperSetupResult(true, "Simulated Long Paper profile saved."));
    }

    public async Task<DriverCheckResult> RunDriverCheckAsync(string reportPath, CancellationToken cancellationToken = default)
    {
        await File.WriteAllTextAsync(reportPath, "# Simulated driver check\n\nNo real scanner was involved.\n", cancellationToken);
        return new DriverCheckResult(true, "Simulated report written.", reportPath);
    }

    /// <summary>A white, receipt-width strip with blocks of "print", followed by a blank tail.</summary>
    private static void FillRow(byte[] row, int y, int width, int height, int dpi, int channels, bool runsOffEnd)
    {
        Array.Fill(row, (byte)245);
        var paperWidth = (int)(77 * dpi / 25.4);
        var left = (width - paperWidth) / 2;
        var lineHeight = Math.Max(4, dpi / 8);
        var printEnd = runsOffEnd ? height : height - (height / 12);
        var inMargin = y < dpi / 4 || y > printEnd;
        var onLine = y % lineHeight < lineHeight * 3 / 5;
        if (inMargin || !onLine)
        {
            return;
        }

        var lineNumber = y / lineHeight;
        var lineWidth = paperWidth * (40 + (lineNumber * 37 % 55)) / 100;
        var cell = Math.Max(2, dpi / 40);
        for (var x = 0; x < lineWidth; x++)
        {
            if ((x / cell) % 3 == 2)
            {
                continue;
            }

            var i = (left + x) * channels;
            for (var c = 0; c < channels; c++)
            {
                row[i + c] = 40;
            }
        }
    }
}
