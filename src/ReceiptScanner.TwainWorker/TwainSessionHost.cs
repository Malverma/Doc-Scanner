using System.Reflection;
using NTwain;
using NTwain.Data;
using ReceiptScanner.Core.Scanning;

namespace ReceiptScanner.TwainWorker;

internal sealed class WorkerException(ScanErrorCode code, string message) : Exception(message)
{
    public ScanErrorCode Code { get; } = code;
}

/// <summary>Opens the TWAIN session and the DS-640's source, and guarantees they get closed.</summary>
internal sealed class TwainSessionHost : IDisposable
{
    private const string ExpectedModel = "DS-640";

    private TwainSessionHost(Win32Pump pump, TwainSession session)
    {
        Pump = pump;
        Session = session;
    }

    public Win32Pump Pump { get; }

    public TwainSession Session { get; }

    public DataSource Source { get; private set; } = null!;

    public string DriverVersion => Source.Version.Info ?? "";

    public static TwainSessionHost Open()
    {
        PlatformInfo.Current.PreferNewDSM = false;
        Output.Log($"DSM: {PlatformInfo.Current.ExpectedDsmPath} (exists: {PlatformInfo.Current.DsmExists}); 64-bit process: {Environment.Is64BitProcess}");
        if (!PlatformInfo.Current.DsmExists)
        {
            throw new WorkerException(ScanErrorCode.DriverMissing, "The Windows TWAIN manager (twain_32.dll) wasn't found.");
        }

        var identity = TWIdentity.CreateFromAssembly(DataGroups.Image, Assembly.GetExecutingAssembly());
        var host = new TwainSessionHost(new Win32Pump(), new TwainSession(identity) { StopOnTransferError = true });
        try
        {
            var rc = host.Session.Open(host.Pump);
            if (rc != ReturnCode.Success)
            {
                throw new WorkerException(ScanErrorCode.DriverMissing, $"Couldn't open the TWAIN manager: {rc}");
            }

            var sources = host.Session.GetSources().ToList();
            Output.Log($"TWAIN sources: {string.Join(" | ", sources.Select(s => s.Name))}");
            var source = sources.FirstOrDefault(s => s.Name.Contains(ExpectedModel, StringComparison.OrdinalIgnoreCase))
                ?? throw new WorkerException(ScanErrorCode.ScannerNotFound, "No TWAIN source for the DS-640 is installed.");

            rc = source.Open();
            if (rc != ReturnCode.Success)
            {
                // The source isn't open, so only the session can report a status. A driver that's installed
                // but can't be opened almost always means the scanner is unplugged or asleep.
                var status = host.Session.GetStatus();
                throw new WorkerException(ScanErrorCode.ScannerNotFound, $"Couldn't open {source.Name}: {rc} (status {status.ConditionCode})");
            }

            host.Source = source;
            Output.Log($"Source: {source.Name}; manufacturer: {source.Manufacturer}; family: {source.ProductFamily}; driver: {source.Version.Info}; protocol: {source.ProtocolVersion}");
            return host;
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        try
        {
            if (Session.State > 4)
            {
                Session.ForceStepDown(2);
                return;
            }

            if (Session.State == 4)
            {
                Session.CurrentSource?.Close();
            }

            if (Session.State >= 3)
            {
                Session.Close();
            }
        }
        catch (Exception ex)
        {
            // The process is about to exit, which releases everything anyway.
            Output.Log($"Error while closing TWAIN: {ex.GetType().Name}: {ex.Message}");
        }
    }
}

internal static class ErrorMapping
{
    public static WorkerException FromStatus(TWStatus status, string context)
    {
        var code = status.ConditionCode switch
        {
            ConditionCode.NoMedia => ScanErrorCode.NoPaper,
            ConditionCode.PaperJam or ConditionCode.PaperDoubleFeed => ScanErrorCode.Jam,
            ConditionCode.Interlock => ScanErrorCode.CoverOpen,
            ConditionCode.CheckDeviceOnline => ScanErrorCode.Disconnected,
            ConditionCode.NoDS => ScanErrorCode.ScannerNotFound,
            ConditionCode.OperationError => ScanErrorCode.DriverReported,
            _ => ScanErrorCode.Unknown,
        };
        return new WorkerException(code, $"{context} (status {status.ConditionCode})");
    }
}
