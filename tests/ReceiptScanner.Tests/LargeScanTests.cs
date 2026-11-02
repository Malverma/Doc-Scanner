using ReceiptScanner.Core.Imaging;
using ReceiptScanner.Core.Scanning;
using ReceiptScanner.Core.Storage;

namespace ReceiptScanner.Tests;

/// <summary>Worst-case size check. It writes gigabytes, so it only runs when RS_LARGE names a folder.</summary>
public class LargeScanTests
{
    [Fact]
    public async Task Longest_color_scan_at_600_dpi_stays_small_in_memory()
    {
        var output = Environment.GetEnvironmentVariable("RS_LARGE");
        if (output is null)
        {
            return;
        }

        Directory.CreateDirectory(output);
        var raw = Path.Combine(output, "large.raw");
        var backend = new SimulatedBackend(SimulatedBehavior.Normal, 1800, TimeSpan.Zero);
        var request = new ScanRequest(600, ColorMode.Color, raw, LongPaperStrategy.Auto, "", false);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var outcome = Assert.IsType<ScanOutcome.Completed>(await backend.ScanAsync(request, new Progress<ScanProgress>(), TestContext.Current.CancellationToken));
        var scanTime = clock.Elapsed;
        var image = outcome.Image;

        clock.Restart();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var analysis = ImageAnalysis.Analyze(image);
        var analyzeTime = clock.Elapsed;

        clock.Restart();
        var preview = ImageOps.BuildPreview(image, analysis.TrimRegion, 640);
        var previewTime = clock.Elapsed;

        clock.Restart();
        var saved = ReceiptStore.Save(image, analysis.TrimRegion, output, OutputFormat.Both, DateTime.Now);
        var saveTime = clock.Elapsed;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before - preview.Bgra.Length;

        var sizes = string.Join(", ", saved.Files.Select(f => $"{Path.GetExtension(f)} {new FileInfo(f).Length / 1048576.0:0.0} MB"));
        var summary = $"raw {new FileInfo(raw).Length / 1048576.0:0} MB {image.Width}x{image.Height}; generate {scanTime.TotalSeconds:0.0}s; analyze {analyzeTime.TotalSeconds:0.0}s; preview {previewTime.TotalSeconds:0.0}s {preview.Width}x{preview.Height}; save {saveTime.TotalSeconds:0.0}s; allocated excluding preview {allocated / 1048576.0:0.0} MB; {sizes}; trim {analysis.TrimRegion}";
        await File.WriteAllTextAsync(Path.Combine(output, "summary.txt"), summary, TestContext.Current.CancellationToken);
        File.Delete(raw);

        Assert.True(allocated < 200 * 1048576L, summary);
    }
}
