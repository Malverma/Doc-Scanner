using System.Text.Json;

namespace ReceiptScanner.TwainWorker;

/// <summary>
/// Brother's driver state, saved after the user selects Long Paper (spec Option B). The format belongs
/// to the driver, so a profile is only valid for the exact driver version that produced it.
/// </summary>
internal sealed record LongPaperProfile(string SourceName, string DriverVersion, string DataBase64, DateTimeOffset CapturedAt)
{
    public static LongPaperProfile? TryLoad(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<LongPaperProfile>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(string path)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this));
        File.Move(temp, path, overwrite: true);
    }
}
