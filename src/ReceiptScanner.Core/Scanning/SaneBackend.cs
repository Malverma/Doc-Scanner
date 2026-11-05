using System.Diagnostics;
using System.Globalization;
using System.Text;
using ReceiptScanner.Core.Storage;

namespace ReceiptScanner.Core.Scanning;

/// <summary>
/// Scans on Linux through SANE's scanimage, one process per operation like the Windows worker. Long paper
/// is turned on with the model's own driver option, such as epsonscan2's --long-paper-mode.
/// </summary>
public sealed class SaneBackend(AppLog log, string scanimagePath = "scanimage") : IScanBackend
{
    private const string LongPaperMethod = "SANE driver option";
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    // Network protocols that only offer standard page sizes, so a direct driver is preferred.
    private static readonly string[] GenericBackends = ["airscan:", "escl:", "v4l:"];

    public bool IsSimulated => false;

    public bool AppliesScanWidth => false;

    public string? PreferredScannerId { get; set; }

    public ScannerModel? Model { get; private set; }

    /// <summary>The SANE device last found, such as "epsonscan2:Epson RR-70W:...".</summary>
    public string? Device { get; private set; }

    public async Task<ScannerStatus> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var failure = await FindAsync(cancellationToken);
        if (failure is not null)
        {
            return new ScannerStatus(false, "Scanner component is missing", failure);
        }

        return Model is null
            ? new ScannerStatus(false, ScanErrorText.Describe(ScanErrorCode.ScannerNotFound, ScannerModels.FromId(PreferredScannerId)).Title)
            : new ScannerStatus(true, $"{Model.Name} ready", Device, Model);
    }

    public async Task<ScanOutcome> ScanAsync(ScanRequest request, IProgress<ScanProgress> progress, CancellationToken cancellationToken)
    {
        // Listing devices is slow because SANE also searches the network, so a found device is reused.
        if (Device is null || ScannerModels.FromId(PreferredScannerId) is { } preferred && preferred != Model)
        {
            var failure = await FindAsync(cancellationToken);
            if (failure is not null)
            {
                return Fail(request, ScanErrorCode.DriverMissing, failure, null);
            }
        }

        if (Model is null || Device is null)
        {
            return Fail(request, ScanErrorCode.ScannerNotFound, "No supported SANE device", null);
        }

        var longPaperArgs = Model.SaneLongPaperArgs;
        var status = new LongPaperStatus(
            LongPaperMethod,
            longPaperArgs.Count > 0,
            longPaperArgs.Count > 0
                ? $"{Device}: {string.Join(' ', longPaperArgs)}"
                : $"{Device}: this driver has no long paper option, so the scan uses its largest page",
            null);
        if (!status.Confirmed && !request.AllowUnconfirmedLongPaper)
        {
            return Fail(request, ScanErrorCode.LongPaperUnconfirmed, status.Evidence, status);
        }

        var pnm = request.RawOutputPath + ".pnm";
        List<string> args =
        [
            "-d", Device,
            "--mode", request.Color == ColorMode.Color ? "Color" : "Grayscale",
            "--resolution", request.Dpi.ToString(CultureInfo.InvariantCulture),
            "--format=pnm",
            "-o", pnm,
            .. longPaperArgs,
        ];
        log.Info($"SANE scan: {string.Join(' ', args)}");
        progress.Report(new ScanProgress(0, request.Dpi, false, "Waiting for the scanner"));

        // scanimage holds an image of unknown length until it ends, so a growing file is only a hint.
        var estimatedRowBytes = Math.Max(1, (long)(request.Dpi * 8.5) * (request.Color == ColorMode.Color ? 3 : 1));
        using var watchDone = new CancellationTokenSource();
        var watch = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(ProgressInterval);
            try
            {
                while (await timer.WaitForNextTickAsync(watchDone.Token))
                {
                    var length = File.Exists(pnm) ? new FileInfo(pnm).Length : 0;
                    if (length > 0)
                    {
                        progress.Report(new ScanProgress(length / estimatedRowBytes, request.Dpi, true));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // The scan ended.
            }
        }, CancellationToken.None);

        var run = await RunAsync(args, cancellationToken);
        await watchDone.CancelAsync();
        await watch;

        try
        {
            if (run.StartFailure is not null)
            {
                return Fail(request, ScanErrorCode.DriverMissing, run.StartFailure, status);
            }

            if (run.Killed)
            {
                return Fail(request, ScanErrorCode.StoppedByUser, null, status);
            }

            if (run.ExitCode != 0)
            {
                var message = LastMessage(run.Stderr);
                var code = MapError(message);
                if (code is ScanErrorCode.ScannerNotFound or ScanErrorCode.Disconnected)
                {
                    Device = null;
                }

                return Fail(request, code, message, status);
            }

            var image = PnmToRaw(pnm, request.RawOutputPath, request.Dpi);
            if (image is null || !image.IsIntact())
            {
                return Fail(request, ScanErrorCode.IncompleteData, "scanimage output wasn't a complete 8-bit image", status);
            }

            log.Info($"Scan complete: {image.Width}x{image.Height}, {image.BytesPerPixel} byte(s) per pixel, {image.Dpi} dpi, {image.HeightMillimeters:0.0} mm");
            // No frame height: long paper mode ends at the paper's end, not at a length the driver set.
            return new ScanOutcome.Completed(image, status);
        }
        finally
        {
            TryDelete(pnm);
        }
    }

    public async Task<LongPaperSetupResult> SetupLongPaperAsync(string profilePath, CancellationToken cancellationToken = default)
    {
        var probe = await ProbeAsync(cancellationToken);
        if (!probe.Found)
        {
            return new LongPaperSetupResult(false, probe.Summary);
        }

        return Model!.SaneLongPaperArgs.Count > 0
            ? new LongPaperSetupResult(true, $"No setup needed. Long paper is turned on for every {Model.Name} scan.")
            : new LongPaperSetupResult(false, $"The {Model.Name}'s Linux driver has no long paper option.");
    }

    public async Task<DriverCheckResult> RunDriverCheckAsync(string reportPath, CancellationToken cancellationToken = default)
    {
        var failure = await FindAsync(cancellationToken);
        if (failure is not null || Device is null)
        {
            return new DriverCheckResult(false, failure ?? ScanErrorText.Describe(ScanErrorCode.ScannerNotFound, Model).Title, null);
        }

        var run = await RunAsync(["-d", Device, "-A"], cancellationToken);
        var report = new StringBuilder()
            .Append("# SANE driver report\n\n")
            .Append(CultureInfo.InvariantCulture, $"- Generated: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}\n")
            .Append(CultureInfo.InvariantCulture, $"- Device: {Device}\n")
            .Append(CultureInfo.InvariantCulture, $"- Recognized as: {Model?.DisplayName}\n")
            .Append(CultureInfo.InvariantCulture, $"- Long paper options used: {string.Join(' ', Model?.SaneLongPaperArgs ?? [])}\n\n")
            .Append("## Options\n\n```\n").Append(run.Stdout).Append("```\n");
        await File.WriteAllTextAsync(reportPath, report.ToString(), CancellationToken.None);
        return new DriverCheckResult(run.ExitCode == 0, run.ExitCode == 0 ? "Report saved" : LastMessage(run.Stderr), reportPath);
    }

    /// <summary>
    /// Lists SANE devices and picks one of a supported model, setting <see cref="Model"/> and
    /// <see cref="Device"/>. Returns why scanimage couldn't run, or null.
    /// </summary>
    private async Task<string?> FindAsync(CancellationToken cancellationToken)
    {
        var run = await RunAsync(["-L"], cancellationToken);
        if (run.StartFailure is not null)
        {
            return $"scanimage couldn't start: {run.StartFailure}. Install the sane package.";
        }

        var devices = ParseDeviceList(run.Stdout);
        var pick = Pick(devices, PreferredScannerId);
        Model = pick?.Model;
        Device = pick?.Device;
        log.Info($"SANE devices: {string.Join(" | ", devices.Select(d => d.Device))}; using: {Device ?? "none"}");
        return null;
    }

    public static List<(string Device, string Description)> ParseDeviceList(string output)
    {
        var devices = new List<(string, string)>();
        foreach (var line in output.Split('\n'))
        {
            // device `epsonscan2:Epson RR-70W:X8XP041349:esci2:usb:ES0230:395' is a EPSON Epson RR-70W flatbed scanner
            var start = line.IndexOf("device `", StringComparison.Ordinal);
            var end = line.IndexOf("' is a ", StringComparison.Ordinal);
            if (start >= 0 && end > start)
            {
                devices.Add((line[(start + 8)..end], line[(end + 7)..].Trim()));
            }
        }

        return devices;
    }

    public static (string Device, ScannerModel Model)? Pick(IReadOnlyList<(string Device, string Description)> devices, string? preferredId)
    {
        var preferred = ScannerModels.FromId(preferredId);
        IEnumerable<ScannerModel> candidates = preferred is null ? ScannerModels.All : [preferred];
        foreach (var model in candidates)
        {
            var matches = devices.Where(d => model.Matches(d.Device) || model.Matches(d.Description)).ToList();
            var best = matches.FirstOrDefault(d => !GenericBackends.Any(g => d.Device.StartsWith(g, StringComparison.OrdinalIgnoreCase)));
            if (best == default)
            {
                best = matches.FirstOrDefault();
            }

            if (best != default)
            {
                return (best.Device, model);
            }
        }

        return null;
    }

    public static ScanErrorCode MapError(string message)
    {
        bool Has(string text) => message.Contains(text, StringComparison.OrdinalIgnoreCase);
        return true switch
        {
            _ when Has("out of documents") || Has("no documents") => ScanErrorCode.NoPaper,
            _ when Has("jammed") => ScanErrorCode.Jam,
            _ when Has("cover is open") => ScanErrorCode.CoverOpen,
            _ when Has("cancelled") || Has("canceled") => ScanErrorCode.Canceled,
            _ when Has("error during device i/o") => ScanErrorCode.Disconnected,
            _ when Has("open of device") || Has("invalid argument") && Has("open") => ScanErrorCode.ScannerNotFound,
            _ when Has("sane_start") || Has("sane_read") => ScanErrorCode.DriverReported,
            _ => ScanErrorCode.Unknown,
        };
    }

    /// <summary>Strips the PNM header so the file matches <see cref="RawImage"/>. Returns null for anything but 8-bit P5 or P6.</summary>
    public static RawImage? PnmToRaw(string pnmPath, string rawPath, int dpi)
    {
        if (!File.Exists(pnmPath))
        {
            return null;
        }

        using var input = new FileStream(pnmPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        string? Token()
        {
            var token = new StringBuilder();
            while (true)
            {
                var b = input.ReadByte();
                if (b < 0)
                {
                    return token.Length > 0 ? token.ToString() : null;
                }

                if (b == '#' && token.Length == 0)
                {
                    while (b >= 0 && b != '\n')
                    {
                        b = input.ReadByte();
                    }

                    continue;
                }

                if (char.IsWhiteSpace((char)b))
                {
                    if (token.Length > 0)
                    {
                        return token.ToString();
                    }

                    continue;
                }

                token.Append((char)b);
            }
        }

        var magic = Token();
        var bytesPerPixel = magic switch { "P5" => 1, "P6" => 3, _ => 0 };
        if (bytesPerPixel == 0
            || !int.TryParse(Token(), CultureInfo.InvariantCulture, out var width)
            || !int.TryParse(Token(), CultureInfo.InvariantCulture, out var height)
            || Token() != "255")
        {
            return null;
        }

        // Token() consumed the single whitespace byte after the max value, so the pixels start here.
        using (var output = new FileStream(rawPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            input.CopyTo(output);
        }

        return new RawImage(rawPath, width, height, bytesPerPixel, dpi);
    }

    private ScanOutcome.Failed Fail(ScanRequest request, ScanErrorCode code, string? detail, LongPaperStatus? status)
    {
        TryDelete(request.RawOutputPath);
        log.Error($"Scan failed: {code} {detail}");
        return new ScanOutcome.Failed(code, detail, status);
    }

    /// <summary>
    /// scanimage's own last message. Drivers can write their debug logs to stderr too, such as Epson's
    /// "[date][scanimage]..." lines when /tmp/epson exists, so lines from scanimage come first.
    /// </summary>
    public static string LastMessage(string stderr)
    {
        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.LastOrDefault(l => l.StartsWith("scanimage:", StringComparison.Ordinal))
            ?? lines.LastOrDefault(l => !l.StartsWith('['))
            ?? "";
    }

    private async Task<SaneRun> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(scanimagePath)
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

        using var process = new Process { StartInfo = info };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new SaneRun(-1, false, "", "", ex.Message);
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

        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        await process.WaitForExitAsync(CancellationToken.None);
        var errors = await stderr;
        if (errors.Length > 0)
        {
            log.Info($"scanimage: {LastMessage(errors)}");
        }

        return new SaneRun(process.ExitCode, killed, await stdout, errors, null);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Temp files are cleaned up on the next launch.
        }
    }

    private sealed record SaneRun(int ExitCode, bool Killed, string Stdout, string Stderr, string? StartFailure);
}
