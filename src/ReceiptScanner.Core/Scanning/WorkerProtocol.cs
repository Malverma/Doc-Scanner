using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReceiptScanner.Core.Scanning;

/// <summary>One JSON line on the worker's stdout.</summary>
public sealed class WorkerEvent
{
    public const string Log = "log";
    public const string Source = "source";
    public const string LongPaper = "longPaper";
    public const string Waiting = "waiting";
    public const string Rows = "rows";
    public const string Complete = "complete";
    public const string Error = "error";
    public const string Result = "result";

    public required string Type { get; set; }
    public string? Message { get; set; }
    public ScanErrorCode? Code { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public int? BytesPerPixel { get; set; }
    public int? Dpi { get; set; }
    public long? RowCount { get; set; }
    public string? Method { get; set; }
    public bool? Confirmed { get; set; }
    public string? Evidence { get; set; }
    public double? FrameHeightInches { get; set; }
    public bool? Success { get; set; }
}

public static class WorkerProtocol
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(WorkerEvent workerEvent) => JsonSerializer.Serialize(workerEvent, Options);

    /// <summary>Returns null when a line isn't a valid event, such as stray output from a driver.</summary>
    public static WorkerEvent? TryParse(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line[0] != '{')
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<WorkerEvent>(line, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
