using System.Text;
using ReceiptScanner.Core.Imaging;
using ReceiptScanner.Core.Scanning;

namespace ReceiptScanner.Tests;

public class ImageAnalysisTests
{
    [Fact]
    public void Blank_tail_and_sides_are_trimmed_with_padding()
    {
        using var dir = new TempDir();
        var image = Images.Gray(dir.Path, 400, 1000, (x, y) => x is >= 100 and < 200 && y is >= 50 and < 400 ? (byte)30 : Images.Paper);

        var result = ImageAnalysis.Analyze(image);

        Assert.False(result.IsBlank);
        Assert.False(result.ContentAtBottomEdge);
        Assert.Equal(new PixelRegion(100, 50, 200, 400), result.ContentBounds);
        Assert.Equal(new PixelRegion(88, 30, 212, 420), result.TrimRegion);
    }

    [Fact]
    public void Faded_print_counts_as_content()
    {
        using var dir = new TempDir();
        var faded = (byte)(Images.Paper - ImageAnalysis.ContentThreshold);
        var image = Images.Gray(dir.Path, 300, 600, (x, y) => y is >= 500 and < 505 && x is >= 50 and < 90 ? faded : Images.Paper);

        var result = ImageAnalysis.Analyze(image);

        Assert.Equal(505, result.ContentBounds.Bottom);
    }

    [Fact]
    public void Scanner_noise_is_not_content()
    {
        using var dir = new TempDir();
        var random = new Random(7);
        var image = Images.Gray(dir.Path, 300, 300, (_, _) => (byte)(Images.Paper + random.Next(-8, 9)));

        Assert.True(ImageAnalysis.Analyze(image).IsBlank);
    }

    [Fact]
    public void Single_speck_does_not_extend_the_receipt()
    {
        using var dir = new TempDir();
        var image = Images.Gray(dir.Path, 400, 800, (x, y) =>
            (y is >= 50 and < 200 && x is >= 50 and < 150) || (x == 300 && y == 700) ? (byte)30 : Images.Paper);

        Assert.Equal(200, ImageAnalysis.Analyze(image).ContentBounds.Bottom);
    }

    [Fact]
    public void Blank_page_is_kept_whole()
    {
        using var dir = new TempDir();
        var image = Images.Gray(dir.Path, 100, 100, (_, _) => Images.Paper);

        var result = ImageAnalysis.Analyze(image);

        Assert.True(result.IsBlank);
        Assert.Equal(PixelRegion.Full(image), result.TrimRegion);
    }

    [Fact]
    public void Print_reaching_the_last_row_is_flagged()
    {
        using var dir = new TempDir();
        var image = Images.Gray(dir.Path, 300, 500, (x, _) => x is >= 50 and < 90 ? (byte)30 : Images.Paper);

        Assert.True(ImageAnalysis.Analyze(image).ContentAtBottomEdge);
    }

    [Fact]
    public void White_paper_on_a_dark_background_is_found_by_its_difference()
    {
        using var dir = new TempDir();
        var image = Images.Gray(dir.Path, 600, 600, (x, y) => x is >= 200 and < 400 && y < 300 ? (byte)250 : (byte)40);

        Assert.Equal(new PixelRegion(200, 0, 400, 300), ImageAnalysis.Analyze(image).ContentBounds);
    }

    [Fact]
    public void Color_images_are_analyzed_by_brightness()
    {
        using var dir = new TempDir();
        var image = Images.Rgb(dir.Path, 300, 300, (x, y) => x is >= 50 and < 100 && y is >= 40 and < 90 ? ((byte)200, (byte)20, (byte)20) : ((byte)245, (byte)245, (byte)245));

        Assert.Equal(new PixelRegion(50, 40, 100, 90), ImageAnalysis.Analyze(image).ContentBounds);
    }
}

public class CompletenessTests
{
    private static RawImage OfLengthMm(double mm, int dpi = 300) =>
        new("unused", 100, (int)Math.Round(mm * dpi / 25.4), 1, dpi);

    private static ContentAnalysis Analysis(bool blank = false, bool edge = false) =>
        new(default, default, blank, edge, 245);

    private static LongPaperStatus Status(bool confirmed = true, double? frame = 72) => new("Test", confirmed, "", frame);

    [Theory]
    [InlineData(150)]
    [InlineData(420)]
    [InlineData(1800)]
    public void Ordinary_lengths_are_clean(double mm) =>
        Assert.True(Completeness.Assess(OfLengthMm(mm), Analysis(), Status()).IsClean);

    [Theory]
    [InlineData(1827)]
    [InlineData(1828.8)]
    public void Reaching_the_rated_limit_is_suspect(double mm) =>
        Assert.Contains(SuspectReason.AtScannerLimit, Completeness.Assess(OfLengthMm(mm), Analysis(), Status()).Reasons);

    [Fact]
    public void Stopping_exactly_at_a_clamped_frame_is_suspect() =>
        Assert.Contains(SuspectReason.AtFrameLimit, Completeness.Assess(OfLengthMm(355.6), Analysis(), Status(frame: 14)).Reasons);

    [Fact]
    public void A_short_reported_frame_does_not_flag_longer_receipts() =>
        Assert.True(Completeness.Assess(OfLengthMm(420), Analysis(), Status(frame: 11)).IsClean);

    [Fact]
    public void Print_at_the_edge_is_suspect() =>
        Assert.Contains(SuspectReason.ContentAtBottomEdge, Completeness.Assess(OfLengthMm(300), Analysis(edge: true), Status()).Reasons);

    [Fact]
    public void Blank_is_suspect_rather_than_discarded() =>
        Assert.Contains(SuspectReason.Blank, Completeness.Assess(OfLengthMm(300), Analysis(blank: true), Status()).Reasons);

    [Fact]
    public void Unconfirmed_long_paper_is_suspect() =>
        Assert.Contains(SuspectReason.LongPaperUnconfirmed, Completeness.Assess(OfLengthMm(300), Analysis(), Status(confirmed: false)).Reasons);
}

public class EncoderTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Png_holds_exactly_the_cropped_pixels(int channels)
    {
        using var dir = new TempDir();
        var image = Pattern(dir.Path, channels);
        var region = new PixelRegion(10, 20, 170, 230);
        using var stream = new MemoryStream();

        ReceiptEncoder.WritePng(image, region, stream);

        var (width, height, colorType, idat) = ParsePng(stream.ToArray());
        Assert.Equal(region.Width, width);
        Assert.Equal(region.Height, height);
        Assert.Equal(channels == 1 ? 0 : 2, colorType);
        var pixels = Images.Unfilter(Images.Inflate(idat), region.Width * channels);
        Assert.Equal(Crop(image, region), pixels);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Pdf_structure_and_pixels_are_correct(int channels)
    {
        using var dir = new TempDir();
        var image = Pattern(dir.Path, channels);
        var region = new PixelRegion(10, 20, 170, 230);
        using var stream = new MemoryStream();

        ReceiptEncoder.WritePdf(image, region, stream);

        var bytes = stream.ToArray();
        var text = Encoding.Latin1.GetString(bytes);

        var xrefAt = int.Parse(text[(text.LastIndexOf("startxref\n", StringComparison.Ordinal) + 10)..].Split('\n')[0]);
        Assert.StartsWith("xref\n0 7\n", text[xrefAt..]);
        var entries = text[(xrefAt + 9)..].Split('\n');
        for (var n = 1; n <= 6; n++)
        {
            var offset = int.Parse(entries[n].Split(' ')[0]);
            Assert.StartsWith($"{n} 0 obj", text[offset..]);
        }

        var length = int.Parse(text[(text.IndexOf("6 0 obj\n", StringComparison.Ordinal) + 8)..].Split('\n')[0]);
        var dataStart = text.IndexOf("stream\n", text.IndexOf("5 0 obj", StringComparison.Ordinal), StringComparison.Ordinal) + 7;
        var pixels = Images.Unfilter(Images.Inflate(bytes[dataStart..(dataStart + length)]), region.Width * channels);
        Assert.Equal(Crop(image, region), pixels);
        Assert.Contains("/MediaBox [0 0 115.2 151.2]", text);
    }

    private static RawImage Pattern(string dir, int channels) => channels == 1
        ? Images.Gray(dir, 200, 250, (x, y) => (byte)((x * 3) ^ (y * 7)))
        : Images.Rgb(dir, 200, 250, (x, y) => ((byte)x, (byte)y, (byte)(x + y)));

    private static byte[] Crop(RawImage image, PixelRegion region)
    {
        var all = File.ReadAllBytes(image.Path);
        var channels = image.BytesPerPixel;
        var output = new byte[region.Width * region.Height * channels];
        for (var y = 0; y < region.Height; y++)
        {
            Array.Copy(all, ((region.Top + y) * image.Width + region.Left) * channels, output, y * region.Width * channels, region.Width * channels);
        }

        return output;
    }

    private static (int Width, int Height, int ColorType, byte[] Idat) ParsePng(byte[] png)
    {
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
        var offset = 8;
        var width = 0;
        var height = 0;
        var colorType = -1;
        using var idat = new MemoryStream();
        var sawEnd = false;
        while (offset < png.Length)
        {
            var length = (png[offset] << 24) | (png[offset + 1] << 16) | (png[offset + 2] << 8) | png[offset + 3];
            var type = Encoding.ASCII.GetString(png, offset + 4, 4);
            var data = png[(offset + 8)..(offset + 8 + length)];
            var stored = (uint)((png[offset + 8 + length] << 24) | (png[offset + 9 + length] << 16) | (png[offset + 10 + length] << 8) | png[offset + 11 + length]);
            Assert.Equal(Crc(png[(offset + 4)..(offset + 8 + length)]), stored);
            switch (type)
            {
                case "IHDR":
                    width = (data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3];
                    height = (data[4] << 24) | (data[5] << 16) | (data[6] << 8) | data[7];
                    colorType = data[9];
                    break;
                case "IDAT":
                    idat.Write(data);
                    break;
                case "IEND":
                    sawEnd = true;
                    break;
            }

            offset += 12 + length;
        }

        Assert.True(sawEnd);
        return (width, height, colorType, idat.ToArray());
    }

    private static uint Crc(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
            }
        }

        return crc ^ 0xFFFFFFFF;
    }
}

public class ImageOpsTests
{
    [Fact]
    public void Quarter_turn_moves_pixels_clockwise()
    {
        using var dir = new TempDir();
        // Top-left pixel is marked. After a clockwise turn it should be at the top-right.
        var image = Images.Gray(dir.Path, 4, 2, (x, y) => x == 0 && y == 0 ? (byte)1 : (byte)0);

        var rotated = ImageOps.Rotate(image, PixelRegion.Full(image), 1, Path.Combine(dir.Path, "r.raw"));

        Assert.Equal(2, rotated.Width);
        Assert.Equal(4, rotated.Height);
        Assert.True(rotated.IsIntact());
        var data = File.ReadAllBytes(rotated.Path);
        Assert.Equal(1, data[1]);
        Assert.Equal(1, data.Sum(b => b));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Four_quarter_turns_restore_the_image(int channels)
    {
        using var dir = new TempDir();
        var image = channels == 1
            ? Images.Gray(dir.Path, 13, 7, (x, y) => (byte)((x * 11) + y))
            : Images.Rgb(dir.Path, 13, 7, (x, y) => ((byte)x, (byte)y, (byte)(x * y)));
        var current = image;
        for (var i = 0; i < 4; i++)
        {
            current = ImageOps.Rotate(current, PixelRegion.Full(current), 1, Path.Combine(dir.Path, $"r{i}.raw"));
        }

        Assert.Equal(File.ReadAllBytes(image.Path), File.ReadAllBytes(current.Path));
    }

    [Fact]
    public void Rotation_applies_the_crop()
    {
        using var dir = new TempDir();
        var image = Images.Gray(dir.Path, 20, 10, (_, _) => 5);

        var rotated = ImageOps.Rotate(image, new PixelRegion(2, 3, 12, 8), 1, Path.Combine(dir.Path, "r.raw"));

        Assert.Equal((5, 10), (rotated.Width, rotated.Height));
    }

    [Fact]
    public void Preview_has_no_unwritten_rows_and_shows_gray_correctly()
    {
        using var dir = new TempDir();
        var image = Images.Gray(dir.Path, 1000, 3333, (_, _) => 200);

        var preview = ImageOps.BuildPreview(image, PixelRegion.Full(image), 300);

        Assert.Equal(300, preview.Width);
        Assert.Equal(1000, preview.Height);
        for (var i = 0; i < preview.Bgra.Length; i += 4)
        {
            Assert.Equal(200, preview.Bgra[i]);
            Assert.Equal(255, preview.Bgra[i + 3]);
        }
    }

    [Fact]
    public void Preview_converts_rgb_to_bgra()
    {
        using var dir = new TempDir();
        var image = Images.Rgb(dir.Path, 10, 10, (_, _) => (10, 20, 30));

        var preview = ImageOps.BuildPreview(image, PixelRegion.Full(image), 10);

        Assert.Equal(new byte[] { 30, 20, 10, 255 }, preview.Bgra[..4]);
    }

    [Fact]
    public void Very_long_preview_stays_within_the_height_limit()
    {
        using var dir = new TempDir();
        var image = Images.Gray(dir.Path, 100, 40000, (_, _) => 200);

        var preview = ImageOps.BuildPreview(image, PixelRegion.Full(image), 100);

        Assert.True(preview.Height <= ImageOps.MaxPreviewHeight);
    }
}
