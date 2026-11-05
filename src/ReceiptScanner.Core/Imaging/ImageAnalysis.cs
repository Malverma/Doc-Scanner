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
/// A narrow receipt on a black backdrop, such as the Epson RR-70W's, is handled separately: the paper is
/// then the most common brightness, so the backdrop would count as content.
/// </summary>
public static class ImageAnalysis
{
    /// <summary>Brightness difference that counts as content. Scanner noise is well under this.</summary>
    public const int ContentThreshold = 24;

    private const double SidePaddingMm = 3;
    private const double EndPaddingMm = 5;
    private const double BottomEdgeBandMm = 1;

    /// <summary>How much darker the outer columns must be than the paper to count as a backdrop.</summary>
    public const int BackdropContrast = 96;

    private const double BackdropPaddingMm = 1;
    private const double MinPaperRowMm = 5;
    private const double PrintMarginMm = 2;

    /// <summary>Bright stretches narrower than this are sensor streaks or specks, not paper.</summary>
    private const double MinPaperStretchMm = 1;

    /// <summary>
    /// A skewed receipt's trailing corner touches the bottom, but a cut-off receipt still spans most of its
    /// width there.
    /// </summary>
    private const double CutOffWidthShare = 0.5;

    public static ContentAnalysis Analyze(RawImage image)
    {
        using var handle = File.OpenHandle(image.Path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
        var stride = image.Width * image.BytesPerPixel;
        var row = new byte[stride];
        var background = FindBackgroundLevel(handle, image, row);
        var backdrop = FindEdgeLevel(handle, image, row);
        if (background - backdrop >= BackdropContrast)
        {
            return AnalyzeOnBackdrop(handle, image, row, backdrop, background);
        }

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

    /// <summary>The paper is whatever is brighter than halfway to the backdrop. Faint sensor streaks stay below that.</summary>
    private static ContentAnalysis AnalyzeOnBackdrop(SafeFileHandle handle, RawImage image, byte[] row, int backdrop, int paper)
    {
        var threshold = (backdrop + paper) / 2;
        int Px(double mm) => (int)Math.Round(mm * image.Dpi / 25.4);
        var minPaper = Math.Max(2, Px(MinPaperRowMm));
        var margin = Px(PrintMarginMm);
        var minPrint = Math.Max(2, image.Width / 1000);
        var bandRows = Math.Max(1, Px(BottomEdgeBandMm));
        var minStretch = Math.Max(2, Px(MinPaperStretchMm));

        int left = image.Width, right = -1, top = -1, bottom = -1, widest = 0, bottomWidth = 0;
        var printFound = false;
        for (var y = 0; y < image.Height; y++)
        {
            ReadRow(handle, image, y, row);
            // The paper spans the bright stretches of at least minStretch, so a thin bright sensor streak
            // can't widen it, while dark print between them, however wide, stays inside it.
            int first = -1, last = -1, count = 0, run = 0;
            for (var x = 0; x <= image.Width; x++)
            {
                if (x < image.Width && Luminance(row, x, image.BytesPerPixel) >= threshold)
                {
                    run++;
                    continue;
                }

                if (run >= minStretch)
                {
                    first = first < 0 ? x - run : first;
                    last = x - 1;
                    count += run;
                }

                run = 0;
            }

            if (count < minPaper)
            {
                continue;
            }

            top = top < 0 ? y : top;
            bottom = y;
            left = Math.Min(left, first);
            right = Math.Max(right, last);
            widest = Math.Max(widest, last - first + 1);
            if (y >= image.Height - bandRows)
            {
                bottomWidth = Math.Max(bottomWidth, last - first + 1);
            }

            if (!printFound)
            {
                var dark = 0;
                for (var x = first + margin; x <= last - margin; x++)
                {
                    if (paper - Luminance(row, x, image.BytesPerPixel) >= ContentThreshold)
                    {
                        dark++;
                    }
                }

                printFound = dark >= minPrint;
            }
        }

        if (top < 0)
        {
            var full = PixelRegion.Full(image);
            return new ContentAnalysis(full, full, IsBlank: true, ContentAtBottomEdge: false, backdrop);
        }

        var bounds = new PixelRegion(left, top, right + 1, bottom + 1);
        var pad = Px(BackdropPaddingMm);
        var trim = new PixelRegion(
            Math.Max(0, left - pad),
            Math.Max(0, top - pad),
            Math.Min(image.Width, right + 1 + pad),
            Math.Min(image.Height, bottom + 1 + pad));
        var cutOff = bottomWidth >= widest * CutOffWidthShare;
        return new ContentAnalysis(bounds, printFound ? trim : PixelRegion.Full(image), IsBlank: !printFound, cutOff, backdrop);
    }

    /// <summary>The most common brightness in the outer 1% of columns, where a narrow receipt rarely reaches.</summary>
    private static int FindEdgeLevel(SafeFileHandle handle, RawImage image, byte[] row)
    {
        var histogram = new long[256];
        var edge = Math.Max(1, image.Width / 100);
        var step = Math.Max(1, image.Height / 2000);
        for (var y = 0; y < image.Height; y += step)
        {
            ReadRow(handle, image, y, row);
            for (var x = 0; x < edge; x++)
            {
                histogram[Luminance(row, x, image.BytesPerPixel)]++;
                histogram[Luminance(row, image.Width - 1 - x, image.BytesPerPixel)]++;
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
