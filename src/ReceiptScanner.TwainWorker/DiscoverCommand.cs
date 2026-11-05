using System.Globalization;
using System.Text;
using NTwain;
using NTwain.Data;
using ReceiptScanner.Core.Scanning;

namespace ReceiptScanner.TwainWorker;

/// <summary>
/// Spec Phase 1. Dumps every capability, asks the user to select Long Paper, then dumps again.
/// The differences show which setting controls Long Paper.
/// </summary>
internal static class DiscoverCommand
{
    private const int CustomCapFirst = 0x8000;
    private const int CustomCapLast = 0x8FFF;

    public static int Run(string reportPath, string? scannerId)
    {
        var report = new StringBuilder();
        void Line(string text = "") => report.Append(text).Append('\n');

        using var host = TwainSessionHost.Open(scannerId);
        var source = host.Source;

        Line("# TWAIN capability report");
        Line();
        Line(string.Create(CultureInfo.InvariantCulture, $"- Generated: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}"));
        Line($"- Source: {source.Name}");
        Line($"- Recognized as: {host.Model.DisplayName}");
        Line($"- Manufacturer: {source.Manufacturer}");
        Line($"- Product family: {source.ProductFamily}");
        Line($"- Driver version: {host.DriverVersion}");
        Line($"- TWAIN protocol: {source.ProtocolVersion}");
        Line($"- TWAIN manager: {PlatformInfo.Current.ExpectedDsmPath}");
        Line($"- Worker is 64-bit: {Environment.Is64BitProcess}");
        Line($"- Saved settings supported (CAP_CUSTOMDSDATA): {LongPaperConfigurator.CustomDataSupported(source)}");
        Line();

        var before = Dump(source);
        var settingsBefore = source.Settings;
        var frameBefore = LongPaperConfigurator.ReadFrameHeight(source);

        Output.Log($"Opening {host.Model.Maker}'s dialog. In it, {host.Model.LongPaperSetting}, then click OK.");
        var opened = DialogRunner.ShowSettingsDialog(host, out var accepted);

        Line($"## {host.Model.Maker} dialog");
        Line();
        Line($"- Opened settings-only dialog: {opened}");
        Line($"- User clicked OK: {accepted}");
        Line();

        var after = opened ? Dump(source) : new SortedDictionary<int, CapValue>();
        var settingsAfter = opened ? source.Settings : null;

        Line("## What changed after selecting Long Paper");
        Line();
        if (!opened || !accepted)
        {
            Line("Not measured: the dialog wasn't opened, or wasn't confirmed.");
        }
        else
        {
            var changes = before.Keys.Union(after.Keys).OrderBy(k => k)
                .Where(k => before.GetValueOrDefault(k)?.Current != after.GetValueOrDefault(k)?.Current)
                .ToList();
            Line(changes.Count == 0 ? "No capability changed." : "| Capability | Before | After |");
            if (changes.Count > 0)
            {
                Line("|---|---|---|");
            }

            foreach (var id in changes)
            {
                Line($"| {CapName(id)} | {Cell(before.GetValueOrDefault(id)?.Current)} | {Cell(after.GetValueOrDefault(id)?.Current)} |");
            }

            Line();
            Line(string.Create(CultureInfo.InvariantCulture, $"- Frame height before: {frameBefore:0.###} in; after: {LongPaperConfigurator.ReadFrameHeight(source):0.###} in"));
            var changedData = settingsBefore is not null && settingsAfter is not null && !settingsBefore.AsSpan().SequenceEqual(settingsAfter);
            Line($"- Saved settings: {settingsBefore?.Length ?? 0} bytes before, {settingsAfter?.Length ?? 0} after, content changed: {changedData}");
        }

        Line();
        Line("## Capabilities before the dialog");
        Line();
        Line("| Capability | Current | Allowed |");
        Line("|---|---|---|");
        foreach (var (id, value) in before)
        {
            Line($"| {CapName(id)} | {Cell(value.Current)} | {Cell(value.Allowed)} |");
        }

        Line();
        Line("## Option A: what the driver accepts");
        Line();
        var status = LongPaperConfigurator.ApplyCapabilities(source);
        Line($"- Confirmed by readback: {status.Confirmed}");
        Line($"- Frame height: {status.FrameHeightInches?.ToString("0.###", CultureInfo.InvariantCulture) ?? "unknown"} in");
        foreach (var item in status.Evidence.Split("; "))
        {
            Line($"- {item}");
        }

        Line();
        Line("Readback shows the driver accepted these values. Only a scan of a long receipt proves they work.");

        File.WriteAllText(reportPath, report.ToString());
        Output.Emit(new WorkerEvent { Type = WorkerEvent.Result, Success = true, Message = "Driver check finished." });
        return 0;
    }

    private static SortedDictionary<int, CapValue> Dump(DataSource source)
    {
        var results = new SortedDictionary<int, CapValue>();

        var advertised = source.Capabilities.CapSupportedCaps.IsSupported
            ? source.Capabilities.CapSupportedCaps.GetValues().Select(c => (int)(ushort)c)
            : [];

        // Vendor capabilities are often not advertised, so that whole range gets probed as well.
        foreach (var id in advertised.Union(Enumerable.Range(CustomCapFirst, CustomCapLast - CustomCapFirst + 1)))
        {
            var value = Read(source, id);
            if (value is not null)
            {
                results[id] = value;
            }
        }

        return results;
    }

    private static CapValue? Read(DataSource source, int id)
    {
        var capId = (CapabilityId)(ushort)id;
        try
        {
            string? current = null;
            string? allowed = null;
            using (var cap = new TWCapability(capId))
            {
                if (source.DGControl.Capability.GetCurrent(cap) == ReturnCode.Success)
                {
                    current = Describe(CapabilityReader.ReadValue(cap));
                }
            }

            using (var cap = new TWCapability(capId))
            {
                if (source.DGControl.Capability.Get(cap) == ReturnCode.Success)
                {
                    allowed = Describe(CapabilityReader.ReadValue(cap));
                }
            }

            return current is null && allowed is null ? null : new CapValue(current, allowed);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new CapValue($"(read failed: {ex.GetType().Name})", null);
        }
    }

    private static string Describe(CapabilityReader reader) => reader.ContainerType switch
    {
        ContainerType.OneValue => Convert.ToString(reader.OneValue, CultureInfo.InvariantCulture) ?? "",
        ContainerType.Range => string.Create(
            CultureInfo.InvariantCulture,
            $"{reader.RangeMinValue}..{reader.RangeMaxValue} step {reader.RangeStepSize}, current {reader.RangeCurrentValue}"),
        _ => string.Join(", ", (reader.CollectionValues ?? []).Select(v => Convert.ToString(v, CultureInfo.InvariantCulture))),
    };

    private static string CapName(int id)
    {
        var capId = (CapabilityId)(ushort)id;
        var hex = string.Create(CultureInfo.InvariantCulture, $"0x{id:X4}");
        return Enum.IsDefined(capId) ? $"{capId} ({hex})" : hex;
    }

    private static string Cell(string? text) => (text ?? "").Replace("|", "\\|", StringComparison.Ordinal);

    private sealed record CapValue(string? Current, string? Allowed);
}
