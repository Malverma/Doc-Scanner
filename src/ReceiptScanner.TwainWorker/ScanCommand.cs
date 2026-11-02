using NTwain;
using NTwain.Data;
using ReceiptScanner.Core.Scanning;

namespace ReceiptScanner.TwainWorker;

/// <summary>Performs one scan. A scan only counts as complete when TWAIN ended it cleanly (spec F6).</summary>
internal sealed class ScanCommand(ScanRequest request)
{
    private static readonly TimeSpan RowEventInterval = TimeSpan.FromMilliseconds(250);

    private TwainSessionHost _host = null!;
    private RawSink _sink = null!;
    private WorkerException? _failure;
    private bool _canceled;
    private bool _pageSeen;
    private DateTime _lastRowEvent = DateTime.MinValue;

    public int Run()
    {
        using var host = TwainSessionHost.Open();
        _host = host;

        var status = LongPaperConfigurator.Apply(host, request.Strategy, request.ProfilePath);
        Output.Emit(new WorkerEvent
        {
            Type = WorkerEvent.LongPaper,
            Method = status.Method,
            Confirmed = status.Confirmed,
            Evidence = status.Evidence,
            FrameHeightInches = status.FrameHeightInches,
        });
        Output.Log($"Long Paper: {status.Method}; confirmed: {status.Confirmed}; {status.Evidence}");

        if (!status.Confirmed && !request.AllowUnconfirmedLongPaper)
        {
            throw new WorkerException(ScanErrorCode.LongPaperUnconfirmed, status.Evidence);
        }

        ConfigureScan();

        using var sink = new RawSink(request.RawOutputPath);
        _sink = sink;

        var session = host.Session;
        session.TransferReady += OnTransferReady;
        session.DataTransferred += OnDataTransferred;
        session.TransferError += OnTransferError;
        session.TransferCanceled += OnTransferCanceled;
        session.SourceDisabled += (_, _) => host.Pump.Quit();

        var mode = request.Strategy == LongPaperStrategy.BrotherDialog ? SourceEnableMode.ShowUI : SourceEnableMode.NoUI;
        Output.Log($"Enabling source: {mode}");
        var rc = host.Source.Enable(mode, false, host.Pump.WindowHandle);
        if (rc != ReturnCode.Success)
        {
            throw ErrorMapping.FromStatus(host.Source.GetStatus(), $"Couldn't start the scan: {rc}");
        }

        // No time limit here: the scanner can feed a long receipt before sending any data, and a
        // timeout could cut the scan short. The app offers Stop Waiting and ends this process if needed.
        Output.Emit(new WorkerEvent { Type = WorkerEvent.Waiting, Message = "Waiting for the scanner" });
        host.Pump.Run();

        if (_failure is not null)
        {
            throw _failure;
        }

        if (_canceled)
        {
            throw new WorkerException(ScanErrorCode.Canceled, "The driver reported the scan as canceled.");
        }

        var image = sink.Finish();
        if (image is null)
        {
            var noData = sink.RowsWritten == 0;
            throw new WorkerException(
                noData ? ScanErrorCode.Canceled : ScanErrorCode.IncompleteData,
                noData ? "The scan ended without any image data." : $"Only {sink.RowsWritten} rows arrived.");
        }

        Output.Emit(new WorkerEvent
        {
            Type = WorkerEvent.Complete,
            Width = image.Width,
            Height = image.Height,
            BytesPerPixel = image.BytesPerPixel,
            Dpi = image.Dpi,
        });
        return 0;
    }

    private void ConfigureScan()
    {
        var caps = _host.Source.Capabilities;

        void Report(string name, ReturnCode rc)
        {
            if (rc != ReturnCode.Success)
            {
                Output.Log($"{name} -> {rc}");
            }
        }

        Report("Xfer mech", caps.ICapXferMech.SetValue(XferMech.Memory));
        Report("Compression", caps.ICapCompression.SetValue(CompressionType.None));
        Report("Feeder", caps.CapFeederEnabled.SetValue(BoolType.True));
        Report("Xfer count", caps.CapXferCount.SetValue(1));
        if (caps.CapIndicators.IsSupported)
        {
            Report("Indicators", caps.CapIndicators.SetValue(BoolType.False));
        }

        if (caps.ICapAutoDiscardBlankPages.IsSupported)
        {
            // A pale receipt must never be discarded as blank (spec 4.2).
            Report("Blank page discard", caps.ICapAutoDiscardBlankPages.SetValue(BlankPage.Disable));
        }

        if (request.Color == ColorMode.Color)
        {
            Report("Pixel type", caps.ICapPixelType.SetValue(PixelType.RGB));
            Report("Bit depth", caps.ICapBitDepth.SetValue(24));
        }
        else
        {
            Report("Pixel type", caps.ICapPixelType.SetValue(PixelType.Gray));
            Report("Bit depth", caps.ICapBitDepth.SetValue(8));
        }

        Report("X resolution", caps.ICapXResolution.SetValue(request.Dpi));
        Report("Y resolution", caps.ICapYResolution.SetValue(request.Dpi));

        if (caps.CapFeederLoaded.IsSupported && caps.CapFeederLoaded.CanGetCurrent)
        {
            // Logged, not enforced. Blocking on this could stop every scan if the driver misreports it,
            // and the driver reports missing paper itself once the scan starts.
            Output.Log($"Feeder loaded: {caps.CapFeederLoaded.GetCurrent()}");
        }
    }

    private void OnTransferReady(object? sender, TransferReadyEventArgs e)
    {
        // One receipt per file. If the driver offers a second page, leave it alone.
        if (_pageSeen)
        {
            Output.Log("The driver offered another page. Ignoring it.");
            e.CancelAll = true;
            return;
        }

        try
        {
            var info = e.PendingImageInfo;
            var flavor = PixelFlavor.Chocolate;
            var flavorCap = _host.Source.Capabilities.ICapPixelFlavor;
            if (flavorCap.IsSupported && flavorCap.CanGetCurrent)
            {
                flavor = flavorCap.GetCurrent();
            }

            Output.Log($"Page: {info.ImageWidth}x{info.ImageLength}, {info.BitsPerPixel} bpp, {info.XResolution:0.#} dpi, {info.PixelType}, {info.Compression}, flavor {flavor}");
            _sink.Start(info, flavor, request.Dpi);
            _pageSeen = true;
        }
        catch (Exception ex)
        {
            _failure ??= new WorkerException(ScanErrorCode.Unknown, ex.Message);
            e.CancelAll = true;
        }
    }

    private void OnDataTransferred(object? sender, DataTransferredEventArgs e)
    {
        if (_failure is not null || e.MemoryData is null)
        {
            if (e.MemoryData is null)
            {
                _failure ??= new WorkerException(ScanErrorCode.Unknown, $"Expected a memory transfer but got {e.TransferType}.");
                _host.Pump.Quit();
            }

            return;
        }

        try
        {
            _sink.WriteStrip(e.MemoryInfo, e.MemoryData);
            var now = DateTime.UtcNow;
            if (now - _lastRowEvent >= RowEventInterval)
            {
                _lastRowEvent = now;
                Output.Emit(new WorkerEvent { Type = WorkerEvent.Rows, RowCount = _sink.RowsWritten });
            }
        }
        catch (Exception ex)
        {
            _failure ??= new WorkerException(ScanErrorCode.Unknown, $"{ex.GetType().Name}: {ex.Message}");
            _host.Pump.Quit();
        }
    }

    private void OnTransferError(object? sender, TransferErrorEventArgs e)
    {
        _failure ??= e.Exception is not null
            ? new WorkerException(ScanErrorCode.Unknown, $"{e.Exception.GetType().Name}: {e.Exception.Message}")
            : ErrorMapping.FromStatus(e.SourceStatus, $"Transfer failed: {e.ReturnCode}");
        Output.Log($"Transfer error: {_failure.Code}: {_failure.Message}; {_sink.RowsWritten} rows so far");
        _host.Pump.Quit();
    }

    private void OnTransferCanceled(object? sender, TransferCanceledEventArgs e)
    {
        // A driver can report a jam as a cancel. Either way the page isn't complete.
        _canceled = true;
        Output.Log($"Transfer canceled by driver; {_sink.RowsWritten} rows so far");
        _host.Pump.Quit();
    }
}
