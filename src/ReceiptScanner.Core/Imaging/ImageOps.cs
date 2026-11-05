using ReceiptScanner.Core.Scanning;

namespace ReceiptScanner.Core.Imaging;

/// <summary>A downscaled BGRA image for display, with no dependency on any UI framework.</summary>
public sealed record PreviewImage(int Width, int Height, byte[] Bgra);

public static class ImageOps
{
    /// <summary>Tall enough for readable long receipts, small enough for any GPU texture limit.</summary>
    public const int MaxPreviewHeight = 8000;

    public static PreviewImage BuildPreview(RawImage image, PixelRegion region, int maxWidth)
    {
        var scale = Math.Min(1d, (double)maxWidth / region.Width);
        scale = Math.Min(scale, (double)MaxPreviewHeight / region.Height);
        var width = Math.Max(1, (int)Math.Round(region.Width * scale));
        var height = Math.Max(1, (int)Math.Round(region.Height * scale));

        var channels = image.BytesPerPixel;
        var sums = new long[width * channels];
        var counts = new int[width];
        var bgra = new byte[width * height * 4];
        var row = new byte[image.Width * channels];
        var currentRow = 0;

        using var handle = File.OpenHandle(image.Path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
        for (var y = 0; y < region.Height; y++)
        {
            ImageAnalysis.ReadRow(handle, image, region.Top + y, row);
            for (var x = 0; x < region.Width; x++)
            {
                var dx = Math.Min(width - 1, (int)(x * (long)width / region.Width));
                var source = (region.Left + x) * channels;
                for (var c = 0; c < channels; c++)
                {
                    sums[(dx * channels) + c] += row[source + c];
                }

                counts[dx]++;
            }

            var targetRow = Math.Min(height - 1, (int)((y + 1) * (long)height / region.Height));
            var isLast = y == region.Height - 1;
            if (targetRow != currentRow || isLast)
            {
                FlushRow(sums, counts, channels, bgra, currentRow, width);
                currentRow = targetRow;
            }
        }

        return new PreviewImage(width, height, bgra);
    }

    private static void FlushRow(long[] sums, int[] counts, int channels, byte[] bgra, int y, int width)
    {
        for (var x = 0; x < width; x++)
        {
            var i = ((y * width) + x) * 4;
            var count = Math.Max(1, counts[x]);
            if (channels == 1)
            {
                var gray = (byte)(sums[x] / count);
                bgra[i] = gray;
                bgra[i + 1] = gray;
                bgra[i + 2] = gray;
            }
            else
            {
                bgra[i] = (byte)(sums[(x * 3) + 2] / count);
                bgra[i + 1] = (byte)(sums[(x * 3) + 1] / count);
                bgra[i + 2] = (byte)(sums[x * 3] / count);
            }

            bgra[i + 3] = 255;
        }

        Array.Clear(sums);
        Array.Clear(counts);
    }

    /// <summary>
    /// Writes a new raw file holding the region turned clockwise by 90 degree steps. It works through a
    /// band of rows at a time, so a 660 MB scan needs no more memory than a small one.
    /// </summary>
    public static RawImage Rotate(RawImage image, PixelRegion region, int quarterTurns, string outputPath, int maxBandBytes = 32 << 20)
    {
        var turns = ((quarterTurns % 4) + 4) % 4;
        var channels = image.BytesPerPixel;
        var newWidth = turns % 2 == 0 ? region.Width : region.Height;
        var newHeight = turns % 2 == 0 ? region.Height : region.Width;
        var sourceStride = region.Width * channels;
        var bandRows = Math.Clamp(maxBandBytes / sourceStride, 1, region.Height);

        var band = new byte[bandRows * sourceStride];
        var fullRow = new byte[image.Width * channels];
        var segment = new byte[Math.Max(newWidth, bandRows) * channels];

        using var input = File.OpenHandle(image.Path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
        using var output = File.OpenHandle(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, FileOptions.RandomAccess, (long)newWidth * newHeight * channels);

        for (var bandTop = 0; bandTop < region.Height; bandTop += bandRows)
        {
            var rows = Math.Min(bandRows, region.Height - bandTop);
            for (var r = 0; r < rows; r++)
            {
                ImageAnalysis.ReadRow(input, image, region.Top + bandTop + r, fullRow);
                Buffer.BlockCopy(fullRow, region.Left * channels, band, r * sourceStride, sourceStride);
            }

            // Each band fills a run of rows when turning 180 degrees, and a run of columns otherwise.
            if (turns % 2 == 0)
            {
                for (var r = 0; r < rows; r++)
                {
                    var targetY = turns == 0 ? bandTop + r : region.Height - 1 - bandTop - r;
                    for (var x = 0; x < region.Width; x++)
                    {
                        var sx = turns == 0 ? x : region.Width - 1 - x;
                        CopyPixel(band, (r * sourceStride) + (sx * channels), segment, x * channels, channels);
                    }

                    RandomAccess.Write(output, segment.AsSpan(0, sourceStride), (long)targetY * newWidth * channels);
                }
            }
            else
            {
                // Clockwise, the last source row becomes the first column. Counter-clockwise, the first does.
                var firstColumn = turns == 1 ? region.Height - bandTop - rows : bandTop;
                for (var y = 0; y < newHeight; y++)
                {
                    var sx = turns == 1 ? y : region.Width - 1 - y;
                    for (var i = 0; i < rows; i++)
                    {
                        var r = turns == 1 ? rows - 1 - i : i;
                        CopyPixel(band, (r * sourceStride) + (sx * channels), segment, i * channels, channels);
                    }

                    RandomAccess.Write(output, segment.AsSpan(0, rows * channels), (((long)y * newWidth) + firstColumn) * channels);
                }
            }
        }

        return image with { Path = outputPath, Width = newWidth, Height = newHeight };
    }

    private static void CopyPixel(byte[] from, int fromIndex, byte[] to, int toIndex, int channels)
    {
        for (var c = 0; c < channels; c++)
        {
            to[toIndex + c] = from[fromIndex + c];
        }
    }
}
