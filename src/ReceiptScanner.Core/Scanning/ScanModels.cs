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

    /// <summary>Shows the driver's own dialog on every scan. Named for the first driver, kept so saved settings load.</summary>
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
    bool AllowUnconfirmedLongPaper,
    double? ExpectedWidthMm = null,
    double? ExpectedLengthMm = null,
    string? ScannerId = null);

/// <summary>What the scanning layer knows about how Long Paper was enabled.</summary>
public sealed record LongPaperStatus(string Method, bool Confirmed, string Evidence, double? FrameHeightInches);

public sealed record ScanProgress(long RowsReceived, int Dpi, bool DataFlowing, string? Note = null)
{
    public double MillimetersReceived => Dpi <= 0 ? 0 : RowsReceived * 25.4 / Dpi;
}

public abstract record ScanOutcome(LongPaperStatus? LongPaper)
{
    /// <param name="PaperRemaining">The driver says paper is still in the scanner after the transfer ended.</param>
    public sealed record Completed(RawImage Image, LongPaperStatus? Status, bool PaperRemaining = false) : ScanOutcome(Status);

    public sealed record Failed(ScanErrorCode Code, string? Detail, LongPaperStatus? Status) : ScanOutcome(Status);
}

public sealed record ScannerStatus(bool Found, string Summary, string? Detail = null, ScannerModel? Model = null);

public sealed record DriverCheckResult(bool Success, string Message, string? ReportPath);

public sealed record LongPaperSetupResult(bool Success, string Message);

public interface IScanBackend
{
    bool IsSimulated { get; }

    /// <summary>False when the backend scans full width whatever <see cref="ScanRequest.ExpectedWidthMm"/> says.</summary>
    bool AppliesScanWidth => true;

    /// <summary>The model to use when more than one is installed. Null uses the first one found.</summary>
    string? PreferredScannerId { get; set; }

    /// <summary>The model last seen by a probe, scan or setup, if any.</summary>
    ScannerModel? Model { get; }

    Task<ScannerStatus> ProbeAsync(CancellationToken cancellationToken = default);

    /// <summary>Runs one scan. Cancelling ends the wait and may leave the scanner needing a replug.</summary>
    Task<ScanOutcome> ScanAsync(ScanRequest request, IProgress<ScanProgress> progress, CancellationToken cancellationToken);

    /// <summary>Opens the driver's dialog so the user can select Long Paper, then saves the driver state.</summary>
    Task<LongPaperSetupResult> SetupLongPaperAsync(string profilePath, CancellationToken cancellationToken = default);

    /// <summary>Spec Phase 1: dumps capabilities before and after Long Paper is selected.</summary>
    Task<DriverCheckResult> RunDriverCheckAsync(string reportPath, CancellationToken cancellationToken = default);
}
