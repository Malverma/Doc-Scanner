using NTwain;
using NTwain.Data;
using ReceiptScanner.Core.Scanning;

namespace ReceiptScanner.TwainWorker;

/// <summary>Tries to enable Long Paper by the methods in spec section 5.2, reporting what the driver accepted.</summary>
internal static class LongPaperConfigurator
{
    public const float MaxLengthInches = 72f;
    private const float MaxWidthInches = 8.5f;
    private const float FrameToleranceInches = 0.1f;
    private const float MmPerInch = 25.4f;
    private const string CapabilitiesMethod = "TWAIN capabilities";
    private const string ProfileMethod = "Saved driver profile";

    public static LongPaperStatus Apply(TwainSessionHost host, ScanRequest request) => request.Strategy switch
    {
        LongPaperStrategy.Capabilities => ApplyCapabilities(host.Source, ScanSize.From(request)),
        LongPaperStrategy.SavedState => ApplyProfile(host, request.ProfilePath),
        LongPaperStrategy.BrotherDialog => new LongPaperStatus(
            $"{host.Model.Maker} dialog", true, $"Relies on the user choosing long paper in {host.Model.Maker}'s dialog", null),
        _ => ApplyAuto(host, request),
    };

    /// <summary>
    /// Runs after the scan settings are sent, so the page size is the driver's last instruction. Also
    /// applies a requested width to a saved profile, whose frame is otherwise the driver's own.
    /// </summary>
    public static LongPaperStatus Reassert(TwainSessionHost host, ScanRequest request, LongPaperStatus status) => status.Method switch
    {
        CapabilitiesMethod => ApplyCapabilities(host.Source, ScanSize.From(request)),
        ProfileMethod when status.Confirmed => ReassertProfile(host.Source, ScanSize.From(request), status),
        _ => status,
    };

    /// <summary>
    /// A saved profile wins, because it came from the driver's own Long Paper selection. That's stronger
    /// evidence than capability readback, which only shows the driver accepted a value.
    /// </summary>
    private static LongPaperStatus ApplyAuto(TwainSessionHost host, ScanRequest request)
    {
        if (File.Exists(request.ProfilePath))
        {
            var saved = ApplyProfile(host, request.ProfilePath);
            if (saved.Confirmed)
            {
                return saved;
            }

            Output.Log($"Saved profile not used: {saved.Evidence}");
        }

        return ApplyCapabilities(host.Source, ScanSize.From(request));
    }

    public static LongPaperStatus ApplyCapabilities(DataSource source, ScanSize? size = null)
    {
        size ??= ScanSize.Default;
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

        var frameHeight = SetFrame(source, size, evidence);

        var undefinedOn = ReadsTrue(caps.ICapUndefinedImageSize);
        var lengthDetectOn = ReadsTrue(caps.ICapAutomaticLengthDetection);
        var frameOk = frameHeight >= size.RequiredLengthInches - FrameToleranceInches;
        evidence.Add($"Readback: frame={frameHeight:0.###} in, undefined={undefinedOn}, lengthDetect={lengthDetectOn}");

        var confirmed = frameOk || undefinedOn || lengthDetectOn;
        return new LongPaperStatus(CapabilitiesMethod, confirmed, string.Join("; ", evidence), frameHeight > 0 ? frameHeight : null);
    }

    private static LongPaperStatus ApplyProfile(TwainSessionHost host, string profilePath)
    {
        var source = host.Source;
        const string method = ProfileMethod;
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
        source.Capabilities.ICapUnits.SetValue(Unit.Inches);
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

    /// <summary>
    /// Always asks for the scanner's full length, so a short expected length can't cut a receipt off. The
    /// expected length is only tried if the driver won't accept enough, and never replaces a longer frame.
    /// </summary>
    private static float SetFrame(DataSource source, ScanSize size, List<string> evidence)
    {
        source.Capabilities.ICapUnits.SetValue(Unit.Inches);
        var rc = source.DGImage.ImageLayout.Get(out TWImageLayout layout);
        if (rc != ReturnCode.Success)
        {
            evidence.Add($"ImageLayout get -> {rc}");
            return 0;
        }

        var left = LeftEdge(source, size.WidthInches);

        float TrySet(float height)
        {
            layout.Frame = new TWFrame { Left = left, Top = 0, Right = left + size.WidthInches, Bottom = height };
            evidence.Add($"ImageLayout: set {size.WidthInches:0.##}x{height:0.##} in at left {left:0.##} -> {source.DGImage.ImageLayout.Set(layout)}");
            return ReadFrameHeight(source);
        }

        var frameHeight = TrySet(MaxLengthInches);
        if (frameHeight < size.RequiredLengthInches - FrameToleranceInches && size.RequiredLengthInches < MaxLengthInches)
        {
            frameHeight = TrySet(size.RequiredLengthInches);
        }

        return frameHeight;
    }

    private static LongPaperStatus ReassertProfile(DataSource source, ScanSize size, LongPaperStatus status)
    {
        if (source.DGImage.ImageLayout.Get(out TWImageLayout layout) != ReturnCode.Success)
        {
            return status;
        }

        var wanted = (float)(status.FrameHeightInches ?? 0);
        var top = (float)layout.Frame.Top;
        var height = (float)layout.Frame.Bottom - top;
        var heightKept = height >= wanted - FrameToleranceInches;
        if (heightKept && !size.WidthRequested)
        {
            return status;
        }

        var left = size.WidthRequested ? LeftEdge(source, size.WidthInches) : (float)layout.Frame.Left;
        var right = size.WidthRequested ? left + size.WidthInches : (float)layout.Frame.Right;
        layout.Frame = new TWFrame { Left = left, Top = top, Right = right, Bottom = top + Math.Max(height, wanted) };
        var rc = source.DGImage.ImageLayout.Set(layout);
        var after = ReadFrameHeight(source);
        var confirmed = after >= wanted - FrameToleranceInches;
        return status with
        {
            Confirmed = confirmed,
            Evidence = $"{status.Evidence}; frame reasserted, {right - left:0.##} in wide -> {rc}; frame={after:0.###} in",
            FrameHeightInches = after > 0 ? after : status.FrameHeightInches,
        };
    }

    /// <summary>A narrower frame has to sit where the paper feeds, or it would clip a centered receipt.</summary>
    private static float LeftEdge(DataSource source, float widthInches)
    {
        var cap = source.Capabilities.CapFeederAlignment;
        var centered = cap.IsSupported && cap.CanGetCurrent && cap.GetCurrent() == FeederAlignment.Center;
        return centered ? (MaxWidthInches - widthInches) / 2 : 0;
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

    /// <summary>The page the driver needs to accept. By default that's the scanner's full size.</summary>
    internal sealed record ScanSize(float WidthInches, float RequiredLengthInches, bool WidthRequested)
    {
        public static readonly ScanSize Default = new(MaxWidthInches, MaxLengthInches, false);

        public static ScanSize From(ScanRequest request) => new(
            Math.Min(MaxWidthInches, (float)(request.ExpectedWidthMm ?? MaxWidthInches * MmPerInch) / MmPerInch),
            Math.Min(MaxLengthInches, (float)(request.ExpectedLengthMm ?? MaxLengthInches * MmPerInch) / MmPerInch),
            request.ExpectedWidthMm is not null);
    }
}
