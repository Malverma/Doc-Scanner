namespace ReceiptScanner.Core.Scanning;

public sealed record ErrorDescription(string Title, IReadOnlyList<string> Steps);

/// <summary>Plain-language recovery steps (spec F5 and 4.4).</summary>
public static class ScanErrorText
{
    public static ErrorDescription Describe(ScanErrorCode code) => code switch
    {
        ScanErrorCode.NoPaper => new("No receipt detected",
        [
            "Insert one receipt into the feed slot, flat and straight, with the guide against its edge.",
            "Scan again.",
        ]),
        ScanErrorCode.Jam => new("The receipt jammed. Nothing was saved",
        [
            "Open the Top Cover before pulling the paper out. Pulling first can damage the receipt.",
            "Remove the receipt and close the cover.",
            "Support long receipts by hand so they don't curl or drag, then scan again.",
        ]),
        ScanErrorCode.CoverOpen => new("The scanner's cover is open",
        [
            "Close the Top Cover until it clicks.",
            "Scan again.",
        ]),
        ScanErrorCode.ScannerNotFound => new("DS-640 not found",
        [
            "Check the USB cable. Brother specifies a cable of 2 m or less, connected directly to the PC.",
            "Install Brother's DS-640 driver package if it isn't installed.",
            "Scan again. You don't need to restart this app.",
        ]),
        ScanErrorCode.Disconnected => new("The scanner disconnected. Nothing was saved",
        [
            "If a receipt is still in the scanner, open the Top Cover and remove it.",
            "Unplug the USB cable and plug it back in.",
            "Scan again. You don't need to restart this app.",
        ]),
        ScanErrorCode.DriverMissing => new("Brother's scanner driver isn't available",
        [
            "Install Brother's full DS-640 driver package from support.brother.com.",
            "Scan again.",
        ]),
        ScanErrorCode.LongPaperUnconfirmed => new("Long Paper mode couldn't be confirmed",
        [
            "Open Settings and choose Set Up Long Paper.",
            "In Brother's dialog, set Document Size to Long Paper and confirm.",
            "Scan again.",
        ]),
        ScanErrorCode.Canceled => new("The scan was canceled. Nothing was saved",
        [
            "If a receipt is still in the scanner, open the Top Cover and remove it.",
            "If the next scan fails, unplug the scanner and plug it back in.",
        ]),
        ScanErrorCode.StoppedByUser => new("Stopped waiting for the scanner. Nothing was saved",
        [
            "If a receipt is still in the scanner, open the Top Cover and remove it.",
            "Unplug the scanner and plug it back in before scanning again.",
        ]),
        ScanErrorCode.DriverReported => new("Brother's driver reported a problem. Nothing was saved",
        [
            "Follow any message shown by Brother's driver.",
            "If a receipt is still in the scanner, open the Top Cover and remove it.",
            "Scan again.",
        ]),
        ScanErrorCode.IncompleteData => new("The scan ended early. Nothing was saved",
        [
            "If a receipt is still in the scanner, open the Top Cover and remove it.",
            "Scan again.",
        ]),
        _ => new("The scan failed. Nothing was saved",
        [
            "If a receipt is still in the scanner, open the Top Cover and remove it.",
            "Scan again. If it keeps failing, unplug the scanner, plug it back in and check the log.",
        ]),
    };
}
