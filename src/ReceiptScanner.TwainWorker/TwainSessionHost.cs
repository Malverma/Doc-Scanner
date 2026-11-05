using System.Reflection;
using NTwain;
using NTwain.Data;
using ReceiptScanner.Core.Scanning;

namespace ReceiptScanner.TwainWorker;

internal sealed class WorkerException(ScanErrorCode code, string message) : Exception(message)
{
    public ScanErrorCode Code { get; } = code;
}

/// <summary>Opens the TWAIN session and a recognized scanner's source, and guarantees they get closed.</summary>
internal sealed class TwainSessionHost : IDisposable
{

    private TwainSessionHost(Win32Pump pump, TwainSession session)
    {
        Pump = pump;
        Session = session;
    }

    public Win32Pump Pump { get; }

    public TwainSession Session { get; }

    public DataSource Source { get; private set; } = null!;

    public ScannerModel Model { get; private set; } = null!;

    public string DriverVersion => Source.Version.Info ?? "";

    /// <param name="preferredScannerId">A <see cref="ScannerModel.Id"/> to require, or null for the first recognized scanner.</param>
    public static TwainSessionHost Open(string? preferredScannerId)
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
            Output.Log($"TWAIN source details: {string.Join(" | ", sources.Select(Describe))}");
            var preferred = ScannerModels.FromId(preferredScannerId);
            var candidates = ScannerModels.Candidates(sources, Describe, preferredScannerId);
            if (candidates.Count == 0)
            {
                var found = sources.Count == 0 ? "none" : string.Join(", ", sources.Select(s => s.Name));
                throw new WorkerException(ScanErrorCode.ScannerNotFound, preferred is null
                    ? $"No TWAIN source for a supported scanner ({string.Join(", ", ScannerModels.All.Select(m => m.DisplayName))}) is installed. TWAIN sources found: {found}."
                    : $"No TWAIN source for the {preferred.DisplayName} is installed. TWAIN sources found: {found}.");
            }

            // A driver that's installed but can't be opened almost always means its scanner is unplugged or
            // asleep, so the next recognized scanner gets a turn. Only the session can report a status then.
            var failures = new List<string>();
            (DataSource Source, ScannerModel Model)? opened = null;
            foreach (var candidate in candidates)
            {
                rc = candidate.Source.Open();
                if (rc == ReturnCode.Success)
                {
                    opened = candidate;
                    break;
                }

                var failure = $"Couldn't open {candidate.Source.Name}: {rc} (status {host.Session.GetStatus().ConditionCode})";
                Output.Log(failure);
                failures.Add(failure);
            }

            if (opened is not { } pick)
            {
                throw new WorkerException(ScanErrorCode.ScannerNotFound, string.Join("; ", failures));
            }

            var source = pick.Source;
            host.Source = source;
            host.Model = pick.Model;
            Output.Emit(new WorkerEvent { Type = WorkerEvent.Source, ScannerId = pick.Model.Id, Message = $"{source.Name}, driver {host.DriverVersion}" });
            Output.Log($"Source: {source.Name}; manufacturer: {source.Manufacturer}; family: {source.ProductFamily}; driver: {source.Version.Info}; protocol: {source.ProtocolVersion}");
            return host;
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    private static string Describe(DataSource source) => $"{source.Name} ({source.Manufacturer}, {source.ProductFamily})";

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
