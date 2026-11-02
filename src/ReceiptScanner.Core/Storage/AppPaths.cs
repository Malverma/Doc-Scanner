namespace ReceiptScanner.Core.Storage;

/// <summary>Per-user locations. No administrator rights are needed for any of them.</summary>
public sealed class AppPaths
{
    public AppPaths(string? dataRoot = null)
    {
        DataRoot = dataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
            "ReceiptScanner");
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(TempFolder);
        Directory.CreateDirectory(LogFolder);
    }

    public string DataRoot { get; }

    public string TempFolder => Path.Combine(DataRoot, "temp");

    public string LogFolder => Path.Combine(DataRoot, "logs");

    public string SettingsFile => Path.Combine(DataRoot, "settings.json");

    public string LongPaperProfile => Path.Combine(DataRoot, "long-paper-profile.json");

    public string DefaultBaseFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments, Environment.SpecialFolderOption.Create),
        "Receipts");

    public string NewRawPath() => Path.Combine(TempFolder, $"scan-{Guid.NewGuid():N}.raw");

    /// <summary>Removes leftovers from a previous run, such as after a crash or power loss.</summary>
    public void CleanTemp()
    {
        foreach (var file in Directory.EnumerateFiles(TempFolder, "*.raw"))
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // Another instance may be using it. It gets another chance next launch.
            }
            catch (UnauthorizedAccessException)
            {
                // Same as above.
            }
        }
    }
}
