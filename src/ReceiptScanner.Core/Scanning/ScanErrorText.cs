namespace ReceiptScanner.Core.Scanning;

public sealed record ErrorDescription(string Title, IReadOnlyList<string> Steps);

/// <summary>Plain-language recovery steps (spec F5 and 4.4).</summary>
public static class ScanErrorText
{
    /// <param name="model">The scanner in use, when known. Without it the text names no maker.</param>
    public static ErrorDescription Describe(ScanErrorCode code, ScannerModel? model = null)
    {
        var scanner = model?.Name ?? "Scanner";
        var driver = model is null ? "The scanner's driver" : $"{model.Maker}'s driver";
        var package = model is null
            ? "Install the driver package from the scanner maker's website."
            : $"Install {model.DriverPackage} from {model.DriverSite}.";
        var cover = model?.CoverName ?? "scanner's cover";
        return Describe(code, scanner, driver, package, cover, model);
    }

    private static ErrorDescription Describe(ScanErrorCode code, string scanner, string driver, string package, string cover, ScannerModel? model) => code switch
    {
        ScanErrorCode.NoPaper => new("No receipt detected",
        [
            "Insert one receipt into the feed slot, flat and straight, with the guide against its edge.",
            "Scan again.",
        ]),
        ScanErrorCode.Jam => new("The receipt jammed. Nothing was saved",
        [
            $"Open the {cover} before pulling the paper out. Pulling first can damage the receipt.",
            "Remove the receipt and close the cover.",
            "Support long receipts by hand so they don't curl or drag, then scan again.",
        ]),
        ScanErrorCode.CoverOpen => new($"The {cover} is open",
        [
            $"Close the {cover} until it clicks.",
            "Scan again.",
        ]),
        ScanErrorCode.ScannerNotFound => new($"{scanner} not found",
        [
            model?.ConnectionAdvice ?? "Check the scanner is on and its USB cable is connected directly to the PC.",
            model is null
                ? $"Install the driver for a supported scanner: {string.Join(" or ", ScannerModels.All.Select(m => m.DisplayName))}."
                : $"Install {model.DriverPackage} if it isn't installed.",
            "Scan again. You don't need to restart this app.",
        ]),
        ScanErrorCode.Disconnected => new("The scanner disconnected. Nothing was saved",
        [
            $"If a receipt is still in the scanner, open the {cover} and remove it.",
            "Unplug the USB cable and plug it back in.",
            "Scan again. You don't need to restart this app.",
        ]),
        ScanErrorCode.DriverMissing => new($"{driver} isn't available",
        [
            package,
            "Scan again.",
        ]),
        ScanErrorCode.LongPaperUnconfirmed => new("Long Paper mode couldn't be confirmed",
        [
            "Open Settings and choose Set Up Long Paper.",
            model is null
                ? "In the driver's dialog, choose the long paper document size and confirm."
                : $"In {model.Maker}'s dialog, {model.LongPaperSetting} and confirm.",
            "Scan again.",
        ]),
        ScanErrorCode.Canceled => new("The scan was canceled. Nothing was saved",
        [
            $"If a receipt is still in the scanner, open the {cover} and remove it.",
            "If the next scan fails, unplug the scanner and plug it back in.",
        ]),
        ScanErrorCode.StoppedByUser => new("Stopped waiting for the scanner. Nothing was saved",
        [
            $"If a receipt is still in the scanner, open the {cover} and remove it.",
            "Unplug the scanner and plug it back in before scanning again.",
        ]),
        ScanErrorCode.DriverReported => new($"{driver} reported a problem. Nothing was saved",
        [
            $"Follow any message shown by {(model is null ? "the scanner's driver" : driver)}.",
            $"If a receipt is still in the scanner, open the {cover} and remove it.",
            "Scan again.",
        ]),
        ScanErrorCode.IncompleteData => new("The scan ended early. Nothing was saved",
        [
            $"If a receipt is still in the scanner, open the {cover} and remove it.",
            "Scan again.",
        ]),
        _ => new("The scan failed. Nothing was saved",
        [
            $"If a receipt is still in the scanner, open the {cover} and remove it.",
            "Scan again. If it keeps failing, unplug the scanner, plug it back in and check the log.",
        ]),
    };
}
