using ReceiptScanner.Core.Scanning;

namespace ReceiptScanner.Core.Imaging;

public enum SuspectReason
{
    AtScannerLimit,
    AtFrameLimit,
    ContentAtBottomEdge,
    Blank,
    LongPaperUnconfirmed,
}

public sealed record CompletenessResult(IReadOnlyList<SuspectReason> Reasons)
{
    public bool IsClean => Reasons.Count == 0;

    public IEnumerable<string> Messages => Reasons.Select(Describe);

    public static string Describe(SuspectReason reason) => reason switch
    {
        SuspectReason.AtScannerLimit => "The scan reached the scanner's 1828.8 mm (72 in) limit, so the receipt may continue past this point.",
        SuspectReason.AtFrameLimit => "The scan stopped exactly at the length the driver allowed, so the receipt may be cut off.",
        SuspectReason.ContentAtBottomEdge => "Printing runs right up to the bottom edge, so the receipt may be cut off.",
        SuspectReason.Blank => "No printing was detected. A very faded receipt or a sheet fed face-down can cause this.",
        SuspectReason.LongPaperUnconfirmed => "Long Paper mode wasn't confirmed for this scan, so a long receipt may be cut off.",
        _ => "This scan needs checking.",
    };
}

/// <summary>Spec 5.4. The driver may not report a cut-off page, so several signals are combined.</summary>
public static class Completeness
{
    public const double ScannerLimitMm = 1828.8;
    public const double LimitToleranceMm = 2;

    public static CompletenessResult Assess(RawImage image, ContentAnalysis analysis, LongPaperStatus? longPaper)
    {
        var reasons = new List<SuspectReason>();
        var height = image.HeightMillimeters;

        if (height >= ScannerLimitMm - LimitToleranceMm)
        {
            reasons.Add(SuspectReason.AtScannerLimit);
        }
        else if (longPaper?.FrameHeightInches is { } frame && Math.Abs(height - (frame * 25.4)) <= LimitToleranceMm)
        {
            reasons.Add(SuspectReason.AtFrameLimit);
        }

        if (analysis.IsBlank)
        {
            reasons.Add(SuspectReason.Blank);
        }
        else if (analysis.ContentAtBottomEdge)
        {
            reasons.Add(SuspectReason.ContentAtBottomEdge);
        }

        if (longPaper is { Confirmed: false })
        {
            reasons.Add(SuspectReason.LongPaperUnconfirmed);
        }

        return new CompletenessResult(reasons);
    }
}
