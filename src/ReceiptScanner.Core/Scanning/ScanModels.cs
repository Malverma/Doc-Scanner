namespace ReceiptScanner.Core.Scanning;

public enum ColorMode { Gray, Color }

public enum OutputFormat { Pdf, Png, Both }

/// <summary>How Long Paper gets enabled. See spec section 5.2, options A to C.</summary>
public enum LongPaperStrategy
{
    /// <summary>Use a saved driver profile if one matches, otherwise TWAIN capabilities.</summary>
    Auto,
    Capabilities,
    SavedState,
    BrotherDialog,
}

public enum ScanErrorCode
{
    Unknown,
    NoPaper,
    Jam,
    CoverOpen,
    ScannerNotFound,
    Disconnected,
    DriverMissing,
    LongPaperUnconfirmed,
    Canceled,
    DriverReported,
    StoppedByUser,
    IncompleteData,
}

public sealed record ScanRequest(
    int Dpi,
    ColorMode Color,
    string RawOutputPath,
    LongPaperStrategy Strategy,
    string ProfilePath,
    bool AllowUnconfirmedLongPaper);

/// <summary>What the scanning layer knows about how Long Paper was enabled.</summary>
public sealed record LongPaperStatus(string Method, bool Confirmed, string Evidence, double? FrameHeightInches);

public sealed record ScanProgress(long RowsReceived, int Dpi, bool DataFlowing, string? Note = null)
{
    public double MillimetersReceived => Dpi <= 0 ? 0 : RowsReceived * 25.4 / Dpi;
}

public abstract record ScanOutcome(LongPaperStatus? LongPaper)
{
    public sealed record Completed(RawImage Image, LongPaperStatus? Status) : ScanOutcome(Status);

    public sealed record Failed(ScanErrorCode Code, string? Detail, LongPaperStatus? Status) : ScanOutcome(Status);
}

public sealed record ScannerStatus(bool Found, string Summary, string? Detail = null);

public sealed record DriverCheckResult(bool Success, string Message, string? ReportPath);

public sealed record LongPaperSetupResult(bool Success, string Message);

public interface IScanBackend
{
    bool IsSimulated { get; }

    Task<ScannerStatus> ProbeAsync(CancellationToken cancellationToken = default);

    /// <summary>Runs one scan. Cancelling ends the wait and may leave the scanner needing a replug.</summary>
    Task<ScanOutcome> ScanAsync(ScanRequest request, IProgress<ScanProgress> progress, CancellationToken cancellationToken);

    /// <summary>Opens Brother's dialog so the user can select Long Paper, then saves the driver state.</summary>
    Task<LongPaperSetupResult> SetupLongPaperAsync(string profilePath, CancellationToken cancellationToken = default);

    /// <summary>Spec Phase 1: dumps capabilities before and after Long Paper is selected.</summary>
    Task<DriverCheckResult> RunDriverCheckAsync(string reportPath, CancellationToken cancellationToken = default);
}
