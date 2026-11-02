using System.Globalization;

namespace ReceiptScanner.Core.Storage;

/// <summary>Diagnostic log (spec F10). Settings, status codes and sizes only, never receipt contents.</summary>
public sealed class AppLog
{
    private const int KeepFiles = 30;
    private readonly object _gate = new();
    private readonly string _folder;

    public AppLog(string folder)
    {
        _folder = folder;
        Prune();
    }

    public string Folder => _folder;

    public void Info(string message) => Write("INFO", message);

    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        var now = DateTime.Now;
        var line = string.Create(CultureInfo.InvariantCulture, $"{now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
        var path = Path.Combine(_folder, string.Create(CultureInfo.InvariantCulture, $"receipt-scanner-{now:yyyy-MM-dd}.log"));
        try
        {
            lock (_gate)
            {
                File.AppendAllText(path, line);
            }
        }
        catch (IOException)
        {
            // Logging must never break a scan.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above.
        }
    }

    private void Prune()
    {
        try
        {
            foreach (var old in new DirectoryInfo(_folder).GetFiles("*.log").OrderByDescending(f => f.Name).Skip(KeepFiles))
            {
                old.Delete();
            }
        }
        catch (IOException)
        {
            // Old logs staying around is harmless.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above.
        }
    }
}
