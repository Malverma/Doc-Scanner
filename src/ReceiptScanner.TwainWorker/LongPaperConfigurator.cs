using NTwain;
using NTwain.Data;
using ReceiptScanner.Core.Scanning;

namespace ReceiptScanner.TwainWorker;

/// <summary>Tries to enable Long Paper by the methods in spec section 5.2, reporting what the driver accepted.</summary>
internal static class LongPaperConfigurator
{
    public const float MaxLengthInches = 72f;
    private const float MaxWidthInches = 8.5f;
    private const float ConfirmedFrameInches = 71.9f;

    public static LongPaperStatus Apply(TwainSessionHost host, LongPaperStrategy strategy, string profilePath) => strategy switch
    {
        LongPaperStrategy.Capabilities => ApplyCapabilities(host.Source),
        LongPaperStrategy.SavedState => ApplyProfile(host, profilePath),
        LongPaperStrategy.BrotherDialog => new LongPaperStatus(
            "Brother dialog", true, "Relies on Long Paper being selected in Brother's dialog", null),
        _ => ApplyAuto(host, profilePath),
    };

    /// <summary>
    /// A saved profile wins, because it came from the driver's own Long Paper selection. That's stronger
    /// evidence than capability readback, which only shows the driver accepted a value.
    /// </summary>
    private static LongPaperStatus ApplyAuto(TwainSessionHost host, string profilePath)
    {
        if (File.Exists(profilePath))
        {
            var saved = ApplyProfile(host, profilePath);
            if (saved.Confirmed)
            {
                return saved;
            }

            Output.Log($"Saved profile not used: {saved.Evidence}");
        }

        return ApplyCapabilities(host.Source);
    }

    public static LongPaperStatus ApplyCapabilities(DataSource source)
    {
        var caps = source.Capabilities;
        var evidence = new List<string>();

        void SetBool(string name, ICapWrapper<BoolType> cap, BoolType value)
        {
            if (!cap.IsSupported)
            {
                evidence.Add($"{name}=unsupported");
                return;
            }

            evidence.Add($"{name}: set {value} -> {cap.SetValue(value)}");
        }

        SetBool("AutoBorderDetection", caps.ICapAutomaticBorderDetection, BoolType.False);
        SetBool("AutoDeskew", caps.ICapAutomaticDeskew, BoolType.False);

        if (caps.ICapSupportedSizes.IsSupported)
        {
            evidence.Add($"SupportedSizes: set None -> {caps.ICapSupportedSizes.SetValue(SupportedSize.None)}");
        }

        SetBool("UndefinedImageSize", caps.ICapUndefinedImageSize, BoolType.True);
        SetBool("AutoLengthDetection", caps.ICapAutomaticLengthDetection, BoolType.True);

        var frameHeight = SetFrame(source, evidence);

        var undefinedOn = ReadsTrue(caps.ICapUndefinedImageSize);
        var lengthDetectOn = ReadsTrue(caps.ICapAutomaticLengthDetection);
        var frameOk = frameHeight >= ConfirmedFrameInches;
        evidence.Add($"Readback: frame={frameHeight:0.###} in, undefined={undefinedOn}, lengthDetect={lengthDetectOn}");

        var confirmed = frameOk || undefinedOn || lengthDetectOn;
        return new LongPaperStatus("TWAIN capabilities", confirmed, string.Join("; ", evidence), frameHeight > 0 ? frameHeight : null);
    }

    private static LongPaperStatus ApplyProfile(TwainSessionHost host, string profilePath)
    {
        var source = host.Source;
        const string method = "Saved driver profile";
        var profile = LongPaperProfile.TryLoad(profilePath);
        if (profile is null)
        {
            return new LongPaperStatus(method, false, "No saved profile. Run Long Paper setup.", null);
        }

        if (profile.SourceName != source.Name || profile.DriverVersion != host.DriverVersion)
        {
            return new LongPaperStatus(method, false,
                $"Profile is for {profile.SourceName} {profile.DriverVersion}, but the driver is {source.Name} {host.DriverVersion}. Run Long Paper setup again.", null);
        }

        if (!CustomDataSupported(source))
        {
            return new LongPaperStatus(method, false, "The driver doesn't support restoring saved settings.", null);
        }

        var saved = Convert.FromBase64String(profile.DataBase64);
        source.Settings = saved;

        // The Settings setter hides failures, so a readback is the only way to know it worked.
        var current = source.Settings;
        var matches = current is not null && current.AsSpan().SequenceEqual(saved);
        var frame = ReadFrameHeight(source);
        return new LongPaperStatus(method, matches,
            matches ? $"Restored {saved.Length} bytes; readback matches; frame={frame:0.###} in" : "The driver's settings didn't match the profile after restoring.",
            frame > 0 ? frame : null);
    }

    public static bool CustomDataSupported(DataSource source)
    {
        var cap = source.Capabilities.CapCustomDSData;
        return cap.IsSupported && cap.GetCurrent() == BoolType.True;
    }

    private static float SetFrame(DataSource source, List<string> evidence)
    {
        source.Capabilities.ICapUnits.SetValue(Unit.Inches);
        var rc = source.DGImage.ImageLayout.Get(out TWImageLayout layout);
        if (rc != ReturnCode.Success)
        {
            evidence.Add($"ImageLayout get -> {rc}");
            return 0;
        }

        layout.Frame = new TWFrame { Left = 0, Top = 0, Right = MaxWidthInches, Bottom = MaxLengthInches };
        evidence.Add($"ImageLayout: set 8.5x72 in -> {source.DGImage.ImageLayout.Set(layout)}");
        return ReadFrameHeight(source);
    }

    public static float ReadFrameHeight(DataSource source)
    {
        return source.DGImage.ImageLayout.Get(out TWImageLayout layout) == ReturnCode.Success
            ? (float)layout.Frame.Bottom - (float)layout.Frame.Top
            : 0;
    }

    private static bool ReadsTrue(ICapWrapper<BoolType> cap)
    {
        return cap.IsSupported && cap.CanGetCurrent && cap.GetCurrent() == BoolType.True;
    }
}
