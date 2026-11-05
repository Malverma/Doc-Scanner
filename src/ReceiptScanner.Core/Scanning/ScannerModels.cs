namespace ReceiptScanner.Core.Scanning;

/// <summary>A scanner the app recognizes, and what the user needs to know to drive it.</summary>
/// <param name="Id">Stable key, saved in settings and passed to the worker.</param>
/// <param name="SourceNameMatches">Text that identifies the model in a TWAIN source name.</param>
/// <param name="LongPaperSetting">What to pick in the driver's dialog to scan long paper.</param>
/// <param name="CoverName">What the maker calls the cover that opens to clear a jam.</param>
/// <param name="SaneLongPaperArgs">
/// scanimage options that turn on long paper with the maker's Linux driver. Empty when there's no such
/// switch, so the scan falls back to the driver's largest page.
/// </param>
public sealed record ScannerModel(
    string Id,
    string Name,
    string Maker,
    IReadOnlyList<string> SourceNameMatches,
    string DriverPackage,
    string DriverSite,
    string ConnectionAdvice,
    string LongPaperSetting,
    string CoverName,
    IReadOnlyList<string> SaneLongPaperArgs)
{
    public string DisplayName => $"{Maker} {Name}";

    public bool Matches(string sourceName) =>
        SourceNameMatches.Any(m => sourceName.Contains(m, StringComparison.OrdinalIgnoreCase));
}

public static class ScannerModels
{
    public static readonly ScannerModel BrotherDs640 = new(
        "brother-ds640",
        "DS-640",
        "Brother",
        ["DS-640"],
        "Brother's full DS-640 driver package",
        "support.brother.com",
        "Check the USB cable. Brother specifies a cable of 2 m or less, connected directly to the PC.",
        "set Document Size to Long Paper",
        "Top Cover",
        []);

    /// <summary>Epson's TWAIN driver for this model comes with Epson Scan 2.</summary>
    public static readonly ScannerModel EpsonRr70W = new(
        "epson-rr70w",
        "RR-70W",
        "Epson",
        ["RR-70W", "RR70W"],
        "Epson Scan 2 and the RR-70W's scanner driver",
        "epson.com/support (search for RR-70W)",
        "Turn the scanner on and check the USB cable. On Wi-Fi, select the scanner in Epson Scan 2 Utility.",
        "set Document Size to Auto Detect (Long Paper)",
        "scanner cover",
        ["--long-paper-mode=yes"]);

    /// <summary>In the order the worker prefers them when more than one is installed.</summary>
    public static readonly IReadOnlyList<ScannerModel> All = [BrotherDs640, EpsonRr70W];

    public static ScannerModel? FromId(string? id) =>
        All.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

    public static ScannerModel? FromSourceName(string? sourceName) =>
        sourceName is null ? null : All.FirstOrDefault(m => m.Matches(sourceName));

    /// <summary>
    /// The source to open: the preferred model's if given, otherwise the first source of any known model,
    /// in the order of <see cref="All"/>. Returns null when nothing matches.
    /// </summary>
    public static (string Source, ScannerModel Model)? Pick(IEnumerable<string> sourceNames, string? preferredId) =>
        Candidates(sourceNames, n => n, preferredId).Cast<(string, ScannerModel)?>().FirstOrDefault();

    /// <summary>
    /// Every source of a supported model, in the order to try them: by <see cref="All"/>, then as listed.
    /// A driver can stay installed after its scanner is unplugged, so the caller tries each in turn.
    /// </summary>
    /// <param name="describe">The text to match, such as the source's name, product family and maker.</param>
    public static List<(T Source, ScannerModel Model)> Candidates<T>(IEnumerable<T> sources, Func<T, string> describe, string? preferredId)
    {
        var list = sources.ToList();
        var preferred = FromId(preferredId);
        IEnumerable<ScannerModel> models = preferred is null ? All : [preferred];
        return models.SelectMany(m => list.Where(s => m.Matches(describe(s))).Select(s => (s, m))).ToList();
    }
}
