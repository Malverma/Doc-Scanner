using ReceiptScanner.Core.Scanning;
using Microsoft.Win32.SafeHandles;

namespace ReceiptScanner.Core.Imaging;

public sealed record ContentAnalysis(
    PixelRegion ContentBounds,
    PixelRegion TrimRegion,
    bool IsBlank,
    bool ContentAtBottomEdge,
    int BackgroundLevel);

/// <summary>
/// Finds printed content by how far pixels differ from the most common brightness. Using the absolute
/// difference also handles a scanner background that is darker than the paper. The thresholds are
/// deliberately low: faded thermal print must never be trimmed away.
/// </summary>
public static class ImageAnalysis
{
    /// <summary>Brightness difference that counts as content. Scanner noise is well under this.</summary>
    public const int ContentThreshold = 24;

    private const double SidePaddingMm = 3;
    private const double EndPaddingMm = 5;
    private const double BottomEdgeBandMm = 1;

    public static ContentAnalysis Analyze(RawImage image)
    {
        using var handle = File.OpenHandle(image.Path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
        var stride = image.Width * image.BytesPerPixel;
        var row = new byte[stride];
        var background = FindBackgroundLevel(handle, image, row);

        // A single-pixel speck shouldn't count, but a short faint line of print should.
        var minRowPixels = Math.Max(2, image.Width / 1000);
        var bandRows = Math.Max(1, (int)Math.Round(BottomEdgeBandMm * image.Dpi / 25.4));
        var columnHits = new int[image.Width];
        var firstRow = -1;
        var lastRow = -1;

        for (var y = 0; y < image.Height; y++)
        {
            ReadRow(handle, image, y, row);
            var hits = 0;
            for (var x = 0; x < image.Width; x++)
            {
                if (Math.Abs(Luminance(row, x, image.BytesPerPixel) - background) >= ContentThreshold)
                {
                    hits++;
                    columnHits[x]++;
                }
            }

            if (hits >= minRowPixels)
            {
                if (firstRow < 0)
                {
                    firstRow = y;
                }

                lastRow = y;
            }
        }

        if (firstRow < 0)
        {
            var full = PixelRegion.Full(image);
            return new ContentAnalysis(full, full, IsBlank: true, ContentAtBottomEdge: false, background);
        }

        var left = Array.FindIndex(columnHits, hits => hits >= 2);
        var right = Array.FindLastIndex(columnHits, hits => hits >= 2);
        if (left < 0)
        {
            left = 0;
            right = image.Width - 1;
        }

        var bounds = new PixelRegion(left, firstRow, right + 1, lastRow + 1);
        var side = (int)Math.Round(SidePaddingMm * image.Dpi / 25.4);
        var end = (int)Math.Round(EndPaddingMm * image.Dpi / 25.4);
        var trim = new PixelRegion(
            Math.Max(0, left - side),
            Math.Max(0, firstRow - end),
            Math.Min(image.Width, right + 1 + side),
            Math.Min(image.Height, lastRow + 1 + end));

        var atEdge = lastRow >= image.Height - bandRows;
        return new ContentAnalysis(bounds, trim, IsBlank: false, atEdge, background);
    }

    internal static void ReadRow(SafeFileHandle handle, RawImage image, int y, byte[] row)
    {
        var offset = (long)y * image.Width * image.BytesPerPixel;
        var total = 0;
        while (total < row.Length)
        {
            var read = RandomAccess.Read(handle, row.AsSpan(total), offset + total);
            if (read == 0)
            {
                throw new EndOfStreamException($"Scan data ended early at row {y} of {image.Height}.");
            }

            total += read;
        }
    }

    private static int FindBackgroundLevel(SafeFileHandle handle, RawImage image, byte[] row)
    {
        var histogram = new long[256];
        var step = Math.Max(1, image.Height / 2000);
        for (var y = 0; y < image.Height; y += step)
        {
            ReadRow(handle, image, y, row);
            for (var x = 0; x < image.Width; x++)
            {
                histogram[Luminance(row, x, image.BytesPerPixel)]++;
            }
        }

        var mode = 0;
        for (var level = 1; level < histogram.Length; level++)
        {
            if (histogram[level] > histogram[mode])
            {
                mode = level;
            }
        }

        return mode;
    }

    private static int Luminance(byte[] row, int x, int bytesPerPixel)
    {
        if (bytesPerPixel == 1)
        {
            return row[x];
        }

        var i = x * 3;
        return ((row[i] * 77) + (row[i + 1] * 150) + (row[i + 2] * 29)) >> 8;
    }
}
