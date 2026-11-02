using System.Text.Json;
using System.Text.Json.Serialization;
using ReceiptScanner.Core.Scanning;

namespace ReceiptScanner.Core.Storage;

public sealed class AppSettings
{
    /// <summary>600 is the scanner's optical limit, so 1200 is never offered (spec 4.2).</summary>
    public static readonly IReadOnlyList<int> AllowedDpi = [200, 300, 600];

    public string BaseFolder { get; set; } = "";
    public string? TargetFolder { get; set; }
    public int Dpi { get; set; } = 300;
    public ColorMode Color { get; set; } = ColorMode.Gray;
    public OutputFormat Format { get; set; } = OutputFormat.Pdf;
    public bool TrimBlankSpace { get; set; } = true;
    public LongPaperStrategy LongPaper { get; set; } = LongPaperStrategy.Auto;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load(string path, string defaultBaseFolder, AppLog? log = null)
    {
        AppSettings? loaded = null;
        try
        {
            if (File.Exists(path))
            {
                loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            log?.Error($"Settings could not be read, so defaults are in use: {ex.GetType().Name}");
        }

        var settings = loaded ?? new AppSettings();
        if (string.IsNullOrWhiteSpace(settings.BaseFolder))
        {
            settings.BaseFolder = defaultBaseFolder;
        }

        if (!AllowedDpi.Contains(settings.Dpi))
        {
            settings.Dpi = 300;
        }

        return settings;
    }

    public void Save(string path)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Options));
        File.Move(temp, path, overwrite: true);
    }
}
