using System.Globalization;
using ReceiptScanner.Core.Scanning;
using ReceiptScanner.TwainWorker;

namespace ReceiptScanner.TwainWorker;

internal static class Program
{
    /// <summary>Exit code 0 is success. Results and errors reach the app as JSON events on stdout.</summary>
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new WorkerException(ScanErrorCode.DriverMissing, "The TWAIN worker only runs on Windows.");
            }

            var command = args.FirstOrDefault() ?? "";
            var options = ParseOptions(args.Skip(1).ToArray());
            var scanner = options.GetValueOrDefault("scanner");
            switch (command)
            {
                case "probe":
                    // Opening the source reports it.
                    using (TwainSessionHost.Open(scanner))
                    {
                    }

                    return 0;
                case "scan":
                    return new ScanCommand(BuildRequest(options)).Run();
                case "setup":
                    return SetupCommand.Run(Required(options, "profile"), scanner);
                case "discover":
                    return DiscoverCommand.Run(Required(options, "out"), scanner);
                default:
                    throw new ArgumentException($"Unknown command \"{command}\". Use probe, scan, setup or discover.");
            }
        }
        catch (WorkerException ex)
        {
            Output.Emit(new WorkerEvent { Type = WorkerEvent.Error, Code = ex.Code, Message = ex.Message });
            return 1;
        }
        catch (DllNotFoundException ex)
        {
            Output.Emit(new WorkerEvent { Type = WorkerEvent.Error, Code = ScanErrorCode.DriverMissing, Message = ex.Message });
            return 1;
        }
        catch (Exception ex)
        {
            Output.Emit(new WorkerEvent { Type = WorkerEvent.Error, Code = ScanErrorCode.Unknown, Message = $"{ex.GetType().Name}: {ex.Message}" });
            return 2;
        }
    }

    private static ScanRequest BuildRequest(Dictionary<string, string> options) => new(
        int.Parse(Required(options, "dpi"), CultureInfo.InvariantCulture),
        Enum.Parse<ColorMode>(Required(options, "color")),
        Required(options, "out"),
        Enum.Parse<LongPaperStrategy>(Required(options, "strategy")),
        Required(options, "profile"),
        options.ContainsKey("allow-unconfirmed"),
        OptionalNumber(options, "width-mm"),
        OptionalNumber(options, "length-mm"),
        options.GetValueOrDefault("scanner"));

    private static double? OptionalNumber(Dictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) ? double.Parse(value, CultureInfo.InvariantCulture) : null;

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unexpected argument \"{args[i]}\".");
            }

            var name = args[i][2..];
            var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
            options[name] = hasValue ? args[++i] : "";
        }

        return options;
    }

    private static string Required(Dictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) && value.Length > 0
            ? value
            : throw new ArgumentException($"Missing --{name}.");
}
