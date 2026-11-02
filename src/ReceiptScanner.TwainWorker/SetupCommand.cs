using NTwain;
using NTwain.Data;
using ReceiptScanner.Core.Scanning;

namespace ReceiptScanner.TwainWorker;

/// <summary>Spec Option B: the user selects Long Paper in Brother's dialog, then the driver state is saved.</summary>
internal static class SetupCommand
{
    public static int Run(string profilePath)
    {
        using var host = TwainSessionHost.Open();
        if (!LongPaperConfigurator.CustomDataSupported(host.Source))
        {
            Result(false, "This driver doesn't support saved settings. Use Brother's dialog for each scan instead.");
            return 1;
        }

        if (!DialogRunner.ShowSettingsDialog(host, out var accepted))
        {
            Result(false, "Brother's driver can't open its settings on their own here. Use Brother's dialog for each scan instead.");
            return 1;
        }

        if (!accepted)
        {
            Result(false, "Setup was canceled, so nothing changed.");
            return 1;
        }

        var data = host.Source.Settings;
        if (data is null || data.Length == 0)
        {
            Result(false, "The driver didn't return its settings, so the profile couldn't be saved.");
            return 1;
        }

        new LongPaperProfile(host.Source.Name, host.DriverVersion, Convert.ToBase64String(data), DateTimeOffset.Now).Save(profilePath);
        Output.Log($"Saved a Long Paper profile of {data.Length} bytes for driver {host.DriverVersion}");
        Result(true, "Long Paper settings saved. Scans will now use them.");
        return 0;
    }

    private static void Result(bool success, string message) =>
        Output.Emit(new WorkerEvent { Type = WorkerEvent.Result, Success = success, Message = message });
}

internal static class DialogRunner
{
    /// <summary>Returns false if the dialog couldn't open. Accepted is true only if the user clicked OK.</summary>
    public static bool ShowSettingsDialog(TwainSessionHost host, out bool accepted)
    {
        accepted = false;
        var session = host.Session;

        void OnDisabled(object? sender, EventArgs e) => host.Pump.Quit();

        session.SourceDisabled += OnDisabled;
        try
        {
            var rc = host.Source.Enable(SourceEnableMode.ShowUIOnly, false, host.Pump.WindowHandle);
            if (rc != ReturnCode.Success)
            {
                Output.Log($"Settings-only dialog failed to open: {rc}, status {host.Source.GetStatus().ConditionCode}");
                return false;
            }

            host.Pump.Run();
            accepted = session.DisableReason == Message.CloseDSOK;
            Output.Log($"Dialog closed: {session.DisableReason}");
            return true;
        }
        finally
        {
            session.SourceDisabled -= OnDisabled;
        }
    }
}
