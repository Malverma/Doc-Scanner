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

    /// <summary>Writes a new raw file holding the region turned clockwise by 90 degree steps.</summary>
    public static RawImage Rotate(RawImage image, PixelRegion region, int quarterTurns, string outputPath)
    {
        var turns = ((quarterTurns % 4) + 4) % 4;
        var channels = image.BytesPerPixel;
        var newWidth = turns % 2 == 0 ? region.Width : region.Height;
        var newHeight = turns % 2 == 0 ? region.Height : region.Width;

        // The app runs as x64, and the largest possible scan (about 660 MB) fits one array.
        var source = new byte[checked(region.Width * region.Height * channels)];
        var row = new byte[image.Width * channels];
        using (var handle = File.OpenHandle(image.Path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan))
        {
            for (var y = 0; y < region.Height; y++)
            {
                ImageAnalysis.ReadRow(handle, image, region.Top + y, row);
                Buffer.BlockCopy(row, region.Left * channels, source, y * region.Width * channels, region.Width * channels);
            }
        }

        using (var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            var outRow = new byte[newWidth * channels];
            for (var y = 0; y < newHeight; y++)
            {
                for (var x = 0; x < newWidth; x++)
                {
                    var (sx, sy) = turns switch
                    {
                        1 => (y, region.Height - 1 - x),
                        2 => (region.Width - 1 - x, region.Height - 1 - y),
                        3 => (region.Width - 1 - y, x),
                        _ => (x, y),
                    };
                    var from = ((sy * region.Width) + sx) * channels;
                    for (var c = 0; c < channels; c++)
                    {
                        outRow[(x * channels) + c] = source[from + c];
                    }
                }

                output.Write(outRow);
            }
        }

        return image with { Path = outputPath, Width = newWidth, Height = newHeight };
    }
}
